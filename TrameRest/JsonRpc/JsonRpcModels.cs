using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using TrameCommon.Models;

namespace TrameRest.JsonRpc;

/// <summary>
/// JSON-RPC 2.0 Anfrage-DTO (eingehend). <see cref="Id"/> und <see cref="Params"/>
/// werden als <see cref="JsonElement"/> gehalten, damit der Originaltyp der id
/// (Number/String) fürs Echo erhalten bleibt und params als Object (named) oder
/// Array (positional) ausgelegt werden kann.
/// </summary>
internal sealed class JsonRpcRequest
{
    [JsonPropertyName("jsonrpc")]
    public string? JsonRpc { get; set; }

    [JsonPropertyName("method")]
    public string? Method { get; set; }

    /// <summary>Object (named) | Array (positional) | null.</summary>
    [JsonPropertyName("params")]
    public JsonElement? Params { get; set; }

    /// <summary>String | Number | null — null/fehlend bedeutet Notification.</summary>
    [JsonPropertyName("id")]
    public JsonElement? Id { get; set; }
}

/// <summary>
/// Ergebnis des Parsens eines JSON-RPC-Items. Vereint Validierung,
/// TrameRequest-Übersetzung, Capability-Erkennung und id/Notification-Status.
/// <see cref="InvokeIndex"/> wird beim Aufbau der Invoke-Liste gesetzt (-1 = nicht
/// invoked); <see cref="Response"/> nimmt das fertige JSON-RPC-Response-Objekt auf
/// (null = Notification, wird in der Response-Liste übersprungen).
/// </summary>
internal sealed class ParsedRpcItem
{
    public JsonElement? Id;
    public bool IsNotification = true;
    public bool IsValid;
    public int ErrorCode;
    public string? ErrorMessage;
    public TrameRequest? Request;
    /// <summary>"trame.discover" | "trame.capabilities" | null.</summary>
    public string? Capability;
    public int InvokeIndex = -1;
    public JsonNode? Response;
}