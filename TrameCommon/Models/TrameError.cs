using MessagePack;
using System.Text.Json.Serialization;
using TrameCommon.Results;

namespace TrameCommon.Models;

/// <summary>
/// Unified error model for all Trame transports.
/// Carries structured error information across REST, SignalR, and WebSocket.
/// </summary>
[MessagePackObject]
public class TrameError
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
    /// bestehende 1.0.0-Responses haben <see cref="TrameErrorCategory.None"/> und
    /// bleiben damit abwärtskompatibel. Erlaubt Clients, Fehler einheitlich über
    /// alle Transporte zu behandeln; generierte Clients können pro Kategorie
    /// typisierte Exceptions werfen. Siehe <c>ERROR_CATALOG.md</c>.
    /// </summary>
    [Key(4)]
    [JsonPropertyName("category")]
    public TrameErrorCategory Category { get; set; } = TrameErrorCategory.None;

    /// <summary>
    /// Creates an TrameError from an TrameResponse (when Code != 200). Die Message
    /// stammt aus <see cref="TrameResponse.Error"/> (Fehler tragen seit dem Single-Pass-
    /// Fix ihre Message in Error.Message, nicht mehr in Data).
    /// </summary>
    public static TrameError FromResponse(TrameResponse response)
    {
        return new TrameError
        {
            Code = response.Code,
            Message = response.Error?.Message ?? $"Trame call failed with code {response.Code}.",
            RequestId = response.Id,
            Category = response.Error?.Category ?? TrameErrorCategory.None,
        };
    }
}