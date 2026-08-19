using Microsoft.Extensions.Logging;
using SleipnirCore.Services;
using SleipnirCore.Tracing;

namespace SleipnirCore.Events;

/// <summary>
/// <see cref="IObserver{T}"/> for an <b>ephemeral</b> (non-resumable) event subscription:
/// serializes OnNext/OnCompleted/OnError into logical event frames via <see cref="EventFrame"/>
/// and writes them into the per-subscription <see cref="EventBuffer"/>. Transport-agnostic —
/// the buffer is drained by the owning transport's pump (WebSocket send channel / SSE body).
/// Drop counting is correct: DropOldest evicts (TryEnqueue returns true, onDropped invoked),
/// DropWrite discards (TryEnqueue returns false, onDropped invoked), Block never drops,
/// Unbounded never drops.
/// </summary>
internal sealed class EventObserver<T> : IObserver<T>
{
    private readonly EphemeralSubscriptionState _state;
    private readonly string _subscriptionId;
    private readonly ILogger? _logger;

    public EventObserver(EphemeralSubscriptionState state, string subscriptionId, ILogger? logger)
    {
        _state = state;
        _subscriptionId = subscriptionId;
        _logger = logger;
    }

    public void OnNext(T value)
    {
        var eventId = _state.NextEventId();
        var frame = EventFrame.Event(_subscriptionId, eventId, value);
        // Drop counting is correct: DropOldest evicts (TryEnqueue returns true, onDropped
        // invoked), DropWrite discards (TryEnqueue returns false, onDropped invoked), Block
        // never drops, Unbounded never drops. The earlier DropOldest channel path always
        // returned true on saturation → onDropped was unreachable dead code.
        _state.Buffer.TryEnqueue(frame, OnDropped);
    }

    private void OnDropped()
    {
        _state.RecordDrop();
        SleipnirMetrics.EventDropped(_subscriptionId);
        _logger?.LogWarning("Event dropped for subscription {SubscriptionId} (buffer full)", _subscriptionId);
    }

    public void OnCompleted()
        => _state.Buffer.EnqueueTerminal(EventFrame.Complete(_subscriptionId));

    public void OnError(Exception error)
        => _state.Buffer.EnqueueTerminal(EventFrame.Error(_subscriptionId, error.Message));
}

/// <summary>
/// <see cref="IObserver{T}"/> for a <b>durable</b> (resumable) subscription (Phase R):
/// serializes each event frame with the <see cref="DurableSubscriptionState"/>-owned monotonic
/// <c>eventId</c> (stable across reconnects) and forwards it to the store state (replay ring
/// buffer + optional live tap). OnCompleted/OnError are recorded as a terminal frame (replayed
/// on resume) and forwarded to the live tap. Transport-agnostic — the durable state lives in
/// the process-wide <see cref="SleipnirSubscriptionStore"/>; whichever transport (WebSocket,
/// SSE) attached the tap drains it.
/// </summary>
internal sealed class DurableEventObserver<T> : IObserver<T>
{
    private readonly DurableSubscriptionState _state;
    private readonly ILogger? _logger;

    public DurableEventObserver(DurableSubscriptionState state, ILogger? logger)
    {
        _state = state;
        _logger = logger;
    }

    public void OnNext(T value)
    {
        var eventId = _state.NextEventId();
        var frame = EventFrame.Event(_state.SubscriptionId, eventId, value);
        // AppendEvent records into the replay ring buffer (evict-oldest on cap → drop counter
        // via the store) AND forwards to the attached live tap, if any. With no tap
        // (disconnected) the frame lives only in the ring buffer → replayed on resume.
        _state.AppendEvent(eventId, frame);
    }

    public void OnCompleted()
        => _state.SetTerminal(EventFrame.Complete(_state.SubscriptionId));

    public void OnError(Exception error)
    {
        _state.SetTerminal(EventFrame.Error(_state.SubscriptionId, error.Message));
        _logger?.LogError(error, "Durable event source errored for subscription {SubscriptionId}", _state.SubscriptionId);
    }
}