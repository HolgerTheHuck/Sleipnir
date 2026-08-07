using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using TrameCommon.Models;

namespace TrameClient.Trame;

/// <summary>
/// Eine aktive Event-Subscription auf dem <see cref="TrameWebSocketClient"/> (Phase 3).
/// Kapselt die <c>subscriptionId</c> und einen <see cref="TrameSubject{T}"/>, der die
/// empfangenen Events pusht. <see cref="Dispose"/> sendet Unsubscribe und beendet die
/// Subscription. Siehe <c>docs/design/phase-3-events.md</c>.
/// </summary>
/// <typeparam name="T">Der Event-Payload-Typ (wird aus JSON deserialisiert).</typeparam>
public sealed class TrameSubscription<T> : IObservable<T>, IDisposable
{
    private readonly TrameSubject<T> _subject = new();
    private readonly Func<string, CancellationToken, Task> _unsubscribeAsync;
    private readonly string _subscriptionId;
    private readonly CancellationToken _ct;
    private bool _disposed;

    internal TrameSubscription(string subscriptionId, Func<string, CancellationToken, Task> unsubscribeAsync, CancellationToken ct)
    {
        _subscriptionId = subscriptionId;
        _unsubscribeAsync = unsubscribeAsync;
        _ct = ct;
    }

    internal string SubscriptionId => _subscriptionId;
    internal TrameSubject<T> Subject => _subject;

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
/// Minimal-Subject (IObservable+IObserver) ohne System.Reactive-Abhängigkeit. Erlaubt
/// Multiple Observer, pusht OnNext/OnCompleted/OnError. Thread-safe über ein Lock.
/// </summary>
internal sealed class TrameSubject<T> : IObservable<T>, IObserver<T>, IDisposable
{
    private readonly List<IObserver<T>> _observers = new();
    private readonly object _lock = new();
    private bool _completed;

    public IDisposable Subscribe(IObserver<T> observer)
    {
        lock (_lock)
        {
            if (_completed) { observer.OnCompleted(); return new NoopDisposable(); }
            _observers.Add(observer);
        }
        return new SubscriptionDisposable(this, observer);
    }

    public void OnNext(T value)
    {
        List<IObserver<T>> snapshot;
        lock (_lock) { if (_completed) return; snapshot = _observers.ToList(); }
        foreach (var o in snapshot) o.OnNext(value);
    }

    public void OnCompleted()
    {
        List<IObserver<T>> snapshot;
        lock (_lock) { if (_completed) return; _completed = true; snapshot = _observers.ToList(); _observers.Clear(); }
        foreach (var o in snapshot) o.OnCompleted();
    }

    public void OnError(Exception error)
    {
        List<IObserver<T>> snapshot;
        lock (_lock) { if (_completed) return; _completed = true; snapshot = _observers.ToList(); _observers.Clear(); }
        foreach (var o in snapshot) o.OnError(error);
    }

    public void Dispose()
    {
        lock (_lock) { _observers.Clear(); _completed = true; }
    }

    private void RemoveObserver(IObserver<T> observer)
    {
        lock (_lock) { _observers.Remove(observer); }
    }

    private sealed class SubscriptionDisposable : IDisposable
    {
        private readonly TrameSubject<T> _subject;
        private readonly IObserver<T> _observer;
        public SubscriptionDisposable(TrameSubject<T> subject, IObserver<T> observer) { _subject = subject; _observer = observer; }
        public void Dispose() => _subject.RemoveObserver(_observer);
    }

    private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
}