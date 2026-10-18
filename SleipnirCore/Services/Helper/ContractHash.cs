using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SleipnirCore.Model.Messages.Mex;

namespace SleipnirCore.Services.Helper;

/// <summary>
/// The contract's identity (<see cref="DiscoveryInfo.ContractHash"/>): SHA-256 over a canonical
/// form, so the same contract always hashes the same — independent of controller registration
/// order (the live discovery follows ConcurrentDictionary enumeration, the committed file is
/// sorted), C# member order, and documentation edits.
/// <list type="bullet">
/// <item>Every object is key-sorted (ordinal); controllers, methods, contract-type properties
/// and enum members are sorted by name; parameters keep signature order (positional binding is
/// part of the contract).</item>
/// <item><c>documentation</c>, <c>example</c> and <c>deprecated</c> are dropped — advisory, no
/// wire effect — as is <c>contractHash</c> itself.</item>
/// </list>
/// </summary>
internal static class ContractHash
{
    private static readonly HashSet<string> Excluded = new(StringComparer.Ordinal)
    {
        "documentation", "example", "deprecated", "contractHash",
    };

    // Arrays whose order is incidental, and the key they are sorted by.
    private static readonly Dictionary<string, string> SortedArrays = new(StringComparer.Ordinal)
    {
        ["controllers"] = "name",
        ["methods"] = "methodName",
        ["properties"] = "propertyName",
        ["members"] = "name",
    };

    public static string Compute(DiscoveryInfo discovery)
    {
        var node = JsonSerializer.SerializeToNode(discovery, DiscoverySerialization.Options)!;
        var canonical = Canonicalize(node, parentKey: null)!.ToJsonString();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static JsonNode? Canonicalize(JsonNode? node, string? parentKey)
    {
        switch (node)
        {
            case JsonObject obj:
                {
                    var result = new JsonObject();
                    foreach (var (key, value) in obj.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                    {
                        if (Excluded.Contains(key))
                            continue;
                        result[key] = Canonicalize(value, key);
                    }
                    return result;
                }
            case JsonArray arr:
                {
                    var items = arr.Select(item => Canonicalize(item, parentKey: null)).ToList();
                    if (parentKey != null && SortedArrays.TryGetValue(parentKey, out var sortKey))
                        items = items.OrderBy(i => (i as JsonObject)?[sortKey]?.GetValue<string>() ?? "", StringComparer.Ordinal).ToList();
                    var result = new JsonArray();
                    foreach (var item in items)
                        result.Add(item);
                    return result;
                }
            default:
                return node?.DeepClone();
        }
    }
}
