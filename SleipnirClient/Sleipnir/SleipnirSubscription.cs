using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using SleipnirCommon.Models;

namespace SleipnirClient.Sleipnir;

/// <summary>
/// Eine aktive Event-Subscription auf dem <see cref="SleipnirWebSocketClient"/> (Phase 3).
/// Kapselt die <c>subscriptionId</c> und einen <see cref="SleipnirSubject{T}"/>, der die
/// empfangenen Events pusht. <see cref="Dispose"/> sendet Unsubscribe und beendet die
/// Subscription. Siehe <c>docs/design/phase-3-events.md</c>.
/// </summary>
/// <typeparam name="T">Der Event-Payload-Typ (wird aus JSON deserialisiert).</typeparam>
public sealed class SleipnirSubscription<T> : IObservable<T>, IDisposable
{
    private readonly SleipnirSubject<T> _subject = new();
    private readonly Func<string, CancellationToken, Task> _unsubscribeAsync;
    private readonly string _subscriptionId;
    private readonly CancellationToken _ct;
    private bool _disposed;

    internal SleipnirSubscription(string subscriptionId, Func<string, CancellationToken, Task> unsubscribeAsync, CancellationToken ct)
    {
        _subscriptionId = subscriptionId;
        _unsubscribeAsync = unsubscribeAsync;
        _ct = ct;
    }

    internal string SubscriptionId => _subscriptionId;
    internal SleipnirSubject<T> Subject => _subject;

    /// <summary>Subscribiert auf die Events. Gibt einen IDisposable zurück, der den
    /// Observer abmeldet (nicht die Server-Subscription — dafür <see cref="Dispose"/>).</summary>
    public IDisposable Subscribe(IObserver<T> observer) => _subject.Subscribe(observer);

    /// <summary>Sendet Unsubscribe an den Server und beendet die Subscription.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _unsubscribeAsync(_subscriptionId, _ct).Wait(TimeSpan.FromSeconds(5)); } catch { /* best-effort */ }
        _subject.OnCompleted();
        _subject.Dispose();
    }
}

/// <summary>
/// Minimal subject (IObservable+IObserver) without a System.Reactive dependency. Multiple
/// observers; pushes OnNext/OnCompleted/OnError. Thread-safe via one lock.
/// </summary>
/// <remarks>
/// <c>SubscribeAsync</c> returns the subscription only after the server's ack, so a hot source's
/// first events — pushed right behind the ack — arrive before the caller can attach an observer.
/// They (and a terminal signal) are held for the <b>first</b> observer and replayed to it in order;
/// after that the subject is plain hot. Bounded by <see cref="MaxHeldForFirstObserver"/> (oldest
/// dropped beyond).
/// </remarks>
internal sealed class SleipnirSubject<T> : IObservable<T>, IObserver<T>, IDisposable
{
    internal const int MaxHeldForFirstObserver = 1024;

    private readonly List<IObserver<T>> _observers = new();
    private readonly object _lock = new();
    private bool _completed;
    private Queue<T>? _held = new();
    private Exception? _heldError;
    private bool _heldCompleted;

    public IDisposable Subscribe(IObserver<T> observer)
    {
        lock (_lock)
        {
            if (_held != null)
            {
                // First observer: replay under the lock, so no concurrent OnNext overtakes the
                // held events.
                var held = _held;
                _held = null;
                foreach (var value in held) observer.OnNext(value);
                if (_heldError != null) { observer.OnError(_heldError); return new NoopDisposable(); }
                if (_heldCompleted) { observer.OnCompleted(); return new NoopDisposable(); }
            }
            if (_completed) { observer.OnCompleted(); return new NoopDisposable(); }
            _observers.Add(observer);
        }
        return new SubscriptionDisposable(this, observer);
    }

    public void OnNext(T value)
    {
        List<IObserver<T>> snapshot;
        lock (_lock)
        {
            if (_completed) return;
            if (_held != null)
            {
                if (_held.Count >= MaxHeldForFirstObserver) _held.Dequeue();
                _held.Enqueue(value);
                return;
            }
            snapshot = _observers.ToList();
        }
        foreach (var o in snapshot) o.OnNext(value);
    }

    public void OnCompleted()
    {
        List<IObserver<T>> snapshot;
        lock (_lock)
        {
            if (_completed) return;
            _completed = true;
            if (_held != null) { _heldCompleted = true; return; }
            snapshot = _observers.ToList();
            _observers.Clear();
        }
        foreach (var o in snapshot) o.OnCompleted();
    }

    public void OnError(Exception error)
    {
        List<IObserver<T>> snapshot;
        lock (_lock)
        {
            if (_completed) return;
            _completed = true;
            if (_held != null) { _heldError = error; return; }
            snapshot = _observers.ToList();
            _observers.Clear();
        }
        foreach (var o in snapshot) o.OnError(error);
    }

    public void Dispose()
    {
        lock (_lock) { _observers.Clear(); _completed = true; _held = null; }
    }

    private void RemoveObserver(IObserver<T> observer)
    {
        lock (_lock) { _observers.Remove(observer); }
    }

    private sealed class SubscriptionDisposable : IDisposable
    {
        private readonly SleipnirSubject<T> _subject;
        private readonly IObserver<T> _observer;
        public SubscriptionDisposable(SleipnirSubject<T> subject, IObserver<T> observer) { _subject = subject; _observer = observer; }
        public void Dispose() => _subject.RemoveObserver(_observer);
    }

    private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
}