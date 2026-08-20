namespace Sleipnir.Guide.Api.Services;

// A minimal hot IObservable<T> with no System.Reactive dependency — the exact pattern from
// SleipnirTests/Fixtures/ResumableEventFixture.cs. PriceFeedService holds one of these per
// symbol and calls Push(tick) on a timer; the [SleipnirEvent] controller yields the same
// long-lived singleton instance, so a durable subscription (Resumable = true) re-attaches to
// the *same* source across disconnects and the framework replays the missed eventId tail.
//
// Thread-safety: Push snapshots the observer list under a lock and invokes OnNext *outside*
// the lock, so a re-entrant Subscribe/Dispose inside an OnNext handler cannot deadlock. The
// timer thread calls Push; observers are added/removed by the framework's transports. There
// is NO replay buffer here — resume/replay is the framework's job (SleipnirSubscriptionStore
// ring buffer), which is why the event method must return a *hot* singleton, not a cold observable.
public sealed class HotObservable<T> : IObservable<T>
{
    private readonly List<IObserver<T>> _observers = new();
    private readonly object _lock = new();

    public IDisposable Subscribe(IObserver<T> observer)
    {
        lock (_lock) _observers.Add(observer);
        return new Unsubscriber(() => { lock (_lock) _observers.Remove(observer); });
    }

    // Called by the timer thread. Snapshot under the lock, invoke outside it (see above).
    public void Push(T value)
    {
        IObserver<T>[] snapshot;
        lock (_lock) snapshot = _observers.ToArray();
        foreach (var o in snapshot) o.OnNext(value);
    }

    public void Complete()
    {
        IObserver<T>[] snapshot;
        lock (_lock) snapshot = _observers.ToArray();
        foreach (var o in snapshot) o.OnCompleted();
    }

    private sealed class Unsubscriber : IDisposable
    {
        private Action? _dispose;
        public Unsubscriber(Action dispose) => _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}