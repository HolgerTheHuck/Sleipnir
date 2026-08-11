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
/// (<c>{type:"event",subscriptionId,eventId,data}</c>) über einen bounded Channel +
/// Send-Loop, und räumt bei Disconnect automatisch auf.
/// </summary>
/// <remarks>
/// <para>
/// <b>Backpressure</b> (Entscheidung 7): pro Subscription ein bounded Channel
/// (Default-Cap aus <c>SleipnirOptions.EventBufferCapacity</c>,Fallback 100). Ist der
/// Channel voll, wird das älteste Element gedroppt und <c>sleipnir.event.dropped</c>
/// inkrementiert — deterministisch, DoS-sicher.
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
    private readonly ILogger? _logger;
    private readonly int _bufferCapacity;
    private readonly CancellationTokenSource _disposeCts = new();

    // subscriptionId → Subscription-State (Channel, eventId-Counter, IDisposable vom IObservable).
    private readonly ConcurrentDictionary<string, SubscriptionState> _subscriptions = new();

    // Ein Send-Loop pro Connection, der Event-Frames serialisiert auf den Socket schreibt
    // (WebSocket.SendAsync ist nicht thread-safe für konkurrierende Sends).
    private readonly Channel<string> _sendChannel;
    private readonly Task _sendLoopTask;

    public SleipnirSubscriptionManager(WebSocket webSocket, ISleipnirCore sleipnirCore, ILogger? logger, int bufferCapacity = 100)
    {
        _webSocket = webSocket;
        _sleipnirCore = sleipnirCore;
        _logger = logger;
        _bufferCapacity = bufferCapacity > 0 ? bufferCapacity : 100;
        // Hotfix 1.1.1: Kapazität war _bufferCapacity * _subscriptions.Count + 100, aber
        // _subscriptions ist im Ctor leer → fix 100. Korrekt: fester Sende-Puffer, der
        // unabhängig von Subscription-Anzahl skaliert (Events haben eigene per-Subscription-Buffer).
        _sendChannel = Channel.CreateBounded<string>(new BoundedChannelOptions(_bufferCapacity + 256)
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

        var state = new SubscriptionState(subscriptionId, _bufferCapacity);
        if (!_subscriptions.TryAdd(subscriptionId, state))
        {
            state.Dispose();
            return SleipnirResults.Error(SleipnirErrorCodes.Conflict, "Subscription ID collision — retry.", SleipnirCommon.Results.SleipnirErrorCategory.Conflict);
        }

        // Auf dem Observable subscribieren; jedes OnNext → Event-Frame in den Send-Channel.
        state.Disposable = observable.Subscribe(new EventObserver<object?>(state, subscriptionId, _logger));

        // Pump-Task: liest aus dem per-Subscription-Buffer und schreibt in den Send-Channel.
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var frame in state.Buffer.Reader.ReadAllAsync(_disposeCts.Token))
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
            state.Dispose();
        _subscriptions.Clear();

        try { await _sendLoopTask; } catch { /* ignore */ }
        _disposeCts.Dispose();
    }

    private sealed class SubscriptionState : IDisposable
    {
        public string SubscriptionId { get; }
        public Channel<string> Buffer { get; }
        public IDisposable? Disposable { get; set; }
        private long _eventIdCounter;

        public SubscriptionState(string subscriptionId, int bufferCapacity)
        {
            SubscriptionId = subscriptionId;
            Buffer = Channel.CreateBounded<string>(new BoundedChannelOptions(bufferCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,
            });
        }

        public long NextEventId() => Interlocked.Increment(ref _eventIdCounter);

        public void Dispose()
        {
            Disposable?.Dispose();
            Buffer.Writer.TryComplete();
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
            if (!_state.Buffer.Writer.TryWrite(frame))
            {
                SleipnirMetrics.EventDropped(_subscriptionId);
                _logger?.LogWarning("Event dropped for subscription {SubscriptionId} (buffer full)", _subscriptionId);
            }
        }

        public void OnCompleted()
        {
            var frame = JsonSerializer.Serialize(new { type = "complete", subscriptionId = _subscriptionId }, SleipnirJsonOptions.Default);
            _state.Buffer.Writer.TryWrite(frame);
            _state.Buffer.Writer.TryComplete();
        }

        public void OnError(Exception error)
        {
            var frame = JsonSerializer.Serialize(new { type = "error", subscriptionId = _subscriptionId, message = error.Message }, SleipnirJsonOptions.Default);
            _state.Buffer.Writer.TryWrite(frame);
            _state.Buffer.Writer.TryComplete();
        }
    }
}