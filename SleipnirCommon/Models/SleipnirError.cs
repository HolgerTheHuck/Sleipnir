using MessagePack;
using System.Collections.Generic;
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
    /// Field-level validation errors in the ASP.NET Core <c>ValidationProblemDetails.errors</c>
    /// shape (RFC 9457): field path → one or more messages, e.g.
    /// <c>{ "scenario.steps[1].hsn": ["Must be 4 digits."] }</c>. Set by
    /// <see cref="SleipnirResults.ValidationProblem(IDictionary{string, string[]}, string?, string?)"/>
    /// and by the opt-in parameter validation (<c>SleipnirOptions.ValidateParameters</c>);
    /// <c>null</c> on every other error. Additive (STABILITY.md §3.2): omitted from JSON when
    /// <c>null</c>, so existing error payloads stay byte-identical; older clients ignore it.
    /// Path convention (framework-generated keys): camelCase wire names, <c>.</c> for members,
    /// <c>[i]</c> for list indexes, rooted at the parameter name. See <c>ERROR_CATALOG.md</c>.
    /// </summary>
    [Key(5)]
    [JsonPropertyName("errors")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string[]>? Errors { get; set; }

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