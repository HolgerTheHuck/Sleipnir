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
    private readonly SleipnirSubscriptionStore _store;
    private readonly ILogger? _logger;
    private readonly int _defaultBufferCapacity;
    private readonly EventBackpressureStrategy _defaultStrategy;
    private readonly CancellationTokenSource _disposeCts = new();

    // subscriptionId → Subscription-State (Channel, eventId-Counter, IDisposable vom IObservable).
    private readonly ConcurrentDictionary<string, SubscriptionState> _subscriptions = new();

    // Phase R: durable subscription ids currently attached to THIS connection. The real
    // state lives in the SleipnirSubscriptionStore (process-wide); this set is only for
    // disconnect cleanup (Detach — keep source + buffer for resume). Ephemeral subscriptions
    // stay in _subscriptions above; durable ones live here + in the store.
    private readonly ConcurrentDictionary<string, byte> _attachedDurable = new();

    // Ein Send-Loop pro Connection, der Event-Frames serialisiert auf den Socket schreibt
    // (WebSocket.SendAsync ist nicht thread-safe für konkurrierende Sends).
    private readonly Channel<string> _sendChannel;
    private readonly Task _sendLoopTask;

    public SleipnirSubscriptionManager(
        WebSocket webSocket,
        ISleipnirCore sleipnirCore,
        SleipnirConnectionRegistry connectionRegistry,
        SleipnirSubscriptionStore store,
        ILogger? logger,
        int bufferCapacity = 100,
        EventBackpressureStrategy backpressureStrategy = EventBackpressureStrategy.DropOldest)
    {
        _webSocket = webSocket;
        _sleipnirCore = sleipnirCore;
        _connectionRegistry = connectionRegistry;
        _store = store;
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
    public async Task<SleipnirResponse?> HandleSubscribeAsync(
        SleipnirRequest request, HttpContext? context, CancellationToken ct,
        long? lastEventId = null, string? resumeSubscriptionId = null)
    {
        // ── Phase R: resume path ──────────────────────────────────────────────
        // A resume carries the durable subscriptionId + the last eventId the client processed.
        // If the durable state still lives in the store, re-attach a live tap and replay the
        // gap. If it has been GC'd (TTL expired) or was never resumable, fall through to a
        // fresh subscribe (degrade — documented). R1 does not re-check auth here (R3 wires
        // the reconnect auth re-check; safe because no client resumes until R2 ships the hook).
        if (!string.IsNullOrEmpty(resumeSubscriptionId)
            && _store.Lookup(resumeSubscriptionId!) is { } existingState)
        {
            var tap = existingState.Attach(lastEventId ?? 0);
            _attachedDurable[tap.SubscriptionId] = 1;
            _store.OnAttached();   // gauge: a client is (re)attached to this durable subscription
            StartDurablePump(tap, ct);
            return new SleipnirResponse
            {
                Code = SleipnirErrorCodes.Ok,
                Data = JsonSerializer.SerializeToElement(
                    new { subscriptionId = tap.SubscriptionId, replayedFrom = tap.ReplayedFrom },
                    SleipnirJsonOptions.Default),
                Id = request.Id,
            };
        }

        // ── Fresh subscribe path ──────────────────────────────────────────────
        var result = await _sleipnirCore.SubscribeAsync(request, context, ct);
        if (result.Error != null)
            return result.Error;

        // Phase R: resumable events go to the durable store (source kept alive across
        // disconnects, replay ring buffer, stable subscriptionId). Non-resumable events keep
        // the v1 ephemeral per-connection path unchanged.
        if (result.Resumable)
            return await CreateDurableAsync(request, result, ct);

        return CreateEphemeral(request, result);
    }

    /// <summary>Fresh durable subscribe: register state, subscribe the observer, attach the tap.</summary>
    private async Task<SleipnirResponse?> CreateDurableAsync(SleipnirRequest request, SleipnirSubscribeResult result, CancellationToken ct)
    {
        var observable = result.Observable!;
        var state = _store.BeginCreate(result.EventBackpressureStrategy);
        if (state == null)
            return SleipnirResults.Error(SleipnirErrorCodes.ServiceUnavailable, "Durable subscription cap reached — retry later.",
                SleipnirCommon.Results.SleipnirErrorCategory.ResourceExhausted);

        // Subscribe the observer FIRST so events produced before Attach land in the ring
        // buffer (the attach snapshot then replays them — no lost events on the create path).
        state.SourceSubscription = observable.Subscribe(new DurableEventObserver<object?>(state, _logger));

        var tap = state.Attach(0);
        _attachedDurable[tap.SubscriptionId] = 1;
        _store.OnAttached();
        StartDurablePump(tap, ct);

        return new SleipnirResponse
        {
            Code = SleipnirErrorCodes.Ok,
            Data = JsonSerializer.SerializeToElement(new { subscriptionId = tap.SubscriptionId }, SleipnirJsonOptions.Default),
            Id = request.Id,
        };
    }

    /// <summary>Durable live-tap pump: drains the tap (replayed + live frames) into the send channel.</summary>
    private void StartDurablePump(Tap tap, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var frame in tap.Reader.ReadAllAsync(_disposeCts.Token))
                {
                    await _sendChannel.Writer.WriteAsync(frame, _disposeCts.Token);
                }
            }
            catch (OperationCanceledException) { /* Dispose/disconnect */ }
            catch (Exception ex) { _logger?.LogError(ex, "Durable pump task failed for subscription {SubscriptionId}", tap.SubscriptionId); }
        }, _disposeCts.Token);
    }

    /// <summary>Fresh ephemeral subscribe (v1 path, non-resumable events): per-connection state + buffer.</summary>
    private SleipnirResponse CreateEphemeral(SleipnirRequest request, SleipnirSubscribeResult result)
    {
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
    /// Verarbeitet einen Unsubscribe-Request: disposed die Subscription (ephemeral) bzw.
    /// destroyed die durable Subscription (Phase R — explicit unsubscribe tears down the
    /// source + replay buffer; a disconnect-only Detach would keep it for resume).
    /// </summary>
    public Task<SleipnirResponse?> HandleUnsubscribeAsync(string subscriptionId, string? requestId, CancellationToken ct)
    {
        // Phase R: durable unsubscribe — destroy the source + buffer (the client opted out).
        // store.Destroy decrements the gauge iff a tap was still attached.
        if (_attachedDurable.TryRemove(subscriptionId, out _))
        {
            _store.Destroy(subscriptionId);
            return Task.FromResult<SleipnirResponse?>(new SleipnirResponse { Code = SleipnirErrorCodes.Ok, Id = requestId ?? string.Empty });
        }

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

        // Alle ephemeral Subscriptions disposed.
        foreach (var state in _subscriptions.Values)
        {
            state.Dispose();
            // Observability: each subscription still in the dict at disconnect ends here.
            // (Those already removed via explicit unsubscribe are gone — no double decrement.)
            _connectionRegistry.DecSubscription();
        }
        _subscriptions.Clear();

        // Phase R: durable subscriptions are DETACHED, not destroyed — the source subscription
        // + replay ring buffer persist in the process-wide store for a resume on reconnect.
        // store.Detach decrements the gauge (symmetric with OnAttached at subscribe/resume).
        foreach (var durableId in _attachedDurable.Keys)
            _store.Detach(durableId);
        _attachedDurable.Clear();

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

    /// <summary>
    /// IObserver implementation for <b>durable</b> subscriptions (Phase R): serializes
    /// each event frame with the <see cref="DurableSubscriptionState"/>-owned monotonic
    /// <c>eventId</c> (stable across reconnects) and forwards it to the store state
    /// (replay ring buffer + optional live tap). OnCompleted/OnError are recorded as a
    /// terminal frame (replayed on resume) and forwarded to the live tap.
    /// </summary>
    private sealed class DurableEventObserver<T> : IObserver<T>
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
            var frame = JsonSerializer.Serialize(new
            {
                type = "event",
                subscriptionId = _state.SubscriptionId,
                eventId,
                data = value,
            }, SleipnirJsonOptions.Default);
            // AppendEvent records into the replay ring buffer (evict-oldest on cap → drop
            // counter via the store) AND forwards to the attached live tap, if any. With no
            // tap (disconnected) the frame lives only in the ring buffer → replayed on resume.
            _state.AppendEvent(eventId, frame);
        }

        public void OnCompleted()
        {
            var frame = JsonSerializer.Serialize(new { type = "complete", subscriptionId = _state.SubscriptionId }, SleipnirJsonOptions.Default);
            _state.SetTerminal(frame);
        }

        public void OnError(Exception error)
        {
            var frame = JsonSerializer.Serialize(new { type = "error", subscriptionId = _state.SubscriptionId, message = error.Message }, SleipnirJsonOptions.Default);
            _state.SetTerminal(frame);
            _logger?.LogError(error, "Durable event source errored for subscription {SubscriptionId}", _state.SubscriptionId);
        }
    }
}