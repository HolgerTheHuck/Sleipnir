using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Net.WebSockets;
using SleipnirCommon.Models;
using SleipnirCommon.Results;
using SleipnirCore.Services;
using SleipnirCore.Tracing;

namespace SleipnirWebSocket;

/// <summary>
/// Pro-Connection Subscription-Manager (Phase 3, Events). Hält aktive IObservable-
/// Subscriptions pro WebSocket-Connection, pusht Events als separierte Frames
/// (<c>{type:"event",subscriptionId,eventId,data}</c>) über einen pro-Subscription
/// Backpressure-Buffer + Send-Loop, und räumt bei Disconnect automatisch auf.
/// </summary>
/// <remarks>
/// <para>
/// <b>Backpressure</b> (Entscheidung 7, erweitert): pro Subscription ein Buffer mit
/// wählbarer Überschuss-Strategie (<c>EventBackpressureStrategy</c>: DropOldest/DropWrite/
/// Block/Unbounded), Kapazität aus <c>SleipnirOptions.EventBufferCapacity</c> (Fallback 100),
/// per-Event überschreibbar via <c>[SleipnirEvent]</c>. Verlorene Events zählen korrekt in
/// <c>sleipnir.event.dropped</c> (DropOldest evict, DropWrite reject) — der frühere
/// <c>DropOldest</c>-Channel-Pfad konnte Sättigung nicht erkennen (TryWrite liefert immer
/// true), deshalb eigener <see cref="EventBuffer"/>.
/// </para>
/// <para>
/// <b>Reconnect</b> (Entscheidung 6): subscriptionId ist pro-Connection. Bei
/// Disconnect werden alle Subscriptions disposed; der Client re-subscribed nach
/// Reconnect mit neuen Parametern (client-seitig). Gap-Events während Drop gehen
/// verloren (at-most-once-while-disconnected, Entscheidung 2).
/// </para>
/// <para>
/// Siehe <c>docs/design/phase-3-events.md</c>.
/// </para>
/// </remarks>
internal sealed class SleipnirSubscriptionManager : IAsyncDisposable
{
    private readonly WebSocket _webSocket;
    private readonly ISleipnirCore _sleipnirCore;
    private readonly SleipnirConnectionRegistry _connectionRegistry;
    private readonly ILogger? _logger;
    private readonly int _defaultBufferCapacity;
    private readonly EventBackpressureStrategy _defaultStrategy;
    private readonly CancellationTokenSource _disposeCts = new();

    // subscriptionId → Subscription-State (Channel, eventId-Counter, IDisposable vom IObservable).
    private readonly ConcurrentDictionary<string, SubscriptionState> _subscriptions = new();

    // Ein Send-Loop pro Connection, der Event-Frames serialisiert auf den Socket schreibt
    // (WebSocket.SendAsync ist nicht thread-safe für konkurrierende Sends).
    private readonly Channel<string> _sendChannel;
    private readonly Task _sendLoopTask;

    public SleipnirSubscriptionManager(
        WebSocket webSocket,
        ISleipnirCore sleipnirCore,
        SleipnirConnectionRegistry connectionRegistry,
        ILogger? logger,
        int bufferCapacity = 100,
        EventBackpressureStrategy backpressureStrategy = EventBackpressureStrategy.DropOldest)
    {
        _webSocket = webSocket;
        _sleipnirCore = sleipnirCore;
        _connectionRegistry = connectionRegistry;
        _logger = logger;
        _defaultBufferCapacity = bufferCapacity > 0 ? bufferCapacity : 100;
        _defaultStrategy = backpressureStrategy;
        // Hotfix 1.1.1: fester Sende-Puffer, unabhängig von Subscription-Anzahl (Events haben
        // eigene per-Subscription-Buffer). DropOldest hier ist nur der Socket-Send-Puffer
        // (Call-Response + Event-Frames vor dem Schreiben); Sättigung hier ist nicht
        // Event-Backpressure und wird nicht in sleipnir.event.dropped gezählt.
        _sendChannel = Channel.CreateBounded<string>(new BoundedChannelOptions(_defaultBufferCapacity + 256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        _sendLoopTask = Task.Run(() => SendLoopAsync(_disposeCts.Token));
    }

    /// <summary>Verarbeitet einen Subscribe-Request: ruft SubscribeAsync, subscribiert das Observable, pusht Events.</summary>
    public async Task<SleipnirResponse?> HandleSubscribeAsync(SleipnirRequest request, HttpContext? context, CancellationToken ct)
    {
        var result = await _sleipnirCore.SubscribeAsync(request, context, ct);
        if (result.Error != null)
            return result.Error;

        var observable = result.Observable!;
        var subscriptionId = Guid.NewGuid().ToString("N");

        // Backpressure pro-Subscription aus dem aufgelösten Subscribe-Ergebnis (Per-Event-
        // Override ?? globale Option, bereits im Invoker aufgelöst); Fallback auf die
        // Manager-Defaults (reiner Safety-Net, regulärer Pfad liefert immer konkrete Werte).
        var capacity = result.EventBufferCapacity > 0 ? result.EventBufferCapacity : _defaultBufferCapacity;
        var strategy = result.EventBackpressureStrategy;
        var state = new SubscriptionState(subscriptionId, capacity, strategy, _disposeCts.Token);
        if (!_subscriptions.TryAdd(subscriptionId, state))
        {
            state.Dispose();
            return SleipnirResults.Error(SleipnirErrorCodes.Conflict, "Subscription ID collision — retry.", SleipnirCommon.Results.SleipnirErrorCategory.Conflict);
        }

        // Observability: count the now-active subscription (process-wide gauge + JSON snapshot).
        _connectionRegistry.IncSubscription();

        // Auf dem Observable subscribieren; jedes OnNext → Event-Frame in den Send-Channel.
        state.Disposable = observable.Subscribe(new EventObserver<object?>(state, subscriptionId, _logger));

        // Pump-Task: liest aus dem per-Subscription-Buffer und schreibt in den Send-Channel.
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var frame in state.Buffer.ReadAllAsync(_disposeCts.Token))
                {
                    await _sendChannel.Writer.WriteAsync(frame, _disposeCts.Token);
                }
            }
            catch (OperationCanceledException) { /* Dispose */ }
            catch (Exception ex) { _logger?.LogError(ex, "Pump task failed for subscription {SubscriptionId}", subscriptionId); }
        }, _disposeCts.Token);

        // Subscribe-Response: subscriptionId an den Client.
        return new SleipnirResponse
        {
            Code = SleipnirErrorCodes.Ok,
            Data = JsonSerializer.SerializeToElement(new { subscriptionId }, SleipnirJsonOptions.Default),
            Id = request.Id,
        };
    }

    /// <summary>
    /// Verarbeitet einen Unsubscribe-Request: disposed die Subscription.
    /// </summary>
    public Task<SleipnirResponse?> HandleUnsubscribeAsync(string subscriptionId, string? requestId, CancellationToken ct)
    {
        if (_subscriptions.TryRemove(subscriptionId, out var state))
        {
            state.Dispose();
            // Observability: the subscription ended (explicit unsubscribe). Decrement once;
            // the DisposeAsync path only sees subscriptions still in the dict, so no double-count.
            _connectionRegistry.DecSubscription();
            return Task.FromResult<SleipnirResponse?>(new SleipnirResponse { Code = SleipnirErrorCodes.Ok, Id = requestId ?? string.Empty });
        }
        return Task.FromResult<SleipnirResponse?>(SleipnirResults.Error(SleipnirErrorCodes.NotFound, $"Subscription '{subscriptionId}' not found.",
            SleipnirCommon.Results.SleipnirErrorCategory.NotFound, null));
    }

    /// <summary>
    /// Sendet eine Nachricht (Call-Response, Subscribe-Response, Error) über den gemeinsamen
    /// Send-Channel — NICHT direkt via WebSocket.SendAsync. Das stellt sicher, dass es nur
    /// einen Sender auf dem Socket gibt (den SendLoop), und verhindert konkurrierende Sends
    /// zwischen Call-Responses (Middleware-Thread) und Event-Frames (Pump-Tasks).
    /// Hotfix 1.1.1: Thread-Safety für konkurrierende Sends.
    /// </summary>
    public async ValueTask EnqueueSendAsync(string json, CancellationToken ct = default)
    {
        await _sendChannel.Writer.WriteAsync(json, ct);
    }

    private async Task SendLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var frame in _sendChannel.Reader.ReadAllAsync(ct))
            {
                if (_webSocket.State != WebSocketState.Open) return;
                var bytes = System.Text.Encoding.UTF8.GetBytes(frame);
                using var sendCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await _webSocket.SendAsync(new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text, endOfMessage: true, sendCts.Token);
            }
        }
        catch (OperationCanceledException) { /* Dispose */ }
        catch (Exception ex) { _logger?.LogError(ex, "Send loop failed"); }
    }

    public async ValueTask DisposeAsync()
    {
        _disposeCts.Cancel();
        _sendChannel.Writer.TryComplete();

        // Alle Subscriptions disposed.
        foreach (var state in _subscriptions.Values)
        {
            state.Dispose();
            // Observability: each subscription still in the dict at disconnect ends here.
            // (Those already removed via explicit unsubscribe are gone — no double decrement.)
            _connectionRegistry.DecSubscription();
        }
        _subscriptions.Clear();

        try { await _sendLoopTask; } catch { /* ignore */ }
        _disposeCts.Dispose();
    }

    private sealed class SubscriptionState : IDisposable
    {
        public string SubscriptionId { get; }
        public EventBuffer Buffer { get; }
        public IDisposable? Disposable { get; set; }
        public long DroppedCount => Interlocked.Read(ref _droppedCount);
        private long _eventIdCounter;
        private long _droppedCount;

        public SubscriptionState(string subscriptionId, int bufferCapacity, EventBackpressureStrategy strategy, CancellationToken disposeToken)
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

    /// <summary>
    /// Pro-Subscription Backpressure-Buffer mit wählbarer Überschuss-Strategie
    /// (<see cref="EventBackpressureStrategy"/>). Single-Writer (der EventObserver),
    /// Single-Reader (der Pump-Task). Im Gegensatz zu einem
    /// <c>BoundedChannel(DropOldest)</c> (dessen <c>TryWrite</c> bei Sättigung immer
    /// <c>true</c> liefert und so Drops verdeckt) zählt dieser Buffer verlorene Events
    /// korrekt über den <c>onDropped</c>-Callback → <c>sleipnir.event.dropped</c>.
    /// </summary>
    internal sealed class EventBuffer
    {
        private readonly int _capacity;                  // 0 = unbounded
        private readonly EventBackpressureStrategy _strategy;
        private readonly bool _unbounded;
        private readonly Queue<string> _queue = new();
        private readonly object _lock = new();
        private readonly SemaphoreSlim _items;           // freigegeben pro enqueue; Reader wartet darauf
        private readonly SemaphoreSlim? _space;          // nur Block: freie Slots; Writer wartet darauf
        private readonly CancellationToken _disposeToken;
        private bool _completed;

        public EventBuffer(int capacity, EventBackpressureStrategy strategy, CancellationToken disposeToken)
        {
            _disposeToken = disposeToken;
            _strategy = strategy;
            _unbounded = strategy == EventBackpressureStrategy.Unbounded || capacity <= 0;
            _capacity = _unbounded ? 0 : capacity;
            _items = new SemaphoreSlim(0);
            _space = (strategy == EventBackpressureStrategy.Block && !_unbounded) ? new SemaphoreSlim(_capacity) : null;
        }

        public long Count { get { lock (_lock) return _queue.Count; } }

        /// <summary>
        /// Versucht, einen Event-Frame zu enqueuen. Bei DropOldest wird bei vollem Buffer
        /// das älteste Element evicted und <paramref name="onDropped"/> gerufen (liefert
        /// <c>true</c>). Bei DropWrite wird das neueste verworfen und <paramref name="onDropped"/>
        /// gerufen (liefert <c>false</c>). Bei Block wartet der Aufrufer synchron auf einen
        /// freien Slot (Producer-Backpressure; niemals <paramref name="onDropped"/>). Bei
        /// Unbounded immer <c>true</c>. <paramref name="onDropped"/> wird synchron unter dem
        /// Lock gerufen — der Callback darf das Lock nicht erneutnehmen.
        /// </summary>
        public bool TryEnqueue(string frame, Action onDropped)
        {
            if (_unbounded)
            {
                lock (_lock)
                {
                    if (_completed) return false;
                    _queue.Enqueue(frame);
                }
                _items.Release();
                return true;
            }

            if (_strategy == EventBackpressureStrategy.Block)
            {
                // Producer-Backpressure: synchron auf einen freien Slot warten. Ein Dispose
                // weckt über den CancellationToken (OCE → wir verwerfen still, kein Drop-Zähler).
                try { _space!.Wait(_disposeToken); }
                catch (OperationCanceledException) { return false; }
                lock (_lock)
                {
                    if (_completed)
                    {
                        // Während des Wartens wurde disposed — Slot nicht freigeben (Buffer ist tot).
                        return false;
                    }
                    _queue.Enqueue(frame);
                }
                _items.Release();
                return true;
            }

            lock (_lock)
            {
                if (_completed) return false;
                if (_queue.Count >= _capacity)
                {
                    if (_strategy == EventBackpressureStrategy.DropOldest)
                    {
                        _queue.Dequeue();   // ältestes evicten
                        _queue.Enqueue(frame);
                        onDropped();        // synchron, ohne Lock-Reentrancy
                        _items.Release();
                        return true;
                    }
                    // DropWrite: neuestes verwerfen
                    onDropped();
                    return false;
                }
                _queue.Enqueue(frame);
            }
            _items.Release();
            return true;
        }

        /// <summary>
        /// Enqueuet einen Terminal-Frame (complete/error) ohne Kapazitätsprüfung — er muss
        /// den Client erreichen, unabhängig von Backpressure. Danach ist der Buffer komplett.
        /// </summary>
        public void EnqueueTerminal(string frame)
        {
            lock (_lock)
            {
                if (_completed) return;
                _queue.Enqueue(frame);
                _completed = true;
            }
            // Reader wecken (falls er auf einem leeren Buffer wartet) + ggf. Slot freigeben.
            _items.Release();
        }

        public async IAsyncEnumerable<string> ReadAllAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            while (true)
            {
                string? frame = null;
                bool mustWait = false;
                lock (_lock)
                {
                    if (_queue.Count > 0)
                    {
                        frame = _queue.Dequeue();
                        _space?.Release();
                    }
                    else if (_completed)
                    {
                        yield break;               // drained + completed → stop without blocking
                    }
                    else
                    {
                        mustWait = true;           // empty + live → wait for an item / completion wake
                    }
                }
                if (frame != null)
                {
                    yield return frame;
                    continue;                      // re-check the queue before blocking again
                }
                if (mustWait)
                {
                    await _items.WaitAsync(ct).ConfigureAwait(false);
                    // loop: re-check under lock (item arrived, or a completion wake)
                }
            }
        }

        public void Complete()
        {
            bool wake;
            lock (_lock)
            {
                if (_completed) return;
                _completed = true;
                wake = _queue.Count == 0;   // nur wecken, falls der Reader blockiert (leere Queue)
            }
            if (wake) _items.Release();
        }
    }

    /// <summary>
    /// IObserver-Implementierung, die OnNext/OnCompleted/OnError in Event-Frames
    /// serialisiert und in den per-Subscription-Buffer schreibt.
    /// </summary>
    private sealed class EventObserver<T> : IObserver<T>
    {
        private readonly SubscriptionState _state;
        private readonly string _subscriptionId;
        private readonly ILogger? _logger;

        public EventObserver(SubscriptionState state, string subscriptionId, ILogger? logger)
        {
            _state = state;
            _subscriptionId = subscriptionId;
            _logger = logger;
        }

        public void OnNext(T value)
        {
            var eventId = _state.NextEventId();
            var frame = JsonSerializer.Serialize(new
            {
                type = "event",
                subscriptionId = _subscriptionId,
                eventId,
                data = value,
            }, SleipnirJsonOptions.Default);
            // Drop zählt korrekt: DropOldest evictet (TryEnqueue liefert true, onDropped
            // gerufen), DropWrite verwirft (TryEnqueue liefert false, onDropped gerufen),
            // Block verliert nie, Unbounded verliert nie. Der frühere DropOldest-Channel
            // lieferte bei Sättigung immer true → onDropped war unreachable Dead Code.
            if (!_state.Buffer.TryEnqueue(frame, OnDropped))
            {
                // TryEnqueue hat bereits onDropped gerufen (DropWrite) bzw. false ohne Drop
                // bei Dispose/Block-Abbruch — kein doppelter Drop-Zähler.
            }
        }

        private void OnDropped()
        {
            _state.RecordDrop();
            SleipnirMetrics.EventDropped(_subscriptionId);
            _logger?.LogWarning("Event dropped for subscription {SubscriptionId} (buffer full)", _subscriptionId);
        }

        public void OnCompleted()
        {
            var frame = JsonSerializer.Serialize(new { type = "complete", subscriptionId = _subscriptionId }, SleipnirJsonOptions.Default);
            _state.Buffer.EnqueueTerminal(frame);
        }

        public void OnError(Exception error)
        {
            var frame = JsonSerializer.Serialize(new { type = "error", subscriptionId = _subscriptionId, message = error.Message }, SleipnirJsonOptions.Default);
            _state.Buffer.EnqueueTerminal(frame);
        }
    }
}