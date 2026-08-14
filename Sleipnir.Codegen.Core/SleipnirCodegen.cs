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

    /// <summary>
    /// Parse, validate, and emit the C# LINQ service contracts from a discovery contract JSON string:
    /// the POCO DTOs plus one <c>[SleipnirServiceContract]</c> interface per controller carrying
    /// <c>[SleipnirMethodContract]</c> methods with <c>Arg&lt;T&gt;</c> parameters and <c>Task&lt;T?&gt;</c>
    /// returns — consumed by <c>Sleipnir.Client.Linq</c> (<c>SleipnirLinqClient</c>). Throws
    /// <see cref="DiscoveryShapeException"/> on a non-conformant payload (same ingress gate as
    /// <see cref="EmitClient"/>). No TS counterpart — interfaces with attributes are C#-only; the
    /// <c>CsCodegenParityTests</c> gate covers <see cref="EmitClient"/> and is unaffected.
    /// </summary>
    public static string EmitContracts(string discoveryJson, EmitCsOptions opts)
    {
        var node = JsonNode.Parse(discoveryJson);
        var discovery = DiscoveryShape.Assert(node);
        var resolver = new NamingResolver();
        var input = EmitterBuilder.Build(discovery, resolver);
        // Drift-gate the navigation edges (refuse-to-emit on a key/param/fetch mismatch). LINQ-contracts
        // path only — never runs in EmitClient, so the Tier-1 source-generator design-time path is
        // unaffected by Tier-2 navigation semantics.
        EmitterBuilder.ValidateNavigation(input);
        return CsContractsEmitter.Emit(input, opts);
    }

    /// <inheritdoc cref="EmitContracts(string, EmitCsOptions)"/> with default options.</inheritdoc>
    public static string EmitContracts(string discoveryJson) => EmitContracts(discoveryJson, new EmitCsOptions());
}