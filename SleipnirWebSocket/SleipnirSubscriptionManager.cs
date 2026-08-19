using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Net.WebSockets;
using SleipnirCommon.Models;
using SleipnirCommon.Results;
using SleipnirCore.Events;
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

    // JSON options shared with the WS middleware (include the SleipnirResponseJsonConverter —
    // explicit nulls, fixed field order). Used to serialize the subscribe-ack HERE so the ack is
    // byte-identical to what the middleware would have sent, while letting the manager enqueue it
    // BEFORE the event pump starts (ack-before-first-frame invariant). See CreateEphemeralAsync /
    // CreateDurableAsync / the resume path.
    private readonly JsonSerializerOptions _jsonOptions;

    // subscriptionId → Subscription-State (Channel, eventId-Counter, IDisposable vom IObservable).
    private readonly ConcurrentDictionary<string, EphemeralSubscriptionState> _subscriptions = new();

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
        JsonSerializerOptions jsonOptions,
        ILogger? logger,
        int bufferCapacity = 100,
        EventBackpressureStrategy backpressureStrategy = EventBackpressureStrategy.DropOldest)
    {
        _webSocket = webSocket;
        _sleipnirCore = sleipnirCore;
        _connectionRegistry = connectionRegistry;
        _store = store;
        _jsonOptions = jsonOptions;
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
        long? lastEventId = null, string? resumeSubscriptionId = null, string? correlationId = null)
    {
        // ── Phase R: resume path ──────────────────────────────────────────────
        // A resume carries the durable subscriptionId + the last eventId the client processed.
        // If the durable state still lives in the store, re-attach a live tap and replay the
        // gap. If it has been GC'd (TTL expired) or was never resumable, fall through to a
        // fresh subscribe (degrade — documented).
        if (!string.IsNullOrEmpty(resumeSubscriptionId)
            && _store.Lookup(resumeSubscriptionId!) is { } existingState)
        {
            // Phase R3: re-run the SAME authorization a fresh subscribe runs, against the
            // ORIGINAL controller/method recorded at create time (NOT the client-claimed route —
            // a caller cannot lie about the route to land a weaker auth check). A role revoked
            // during the disconnect gap must not silently resume. On 401/403 (or 404 if the
            // route vanished) tear down the durable subscription and return the error.
            var authError = await _sleipnirCore.AuthorizeSubscribeAsync(
                existingState.Controller!, existingState.Method!, context);
            if (authError != null)
            {
                _store.Destroy(resumeSubscriptionId!);
                return authError;
            }

            var tap = existingState.Attach(lastEventId ?? 0);
            _attachedDurable[tap.SubscriptionId] = 1;
            _store.OnAttached();   // gauge: a client is (re)attached to this durable subscription
            // Ack BEFORE the pump: enqueue the subscribe-ack on the send channel before any
            // replayed-gap / live frame can be drained, so the ack is always sent first for this
            // subscription (frame-ordering invariant). Returns null → the middleware skips its
            // own enqueue. See EnqueueSubscribeAckAsync for the wire-format rationale.
            await EnqueueSubscribeAckAsync(tap.SubscriptionId, tap.ReplayedFrom, request.Id, correlationId, ct);
            StartDurablePump(tap, ct);
            return null;
        }

        // ── Fresh subscribe path ──────────────────────────────────────────────
        var result = await _sleipnirCore.SubscribeAsync(request, context, ct);
        if (result.Error != null)
            return result.Error;

        // Phase R: resumable events go to the durable store (source kept alive across
        // disconnects, replay ring buffer, stable subscriptionId). Non-resumable events keep
        // the v1 ephemeral per-connection path unchanged.
        if (result.Resumable)
            return await CreateDurableAsync(request, result, correlationId, ct);

        return await CreateEphemeralAsync(request, result, correlationId);
    }

    /// <summary>
    /// Builds the subscribe-ack (<c>{subscriptionId[, replayedFrom]}</c>) and enqueues it on the
    /// send channel <b>before</b> the event pump starts — the ack is therefore guaranteed to
    /// precede any event frame for this subscription on the wire (cold-observable /
    /// replay-snapshot frame-ordering invariant). Serialized with the middleware's JSON options
    /// (<see cref="SleipnirResponseJsonConverter"/>: explicit nulls, fixed field order) so the
    /// bytes are identical to what the middleware would have produced. The <c>Data</c> element is
    /// built with <see cref="SleipnirJsonOptions.Default"/> (<c>WhenWritingNull</c>) as before, so
    /// a null <paramref name="replayedFrom"/> is omitted (fresh subscribe) and a present one is
    /// written (resume). <paramref name="requestId"/>/<paramref name="correlationId"/> replicate
    /// the middleware's <c>Id</c> fallback (<c>request.Id ?? envelopeId ?? ""</c>).
    /// </summary>
    private async Task EnqueueSubscribeAckAsync(
        string subscriptionId, long? replayedFrom, string? requestId, string? correlationId, CancellationToken ct)
    {
        var ack = new SleipnirResponse
        {
            Code = SleipnirErrorCodes.Ok,
            Data = JsonSerializer.SerializeToElement(
                new { subscriptionId, replayedFrom }, SleipnirJsonOptions.Default),
            Id = requestId ?? correlationId ?? string.Empty,
        };
        await EnqueueSendAsync(JsonSerializer.Serialize(ack, _jsonOptions), ct);
    }

    /// <summary>Fresh durable subscribe: register state, subscribe the observer, attach the tap.</summary>
    private async Task<SleipnirResponse?> CreateDurableAsync(
        SleipnirRequest request, SleipnirSubscribeResult result, string? correlationId, CancellationToken ct)
    {
        var observable = result.Observable!;
        var state = _store.BeginCreate(result.EventBackpressureStrategy);
        if (state == null)
            return SleipnirResults.Error(SleipnirErrorCodes.ServiceUnavailable, "Durable subscription cap reached — retry later.",
                SleipnirCommon.Results.SleipnirErrorCategory.ResourceExhausted);

        // Record the ORIGINAL route so a reconnect resume can re-run authorization against the
        // real event (not a client-claimed route). Set before the source is subscribed so the
        // state is consistent even if an event arrives synchronously on Subscribe.
        state.Controller = request.Controller;
        state.Method = request.Method;

        // Subscribe the observer FIRST so events produced before Attach land in the ring
        // buffer (the attach snapshot then replays them — no lost events on the create path).
        state.SourceSubscription = observable.Subscribe(new DurableEventObserver<object?>(state, _logger));

        var tap = state.Attach(0);
        _attachedDurable[tap.SubscriptionId] = 1;
        _store.OnAttached();
        // Ack BEFORE the pump: enqueue the subscribe-ack before the durable pump can drain the
        // replay snapshot / live tap, so the ack is sent first (frame-ordering invariant).
        // Returns null → the middleware skips its own enqueue.
        await EnqueueSubscribeAckAsync(tap.SubscriptionId, replayedFrom: null, request.Id, correlationId, ct);
        StartDurablePump(tap, ct);
        return null;
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
    private async Task<SleipnirResponse?> CreateEphemeralAsync(
        SleipnirRequest request, SleipnirSubscribeResult result, string? correlationId)
    {
        var observable = result.Observable!;
        var subscriptionId = Guid.NewGuid().ToString("N");

        // Backpressure pro-Subscription aus dem aufgelösten Subscribe-Ergebnis (Per-Event-
        // Override ?? globale Option, bereits im Invoker aufgelöst); Fallback auf die
        // Manager-Defaults (reiner Safety-Net, regulärer Pfad liefert immer konkrete Werte).
        var capacity = result.EventBufferCapacity > 0 ? result.EventBufferCapacity : _defaultBufferCapacity;
        var strategy = result.EventBackpressureStrategy;
        var state = new EphemeralSubscriptionState(subscriptionId, capacity, strategy, _disposeCts.Token);
        if (!_subscriptions.TryAdd(subscriptionId, state))
        {
            state.Dispose();
            return SleipnirResults.Error(SleipnirErrorCodes.Conflict, "Subscription ID collision — retry.", SleipnirCommon.Results.SleipnirErrorCategory.Conflict);
        }

        // Observability: count the now-active subscription (process-wide gauge + JSON snapshot).
        _connectionRegistry.IncSubscription();

        // Auf dem Observable subscribieren; jedes OnNext → Event-Frame in den per-Subscription-
        // Buffer. Subscribe stays BEFORE the ack enqueue so synchronous-cold frames land in the
        // buffer (no event is lost) — only the pump (which drains the buffer onto the wire) is
        // deferred until after the ack is enqueued, which guarantees ack-before-first-frame
        // without opening a hot-observable event-loss window.
        state.Disposable = observable.Subscribe(new EventObserver<object?>(state, subscriptionId, _logger));

        // Ack BEFORE the pump: enqueue the subscribe-ack on the send channel before the pump
        // starts draining the per-subscription buffer, so the ack is always sent first for this
        // subscription (frame-ordering invariant — fixes the cold-observable race where a sync
        // cold observable's frames could reach the wire before the ack). Returns null → the
        // middleware skips its own enqueue.
        await EnqueueSubscribeAckAsync(subscriptionId, replayedFrom: null, request.Id, correlationId, CancellationToken.None);

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

        return null;
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

}