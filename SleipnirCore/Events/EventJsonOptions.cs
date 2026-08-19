using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SleipnirCore.Events;

/// <summary>
/// Shared JSON serializer options for event-<b>frame</b> serialization (camelCase + relaxed
/// encoder + <c>WhenWritingNull</c>). Used by every event transport's observer
/// (WebSocket, SSE) so the logical event frame <c>{type,subscriptionId,eventId,data}</c>
/// serializes byte-identically across transports.
/// <para>
/// This is the frame-only options instance. The per-transport wire <b>envelope</b> (e.g. the
/// WebSocket <c>SleipnirResponseJsonConverter</c> used for the subscribe-ack, with its explicit
/// nulls + fixed field order) is owned by the transport, not here — event frames are anonymous
/// objects serialized with default semantics, so no converter applies and the output is identical
/// to the former WebSocket-local <c>SleipnirJsonOptions.Default</c>.
/// </para>
/// </summary>
internal static class EventJsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}