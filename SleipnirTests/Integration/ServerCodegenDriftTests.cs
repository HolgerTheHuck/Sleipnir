// Automated drift-detection gate for the server-side export tool (Sleipnir.Server.Codegen).
//
// Slice 3 wired the export tool into Story01's build (AfterBuild target → drift-check on every
// `dotnet build Sleipnir.sln`), but that only secures the *happy path* (committed contract matches
// runtime). It does NOT assert that drift is actually *detected* when the contract goes stale, nor
// that --regen repairs it — those were only proven by an ad-hoc manual test. This test automates
// the full negative cycle against the real Story01 assembly:
//   1. Happy path: tool against a copy of the committed contract → exit 0 (no drift).
//   2. Drift path: tool against a tampered contract (a method renamed in the JSON) → exit 1
//      (drift detected), and the contract file is left untouched.
//   3. Regen path: tool with --regen against the tampered contract → exit 0, and the file is
//      rewritten to match the runtime discovery again.
//
// The tool runs in its own process (dotnet Sleipnir.Server.Codegen.dll ...), loading the built
// Story01 assembly exactly as the MSBuild target does. Requires `dotnet build Sleipnir.sln` first
// (Story01 and the tool are not ProjectReferences of SleipnirTests — same constraint as
// DiscoveryContractTests).
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace SleipnirTests.Integration;

public class ServerCodegenDriftTests
{
    private readonly ITestOutputHelper _output;
    public ServerCodegenDriftTests(ITestOutputHelper output) => _output = output;

    private static DirectoryInfo ResolveRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "stories"))
                && Directory.Exists(Path.Combine(dir.FullName, "clients"))
                && File.Exists(Path.Combine(dir.FullName, "Sleipnir.sln")))
            {
                return dir;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repo root from " + AppContext.BaseDirectory);
    }

    // Prefer the build config matching the running test bin dir; fall back to whichever exists.
    private static IEnumerable<string> ConfigOrder()
        => AppContext.BaseDirectory.Contains(Path.DirectorySeparatorChar + "Debug", StringComparison.OrdinalIgnoreCase)
            ? new[] { "Debug", "Release" }
            : new[] { "Release", "Debug" };

    private static string ResolveStory01Dll()
    {
        var repo = ResolveRepoRoot();
        var binRoot = Path.Combine(repo.FullName, "stories", "01-n-plus-one-screen", "bin");
        foreach (var cfg in ConfigOrder())
        {
            var dll = Path.Combine(binRoot, cfg, "net8.0", "Story01.dll");
            if (File.Exists(dll)) return dll;
        }
        if (Directory.Exists(binRoot))
        {
            var found = Directory.GetFiles(binRoot, "Story01.dll", SearchOption.AllDirectories);
            if (found.Length > 0) return found[0];
        }
        throw new FileNotFoundException(
            "Story01.dll not built. Run `dotnet build Sleipnir.sln` first. Searched under " + binRoot);
    }

    private static string ResolveToolDll()
    {
        var repo = ResolveRepoRoot();
        foreach (var cfg in ConfigOrder())
        {
            var dll = Path.Combine(repo.FullName, "Sleipnir.Server.Codegen", "bin", cfg, "net8.0", "Sleipnir.Server.Codegen.dll");
            if (File.Exists(dll)) return dll;
        }
        throw new FileNotFoundException(
            "Sleipnir.Server.Codegen.dll not built. Run `dotnet build Sleipnir.sln` first.");
    }

    private static string ResolveCommittedContract()
    {
        var repo = ResolveRepoRoot();
        return Path.Combine(repo.FullName, "stories", "01-n-plus-one-screen", "contract.sleipnir.json");
    }

    /// <summary>The guide server fixture (guide/server). Story01's controllers carry exactly one
    /// method each, so method-order cases (reorder noise, non-alphabetical signature order) need
    /// a server with multi-method controllers — the guide's Portfolio has five. The export tool
    /// is assembly-agnostic (same MSBuild target on any server), so the harness points it at
    /// whichever server a scenario needs.</summary>
    private static string ResolveGuideDll()
    {
        var repo = ResolveRepoRoot();
        var binRoot = Path.Combine(repo.FullName, "guide", "server", "bin");
        foreach (var cfg in ConfigOrder())
        {
            var dll = Path.Combine(binRoot, cfg, "net8.0", "Story.Api.dll");
            if (File.Exists(dll)) return dll;
        }
        if (Directory.Exists(binRoot))
        {
            var found = Directory.GetFiles(binRoot, "Story.Api.dll", SearchOption.AllDirectories);
            if (found.Length > 0) return found[0];
        }
        throw new FileNotFoundException(
            "Story.Api.dll not built. Run `dotnet build Sleipnir.sln` first. Searched under " + binRoot);
    }

    private static string ResolveGuideContract()
    {
        var repo = ResolveRepoRoot();
        return Path.Combine(repo.FullName, "guide", "server", "contract.sleipnir.json");
    }

    // Run the export tool in its own process; return (exitCode, stdout).
    private (int ExitCode, string Stdout) RunTool(string contractPath, bool regen, string? assemblyPath = null)
    {
        var tool = ResolveToolDll();
        var args = $"--assembly \"{assemblyPath ?? ResolveStory01Dll()}\" --contract \"{contractPath}\"{(regen ? " --regen" : "")}";
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{tool}\" {args}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(assemblyPath ?? ResolveStory01Dll())!,
        };
        using var p = Process.Start(psi)!;
        // Drain concurrently to avoid the pipe-buffer deadlock.
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        var exited = p.WaitForExit(60_000);
        if (!exited)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            p.WaitForExit(5000);
        }
        var stdout = stdoutTask.Result;
        var stderr = stderrTask.Result;
        if (p.ExitCode == 2) // tool error — surface stderr for diagnosis
            _output.WriteLine("tool stderr:\n" + stderr);
        return (p.ExitCode, stdout);
    }

    // Sort the order-incidental arrays (controllers, methods, contract-type properties) the same
    // way the tool's own drift-check normalizes both sides; this mirrors it for the test's
    // assertions.
    private static string Normalize(string json)
    {
        var node = JsonNode.Parse(json)!;
        if (node is JsonObject obj)
        {
            if (obj["controllers"] is JsonArray arr)
            {
                SortArrayBy(arr, c => c?["name"]?.GetValue<string>() ?? "");
                foreach (var c in arr)
                {
                    if (c is JsonObject co && co["methods"] is JsonArray methods)
                        SortArrayBy(methods, m => m?["methodName"]?.GetValue<string>() ?? "");
                }
            }
            if (obj["types"] is JsonObject types)
            {
                foreach (var t in types)
                {
                    if (t.Value is JsonObject to && to["properties"] is JsonArray properties)
                        SortArrayBy(properties, p => p?["propertyName"]?.GetValue<string>() ?? "");
                }
            }
        }
        return node.ToJsonString();
    }

    private static void SortArrayBy(JsonArray array, Func<JsonNode?, string> key)
    {
        var sorted = array.OrderBy(key, StringComparer.Ordinal).ToArray();
        array.Clear();
        foreach (var node in sorted) array.Add(node!);
    }

    [Fact]
    public void ReorderOnlyDiff_IsNotDrift_ExitZero_FileUntouched()
    {
        // Reordering an order-incidental array (methods, contract-type properties) in the
        // committed contract must NOT be drift — the tool normalizes order on both sides, so a
        // moved member never breaks a build and never produces a contract diff. This pins the
        // guarantee that makes the committed contract readable in git: every diff line is a real
        // contract change. Story01's controllers are single-method, so the reorder runs against
        // the guide server fixture (Portfolio has five methods).
        var committedJson = File.ReadAllText(ResolveGuideContract());
        var reordered = ReorderIncidentalArrays(committedJson);
        reordered.Should().NotBe(committedJson, "the tamper must actually move something (otherwise this test is vacuous)");

        var tmp = Path.Combine(Path.GetTempPath(), "sleipnir-drift-test-reorder.json");
        File.WriteAllText(tmp, reordered);

        try
        {
            var (exit, stdout) = RunTool(tmp, regen: false, assemblyPath: ResolveGuideDll());
            if (exit != 0) _output.WriteLine("stdout:\n" + stdout);
            exit.Should().Be(0, "a reordered-but-content-identical contract must pass the drift-check");
            stdout.Should().Contain("drift-check passed");
            File.ReadAllText(tmp).Should().Be(reordered, "the tool must not rewrite the contract on a passing check");
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    [Fact]
    public void RegeneratedContract_IsSortedByName_And_PreservesParameterSignatureOrder()
    {
        // The export sorts every order-incidental collection by name so the committed file only
        // ever churns on real contract changes: controllers, methods, contract-type properties,
        // enum members, and the types object's keys. Parameters keep SIGNATURE order (positional
        // `num` binding). Runs over both fixture servers; the guide's Account.Login
        // (username, password) pins the parameter-order invariant for real.
        foreach (var (assemblyPath, committedPath) in new[]
                 {
                     ((string?)null, ResolveCommittedContract()),
                     (ResolveGuideDll(), ResolveGuideContract()),
                 })
        {
            var committedJson = File.ReadAllText(committedPath);
            var tmp = Path.Combine(
                Path.GetTempPath(),
                "sleipnir-drift-test-sorted-" + Path.GetFileName(Path.GetDirectoryName(committedPath)!) + ".json");
            File.WriteAllText(tmp, committedJson);

            try
            {
                var (exit, stdout) = RunTool(tmp, regen: true, assemblyPath: assemblyPath);
                if (exit != 0) _output.WriteLine("stdout:\n" + stdout);
                exit.Should().Be(0, $"--regen must succeed for '{committedPath}'");
                stdout.Should().Contain("regenerated");

                var regenerated = JsonNode.Parse(File.ReadAllText(tmp))!;

                var controllers = regenerated["controllers"]!.AsArray();
                controllers.Select(c => c!["name"]!.GetValue<string>())
                    .Should().BeInAscendingOrder($"[{committedPath}] controllers must be sorted by name");

                foreach (var c in controllers)
                {
                    c!["methods"]!.AsArray()
                        .Select(m => m!["methodName"]!.GetValue<string>())
                        .Should().BeInAscendingOrder($"[{committedPath}] methods of controller '{c["name"]}' must be sorted");
                }

                var types = regenerated["types"]!.AsObject();
                types.Select(t => t.Key)
                    .Should().BeInAscendingOrder($"[{committedPath}] the types object's keys must be sorted");
                foreach (var t in types)
                {
                    t.Value!["properties"]?.AsArray()
                        ?.Select(p => p!["propertyName"]!.GetValue<string>())
                        .Should().BeInAscendingOrder($"[{committedPath}] properties of type '{t.Key}' must be sorted");
                }

                // Parameters are signature-ordered, NOT alphabetized: a method whose parameter
                // names are not ascending must survive regen with that exact sequence.
                foreach (var c in JsonNode.Parse(committedJson)!["controllers"]!.AsArray())
                {
                    foreach (var m in c!["methods"]!.AsArray())
                    {
                        var originalNames = (m!["parameters"]?.AsArray()
                                ?.Select(p => p!["parameterName"]!.GetValue<string>()).ToList() ?? []);
                        if (originalNames.Count <= 1
                            || originalNames.SequenceEqual(originalNames.OrderBy(n => n, StringComparer.Ordinal)))
                        {
                            continue; // nothing the parameter-sort invariant could disturb here
                        }
                        var methodName = m["methodName"]!.GetValue<string>();
                        regenerated["controllers"]!.AsArray()
                            .Single(rc => rc!["name"]!.GetValue<string>() == c["name"]!.GetValue<string>())
                            ["methods"]!.AsArray()
                            .Single(rm => rm!["methodName"]!.GetValue<string>() == methodName)
                            ["parameters"]!.AsArray()
                            .Select(p => p!["parameterName"]!.GetValue<string>())
                            .Should().Equal(originalNames,
                                $"[{committedPath}] '{c["name"]}.{methodName}' has non-alphabetical signature order; regen must preserve it");
                    }
                }
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
        }
    }

    /// <summary>Reverse every order-incidental array with >= 2 elements (multi-method
    /// controllers, multi-property contract types) — a pure reorder, no content change. Returns
    /// the reordered JSON.</summary>
    private static string ReorderIncidentalArrays(string committedJson)
    {
        var node = JsonNode.Parse(committedJson)!;
        var moved = 0;

        foreach (var c in node["controllers"]!.AsArray())
        {
            var methods = c!["methods"]!.AsArray();
            if (methods.Count < 2) continue;
            ReverseInPlace(methods);
            moved++;
        }

        foreach (var t in node["types"]!.AsObject())
        {
            if (t.Value!["properties"] is not JsonArray props || props.Count < 2) continue;
            ReverseInPlace(props);
            moved++;
        }

        if (moved == 0)
            throw new InvalidOperationException(
                "Fixture has no multi-method controller and no multi-property type — cannot test reordering.");
        return node.ToJsonString();

        static void ReverseInPlace(JsonArray array)
        {
            var reversed = array.Reverse().ToList();
            array.Clear();
            foreach (var n in reversed) array.Add(n!.DeepClone());
        }
    }

    private static string TamperContract(string committedJson)
    {
        // Rename a method in the committed JSON so the regenerated (runtime) discovery no longer
        // matches. Order.GetById -> GetById_TAMPERED (the runtime still has GetById → drift).
        var node = JsonNode.Parse(committedJson)!;
        var controllers = node["controllers"]!.AsArray();
        foreach (var c in controllers)
        {
            var methods = c!["methods"]!.AsArray();
            foreach (var m in methods)
            {
                if (c!["name"]?.GetValue<string>() == "Order"
                    && m!["methodName"]?.GetValue<string>() == "GetById")
                {
                    m["methodName"] = "GetById_TAMPERED";
                    return node.ToJsonString();
                }
            }
        }
        throw new InvalidOperationException("Could not find Order.GetById in the committed contract to tamper.");
    }

    [Fact]
    public void HappyPath_CommittedContract_MatchesRuntime_ExitZero()
    {
        var committed = ResolveCommittedContract();
        File.Exists(committed).Should().BeTrue("the Story01 contract must be committed first (regenerate via SLEIPNIR_REGEN_GOLDEN=1 dotnet build)");
        var committedJson = File.ReadAllText(committed);

        var tmp = Path.Combine(Path.GetTempPath(), "sleipnir-drift-test-happy.json");
        File.WriteAllText(tmp, committedJson);

        try
        {
            var (exit, stdout) = RunTool(tmp, regen: false);
            if (exit != 0) _output.WriteLine("stdout:\n" + stdout);
            exit.Should().Be(0, "a copy of the committed contract must not drift from the runtime discovery");
            stdout.Should().Contain("drift-check passed", "the tool must report a passing check on the happy path");
            // The file is left untouched (no --regen).
            File.ReadAllText(tmp).Should().Be(committedJson, "the tool must not rewrite the contract on a passing check");
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    [Fact]
    public void DriftPath_TamperedContract_IsDetected_ExitOne_FileUntouched()
    {
        var committedJson = File.ReadAllText(ResolveCommittedContract());
        var tampered = TamperContract(committedJson);

        var tmp = Path.Combine(Path.GetTempPath(), "sleipnir-drift-test-tampered.json");
        File.WriteAllText(tmp, tampered);

        try
        {
            var (exit, stdout) = RunTool(tmp, regen: false);
            exit.Should().Be(1, "a tampered contract (Order.GetById renamed) must be detected as drift");
            stdout.Should().Contain("drift detected", "the tool must report drift on the failure path");
            // The file is left untouched (no --regen) — the committed contract is not silently overwritten.
            File.ReadAllText(tmp).Should().Be(tampered, "the tool must not rewrite the contract when drift is detected without --regen");
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    [Fact]
    public void RegenPath_TamperedContract_IsRepaired_ExitZero_FileMatchesRuntime()
    {
        var committedJson = File.ReadAllText(ResolveCommittedContract());
        var tampered = TamperContract(committedJson);

        var tmp = Path.Combine(Path.GetTempPath(), "sleipnir-drift-test-regen.json");
        File.WriteAllText(tmp, tampered);

        try
        {
            var (exit, stdout) = RunTool(tmp, regen: true);
            exit.Should().Be(0, "--regen must succeed (exit 0) even when the committed contract had drifted");
            stdout.Should().Contain("regenerated", "the tool must report a regeneration on the --regen path");
            // After regen, the file matches the runtime discovery again (normalized — both sides are
            // controller-sorted by the tool).
            var regenerated = File.ReadAllText(tmp);
            Normalize(regenerated).Should().Be(Normalize(committedJson),
                "regen must overwrite the tampered contract with the runtime discovery");
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }
}