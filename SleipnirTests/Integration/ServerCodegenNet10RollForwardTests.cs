// Regression guard for the 1.1.2 net10 discovery bug:
//   The export tool (Sleipnir.Server.Codegen) targets net8.0 and reflects the consumer's BUILT server
//   assembly via Assembly.LoadFrom. A net8-pinned tool process cannot load/reflect a net10 server
//   assembly's controller types, so discovery silently returned an EMPTY contract and the drift-check
//   passed vacuously (empty == empty). The fix bakes `<RollForward>LatestMajor</RollForward>` into the
//   tool's runtimeconfig so `dotnet <tool.dll>` runs on the consumer's installed runtime (net10 on a
//   .NET 10 consumer), under which the net10 server assembly's [SleipnirController] scan succeeds.
//
// This test builds a MINIMAL net10 server on the fly and runs the real (in-repo-built) tool against it,
// asserting non-empty discovery — exactly the scenario that regressed. It is the CI guard that the
// net8-only Story01 drift tests cannot provide. Skips gracefully (early return) when no 10.x SDK is
// installed, so local dev without .NET 10 is not blocked; CI installs 10.0.x so it runs there.
using System.Diagnostics;
using System.Text.Json.Nodes;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace SleipnirTests.Integration;

public class ServerCodegenNet10RollForwardTests
{
    private readonly ITestOutputHelper _output;
    public ServerCodegenNet10RollForwardTests(ITestOutputHelper output) => _output = output;

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

    private static string ResolveToolDll()
    {
        var repo = ResolveRepoRoot();
        foreach (var cfg in new[] { "Debug", "Release" })
        {
            var dll = Path.Combine(repo.FullName, "Sleipnir.Server.Codegen", "bin", cfg, "net8.0", "Sleipnir.Server.Codegen.dll");
            if (File.Exists(dll)) return dll;
        }
        throw new FileNotFoundException(
            "Sleipnir.Server.Codegen.dll not built. Run `dotnet build Sleipnir.sln` first.");
    }

    // True when a 10.x SDK is installed (so we can build a net10 project). CI installs 10.0.x.
    private bool HasNet10Sdk()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "--list-sdks",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return stdout.Contains("10.", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static (int ExitCode, string Stdout, string Stderr) RunProcess(string fileName, string args, string? workingDir = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (workingDir is not null) psi.WorkingDirectory = workingDir;
        using var p = Process.Start(psi)!;
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        var exited = p.WaitForExit(120_000);
        if (!exited)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            p.WaitForExit(5000);
        }
        return (p.ExitCode, stdoutTask.Result, stderrTask.Result);
    }

    [Fact]
    public void Net10Server_DiscoveredByTool_RollForwardLatestMajor_NonEmptyContract()
    {
        if (!HasNet10Sdk())
        {
            _output.WriteLine("No .NET 10 SDK installed — skipping net10 roll-forward regression guard.");
            return;
        }

        var repo = ResolveRepoRoot();
        var tool = ResolveToolDll();
        var tmp = Path.Combine(Path.GetTempPath(), "sleipnir-net10-rg-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        try
        {
            // Minimal net10 server: one [SleipnirController] with one [SleipnirMethod].
            var corePath = Path.Combine(repo.FullName, "SleipnirCore", "SleipnirCore.csproj");
            var commonPath = Path.Combine(repo.FullName, "SleipnirCommon", "SleipnirCommon.csproj");
            File.WriteAllText(Path.Combine(tmp, "Net10Server.csproj"), $"""
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>Net10Server</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="{corePath}" />
    <ProjectReference Include="{commonPath}" />
  </ItemGroup>
</Project>
""");
            File.WriteAllText(Path.Combine(tmp, "Program.cs"), """
namespace Net10Server;
public static class Program { public static void Main() {} }
""");
            File.WriteAllText(Path.Combine(tmp, "Controllers.cs"), """
using SleipnirCore.Attributes;
namespace Net10Server;

[SleipnirController("Orders")]
public class OrdersController
{
    [SleipnirMethod("GetById")]
    public Order GetOrderById(int id) => new Order { Id = id, Name = "x" };
}

public class Order { public int Id { get; set; } public string Name { get; set; } = ""; }
""");

            var (buildExit, _, buildErr) = RunProcess("dotnet", $"build \"{Path.Combine(tmp, "Net10Server.csproj")}\" -c Release", tmp);
            buildExit.Should().Be(0, "the net10 server must build; stderr: " + buildErr);

            var serverDll = Path.Combine(tmp, "bin", "Release", "net10.0", "Net10Server.dll");
            File.Exists(serverDll).Should().BeTrue($"net10 server dll should exist at {serverDll}");

            // Run the tool exactly as the MSBuild target does: `dotnet <tool.dll> --assembly <dll> --contract <out>`.
            // No --roll-forward flag — the fix is baked into the tool's runtimeconfig (LatestMajor).
            var contract = Path.Combine(tmp, "contract.sleipnir.json");
            var (exit, stdout, stderr) = RunProcess("dotnet", $"\"{tool}\" --assembly \"{serverDll}\" --contract \"{contract}\"", tmp);
            if (exit != 0) _output.WriteLine("tool stdout:\n" + stdout + "\ntool stderr:\n" + stderr);

            exit.Should().Be(0, "the tool must discover the net10 server's controller (roll-forward to net10 runtime). " +
                                "A non-zero exit (esp. 2 = tool error with '0 [SleipnirController] types') means the " +
                                "roll-forward fix regressed and discovery went empty.");

            File.Exists(contract).Should().BeTrue("the tool must write the contract on success");
            var json = File.ReadAllText(contract);
            var node = JsonNode.Parse(json)!;
            var controllers = node["controllers"]!.AsArray();
            controllers.Should().NotBeEmpty("net10 discovery must find the Orders controller — the 1.1.2 regression returned empty here");
            controllers.Should().Contain(c => c!["name"]!.GetValue<string>() == "Orders",
                "the Orders controller from the net10 server must appear in the contract");
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }
}