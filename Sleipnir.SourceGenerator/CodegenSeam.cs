// The Roslyn-free, testable seam of the Sleipnir source generator.
//
// SleipnirClientGenerator is a thin Roslyn shell; its only non-trivial logic is the exception→diagnostic
// mapping (a DiscoveryShapeException → SLEIPNIR001 shape error; any other throw → SLEIPNIR002 emit
// failure) plus the decision to suppress source emission on failure. That logic lives here, in a
// class with NO Roslyn-typed members, so it can be unit-tested without loading Microsoft.CodeAnalysis
// (the generator is netstandard2.0; Microsoft.CodeAnalysis.CSharp 4.8.0 ships per-TFM libs, so the
// generator's Roslyn and a net8.0 test host's would not share a single type identity). The CLR loads
// a referenced assembly lazily — only when a JITted method touches a type from it — so calling
// CodegenSeam.TryEmit never loads Roslyn, even though the generator assembly references it.
//
// The diagnostic ids are `const` strings read by SleipnirClientGenerator's DiagnosticDescriptors, so the
// ids here and the ids Roslyn reports are one and the same value (single source of truth).
using System;
using Sleipnir.Codegen.Core;

namespace Sleipnir.SourceGenerator;

internal static class CodegenSeam
{
    internal const string ShapeErrorId = "SLEIPNIR001";
    internal const string EmitErrorId = "SLEIPNIR002";

    /// <summary>
    /// Parse + validate + emit the contract, mapping any failure to the diagnostic id it should
    /// produce. On success returns the generated source with a null diagnostic id; on failure
    /// returns a null source with the diagnostic id + message. SleipnirClientGenerator.Emit turns
    /// this into a Roslyn Diagnostic (or an AddSource) — the descriptor ids are the same constants.
    /// </summary>
    internal static (string? Source, string? DiagnosticId, string Message) TryEmit(string contractText)
    {
        try
        {
            return (SleipnirCodegen.EmitClient(contractText), null, string.Empty);
        }
        catch (DiscoveryShapeException ex)
        {
            return (null, ShapeErrorId, ex.Message);
        }
        catch (Exception ex)
        {
            return (null, EmitErrorId, ex.ToString());
        }
    }
}