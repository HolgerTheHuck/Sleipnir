using System.Text.Json;
using System.Text.Json.Serialization;

namespace SleipnirCore.Model.Messages.Mex
{
    /// <summary>
    /// Deterministic JSON options for the discovery wire. The discovery contract is the spec
    /// (see docs/discovery-schema.md), so its serialization must NOT depend on host JSON
    /// configuration — both the REST endpoint and the JSON-RPC <c>sleipnir.discover</c>
    /// capability serialize with these options. camelCase matches the documented wire casing;
    /// null-valued optional fields (e.g. <c>TypeRef.Nullable</c> absent ⟹ not-nullable) are
    /// omitted so the wire carries only what the contract specifies.
    /// </summary>
    public static class DiscoverySerialization
    {
        public static readonly JsonSerializerOptions Options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
    }
}