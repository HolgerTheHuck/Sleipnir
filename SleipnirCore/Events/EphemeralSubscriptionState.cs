namespace SleipnirCore.Events;

/// <summary>
/// Per-connection state for an <b>ephemeral</b> (non-resumable) event subscription: a stable
/// subscription id, a monotonic <c>eventId</c> counter, a bounded <see cref="EventBuffer"/>, the
/// source <c>IDisposable</c>, and a dropped-count accumulator. Sibling of
/// <see cref="SleipnirCore.Services.DurableSubscriptionState"/> (which adds the replay ring +
/// live tap for resumable events). Transport-agnostic — the buffer is drained by the owning
/// transport's pump.
/// </summary>
internal sealed class EphemeralSubscriptionState : IDisposable
{
    public string SubscriptionId { get; }
    public EventBuffer Buffer { get; }
    public IDisposable? Disposable { get; set; }
    public long DroppedCount => Interlocked.Read(ref _droppedCount);
    private long _eventIdCounter;
    private long _droppedCount;

    public EphemeralSubscriptionState(string subscriptionId, int bufferCapacity, EventBackpressureStrategy strategy, CancellationToken disposeToken)
    {
        SubscriptionId = subscriptionId;
        Buffer = new EventBuffer(bufferCapacity, strategy, disposeToken);
    }

    public long NextEventId() => Interlocked.Increment(ref _eventIdCounter);

    public void RecordDrop() => Interlocked.Increment(ref _droppedCount);

    public void Dispose()
    {
        Disposable?.Dispose();
        Buffer.Complete();
    }
}