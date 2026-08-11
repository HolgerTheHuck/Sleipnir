// Public facade for the C# emitter. Parse a discovery contract JSON, validate its shape
// (the no-drift ingress gate), resolve it into the emitter input, and emit SleipnirGenerated.cs.
// This is the entry point shared by the Roslyn source generator (build path) and the
// server-side export/drift-check tool — both feed it a contract.sleipnir.json and get C# text.
using System.Text.Json.Nodes;

namespace Sleipnir.Codegen.Core;

public static class SleipnirCodegen
{
    /// <summary>
    /// Parse, validate, and emit the C# client from a discovery contract JSON string.
    /// Throws <see cref="DiscoveryShapeException"/> if the payload is not a conformant
    /// discovery contract (the ingress gate refuses malformed input before emission).
    /// </summary>
    public static string EmitClient(string discoveryJson, EmitCsOptions opts)
    {
        var node = JsonNode.Parse(discoveryJson);
        var discovery = DiscoveryShape.Assert(node);
        var resolver = new NamingResolver();
        var input = EmitterBuilder.Build(discovery, resolver);
        return CsEmitter.Emit(input, opts);
    }

    /// <inheritdoc cref="EmitClient(string, EmitCsOptions)"/> with default options.</inheritdoc>
    public static string EmitClient(string discoveryJson) => EmitClient(discoveryJson, new EmitCsOptions());
}