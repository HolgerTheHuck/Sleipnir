// Parity gate for the .NET-native C# emitter (Sleipnir.Codegen.Core). This is the single
// source of truth that the C# port of the emitter produces byte-for-byte the same
// SleipnirGenerated.cs as the committed TS --lang cs snapshot — the cost of running two C#
// emitters (the Roslyn build path on this core, and the TS DevUI/CI path). One input
// (the Story-01 golden discovery), two producers, equal C# output.
//
// Two checks, mirroring clients/codegen/test/unit/{cs-emitter,cs-compile}.test.ts:
//   1. EmitClient(fixture) == committed snapshot byte-for-byte.
//   2. The emitted file compiles against the real SleipnirClient runtime and builds the
//      Story-01 typed diamond (spawn `dotnet build` on a temp project).
using System.Diagnostics;
using System.IO;
using FluentAssertions;
using Sleipnir.Codegen.Core;
using Xunit;
using Xunit.Abstractions;

namespace SleipnirTests.Unit.Core;

public class CsCodegenParityTests
{
    private readonly ITestOutputHelper _output;
    public CsCodegenParityTests(ITestOutputHelper output) => _output = output;

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

    private static string ResolveFixturePath()
    {
        var repo = ResolveRepoRoot();
        return Path.Combine(repo.FullName, "clients", "codegen", "test", "fixtures", "story01-discovery.json");
    }

    private static string ResolveSnapshotPath()
    {
        var repo = ResolveRepoRoot();
        return Path.Combine(repo.FullName, "clients", "codegen", "test", "snapshots", "story01.cs", "SleipnirGenerated.cs");
    }

    [Fact]
    public void EmitClient_MatchesCommittedSnapshotByteForByte()
    {
        var fixture = File.ReadAllText(ResolveFixturePath());
        var snapshot = File.ReadAllText(ResolveSnapshotPath());

        var emitted = SleipnirCodegen.EmitClient(fixture);

        // Normalize line endings before the byte-for-byte compare. The snapshot's
        // on-disk endings depend on the checkout (autocrlf CRLF, archive LF, ubuntu
        // LF), while the emitter uses Environment.NewLine (CRLF on Windows, LF on
        // Linux). Newline style is a platform/checkout artifact, not emitter drift —
        // what matters is that every other byte matches across both producers.
        static string NormalizeNewlines(string s) => s.Replace("\r\n", "\n").Replace("\r", "\n");
        NormalizeNewlines(emitted).Should().Be(NormalizeNewlines(snapshot),
            "the .NET-native C# emitter must produce the same SleipnirGenerated.cs as the committed TS --lang cs snapshot (parity gate; newlines normalized)");
    }

    [Fact]
    public void EmitClient_AliasNormalizesLeadingAt_BothCallStyles()
    {
        // Behavior guard for the 1.2.1 / 1.1.3 alias() bug: BatchEntry.Alias(name) must ENSURE
        // a leading '@' on the wire placeholder (symmetric with Exposes, which strips it for the
        // dependencyMapping key). The pre-fix emitter emitted `new(name)` — bare name on the wire,
        // so the server's ReplaceDependencyByAlias never matched and a typed batch chain sent
        // "ids" where "@ids" was expected. Both call styles must normalize: Alias("ids") and
        // Alias("@ids") both yield "@ids". The byte-for-byte parity test above already locks this
        // line via the committed snapshot; this assertion states the intent readably and survives
        // a snapshot regen that might reorder unrelated lines.
        var fixture = File.ReadAllText(ResolveFixturePath());
        var emitted = SleipnirCodegen.EmitClient(fixture);
        emitted.Should().Contain("public Alias Alias(string name) => new(name.StartsWith('@') ? name : \"@\" + name);");
        // Exposes must still strip the leading '@' for the wire mapping key (the server strips the
        // consumer's '@alias' placeholder before lookup) — the symmetric half of the contract.
        emitted.Should().Contain("_call.Exposes(jsonPath, alias.StartsWith('@') ? alias.Substring(1) : alias);");
    }

    private const string Harness = @"using System.Collections.Generic;
using System.Threading.Tasks;
using Sleipnir.Generated;

public static class Program
{
    public static async Task Main()
    {
        var client = new SleipnirGeneratedClient(""http://localhost:5001"");

        // Single typed call: Call<T> deserializes into the POCO.
        var order = await client.Call<Order>(client.Order.GetById(42));

        // Typed diamond batch (Serial — required for @alias resolution).
        var batch = new Batch();
        var o = batch.Add(client.Order.GetById(42))
            .Exposes(""$.customerId"", ""@customerId"")
            .Exposes(""$.id"", ""@orderId"")
            .Exposes(""$.shippingAddressId"", ""@addressId"");
        batch.Add(client.Customer.GetById(o.Alias(""@customerId"")));
        var lines = batch.Add(client.OrderLine.GetByOrder(o.Alias(""@orderId"")))
            .Exposes(""$[*].articleId"", ""@articleIds"");
        batch.Add(client.Article.GetByIds(lines.Alias(""@articleIds"")));
        batch.Add(client.Stock.GetByArticles(lines.Alias(""@articleIds"")));
        batch.Add(client.Address.GetById(o.Alias(""@addressId"")));

        var resp = await client.Batch(batch);
        // Fetch results by request id (topological order is not request order).
        var fetchedOrder = resp.Get<Order>(""Order.GetById"");
        var customer = resp.Get<Customer>(""Customer.GetById"");
        var fetchedLines = resp.Get<List<OrderLine>>(""OrderLine.GetByOrder"");
        var articles = resp.Get<List<Article>>(""Article.GetByIds"");
    }
}
";

    private static string Csproj(string repoRoot) => $@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <RootNamespace>SleipnirCompileGate</RootNamespace>
    <AssemblyName>SleipnirCompileGate</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include=""{Path.Combine(repoRoot, "SleipnirClient", "SleipnirClient.csproj")}"" />
  </ItemGroup>
</Project>
";

    [Fact]
    public async Task EmittedClient_CompilesAgainstRealRuntime_BuildsTypedDiamond()
    {
        var repo = ResolveRepoRoot();
        var compileDir = Path.Combine(repo.FullName, ".sleipnir-cs-compile");
        if (Directory.Exists(compileDir)) Directory.Delete(compileDir, recursive: true);
        Directory.CreateDirectory(compileDir);

        try
        {
            var fixture = File.ReadAllText(ResolveFixturePath());
            var emitted = SleipnirCodegen.EmitClient(fixture);
            File.WriteAllText(Path.Combine(compileDir, "SleipnirGenerated.cs"), emitted);
            File.WriteAllText(Path.Combine(compileDir, "Harness.cs"), Harness);
            File.WriteAllText(Path.Combine(compileDir, "CsCompile.csproj"), Csproj(repo.FullName));

            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"build \"{Path.Combine(compileDir, "CsCompile.csproj")}\" -c Release --nologo -clp:NoSummary",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = repo.FullName,
            };
            using var p = Process.Start(psi)!;
            // Drain stdout/stderr concurrently with the process to avoid the pipe-buffer deadlock
            // (reading only after WaitForExit lets the child block on a full pipe).
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            var exited = p.WaitForExit(180_000);
            if (!exited)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                p.WaitForExit(5000);
            }
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (p.ExitCode != 0)
            {
                _output.WriteLine("dotnet build stdout:\n" + stdout);
                _output.WriteLine("dotnet build stderr:\n" + stderr);
            }
            p.ExitCode.Should().Be(0, "the emitted SleipnirGenerated.cs must compile against the real SleipnirClient runtime and build the Story-01 typed diamond");
        }
        finally
        {
            if (Directory.Exists(compileDir)) Directory.Delete(compileDir, recursive: true);
        }
    }
}