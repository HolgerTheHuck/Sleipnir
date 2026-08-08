using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TrameCommon.Models;

namespace TrameClient.Trame;

/// <summary>
/// Trame client over plain WebSockets (RFC 6455) with ID-based
/// request/response correlation for parallel calls.
///
/// Auto-Reconnect: when the connection drops unexpectedly (server close,
/// transport error), the client reconnects automatically in the background
/// with exponential backoff (mirrors SignalR's <c>WithAutomaticReconnect</c>).
/// In-flight calls on the drop are rejected (SignalR parity); new
/// calls during a reconnect wait on the same in-flight connection.
/// An explicit <see cref="DisposeAsync"/> is terminal — no reconnect.
/// </summary>
public class TrameWebSocketClient : TrameClientBase, ITrameClient, IAsyncDisposable
{
    /// <summary>
    /// Default backoff intervals (SignalR mirror): 2,2,5,5,10,10,30,30s,
    /// 1min,1min,5min. After the last interval the reconnect gives up
    /// (<see cref="TrameConnectionState.Disconnected"/>).
    /// </summary>
    public static readonly TimeSpan[] DefaultReconnectDelays =
    {
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
    };

    private ClientWebSocket _webSocket;
    private readonly Uri _uri;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly ConcurrentDictionary<string, ITcsHolder> _pendingRequests = new();
    private readonly ConcurrentDictionary<string, ITrameSubscriptionHandler> _subscriptions = new();
    private readonly ConcurrentDictionary<string, SubscribeRequestRecord> _subscribeRequests = new();
    private readonly ILogger<TrameWebSocketClient>? _logger;

    private readonly Func<ClientWebSocket>? _socketFactory;
    private readonly bool _autoReconnect;
    private readonly TimeSpan[] _reconnectDelays;
    private readonly Action<TrameConnectionState>? _onStateChanged;

    private readonly TimeSpan? _callTimeout;
    private CancellationTokenSource? _readerCts;
    private Task? _readerTask;
    private int _requestCounter;

    private TrameConnectionState _state = TrameConnectionState.Disconnected;
    private Task? _reconnectTask;
    private CancellationTokenSource? _reconnectCts;
    // Socket currently in-flight inside ConnectAsync (lazy connect or reconnect). DisposeAsync
    // aborts it so a ConnectAsync that does not promptly honor its CancellationToken (e.g. a hung
    // WebSocket upgrade against a 400 rejection on Linux/ManagedWebSocket) cannot block the
    // await on the reconnect task and deadlock disposal.
    private ClientWebSocket? _connectingSocket;
    private readonly object _connectGate = new();
    private bool _disposed;

    /// <summary>
    /// Non-generic interface to store TaskCompletionSource instances of different types.
    /// SetResult takes an already-parsed result object (TrameResponse or
    /// List&lt;TrameResponse?&gt;) — parsing happens centrally in DispatchResponse via
    /// TrameResponseParser (a single pass, DataBytes instead of a JsonDocument tree),
    /// no longer per Holder via JsonSerializer.Deserialize(string).
    /// </summary>
    private interface ITcsHolder
    {
        void SetResult(object result);
        void SetException(Exception ex);
        void SetCanceled(CancellationToken cancellationToken);
    }

    private sealed class TcsHolder<T> : ITcsHolder
    {
        public readonly TaskCompletionSource<T> Tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void SetResult(object result) => Tcs.TrySetResult((T)result!);
        public void SetException(Exception ex) => Tcs.TrySetException(ex);
        // R4: TrySetCanceled (not SetCanceled) — the non-Try version races the reader thread's
        // TrySetResult and the loser throws an unobserved InvalidOperationException inside a
        // thread-pool cancellation callback, which can terminate the process. Try* mirrors
        // SetResult/SetException and silently no-ops on a already-completed TCS. Pass the token
        // so the resulting OperationCanceledException.CancellationToken is faithful.
        public void SetCanceled(CancellationToken cancellationToken) => Tcs.TrySetCanceled(cancellationToken);
    }

    public TrameWebSocketClient(string serverBaseUrl, ClientWebSocket? webSocket = null,
        string? wsPath = "tramews", TimeSpan? callTimeout = null,
        ILogger<TrameWebSocketClient>? logger = null,
        bool autoReconnect = true, TimeSpan[]? reconnectDelays = null,
        Func<ClientWebSocket>? socketFactory = null,
        Action<TrameConnectionState>? onStateChanged = null)
        : base()
    {
        if (string.IsNullOrWhiteSpace(serverBaseUrl))
            throw new ArgumentException("Server URL must not be empty.", nameof(serverBaseUrl));

        var baseUrl = serverBaseUrl.TrimEnd('/');
        var wsScheme = baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";
        var hostAndPath = baseUrl.Substring(baseUrl.IndexOf("://", StringComparison.Ordinal) + 3);
        var path = (wsPath ?? "tramews").Trim('/');
        _uri = new Uri($"{wsScheme}://{hostAndPath}/{path}");

        _socketFactory = socketFactory;
        _webSocket = webSocket ?? CreateSocket();
        _callTimeout = callTimeout;
        _logger = logger;

        _autoReconnect = autoReconnect;
        _reconnectDelays = reconnectDelays ?? DefaultReconnectDelays;
        if (_reconnectDelays.Length == 0)
            _autoReconnect = false; // empty array explicitly disables reconnect
        _onStateChanged = onStateChanged;
    }

    /// <summary>Current connection state (observer surface for UI/logs).</summary>
    public TrameConnectionState State => _state;

    /// <summary>
    /// Establishes the WebSocket connection and starts the background reader.
    /// A closed/aborted socket is replaced with a fresh one first (closed
    /// sockets are not reusable).
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(TrameWebSocketClient));

        if (_webSocket.State == WebSocketState.Open)
            return;

        // A closed/aborted socket cannot be reused → create a fresh one.
        if (_webSocket.State is WebSocketState.Closed or WebSocketState.Aborted)
        {
            var dead = _webSocket;
            _webSocket = CreateSocket();
            try { dead.Dispose(); } catch { /* ignore */ }
        }

        SetState(TrameConnectionState.Connecting);
        try
        {
            lock (_connectGate)
                _connectingSocket = _webSocket;
            await _webSocket.ConnectAsync(_uri, ct);
        }
        catch
        {
            SetState(TrameConnectionState.Disconnected);
            throw;
        }
        finally
        {
            lock (_connectGate)
                _connectingSocket = null;
        }

        StartReader();
        SetState(TrameConnectionState.Connected);
    }

    public override async Task<TrameResponse?> Call(TrameRequest? request, CancellationToken ct = default)
    {
        if (request == null)
            return null;

        await EnsureConnectedAsync(ct);
        return await SendAndAwaitResponseAsync<TrameResponse?>(request, ct);
    }

    public override async Task<IEnumerable<TrameResponse?>?> Call(TrameMultiRequest? request, CancellationToken ct = default)
    {
        if (request == null)
            return null;

        // Note: requests that arrive without an id are assigned one in place (caller-owned
        // mutation) before serialization, so the server-echoed id can be correlated back to
        // the pending call — see SendAndAwaitResponseAsync (R3).

        await EnsureConnectedAsync(ct);
        return await SendAndAwaitResponseAsync<List<TrameResponse?>>(request, ct);
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(TrameWebSocketClient));
        if (_webSocket.State == WebSocketState.Open)
            return;

        // Is a background reconnect running? Wait on it (do not connect ourselves)
        // so parallel calls share the same in-flight reconnect.
        var reconnect = _reconnectTask;
        if (reconnect != null && _state == TrameConnectionState.Reconnecting)
        {
            try { await reconnect.WaitAsync(ct); }
            catch (OperationCanceledException) { /* ct canceled — re-evaluate below */ }
            if (_webSocket.State == WebSocketState.Open)
                return;
        }

        if (_webSocket.State == WebSocketState.Open)
            return;

        // Connect race (B2): only one caller may connect, the rest wait and see Open.
        await _connectLock.WaitAsync(ct);
        try
        {
            if (_webSocket.State == WebSocketState.Open)
                return;
            await ConnectAsync(ct);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private string NextId()
    {
        var n = Interlocked.Increment(ref _requestCounter);
        return $"ws-{n}-{Guid.NewGuid():N}";
    }

    private async Task<T?> SendAndAwaitResponseAsync<T>(object payload, CancellationToken ct)
    {
        // Assign a unique ID to the request for correlation
        string requestId;
        if (payload is TrameRequest hr)
        {
            requestId = string.IsNullOrEmpty(hr.Id) ? NextId() : hr.Id;
            hr.Id = requestId;
        }
        else if (payload is TrameMultiRequest mr)
        {
            // R3: a TrameMultiRequest without ids hung forever — NextId() was generated but
            // never written into any request, so the server echoed "" and the strict
            // dispatcher dropped the response (with callTimeout=null → infinite wait). Mirror
            // the REST client: assign an id to every request that lacks one before serializing.
            // The server echoes per-request ids; the batch response array is correlated by the
            // first element's id (see ParseMessage), which now matches the stored requestId.
            // Note: this mutates the caller-owned request objects — acceptable, documented on Call.
            if (mr.Requests != null)
            {
                foreach (var r in mr.Requests)
                {
                    if (string.IsNullOrEmpty(r.Id))
                        r.Id = NextId();
                }
            }
            requestId = mr.Requests?.FirstOrDefault()?.Id ?? NextId();
        }
        else
        {
            requestId = NextId();
        }

        var holder = new TcsHolder<T>();
        _pendingRequests[requestId] = holder;

        // Call timeout: a linked CTS that cancels both the send and the wait.
        CancellationTokenSource? timeoutCts = null;
        CancellationTokenSource? linkedCts = null;
        try
        {
            var effectiveCt = ct;
            if (_callTimeout is { } timeout)
            {
                timeoutCts = new CancellationTokenSource(timeout);
                linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                effectiveCt = linkedCts.Token;
            }

            var json = JsonSerializer.Serialize(payload, JsonOptions);
            var bytes = Encoding.UTF8.GetBytes(json);

            // Only serialize sends — receives are handled by the background reader
            await _sendLock.WaitAsync(effectiveCt);
            try
            {
                await _webSocket.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    effectiveCt);
            }
            finally
            {
                _sendLock.Release();
            }

            // Wait for the matching response
            using var reg = effectiveCt.Register(() => holder.SetCanceled(effectiveCt));
            return await holder.Tcs.Task;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Unified error surface (C1): transport errors surface as TrameException.
            throw new TrameException("WebSocket transport error.", ex);
        }
        finally
        {
            _pendingRequests.TryRemove(requestId, out _);
            linkedCts?.Dispose();
            timeoutCts?.Dispose();
        }
    }

    /// <summary>
    /// Background loop that reads all WebSocket messages and dispatches
    /// them to the correct pending request by matching the response ID.
    /// On termination (close/error) all pending calls are rejected and a
    /// background reconnect is triggered (except on dispose).
    /// </summary>
    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 4);
        Exception? terminalError = null;

        try
        {
            while (_webSocket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                // Accumulate bytes (do not decode per chunk) — otherwise multi-byte
                // characters at chunk boundaries get corrupted (A2).
                using var messageBuffer = new MemoryStream();

                WebSocketReceiveResult result;
                do
                {
                    result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await _webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Ack", CancellationToken.None);
                        terminalError = new WebSocketException("WebSocket connection closed by server.");
                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Text && result.Count > 0)
                    {
                        messageBuffer.Write(buffer, 0, result.Count);
                    }
                }
                while (!result.EndOfMessage);

                if (terminalError != null)
                    break;

                if (messageBuffer.Length == 0)
                    continue;

                // Raw bytes via the live MemoryStream buffer (no string intermediate,
                // no copy — the parser only copies the DataBytes slice it keeps).
                // The MemoryStream lives until the end of the iteration (using var),
                // i.e. beyond the synchronous dispatch call.
                var messageBytes = messageBuffer.GetBuffer().AsMemory(0, (int)messageBuffer.Length);

                DispatchResponse(messageBytes);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown (Dispose) — no reconnect.
        }
        catch (Exception ex)
        {
            terminalError = ex;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        // Reject all pending calls (SignalR mirror: in-flight throws on drop).
        CancelAllPending(terminalError ?? new WebSocketException("WebSocket connection closed."));

        // Unexpected termination (not dispose) → background reconnect.
        StartReconnect();
    }

    /// <summary>
    /// Parses the response bytes ONCE via <see cref="TrameResponseParser"/> (captures
    /// ID, envelope and DataBytes in a single pass — no JsonDocument tree), extracts
    /// the correlation ID and resolves the matching pending call. Single responses
    /// correlate by <c>id</c>; batch responses (JSON array) by the ID of the first
    /// element (the server sends per-request IDs since v1).
    /// </summary>
    private void DispatchResponse(ReadOnlyMemory<byte> messageBytes)
    {
        // Phase 3: detect event frames ({type:"event"/"complete"/"error",...}) first.
        // They have no "code" field and correlate by subscriptionId, not by call ID.
        if (TryDispatchEventFrame(messageBytes))
            return;

        try
        {
            var (result, responseId) = ParseMessage(messageBytes);

            if (responseId != null && _pendingRequests.TryGetValue(responseId, out var holder))
            {
                holder.SetResult(result);
                return;
            }

            // No match -> do NOT guess (B3: silent misattribution). Drop + log;
            // the waiting caller aborts via its CancellationToken.
            _logger?.LogWarning("Received WebSocket response with no matching pending request (id={Id}). Dropping.", responseId);
        }
        catch (JsonException ex)
        {
            CancelAllPending(ex);
        }
    }

    /// <summary>
    /// Parses the wire bytes into a single <see cref="TrameResponse"/> (object root)
    /// or a batch list (array root) and returns the correlation ID (for batches,
    /// the ID of the first element). A single pass, DataBytes instead of a JsonElement tree.
    /// </summary>
    private static (object Result, string? Id) ParseMessage(ReadOnlyMemory<byte> messageBytes)
    {
        var span = messageBytes.Span;
        var reader = new Utf8JsonReader(span, new JsonReaderOptions());
        if (!reader.Read())
            throw new JsonException("Empty WebSocket message.");

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var list = TrameResponseParser.ParseArray(span);
            var firstId = list.Count > 0 ? list[0]?.Id : null;
            return (list, firstId);
        }

        var resp = TrameResponseParser.Parse(span);
        return (resp, resp.Id);
    }

    private void CancelAllPending(Exception ex)
    {
        foreach (var kv in _pendingRequests)
        {
            kv.Value.SetException(ex);
        }
        _pendingRequests.Clear();
    }

    private ClientWebSocket CreateSocket()
    {
        if (_socketFactory != null)
            return _socketFactory();
        return new ClientWebSocket();
    }

    /// <summary>
    /// Starts (if active and not already running) a background reader
    /// on the current socket and replaces a previously running reader.
    /// </summary>
    private void StartReader()
    {
        // Clean up the previous reader (on reconnect it has already ended anyway).
        var oldCts = _readerCts;
        _readerCts = new CancellationTokenSource();
        _readerTask = Task.Run(() => ReadLoopAsync(_readerCts.Token), CancellationToken.None);
        if (oldCts != null)
        {
            try { oldCts.Cancel(); } catch { /* ignore */ }
            try { oldCts.Dispose(); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Triggers the background reconnect (idempotent). No-op on dispose,
    /// disabled reconnect, or an already-running reconnect.
    /// </summary>
    private void StartReconnect()
    {
        if (_disposed || !_autoReconnect)
            return;
        if (_reconnectTask != null && !_reconnectTask.IsCompleted)
            return;

        SetState(TrameConnectionState.Reconnecting);
        _reconnectCts?.Dispose();
        _reconnectCts = new CancellationTokenSource();
        var token = _reconnectCts.Token;
        _reconnectTask = ReconnectLoopAsync(token);
    }

    private async Task ReconnectLoopAsync(CancellationToken ct)
    {
        try
        {
            for (var i = 0; i < _reconnectDelays.Length; i++)
            {
                if (_disposed)
                    return;

                try
                {
                    await Task.Delay(_reconnectDelays[i], ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (_disposed)
                    return;

                await _connectLock.WaitAsync(ct);
                try
                {
                    if (_disposed)
                        return;
                    // Someone else (lazy connect) already connected.
                    if (_webSocket.State == WebSocketState.Open)
                        return;

                    // Create a fresh socket (the old one is Closed/Aborted).
                    var socket = CreateSocket();
                    try
                    {
                        lock (_connectGate)
                            _connectingSocket = socket;
                        await socket.ConnectAsync(_uri, ct);
                        _webSocket = socket;
                        StartReader();
                        SetState(TrameConnectionState.Connected);
                        _logger?.LogInformation("WebSocket reconnected after {Attempt} attempt(s).", i + 1);

                        // Phase 3: re-subscribe all active subscriptions (decision 6).
                        _ = Task.Run(() => ResubscribeAllAsync(ct), ct);

                        return; // success
                    }
                    catch (OperationCanceledException)
                    {
                        try { socket.Dispose(); } catch { /* ignore */ }
                        throw; // cancel → leave the loop
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning("WebSocket reconnect attempt {Attempt} failed: {Message}", i + 1, ex.Message);
                        try { socket.Dispose(); } catch { /* ignore */ }
                        // continue to the next backoff interval
                    }
                    finally
                    {
                        lock (_connectGate)
                        {
                            if (ReferenceEquals(_connectingSocket, socket))
                                _connectingSocket = null;
                        }
                    }
                }
                finally
                {
                    _connectLock.Release();
                }
            }

            // Backoff exhausted → give up.
            SetState(TrameConnectionState.Disconnected);
            _logger?.LogWarning("WebSocket reconnect exhausted — connection stays offline.");
        }
        catch (OperationCanceledException)
        {
            // Dispose — no state update (DisposeAsync sets Disconnected).
        }
    }

    private void SetState(TrameConnectionState state)
    {
        _state = state;
        try { _onStateChanged?.Invoke(state); } catch { /* observer errors are not fatal */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        // Stop the reconnect (terminal — no further reconnect).
        _reconnectCts?.Cancel();
        // An in-flight ConnectAsync that does not promptly honor its CancellationToken
        // (e.g. a hung WS upgrade) would block the await _reconnectTask.
        // Abort the connecting socket so ConnectAsync returns immediately.
        ClientWebSocket? inflight;
        lock (_connectGate)
            inflight = _connectingSocket;
        if (inflight != null)
        {
            try { inflight.Dispose(); } catch { /* ignore */ }
        }
        if (_reconnectTask != null)
        {
            try { await _reconnectTask; } catch { /* ignore */ }
        }
        _reconnectCts?.Dispose();

        _readerCts?.Cancel();
        if (_readerTask != null)
        {
            try { await _readerTask; } catch { /* ignore */ }
        }

        CancelAllPending(new ObjectDisposedException(nameof(TrameWebSocketClient)));
        CancelAllSubscriptions(new ObjectDisposedException(nameof(TrameWebSocketClient)));

        if (_webSocket.State == WebSocketState.Open)
        {
            try
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
            }
            catch { /* ignore */ }
        }

        _webSocket.Dispose();
        SetState(TrameConnectionState.Disconnected);
        _sendLock.Dispose();
        _connectLock.Dispose();
        _readerCts?.Dispose();
    }

    // ─── Phase 3: Subscribe / Unsubscribe / Event-Dispatch ────────────────

    /// <summary>
    /// Subscribes to a server event (Phase 3). Sends <c>{kind:"subscribe",...}</c>,
    /// receives the subscribe response with <c>subscriptionId</c> and returns a
    /// <see cref="TrameSubscription{T}"/> that pushes server events. On reconnect
    /// (auto-reconnect on) the client re-subscribes automatically with the same parameters.
    /// </summary>
    public async Task<TrameSubscription<T>> SubscribeAsync<T>(
        string controller, string method, object?[]? args = null, CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct);

        var requestId = NextId();
        var subscribeJson = JsonSerializer.Serialize(new
        {
            kind = "subscribe",
            controller,
            method,
            @params = BuildParams(args),
            id = requestId,
        }, JsonOptions);

        var holder = new TcsHolder<TrameResponse>();
        _pendingRequests[requestId] = holder;
        _subscribeRequests[requestId] = new SubscribeRequestRecord(controller, method, args);

        await SendRawAsync(subscribeJson, ct);

        TrameResponse? response;
        try { response = await holder.Tcs.Task; }
        finally { _pendingRequests.TryRemove(requestId, out _); }

        if (response == null || !response.IsSuccess)
            throw new TrameException($"Subscribe failed: code={response?.Code}, msg={response?.Error?.Message}");

        var subscriptionId = ExtractSubscriptionId(response);
        if (string.IsNullOrEmpty(subscriptionId))
            throw new TrameException("Subscribe response missing subscriptionId.");

        var subscription = new TrameSubscription<T>(subscriptionId!, UnsubscribeAsync, ct);
        _subscriptions[subscriptionId!] = new TrameSubscriptionHandler<T>(subscription);

        return subscription;
    }

    private async Task UnsubscribeAsync(string subscriptionId, CancellationToken ct)
    {
        if (!_subscriptions.TryRemove(subscriptionId, out _)) return;
        _subscribeRequests.Remove(subscriptionId, out _);

        var unsubJson = JsonSerializer.Serialize(new
        {
            kind = "unsubscribe",
            subscriptionId,
            id = NextId(),
        }, JsonOptions);

        try { await SendRawAsync(unsubJson, ct); } catch { /* best-effort */ }
    }

    /// <summary>
    /// Detects event frames in the read loop and routes them to the matching subscription.
    /// Returns true if it was an event frame (already dispatched).
    /// </summary>
    private bool TryDispatchEventFrame(ReadOnlyMemory<byte> messageBytes)
    {
        try
        {
            using var doc = JsonDocument.Parse(messageBytes);
            var root = doc.RootElement;
            // A batch response is a JSON array — not an event frame. JsonElement.TryGetProperty
            // throws InvalidOperationException on a non-object root, so guard the kind first
            // (R3: the WS multi path was never exercised before and surfaced this latent throw).
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("type", out var typeProp)) return false;

            var type = typeProp.GetString();
            if (string.IsNullOrEmpty(type)) return false;

            if (!root.TryGetProperty("subscriptionId", out var subIdProp)) return false;
            var subscriptionId = subIdProp.GetString();
            if (string.IsNullOrEmpty(subscriptionId) || !_subscriptions.TryGetValue(subscriptionId!, out var handler))
                return false;

            switch (type)
            {
                case "event":
                    if (root.TryGetProperty("data", out var dataProp))
                        handler.OnNext(dataProp.GetRawText());
                    return true;
                case "complete":
                    handler.OnCompleted();
                    return true;
                case "error":
                    var msg = root.TryGetProperty("message", out var msgProp) ? msgProp.GetString() : "Subscription error";
                    handler.OnError(new TrameException(msg ?? "Subscription error"));
                    return true;
                default:
                    return false;
            }
        }
        catch (JsonException) { return false; }
    }

    private void CancelAllSubscriptions(Exception ex)
    {
        foreach (var kv in _subscriptions) kv.Value.OnError(ex);
        _subscriptions.Clear();
    }

    /// <summary>
    /// Re-subscribes all active subscriptions after a reconnect (decision 6:
    /// client-side re-subscribe with new subscriptionIds, since the connection is new).
    /// </summary>
    private async Task ResubscribeAllAsync(CancellationToken ct)
    {
        if (_subscriptions.IsEmpty) return;
        _logger?.LogDebug("Re-subscribing {Count} subscriptions after reconnect", _subscriptions.Count);

        var oldSubscriptions = _subscriptions.ToArray();
        _subscriptions.Clear();

        foreach (var kv in oldSubscriptions)
        {
            var oldId = kv.Key;
            var handler = kv.Value;
            if (!_subscribeRequests.TryRemove(oldId, out var record)) continue;

            try
            {
                var requestId = NextId();
                var subscribeJson = JsonSerializer.Serialize(new
                {
                    kind = "subscribe",
                    controller = record.Controller,
                    method = record.Method,
                    @params = BuildParams(record.Args),
                    id = requestId,
                }, JsonOptions);

                var holder = new TcsHolder<TrameResponse>();
                _pendingRequests[requestId] = holder;
                _subscribeRequests[requestId] = record;

                await SendRawAsync(subscribeJson, ct);
                var response = await holder.Tcs.Task;
                _pendingRequests.TryRemove(requestId, out _);

                var newSubId = ExtractSubscriptionId(response);
                if (!string.IsNullOrEmpty(newSubId))
                    _subscriptions[newSubId!] = handler;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Re-subscribe failed for {Controller}.{Method}", record.Controller, record.Method);
                handler.OnError(ex);
            }
        }
    }

    private static string? ExtractSubscriptionId(TrameResponse response)
    {
        if (response.Data == null) return null;
        if (response.Data.Value.ValueKind == JsonValueKind.Object)
        {
            if (response.Data.Value.TryGetProperty("subscriptionId", out var subId))
                return subId.GetString();
        }
        return response.Data.Value.GetString();
    }

    private static List<TrameParameter>? BuildParams(object?[]? args)
    {
        if (args == null || args.Length == 0) return null;
        var list = new List<TrameParameter>(args.Length);
        for (int i = 0; i < args.Length; i++)
            list.Add(new TrameParameter { ParameterName = $"arg{i}", Data = JsonSerializer.SerializeToNode(args[i], JsonOptions) });
        return list;
    }

    private async Task SendRawAsync(string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendLock.WaitAsync(ct);
        try
        {
            await _webSocket.SendAsync(new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        finally { _sendLock.Release(); }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private interface ITrameSubscriptionHandler
    {
        void OnNext(string dataJson);
        void OnCompleted();
        void OnError(Exception ex);
    }

    private sealed class TrameSubscriptionHandler<T> : ITrameSubscriptionHandler
    {
        private readonly TrameSubscription<T> _subscription;
        public TrameSubscriptionHandler(TrameSubscription<T> subscription) => _subscription = subscription;

        public void OnNext(string dataJson)
        {
            try
            {
                var value = JsonSerializer.Deserialize<T>(dataJson, JsonOptions);
                _subscription.Subject.OnNext(value!);
            }
            catch (JsonException ex) { _subscription.Subject.OnError(ex); }
        }

        public void OnCompleted() => _subscription.Subject.OnCompleted();
        public void OnError(Exception ex) => _subscription.Subject.OnError(ex);
    }

    private sealed record SubscribeRequestRecord(string Controller, string Method, object?[]? Args);
}