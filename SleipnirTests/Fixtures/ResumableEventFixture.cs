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

/// <summary>
/// Dedicated resumable hot stream for the Phase R3 end-to-end <c>ResumeTests</c>. Lives apart from
/// <see cref="ResumableEventController.Stream"/> (used by <c>WebSocketResumeTests</c>) so the two
/// integration-test classes can run in parallel without cross-broadcasting pushed values into each
/// other's durable observers — <see cref="HotObservable{T}"/> fans a <c>Push</c> out to every
/// currently-subscribed observer, and a durable observer stays subscribed to the singleton for the
/// durable's lifetime. The R3 tests run sequentially within their class, so they share this one
/// stream safely; host disposal tears down the durable store and unsubscribes between tests.
/// </summary>
[SleipnirController("E2EResumeEvent")]
public class E2EResumeEventController
{
    public static readonly HotObservable<string> Stream = new();

    [SleipnirEvent("Tick", Resumable = true)]
    public IObservable<string> Tick() => Stream;
}

/// <summary>
/// Auth-protected resumable event for the Phase R3 reconnect auth re-check test. The method is
/// gated by <c>[SleipnirAuthorise(Role = "Admin")]</c>; a fresh subscribe succeeds only with an
/// authenticated Admin principal, and a reconnect resume re-runs that same check (Phase R3a). A
/// resume arriving without credentials (role revoked / token dropped during the disconnect gap)
/// must be rejected with 401 and the durable subscription torn down. Uses its own hot stream so
/// it does not interfere with <see cref="ResumableEventController.Stream"/>.
/// </summary>
[SleipnirController("AuthedResumableEvent")]
public class AuthedResumableEventController
{
    public static readonly HotObservable<string> Stream = new();

    [SleipnirEvent("SecureTick", Resumable = true)]
    [SleipnirAuthorise(Role = "Admin")]
    public IObservable<string> SecureTick() => Stream;
}

/// <summary>
/// Plain (non-event) controller for the <see cref="AuthorizeSubscribeTests"/> non-event branch.
/// <c>AuthorizeSubscribeAsync</c> rejects a route that is not a <c>[SleipnirEvent]</c> with 400, so a
/// caller cannot lie that a plain call is a resumable event to land a weaker auth check on resume.
/// </summary>
[SleipnirController("PlainCall")]
public class PlainCallController
{
    [SleipnirMethod("Ping")]
    public string Ping() => "pong";
}