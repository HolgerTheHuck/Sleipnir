using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Xunit;

namespace SleipnirTests.Integration;

/// <summary>
/// No-drift gate (docs/discovery-schema.md §11). The Story-01 discovery output IS the
/// committed codegen golden (<c>clients/codegen/test/fixtures/story01-discovery.json</c>):
/// the C# <see cref="SleipnirCore.Model.Messages.Mex.DiscoveryInfo"/>+<see cref="SleipnirCore.Model.Messages.Mex.TypeRef"/>
/// model is serialized by the same pipeline that produced the golden, and the codegen consumes
/// that exact shape. This test starts the real Story-01 server, fetches
/// <c>GET /api/sleipnir/discovery</c> over HTTP, and asserts the live payload equals the committed
/// golden — so a producer change that drifts the contract fails CI here.
/// </summary>
/// <remarks>
/// <b>Why out-of-process:</b> the test host and the integration fixtures
/// (<c>RestTransportTests</c> et al.) share one AppDomain. <c>UseSleipnir</c>'s auto-discovery
/// fallback scans <c>AppDomain.CurrentDomain.GetAssemblies()</c> unscoped, so loading Story-01's
/// controllers in-process would collide with the Sleipnir sample's <c>CustomerHandler</c> (both
/// <c>[SleipnirController("Customer")]</c>) and break every integration fixture. Running Story-01 as
/// its own process keeps its assembly out of the test AppDomain entirely — no collision — and
/// makes the golden provably <b>derived from observed wire behavior</b>, not authored.
/// <para>
/// <b>Comparison is content-based, not byte-identical:</b> the server's <c>controllers</c> array
/// order follows <c>ConcurrentDictionary</c> enumeration (incidental), so both sides are
/// normalized by sorting <c>controllers</c> by name before comparing. Method and contract-type
/// property order are reflection order — metadata-stable per assembly but not normalized by the
/// framework, so they are normalized here too: a moved C# member must never fire this gate (the
/// export tool sorts these same arrays for file determinism).
/// </para>
/// <para>
/// <b>Regenerating the golden</b> is server-driven, not test-driven (so the real wire order is
/// preserved for the codegen snapshots): run this test with <c>SLEIPNIR_REGEN_GOLDEN=1</c> to
/// overwrite the committed fixture from the live server, then re-run
/// <c>clients/codegen/scripts/regen-snapshots.mjs</c> to refresh the emitted-client snapshots.
/// </para>
/// </remarks>
public class DiscoveryContractTests : IAsyncLifetime
{
    private Process? _server;
    private HttpClient? _http;
    private string? _baseUrl;

    /// <summary>Walk up from the test bin dir to the repo root.</summary>
    private static DirectoryInfo? ResolveRepoRoot()
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
        return null;
    }

    /// <summary>Locate the built Story-01 assembly, preferring the test's own build config.</summary>
    private static string ResolveStory01Dll()
    {
        var repo = ResolveRepoRoot()
            ?? throw new DirectoryNotFoundException("Could not locate repo root from " + AppContext.BaseDirectory);
        var binRoot = Path.Combine(repo.FullName, "stories", "01-n-plus-one-screen", "bin");
        // Prefer the configuration matching the running test bin dir, fall back to any build.
        var preferDebug = AppContext.BaseDirectory.Contains(Path.DirectorySeparatorChar + "Debug", StringComparison.OrdinalIgnoreCase);
        var configs = preferDebug
            ? new[] { "Debug", "Release" }
            : new[] { "Release", "Debug" };
        foreach (var cfg in configs)
        {
            var dll = Path.Combine(binRoot, cfg, "net8.0", "Story01.dll");
            if (File.Exists(dll)) return dll;
        }
        // Last resort: search whatever exists.
        if (Directory.Exists(binRoot))
        {
            var found = Directory.GetFiles(binRoot, "Story01.dll", SearchOption.AllDirectories);
            if (found.Length > 0) return found[0];
        }
        throw new FileNotFoundException(
            "Story01.dll not built. Run `dotnet build Sleipnir.sln` first. Searched under " + binRoot);
    }

    /// <summary>Locate the committed golden by walking up from the test bin dir to the repo root.</summary>
    private static string ResolveGoldenPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "clients", "codegen", "test", "fixtures", "story01-discovery.json");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException(
            "Could not locate clients/codegen/test/fixtures/story01-discovery.json from " + AppContext.BaseDirectory);
    }

    public async Task InitializeAsync()
    {
        var dll = ResolveStory01Dll();
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{dll}\" --urls http://127.0.0.1:0",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.EnvironmentVariables["ASPNETCORE_ENVIRONMENT"] = "Development";

        _server = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start Story-01 server process.");

        // Parse the dynamically-assigned listening URL from the server's stdout/stderr.
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnData(object sender, DataReceivedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            const string marker = "Now listening on: ";
            var i = e.Data.IndexOf(marker, StringComparison.Ordinal);
            if (i >= 0) tcs.TrySetResult(e.Data[(i + marker.Length)..].Trim());
        }
        _server.OutputDataReceived += OnData;
        _server.ErrorDataReceived += OnData;
        _server.BeginOutputReadLine();
        _server.BeginErrorReadLine();

        var gotUrl = await Task.WhenAny(tcs.Task, Task.Delay(30_000));
        if (gotUrl != tcs.Task || tcs.Task.Result is null)
            throw new InvalidOperationException("Story-01 server did not announce a listening URL within 30s.");
        _baseUrl = tcs.Task.Result.TrimEnd('/');

        _http = new HttpClient { BaseAddress = new Uri(_baseUrl + "/"), Timeout = TimeSpan.FromSeconds(10) };

        // Wait until the discovery endpoint actually serves (host warm-up).
        await PollUntilReadyAsync();
    }

    private async Task PollUntilReadyAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var resp = await _http!.GetAsync("/api/sleipnir/discovery");
                if (resp.IsSuccessStatusCode) return;
            }
            catch
            {
                // Server not up yet — keep polling.
            }
            await Task.Delay(200);
        }
        throw new TimeoutException("Story-01 discovery endpoint did not become ready within 30s.");
    }

    public async Task DisposeAsync()
    {
        _http?.Dispose();
        if (_server is { HasExited: false })
        {
            try { _server.Kill(entireProcessTree: true); } catch { }
            try { _server.WaitForExit(5000); } catch { }
        }
        _server?.Dispose();
    }

    /// <summary>Normalize a discovery payload by sorting the order-incidental arrays —
    /// <c>controllers</c> by name, each controller's <c>methods</c> by method name, and each
    /// contract type's <c>properties</c> by property name — so reflection-order changes never
    /// fire the gate. Mirrors Sleipnir.Server.Codegen's NormalizeDiscovery.</summary>
    private static JsonNode NormalizeDiscovery(JsonNode root)
    {
        if (root is not JsonObject obj)
            return root;

        if (obj["controllers"] is JsonArray controllers)
        {
            SortArrayBy(controllers, c => c?["name"]?.GetValue<string>() ?? "");
            foreach (var controller in controllers)
            {
                if (controller is JsonObject co && co["methods"] is JsonArray methods)
                    SortArrayBy(methods, m => m?["methodName"]?.GetValue<string>() ?? "");
            }
        }

        if (obj["types"] is JsonObject types)
        {
            foreach (var type in types)
            {
                if (type.Value is JsonObject to && to["properties"] is JsonArray properties)
                    SortArrayBy(properties, p => p?["propertyName"]?.GetValue<string>() ?? "");
            }
        }

        return obj;
    }

    /// <summary>Sort a JSON array of objects by a string key, in place. Nodes without the key
    /// sort as <c>""</c> (first), matching the export tool's normalization.</summary>
    private static void SortArrayBy(JsonArray array, Func<JsonNode?, string> key)
    {
        var sorted = array.OrderBy(key, StringComparer.Ordinal).ToArray();
        array.Clear();
        foreach (var node in sorted) array.Add(node!);
    }

    private async Task<string> FetchDiscoveryAsync()
    {
        using var resp = await _http!.GetAsync("/api/sleipnir/discovery");
        resp.IsSuccessStatusCode.Should().BeTrue(
            $"discovery endpoint must return 2xx (got {resp.StatusCode})");
        return await resp.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task Story01Discovery_MatchesCommittedGolden()
    {
        var actualJson = await FetchDiscoveryAsync();
        var goldenPath = ResolveGoldenPath();

        // Regen mode: overwrite the committed golden from the live server (compact wire form),
        // so the contract artifact is provably derived from observed behavior.
        if (Environment.GetEnvironmentVariable("SLEIPNIR_REGEN_GOLDEN") == "1")
        {
            await File.WriteAllTextAsync(goldenPath, actualJson.Trim());
            return;
        }

        var goldenJson = await File.ReadAllTextAsync(goldenPath);
        var actualNode = NormalizeDiscovery(JsonNode.Parse(actualJson)!);
        var goldenNode = NormalizeDiscovery(JsonNode.Parse(goldenJson)!);

        var equal = JsonNode.DeepEquals(actualNode, goldenNode);
        if (!equal)
        {
            var actualPretty = actualNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            var goldenPretty = goldenNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            equal.Should().BeTrue(
                "the Story-01 live discovery must match the committed codegen golden (no-drift gate).\n" +
                $"Golden: {goldenPath}\n--- actual (normalized, pretty) ---\n{actualPretty}\n" +
                $"--- golden (normalized, pretty) ---\n{goldenPretty}\n" +
                "To regenerate: run this test with SLEIPNIR_REGEN_GOLDEN=1, then run " +
                "clients/codegen/scripts/regen-snapshots.mjs.");
        }
    }

    [Fact]
    public async Task Story01Discovery_CarriesSchemaVersion()
    {
        // The discoveryVersion seam must be present and non-empty (additive-only gate, §11).
        var actualJson = await FetchDiscoveryAsync();
        var node = JsonNode.Parse(actualJson)!;
        node["discoveryVersion"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        node["discoveryVersion"]!.GetValue<string>().Should().Be("1");
    }
}