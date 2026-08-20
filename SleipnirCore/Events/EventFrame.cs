using System.Text.Json;

namespace SleipnirCore.Events;

/// <summary>
/// Builds the serialized logical event frames shared by every event transport:
/// <list type="bullet">
/// <item><c>{type:"event",subscriptionId,eventId,data}</c></item>
/// <item><c>{type:"complete",subscriptionId}</c></item>
/// <item><c>{type:"error",subscriptionId,message}</c></item>
/// </list>
/// These are the <b>payload</b> frames. On WebSocket each is one text frame; on SSE each becomes
/// an SSE event block (<c>id:</c>/<c>event:</c>/<c>data:</c> lines). Serialized with
/// <see cref="EventJsonOptions.Default"/> so the bytes are identical regardless of transport —
/// the property order and casing match the historical WebSocket-only output exactly.
/// </summary>
internal static class EventFrame
{
    public static string Event(string subscriptionId, long eventId, object? data)
        => JsonSerializer.Serialize(new { type = "event", subscriptionId, eventId, data }, EventJsonOptions.Default);

    public static string Complete(string subscriptionId)
        => JsonSerializer.Serialize(new { type = "complete", subscriptionId }, EventJsonOptions.Default);

    public static string Error(string subscriptionId, string message)
        => JsonSerializer.Serialize(new { type = "error", subscriptionId, message }, EventJsonOptions.Default);

    /// <summary>
    /// The subscribe-ack frame — the FIRST item of a SignalR hub stream. WebSocket and SSE
    /// deliver the ack out-of-band (the WS subscribe <c>SleipnirResponse</c>; the SSE
    /// <c>event: ack</c> block) because they have a separate response channel; a SignalR
    /// <c>IAsyncEnumerable</c> stream has only the stream itself, so the ack travels as the
    /// first yielded frame. <c>replayedFrom</c> is omitted on a fresh subscribe (null →
    /// <c>EventJsonOptions.Default</c> WhenWritingNull); set on a resume so the client learns
    /// the first replayed eventId. The TS SignalR client resolves <c>subscriptionId</c> +
    /// <c>replayedFrom</c> from this item (it needs the id for cross-transport resume).
    /// </summary>
    public static string Ack(string subscriptionId, long? replayedFrom)
        => JsonSerializer.Serialize(new { type = "ack", subscriptionId, replayedFrom }, EventJsonOptions.Default);
}