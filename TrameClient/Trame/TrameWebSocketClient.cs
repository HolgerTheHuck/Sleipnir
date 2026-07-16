using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace TrameClient.Trame;

/// <summary>
/// Trame-Client über reine WebSockets (RFC 6455) mit ID-basierter
/// Request/Response-Korrelation für parallele Calls.
///
/// Auto-Reconnect: Droppt die Verbindung unerwartet (Server-Close,
/// Transportfehler), verbindet der Client im Hintergrund mit exponentiellem
/// Backoff automatisch wieder (spiegelt SignalRs <c>WithAutomaticReconnect</c>
/// nach). In-Flight-Calls beim Drop werden abgelehnt (SignalR-Parität); neue
/// Calls während des Reconnects warten auf dieselbe in-flight Verbindung.
/// Explizites <see cref="DisposeAsync"/> ist terminal — kein Reconnect.
/// </summary>
public class TrameWebSocketClient : TrameClientBase, ITrameClient, IAsyncDisposable
{
    /// <summary>
    /// Standard-Backoff-Intervalle (Spiegel von SignalR): 2,2,5,5,10,10,30,30s,
    /// 1min,1min,5min. Nach dem letzten Interval gibt der Reconnect auf
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
    private bool _disposed;

    /// <summary>
    /// Non-generic interface to store TaskCompletionSource instances of different types.
    /// SetResult nimmt ein bereits geparstes Ergebnisobjekt (TrameResponse bzw.
    /// List&lt;TrameResponse?&gt;) — das Parsing passiert zentral in DispatchResponse via
    /// TrameResponseParser (ein Pass, DataBytes statt JsonDocument-Baum), nicht mehr
    /// pro Holder via JsonSerializer.Deserialize(string).
    /// </summary>
    private interface ITcsHolder
    {
        void SetResult(object result);
        void SetException(Exception ex);
        void SetCanceled();
    }

    private sealed class TcsHolder<T> : ITcsHolder
    {
        public readonly TaskCompletionSource<T> Tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void SetResult(object result) => Tcs.TrySetResult((T)result!);
        public void SetException(Exception ex) => Tcs.TrySetException(ex);
        public void SetCanceled() => Tcs.SetCanceled();
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
            throw new ArgumentException("Server-URL darf nicht leer sein.", nameof(serverBaseUrl));

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
            _autoReconnect = false; // leeres Array schaltet Reconnect explizit aus
        _onStateChanged = onStateChanged;
    }

    /// <summary>Aktueller Verbindungs-Zustand (Observer-Oberfläche für UI/Logs).</summary>
    public TrameConnectionState State => _state;

    /// <summary>
    /// Stellt die WebSocket-Verbindung her und startet den Hintergrund-Reader.
    /// Ein geschlossener/abgebrochener Socket wird zuvor durch einen frischen
    /// ersetzt (geschlossene Sockets sind nicht reusing-fähig).
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(TrameWebSocketClient));

        if (_webSocket.State == WebSocketState.Open)
            return;

        // Geschlossener/abgebrochener Socket kann nicht wiederverwendet werden → frisch erzeugen.
        if (_webSocket.State is WebSocketState.Closed or WebSocketState.Aborted)
        {
            var dead = _webSocket;
            _webSocket = CreateSocket();
            try { dead.Dispose(); } catch { /* ignore */ }
        }

        SetState(TrameConnectionState.Connecting);
        try
        {
            await _webSocket.ConnectAsync(_uri, ct);
        }
        catch
        {
            SetState(TrameConnectionState.Disconnected);
            throw;
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

        await EnsureConnectedAsync(ct);
        return await SendAndAwaitResponseAsync<List<TrameResponse?>>(request, ct);
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(TrameWebSocketClient));
        if (_webSocket.State == WebSocketState.Open)
            return;

        // Läuft ein Hintergrund-Reconnect? Darauf warten (nicht selbst verbinden),
        // damit parallele Calls denselben in-flight Reconnect teilen.
        var reconnect = _reconnectTask;
        if (reconnect != null && _state == TrameConnectionState.Reconnecting)
        {
            try { await reconnect.WaitAsync(ct); }
            catch (OperationCanceledException) { /* ct abgebrochen — unten neu bewerten */ }
            if (_webSocket.State == WebSocketState.Open)
                return;
        }

        if (_webSocket.State == WebSocketState.Open)
            return;

        // Connect-Race (B2): nur ein Caller darf verbinden, Rest wartet und sieht Open.
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
            requestId = mr.Requests?.FirstOrDefault()?.Id ?? NextId();
        }
        else
        {
            requestId = NextId();
        }

        var holder = new TcsHolder<T>();
        _pendingRequests[requestId] = holder;

        // Call-Timeout: verknüpftes CTS, das sowohl das Senden als auch das Warten abbricht.
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
            using var reg = effectiveCt.Register(() => holder.SetCanceled());
            return await holder.Tcs.Task;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Einheitliche Fehleroberfläche (C1): Transportfehler als TrameException.
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
    /// Beim Ende (Close/Fehler) werden alle pending Calls abgelehnt und ein
    /// Hintergrund-Reconnect angestoßen (außer bei Dispose).
    /// </summary>
    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 4);
        Exception? terminalError = null;

        try
        {
            while (_webSocket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                // Bytes sammeln (nicht pro Chunk dekodieren) — sonst korruptieren
                // Multi-Byte-Zeichen an Chunk-Grenzen (A2).
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

                // Roh-Bytes über den Live-MemoryStream-Puffer (kein String-Intermediat,
                // keine Kopie — der Parser kopiert nur den DataBytes-Slice, den er behält).
                // Der MemoryStream lebt bis zum Iterationsende (using var), also über den
                // synchronen Dispatch-Aufruf hinaus.
                var messageBytes = messageBuffer.GetBuffer().AsMemory(0, (int)messageBuffer.Length);

                DispatchResponse(messageBytes);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown (Dispose) — kein Reconnect.
        }
        catch (Exception ex)
        {
            terminalError = ex;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        // Alle pending Calls ablehnen (Spiegel SignalR: in-flight wirft beim Drop).
        CancelAllPending(terminalError ?? new WebSocketException("WebSocket connection closed."));

        // Unerwartetes Ende (nicht Dispose) → Hintergrund-Reconnect.
        StartReconnect();
    }

    /// <summary>
    /// Parst die Response-Bytes EINMAL via <see cref="TrameResponseParser"/> (erfasst
    /// ID, Envelope und DataBytes in einem Pass — kein JsonDocument-Baum), extrahiert
    /// die Korrelations-Id und löst den passenden pending Call auf. Single-Responses
    /// korrelieren per <c>id</c>; Batch-Antworten (JSON-Array) über die Id des ersten
    /// Elements (Server sendet pro-Request-Ids seit v1).
    /// </summary>
    private void DispatchResponse(ReadOnlyMemory<byte> messageBytes)
    {
        try
        {
            var (result, responseId) = ParseMessage(messageBytes);

            if (responseId != null && _pendingRequests.TryGetValue(responseId, out var holder))
            {
                holder.SetResult(result);
                return;
            }

            // Kein Match -> NICHT raten (B3: stille Fehlzuordnung). Verwerfen + loggen;
            // der wartende Caller bricht über sein CancellationToken ab.
            _logger?.LogWarning("Received WebSocket response with no matching pending request (id={Id}). Dropping.", responseId);
        }
        catch (JsonException ex)
        {
            CancelAllPending(ex);
        }
    }

    /// <summary>
    /// Parst die Wire-Bytes in eine einzelne <see cref="TrameResponse"/> (Object-Wurzel)
    /// oder eine Batch-Liste (Array-Wurzel) und liefert die Korrelations-Id (bei Batch
    /// die Id des ersten Elements). Ein Pass, DataBytes statt JsonElement-Baum.
    /// </summary>
    private static (object Result, string? Id) ParseMessage(ReadOnlyMemory<byte> messageBytes)
    {
        var span = messageBytes.Span;
        var reader = new Utf8JsonReader(span, new JsonReaderOptions());
        if (!reader.Read())
            throw new JsonException("Leere WebSocket-Nachricht.");

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
    /// Startet (sofern aktiv und nicht bereits laufend) einen Hintergrund-Reader
    /// auf dem aktuellen Socket und ersetzt einen zuvor laufenden Reader.
    /// </summary>
    private void StartReader()
    {
        // Vorherigen Reader aufräumen (beim Reconnect ist er ohnehin beendet).
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
    /// Stößt den Hintergrund-Reconnect an (idempotent). No-op bei Dispose,
    /// deaktiviertem Reconnect oder bereits laufendem Reconnect.
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
                    // Jemand anderem (lazy Connect) hat bereits verbunden.
                    if (_webSocket.State == WebSocketState.Open)
                        return;

                    // Frischen Socket erzeugen (der alte ist Closed/Aborted).
                    var socket = CreateSocket();
                    try
                    {
                        await socket.ConnectAsync(_uri, ct);
                        _webSocket = socket;
                        StartReader();
                        SetState(TrameConnectionState.Connected);
                        _logger?.LogInformation("WebSocket reconnected after {Attempt} attempt(s).", i + 1);
                        return; // Erfolg
                    }
                    catch (OperationCanceledException)
                    {
                        try { socket.Dispose(); } catch { /* ignore */ }
                        throw; // Abbruch → Schleife verlassen
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning("WebSocket reconnect attempt {Attempt} failed: {Message}", i + 1, ex.Message);
                        try { socket.Dispose(); } catch { /* ignore */ }
                        // weiter zum nächsten Backoff-Intervall
                    }
                }
                finally
                {
                    _connectLock.Release();
                }
            }

            // Backoff erschöpft → aufgeben.
            SetState(TrameConnectionState.Disconnected);
            _logger?.LogWarning("WebSocket reconnect exhausted — connection stays offline.");
        }
        catch (OperationCanceledException)
        {
            // Dispose — kein State-Update (DisposeAsync setzt Disconnected).
        }
    }

    private void SetState(TrameConnectionState state)
    {
        _state = state;
        try { _onStateChanged?.Invoke(state); } catch { /* Observer-Fehler nicht fatal */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        // Reconnect stoppen (terminal — kein weiterer Reconnect).
        _reconnectCts?.Cancel();
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
}