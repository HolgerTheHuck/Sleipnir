// Diagnostic-mapping test for the Trame source generator (Trame.SourceGenerator).
//
// The generator's emission path is transitively covered by CsCodegenParityTests (the Core port is
// byte-faithful) and by Trame.Samples.GeneratedClient (the generator shells Core into a real
// compilation). What those do NOT cover is the generator's diagnostic contract: the thin wrapper
// that maps a DiscoveryShapeException to TRAME001 (shape violation) and any other throw to
// TRAME002 (emit failure), and suppresses source emission on both. That mapping lives in the
// testable seam CodegenSeam.TryEmit, which Emit calls and turns into a Roslyn Diagnostic.
//
// We test the seam directly rather than driving the Roslyn pipeline: the generator is
// netstandard2.0 and the test host is net8.0, and Microsoft.CodeAnalysis.CSharp 4.8.0 ships
// per-TFM libs, so the generator's ISourceGenerator and the test's would not share a single type
// identity across the TFM boundary. The seam returns plain strings (the diagnostic id + message +
// source), so no Roslyn types are needed in the test.
//
// The generator project is referenced under the `Generator` extern alias: it LINKS the
// Trame.Codegen.Core sources into its own assembly (the Roslyn analyzer load context cannot
// resolve a ProjectReference dep), so without the alias the linked Trame.Codegen.Core.TrameCodegen
// would collide with the standalone Trame.Codegen.Core reference (CS0433).
extern alias Generator;

using System.IO;
using FluentAssertions;
using Xunit;

namespace TrameTests.Unit.Core;

public class SourceGeneratorDiagnosticsTests
{
    private static DirectoryInfo ResolveRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "stories"))
                && Directory.Exists(Path.Combine(dir.FullName, "clients"))
                && File.Exists(Path.Combine(dir.FullName, "Trame.sln")))
            {
                return dir;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repo root from " + AppContext.BaseDirectory);
    }

    private static string Story01Fixture()
        => File.ReadAllText(Path.Combine(ResolveRepoRoot().FullName,
            "clients", "codegen", "test", "fixtures", "story01-discovery.json"));

    [Fact]
    public void ValidContract_EmitsGeneratedClient_NoDiagnostic()
    {
        var (source, diagnosticId, message) = Generator.Trame.SourceGenerator.CodegenSeam.TryEmit(Story01Fixture());

        diagnosticId.Should().BeNull("a valid Story-01 contract must not produce a diagnostic");
        message.Should().BeEmpty();
        source.Should().NotBeNull();
        source!.Should().Contain("namespace Trame.Generated");
        source.Should().Contain("TrameGeneratedClient");
    }

    [Fact]
    public void ShapeViolation_UnknownDiscoveryVersion_ReportsTrame001_NoSource()
    {
        // discoveryVersion is validated first; "99" is unknown → DiscoveryShapeException → TRAME001.
        var (source, diagnosticId, message) = Generator.Trame.SourceGenerator.CodegenSeam.TryEmit("""{ "discoveryVersion": "99" }""");

        diagnosticId.Should().Be("TRAME001", "an unknown discoveryVersion must surface the shape diagnostic");
        source.Should().BeNull("no source must be emitted when the contract fails shape validation");
        message.Should().NotBeEmpty("the diagnostic must carry the shape-violation reason");
    }

    [Fact]
    public void MalformedJson_ReportsTrame002_NoSource()
    {
        // Not parseable JSON → JsonException (not a DiscoveryShapeException) → TRAME002 (emit failure).
        var (source, diagnosticId, message) = Generator.Trame.SourceGenerator.CodegenSeam.TryEmit("""{ "discoveryVersion": "1", "broken""");

        diagnosticId.Should().Be("TRAME002", "a malformed JSON contract must surface the emit-failure diagnostic");
        source.Should().BeNull("no source must be emitted when the contract cannot be parsed");
        message.Should().NotBeEmpty("the diagnostic must carry the exception detail");
    }
}