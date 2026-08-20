using System.Collections.Concurrent;
using System.Text.Json;
using MessagePack;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using SleipnirCommon.Models;
// SleipnirException is provided via global using alias from GlobalUsings.cs

namespace SleipnirClient.Sleipnir;

/// <summary>
/// Ein Sleipnir-Client, der über SignalR mit dem Server kommuniziert.
/// Unterstützt automatische Wiederverbindung (SignalR-eigen) und asynchrone
/// Einzel- und Multi-Requests.
/// </summary>
public class SleipnirSignalrClient : SleipnirClientBase, ISleipnirClient, IAsyncDisposable
{
    private readonly HubConnection _connection;
    private bool _disposed;
    private string _jwtToken = string.Empty;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    // Repräsentiert einen laufenden Verbindungsversuch; konkurrierende Caller
    // erwarten dieselbe Task statt selbst StartAsync aufzurufen oder abzuweisen (B1).
    private Task<bool>? _connectingTask;

    // ─── Phase 4c: hub-streaming event subscriptions ───────────────────────
    // Active subscriptions keyed by subscriptionId. SignalR's WithAutomaticReconnect restores the
    // *connection* but NOT server streams — so on Reconnected we re-stream each non-done sub in
    // resume mode (subscriptionId + lastEventId → server replays the gap from its shared buffer,
    // cross-transport). A _reconnecting flag distinguishes a stream-end caused by a reconnect
    // (leave the sub for re-stream) from an unexpected end (fail it).
    private readonly ConcurrentDictionary<string, ISignalrSub> _activeSubs = new();
    private volatile bool _reconnecting;
    private static readonly JsonSerializerOptions FrameJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Erstellt einen neuen SignalR-Client. Optionaler <paramref name="bearer"/>
    /// (JWT) an zweiter Stelle, damit <c>new SleipnirSignalrClient(url, "token")</c>
    /// eindeutig den Bearer setzt (A4) und nicht mit <paramref name="hubPath"/> kollidiert.
    /// </summary>
    public SleipnirSignalrClient(string server,
        string? bearer = null,
        string? hubPath = "sleipnirhub", bool useMessagePack = true,
        TimeSpan? handshakeTimeout = null, TimeSpan? serverTimeout = null,
        TimeSpan? keepAliveInterval = null)
    {
        _jwtToken = bearer ?? string.Empty;

        // AccessTokenProvider reads _jwtToken lazily at call time, so a runtime swap via
        // SetBearer takes effect on subsequent invocations / reconnects without rebuilding
        // the HubConnection.
        var baseUrl = server.TrimEnd('/');
        if (!baseUrl.EndsWith('/'))
            baseUrl += "/";

        var builder = new HubConnectionBuilder()
            .WithUrl($"{baseUrl}{hubPath ?? "sleipnirhub"}", options =>
            {
                // AccessTokenProvider IMMER registrieren (A4): der Provider liest
                // das Token lazy zur Call-Zeit. _jwtToken ist oben bereits gesetzt.
                options.AccessTokenProvider = () =>
                    Task.FromResult<string?>(string.IsNullOrEmpty(_jwtToken) ? null : _jwtToken);
            });

        if (useMessagePack)
        {
            // Custom Resolver (server-seitig gespiegelt): JsonElement (SleipnirResponse.Data
            // seit dem Single-Pass-Fix) wird als native MessagePack-Tokens serialisiert —
            // keine Double-Wrapping-Tax. Gleicher Source wie SleipnirHub (je eigene MP-Version).
            builder.AddMessagePackProtocol(o =>
                o.SerializerOptions = MessagePackSerializerOptions.Standard
                    .WithResolver(SleipnirCommon.MessagePack.JsonElementResolver.Instance));
        }

        builder.WithAutomaticReconnect(new[]
        {
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)
        });

        _connection = builder.Build();

        if (handshakeTimeout.HasValue)
            _connection.HandshakeTimeout = handshakeTimeout.Value;
        if (serverTimeout.HasValue)
            _connection.ServerTimeout = serverTimeout.Value;
        if (keepAliveInterval.HasValue)
            _connection.KeepAliveInterval = keepAliveInterval.Value;

        // SignalR restores the connection on reconnect but NOT server streams — re-stream active
        // subscriptions once the hub is reachable again (resume mode → server replays the gap).
        _connection.Reconnecting += OnReconnectingAsync;
        _connection.Reconnected += OnReconnectedAsync;
    }

    /// <summary>
    /// Sendet einen einzelnen SleipnirRequest an den Server und gibt die Antwort zurück.
    /// </summary>
    public override async Task<SleipnirResponse?> Call(SleipnirRequest? request, CancellationToken ct = default)
    {
        if (request == null)
            return null;

        if (!await Connect())
            throw new SleipnirException("Not connected to server.");

        try
        {
            if (await _connection.InvokeCoreAsync(
                    "DoWork",
                    typeof(SleipnirResponse),
                    new object[] { request },
                    ct
                ) is SleipnirResponse r)
            {
                return r;
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new SleipnirException("Error from server.", ex);
        }
    }

    /// <summary>
    /// Sendet mehrere SleipnirRequests (Batch) an den Server und gibt die Antworten zurück.
    /// </summary>
    public override async Task<IEnumerable<SleipnirResponse?>?> Call(SleipnirMultiRequest? request, CancellationToken ct = default)
    {
        if (request == null)
            return null;

        if (!await Connect())
            throw new SleipnirException("Not connected to server.");

        try
        {
            if (await _connection.InvokeCoreAsync(
                    "DoWorkMany",
                    typeof(IEnumerable<SleipnirResponse?>),
                    new object[] { request },
                    ct
                ) is IEnumerable<SleipnirResponse?> r)
            {
                return r;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new SleipnirException("Error from server.", ex);
        }

        return new List<SleipnirResponse?>();
    }

    /// <summary>
    /// Baut die Verbindung zum Server auf. Ein laufender Verbindungsversuch wird
    /// von konkurrierenden Callern **gemeinsam erwartet** (B1) statt abgewiesen —
    /// parallele erste Calls werfen also nicht "Not connected". SignalRs
    /// <c>WithAutomaticReconnect</c> kümmert sich um Wiederverbindung; <c>.State</c>
    /// ist autoritativ, daher keine eigenen Lifecycle-Handler nötig.
    /// </summary>
    private Task<bool> Connect()
    {
        // Fast path: bereits verbunden.
        if (_connection.State == HubConnectionState.Connected)
            return Task.FromResult(true);

        // Ein Connect/Reconnect läuft bereits -> dieselbe Task mitwarten (B1),
        // statt sofort "Not connected" zu werfen.
        if (_connection.State is HubConnectionState.Connecting or HubConnectionState.Reconnecting
            && _connectingTask is not null)
        {
            return _connectingTask;
        }

        return ConnectSlowPathAsync();
    }

    private async Task<bool> ConnectSlowPathAsync()
    {
        await _connectLock.WaitAsync();
        try
        {
            // Double-Check nach Lock-Erwerb: ein anderer Caller hat evtl. schon
            // verbunden oder einen Versuch gestartet.
            if (_connection.State == HubConnectionState.Connected)
                return true;
            if (_connection.State is HubConnectionState.Connecting or HubConnectionState.Reconnecting
                && _connectingTask is not null)
            {
                return await _connectingTask;
            }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _connectingTask = tcs.Task;
            try
            {
                await _connection.StartAsync();
                tcs.TrySetResult(true);
                return true;
            }
            catch (HttpRequestException)
            {
                // Transienter Netzwerkfehler -> Caller kann es erneut versuchen.
                tcs.TrySetResult(false);
                return false;
            }
            catch (Exception ex)
            {
                var sleipnirEx = new SleipnirException("Error connecting to server via SignalR", ex);
                tcs.TrySetException(sleipnirEx);
                throw sleipnirEx;
            }
            finally
            {
                // Versuch beendet -> Feld freigeben, damit ein späterer Retry
                // (z.B. nach Closed) einen neuen Versuch starten kann.
                _connectingTask = null;
            }
        }
        finally
        {
            _connectLock.Release();
        }
    }

    // ─── Phase 4c: hub-streaming SubscribeAsync / ResumeAsync ──────────────

    /// <summary>
    /// Swap the JWT bearer at runtime. The <c>AccessTokenProvider</c> reads the token lazily at
    /// invocation time, so this takes effect on the next call / reconnect without rebuilding the
    /// <see cref="HubConnection"/>. Used by <see cref="SleipnirTransportRouter.SetBearer"/> fan-out.
    /// </summary>
    public void SetBearer(string? bearer) => _jwtToken = bearer ?? string.Empty;

    /// <summary>
    /// Subscribes to a server event via the hub stream
    /// <c>SubscribeAsync(request, null, null)</c> → <c>IAsyncEnumerable&lt;string&gt;</c>. The first
    /// stream item is the ack (<c>{type:"ack",subscriptionId,replayedFrom?}</c>); subsequent items are
    /// event/complete/error frames (the SAME serialized frames WS/SSE emit, reusing the shared durable
    /// store → cross-transport resume). Resolves with the <see cref="SleipnirSubscription{T}"/> once the
    /// ack arrives. On reconnect, <see cref="WithAutomaticReconnect"/> restores the connection but NOT
    /// the stream — the <c>Reconnected</c> handler re-streams each non-done sub in resume mode.
    /// </summary>
    public async override Task<SleipnirSubscription<T>> SubscribeAsync<T>(
        SleipnirRequest? request, ResumePolicy? resumePolicy = null, CancellationToken ct = default)
    {
        if (request == null)
            throw new ArgumentNullException(nameof(request));
        ThrowIfDisposed();
        if (!await Connect())
            throw new SleipnirException("Not connected to server.");

        var state = new SignalrSubState<T>(this) { Request = request, ResumePolicy = resumePolicy };
        state.InitAbort(ct);
        _ = Task.Run(state.InitialStreamAsync, state.AbortCts.Token);
        return await state.Completion.Task;
    }

    /// <summary>
    /// Resumes a durable subscription by <paramref name="subscriptionId"/> + <paramref name="lastEventId"/>
    /// via the hub stream <c>SubscribeAsync(null, subscriptionId, lastEventId)</c> — the server replays
    /// the gap from its shared buffer, then continues live. Cross-transport: a subscription created over
    /// WebSocket / SSE is resumable here.
    /// </summary>
    public async override Task<SleipnirSubscription<T>> ResumeAsync<T>(
        string subscriptionId, long lastEventId, ResumePolicy? resumePolicy = null, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(subscriptionId))
            throw new ArgumentException("subscriptionId is required.", nameof(subscriptionId));
        ThrowIfDisposed();
        if (!await Connect())
            throw new SleipnirException("Not connected to server.");

        var state = new SignalrSubState<T>(this)
        {
            ResumePolicy = resumePolicy,
            SubscriptionId = subscriptionId,
            LastEventId = lastEventId,
            ResumeOnly = true,
        };
        state.InitAbort(ct);
        // Pre-resolve the handle: a resume has no fresh request, so the ack re-confirms the id; the
        // caller gets the subscription immediately (the subject is live before the first frame).
        state.PreResolve();
        _activeSubs[subscriptionId] = state;
        _ = Task.Run(state.InitialStreamAsync, state.AbortCts.Token);
        return await state.Completion.Task;
    }

    private Task OnReconnectingAsync(Exception? ex)
    {
        _reconnecting = true;
        return Task.CompletedTask;
    }

    private Task OnReconnectedAsync(string? connectionId)
    {
        _reconnecting = false;
        // Re-stream each non-done sub in resume mode (server replays the gap from its shared buffer).
        foreach (var kv in _activeSubs)
        {
            if (!kv.Value.Done)
                _ = kv.Value.RestreamAsync();
        }
        return Task.CompletedTask;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SleipnirSignalrClient));
    }

    private static Exception Translate(Exception ex)
        => ex is SleipnirException ? ex : new SleipnirException("SignalR stream error.", ex);

    /// <summary>Non-generic surface so the reconnect handler can re-stream a sub of any T.</summary>
    private interface ISignalrSub
    {
        bool Done { get; }
        string SubscriptionId { get; }
        Task RestreamAsync();
        void Abort();
    }

    /// <summary>Per-subscription state: the ack TaskCompletionSource, the live cursor, the read loop.</summary>
    private sealed class SignalrSubState<T> : ISignalrSub
    {
        private readonly SleipnirSignalrClient _owner;
        public SleipnirRequest? Request;
        public ResumePolicy? ResumePolicy;
        public string SubscriptionId = "";
        public long LastEventId;
        public bool Done;
        public bool ResumeOnly;
        public SleipnirSubscription<T>? Subscription;
        public CancellationTokenSource? AbortCts;
        public TaskCompletionSource<SleipnirSubscription<T>> Completion { get; }
            = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SignalrSubState(SleipnirSignalrClient owner) => _owner = owner;

        bool ISignalrSub.Done => Done;
        string ISignalrSub.SubscriptionId => SubscriptionId;
        void ISignalrSub.Abort()
        {
            Done = true;
            try { AbortCts?.Cancel(); } catch { /* best-effort */ }
        }

        public void InitAbort(CancellationToken callerCt)
        {
            AbortCts = CancellationTokenSource.CreateLinkedTokenSource(callerCt);
            AbortCts.Token.Register(() => _owner._activeSubs.TryRemove(SubscriptionId, out _));
        }

        /// <summary>Resume-only: hand back the handle immediately; the ack re-confirms the id.</summary>
        public void PreResolve()
        {
            Subscription = new SleipnirSubscription<T>(SubscriptionId, UnsubscribeAsync, AbortCts!.Token);
            Completion.TrySetResult(Subscription);
        }

        public async Task InitialStreamAsync()
        {
            try
            {
                await StreamOnceAsync(isFresh: !ResumeOnly, AbortCts!.Token);
                // Clean stream end without a terminal frame and without a reconnect in flight → fail.
                if (!Done && !_owner._reconnecting && !AbortCts.IsCancellationRequested)
                    FailOrEnd(new SleipnirException("SignalR stream ended."));
            }
            catch (OperationCanceledException) when (AbortCts!.IsCancellationRequested) { /* unsubscribed */ }
            catch (HubException ex)
            {
                var sleipnirEx = new SleipnirException(ex.Message, ex);
                if (Subscription == null && !Completion.Task.IsCompleted)
                { Completion.TrySetException(sleipnirEx); Done = true; }
                else if (!_owner._reconnecting)
                { Subscription?.Subject.OnError(sleipnirEx); Done = true; }
                // if reconnecting, leave for Reconnected to re-stream
            }
            catch (Exception ex)
            {
                if (AbortCts!.IsCancellationRequested) return;
                if (_owner._reconnecting) return; // Reconnected will re-stream
                FailOrEnd(Translate(ex));
            }
        }

        public async Task RestreamAsync()
        {
            if (Done) return;
            try
            {
                if (_owner._connection.State != HubConnectionState.Connected)
                    if (!await _owner.Connect()) return;
                await StreamOnceAsync(isFresh: false, AbortCts!.Token);
                if (!Done && !_owner._reconnecting && !AbortCts.IsCancellationRequested)
                    FailOrEnd(new SleipnirException("SignalR stream ended."));
            }
            catch (OperationCanceledException) { /* aborted */ }
            catch (Exception ex)
            {
                if (AbortCts!.IsCancellationRequested) return;
                if (_owner._reconnecting) return; // next Reconnected re-streams
                FailOrEnd(Translate(ex));
            }
        }

        private void FailOrEnd(Exception ex)
        {
            if (Subscription == null && !Completion.Task.IsCompleted)
            { Completion.TrySetException(ex); Done = true; return; }
            Subscription?.Subject.OnError(ex);
            Done = true;
        }

        /// <summary>Opens one hub stream and reads frames until it ends or is cancelled.</summary>
        private async Task StreamOnceAsync(bool isFresh, CancellationToken ct)
        {
            object?[] args = isFresh
                ? new object?[] { Request!, (string?)null, (long?)null }
                : new object?[] { (SleipnirRequest?)null, SubscriptionId, (long?)LastEventId };

            var stream = _owner._connection.StreamAsync<string>("SubscribeAsync", args, ct);
            await foreach (var frame in stream)
            {
                if (ct.IsCancellationRequested || Done) return;
                HandleFrame(frame, isFresh);
                isFresh = false; // after the ack, remaining frames are events regardless
            }
        }

        private void HandleFrame(string frame, bool isFreshStream)
        {
            try
            {
                using var doc = JsonDocument.Parse(frame);
                var root = doc.RootElement;
                var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                switch (type)
                {
                    case "ack":
                        if (root.TryGetProperty("subscriptionId", out var sid)
                            && sid.ValueKind == JsonValueKind.String)
                        {
                            var newId = sid.GetString();
                            if (!string.IsNullOrEmpty(newId))
                            {
                                if (string.IsNullOrEmpty(SubscriptionId))
                                    SubscriptionId = newId!;
                                long? replayedFrom = root.TryGetProperty("replayedFrom", out var rf)
                                    && rf.ValueKind == JsonValueKind.Number ? rf.GetInt64() : (long?)null;
                                // Resume that returns no replayedFrom = server degraded to fresh → reset cursor.
                                if (!isFreshStream && replayedFrom == null)
                                    LastEventId = 0;
                            }
                        }
                        if (Subscription == null)
                        {
                            Subscription = new SleipnirSubscription<T>(SubscriptionId, UnsubscribeAsync, AbortCts!.Token);
                            _owner._activeSubs[SubscriptionId] = this;
                            Completion.TrySetResult(Subscription);
                        }
                        break;
                    case "event":
                        long? evId = root.TryGetProperty("eventId", out var eid)
                            && eid.ValueKind == JsonValueKind.Number ? eid.GetInt64() : (long?)null;
                        if (evId.HasValue)
                        {
                            if (evId.Value <= LastEventId) break; // replay duplicate
                            LastEventId = evId.Value;
                        }
                        if (root.TryGetProperty("data", out var dataEl))
                        {
                            try
                            {
                                var value = dataEl.Deserialize<T>(FrameJsonOptions);
                                Subscription?.Subject.OnNext(value!);
                            }
                            catch (Exception ex) { Subscription?.Subject.OnError(ex); }
                        }
                        break;
                    case "complete":
                        Done = true;
                        Subscription?.Subject.OnCompleted();
                        break;
                    case "error":
                        Done = true;
                        var msg = root.TryGetProperty("message", out var mp)
                            && mp.ValueKind == JsonValueKind.String
                            ? mp.GetString() ?? "Subscription error" : "Subscription error";
                        Subscription?.Subject.OnError(new SleipnirException(msg));
                        break;
                }
            }
            catch (Exception ex)
            {
                Subscription?.Subject.OnError(ex);
            }
        }

        private Task UnsubscribeAsync(string subscriptionId, CancellationToken ct)
        {
            Done = true;
            _owner._activeSubs.TryRemove(subscriptionId, out _);
            try { AbortCts?.Cancel(); } catch { /* best-effort */ }
            // The server stream ends when the [EnumeratorCancellation] token fires; the hub `finally`
            // detaches the durable tap / disposes the ephemeral state. There is no separate unsubscribe RPC.
            return Task.CompletedTask;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        // End active subscriptions (cancel their streams → server-side cleanup via the hub finally).
        foreach (var kv in _activeSubs)
            kv.Value.Abort();
        _activeSubs.Clear();

        _connection.Reconnecting -= OnReconnectingAsync;
        _connection.Reconnected -= OnReconnectedAsync;

        if (_connection.State == HubConnectionState.Connected)
        {
            await _connection.StopAsync();
        }

        await _connection.DisposeAsync();
        _connectLock.Dispose();
    }
}