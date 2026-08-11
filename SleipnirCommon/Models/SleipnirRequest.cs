using System.Text.Json.Nodes;
using MessagePack;

namespace SleipnirCommon.Models;

/// <summary>
/// Represents a single RPC request sent from client to server.
/// </summary>
[MessagePackObject]
public class SleipnirRequest
{
    [Key(0)]
    public string Controller { get; set; } = string.Empty;

    [Key(1)]
    public string Method { get; set; } = string.Empty;

    /// <summary>
    /// Parameter als nativer JSON-Wert: ein <see cref="JsonArray"/> von
    /// <c>{ parameterName, data, num }</c>-Einträgen, wobei <c>data</c> selbst ein
    /// nativer JSON-Wert ist (keine Doppelkodierung mehr). <c>null</c> = keine Parameter.
    /// </summary>
    [Key(2)]
    public JsonNode? Params { get; set; }

    [Key(3)]
    public byte[]? BinaryData { get; set; }

    [Key(4)]
    public string? Id { get; set; } = string.Empty;

    [Key(5)]
    public Dictionary<string, string>? DependencyMapping { get; set; }
}