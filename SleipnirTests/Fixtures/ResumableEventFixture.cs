using SleipnirCore.Attributes;

namespace SleipnirTests.Fixtures;

/// <summary>
/// Minimal hot <see cref="IObservable{T}"/> for the resume integration test (no
/// System.Reactive dependency). <see cref="Push"/> broadcasts synchronously to every
/// currently-subscribed observer; <see cref="Complete"/> terminates them. The
/// <see cref="ResumableEventController"/> exposes a process-wide singleton instance, so
/// the single durable source subscription (kept alive across disconnect by the
/// <c>SleipnirSubscriptionStore</c>) keeps receiving events the test pushes during a gap.
/// </summary>
public sealed class HotObservable<T> : IObservable<T>
{
    private readonly List<IObserver<T>> _observers = new();
    private readonly object _lock = new();

    public IDisposable Subscribe(IObserver<T> observer)
    {
        lock (_lock) _observers.Add(observer);
        return new Unsubscriber(() =>
        {
            lock (_lock) _observers.Remove(observer);
        });
    }

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

/// <summary>
/// Test controller with a <c>[SleipnirEvent(Resumable = true)]</c> hot stream, auto-discovered
/// by the integration-test host. The method returns the singleton <see cref="Stream"/> so the
/// durable subscription store keeps ONE source subscription alive across disconnects and the
/// test can push events into the gap via <c>ResumableEventController.Stream.Push(...)</c>.
/// </summary>
[SleipnirController("ResumableEvent")]
public class ResumableEventController
{
    public static readonly HotObservable<string> Stream = new();

    [SleipnirEvent("Tick", Resumable = true)]
    public IObservable<string> Tick() => Stream;
}