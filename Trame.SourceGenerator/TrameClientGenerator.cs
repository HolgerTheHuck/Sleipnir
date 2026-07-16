// Roslyn source generator that produces a typed C# Trame client from a committed discovery
// contract (contract.trame.json) at compile time. The generator is a thin shell: it reads the
// contract AdditionalFile, validates + emits via Trame.Codegen.Core, and adds the generated
// TrameGenerated.cs to the compilation. Interface drift between the server's runtime discovery
// and the committed contract is caught downstream by the server-side drift-check task (Slice 3);
// this generator turns a *valid* contract into C#.
//
// Contract file selection: by default the generator emits from any AdditionalFile named
// `contract.trame.json`. The consumer may override the name/path via the `TrameContractFile`
// MSBuild property (surfaced as the `build_property.tramecontractfile` analyzer global option),
// in which case the AdditionalFile whose filename matches that value is preferred.
using System;
using System.IO;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Trame.SourceGenerator;

[Generator]
public sealed class TrameClientGenerator : IIncrementalGenerator
{
    private const string DefaultContractFileName = "contract.trame.json";

    private static readonly DiagnosticDescriptor ContractShapeError = new(
        id: CodegenSeam.ShapeErrorId,
        title: "Invalid Trame discovery contract",
        messageFormat: "The Trame contract '{0}' is not a valid discovery payload: {1}",
        category: "TrameCodegen",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The contract.trame.json referenced by the Trame source generator failed shape validation. The generated client was not emitted.",
        helpLinkUri: null,
        customTags: Array.Empty<string>());

    private static readonly DiagnosticDescriptor ContractEmitError = new(
        id: CodegenSeam.EmitErrorId,
        title: "Trame client emission failed",
        messageFormat: "Generating the Trame client from '{0}' failed: {1}",
        category: "TrameCodegen",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The contract passed shape validation but the C# emitter threw. This is a bug in Trame.Codegen.Core — please file it.",
        helpLinkUri: null,
        customTags: Array.Empty<string>());

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Resolve the configured contract filename once. The analyzer global option is constant
        // for the whole compilation, so reading it per-file is cache-friendly. We combine the
        // AdditionalTexts stream with the single-valued options provider and project each file to
        // either its (Path, Text) pair or null (non-matching / unreadable files are filtered out).
        var configuredName = context.AnalyzerConfigOptionsProvider
            .Select((opts, _) =>
            {
                if (opts.GlobalOptions.TryGetValue("build_property.tramecontractfile", out var raw)
                    && !string.IsNullOrWhiteSpace(raw))
                {
                    return Path.GetFileName(raw.Trim());
                }
                return (string?)null;
            });

        // The selector returns a typed nullable tuple so Select can infer TResult (a bare
        // `return null;` has no type and breaks overload resolution → CS0411).
        var contracts = context.AdditionalTextsProvider
            .Combine(configuredName)
            .Select((pair, ct) =>
            {
                var (file, configured) = pair;
                var fileName = Path.GetFileName(file.Path);
                bool match = !string.IsNullOrEmpty(configured)
                    ? string.Equals(fileName, configured, StringComparison.OrdinalIgnoreCase)
                    : string.Equals(fileName, DefaultContractFileName, StringComparison.OrdinalIgnoreCase);
                (string Path, string Text)? result = null;
                if (match)
                {
                    var text = file.GetText(ct);
                    if (text is not null) result = (file.Path, text.ToString());
                }
                return result;
            })
            .Where(x => x is not null)
            .Select((x, _) => x!.Value);

        context.RegisterSourceOutput(contracts, Emit);
    }

    private static void Emit(SourceProductionContext spc, (string Path, string Text) contract)
    {
        var (source, diagnosticId, message) = CodegenSeam.TryEmit(contract.Text);
        if (diagnosticId is not null)
        {
            // Map the seam's diagnostic id back to the descriptor and report it to the compilation.
            var descriptor = diagnosticId == CodegenSeam.ShapeErrorId ? ContractShapeError : ContractEmitError;
            spc.ReportDiagnostic(Diagnostic.Create(descriptor, Location.None, contract.Path, message));
            return;
        }

        // AddSource wants a SourceText with an explicit encoding (required so the compiler can
        // write generated files to disk under EmitCompilerGeneratedFiles). Encoding.UTF8 would
        // prepend a BOM and break byte-parity with the committed snapshot, so we use a BOM-less
        // UTF-8 — explicit encoding (satisfies AddSource) and no preamble (parity). The em-dash in
        // the auto-generated header round-trips fine through UTF-8.
        spc.AddSource("TrameGenerated.cs", SourceText.From(source!, new UTF8Encoding(false)));
    }
}