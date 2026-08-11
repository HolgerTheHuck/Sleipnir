using MessagePack;
using System.Text.Json.Serialization;
using SleipnirCommon.Results;

namespace SleipnirCommon.Models;

/// <summary>
/// Unified error model for all Sleipnir transports.
/// Carries structured error information across REST, SignalR, and WebSocket.
/// </summary>
[MessagePackObject]
public class SleipnirError
{
    /// <summary>
    /// HTTP-like status code (e.g. 400, 401, 404, 500).
    /// </summary>
    [Key(0)]
    [JsonPropertyName("code")]
    public int Code { get; set; }

    /// <summary>
    /// Human-readable error message.
    /// </summary>
    [Key(1)]
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Additional error details (e.g. stack trace – only populated in Development).
    /// </summary>
    [Key(2)]
    [JsonPropertyName("details")]
    public string? Details { get; set; }

    /// <summary>
    /// Correlation ID matching the originating request.
    /// </summary>
    [Key(3)]
    [JsonPropertyName("requestId")]
    public string? RequestId { get; set; }

    /// <summary>
    /// Semantische Fehler-Kategorie — *zusätzlich* zu <see cref="Code"/>, nicht
    /// ersetzend. Additives Feld (Phase 1, siehe <c>STABILITY.md</c> §1.4/§3.2):
    /// bestehende 1.0.0-Responses haben <see cref="SleipnirErrorCategory.None"/> und
    /// bleiben damit abwärtskompatibel. Erlaubt Clients, Fehler einheitlich über
    /// alle Transporte zu behandeln; generierte Clients können pro Kategorie
    /// typisierte Exceptions werfen. Siehe <c>ERROR_CATALOG.md</c>.
    /// </summary>
    [Key(4)]
    [JsonPropertyName("category")]
    public SleipnirErrorCategory Category { get; set; } = SleipnirErrorCategory.None;

    /// <summary>
    /// Creates an SleipnirError from an SleipnirResponse (when Code != 200). Die Message
    /// stammt aus <see cref="SleipnirResponse.Error"/> (Fehler tragen seit dem Single-Pass-
    /// Fix ihre Message in Error.Message, nicht mehr in Data).
    /// </summary>
    public static SleipnirError FromResponse(SleipnirResponse response)
    {
        return new SleipnirError
        {
            Code = response.Code,
            Message = response.Error?.Message ?? $"Sleipnir call failed with code {response.Code}.",
            RequestId = response.Id,
            Category = response.Error?.Category ?? SleipnirErrorCategory.None,
        };
    }
}