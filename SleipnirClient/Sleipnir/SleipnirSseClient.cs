using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net.Http.Headers;

namespace SleipnirClient.Sleipnir;

/// <summary>
/// Sleipnir SSE (Server-Sent Events) client — <c>HttpClient</c>-based server-push events over REST
/// (<c>text/event-stream</c>). Events-only: <see cref="Call"/> / <see cref="Call(SleipnirMultiRequest?)"/>
/// throw <see cref="NotSupportedException"/> — use <see cref="SleipnirRestJsonClient"/> or
/// <see cref="SleipnirWebSocketClient"/> for calls. This client is the SSE leg the
/// <see cref="SleipnirTransportRouter"/> uses for the <c>auto</c> fallback (WS → REST+SSE) and for the
/// <c>rest</c> capability's event backend.
/// <para>
/// <b>Wire.</b> A single SSE stream carries exactly one event subscription (one <c>GET</c> = one stream =
/// one subscription). Each logical frame <c>{type,subscriptionId,eventId,data}</c> becomes an SSE block
/// (<c>id:</c>/<c>event:</c>/<c>data:</c> lines, blank separator); the subscribe-ack is the first block
/// (<c>event: ack</c>, <c>data: {subscriptionId, replayedFrom?}</c>). The block parser mirrors the tested
/// TS <c>sse.ts</c> (<c>parseSseBlock</c>/<c>dispatchFrame</c>); <c>eventId</c> dedup is at-least-once
/// against the disconnect-buffer replay.
/// </para>
/// <para>
/// <b>Resume (Phase R).</b> A mid-stream drop consults a <see cref="ResumePolicy"/> (Fresh / Resume / Drop).
/// <c>Resume</c> reconnects to <c>GET {apiPath}/events/{subscriptionId}</c> with the <c>Last-Event-Id:</c>
/// header set to the cursor; the server replays the gap (at-least-once; the client dedups by
/// <c>eventId</c>). A <c>410 Gone</c> (durable state GC'd/TTL-expired) degrades a resume to Fresh once; a
/// <c>410</c> on a pure-resume (no fresh params available, see <see cref="ResumeAsync{T}"/>) is terminal.
/// The durable <c>SleipnirSubscriptionStore</c> is process-wide server-side, so a subscription created over
/// WebSocket / SignalR is resumable here (cross-transport resume).
/// </para>
/// </summary>
public sealed class SleipnirSseClient : SleipnirClientBase, ISleipnirClient, IDisposable
{
    /// <summary>Default backoff (ms) — mirrors the TS client + SignalR. Empty array disables reconnect.</summary>
    public static readonly TimeSpan[] DefaultReconnectDelays =
    {
        TimeSpan.Zero,
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
    };

    private readonly HttpClient _httpClient;
    private readonly string _serverBase;
    private readonly string _apiPath;
    private readonly bool _ownsHttpClient;
    private readonly bool _reconnect;
    private readonly TimeSpan[] _reconnectDelays;
    private readonly ResumePolicy? _onResume;
    private string _bearer = string.Empty;
    private bool _disposed;

    public SleipnirSseClient(string serverBaseUrl, HttpClient? httpClient = null,
        string apiPath = "api/sleipnir", string? bearer = null,
        bool reconnect = true, TimeSpan[]? reconnectDelays = null,
        ResumePolicy? onResume = null)
        : base()
    {
        if (string.IsNullOrWhiteSpace(serverBaseUrl))
            throw new ArgumentException("Server URL must not be empty.", nameof(serverBaseUrl));

        if (httpClient != null)
        {
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }
        else
        {
            _httpClient = new HttpClient(new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            }, disposeHandler: true);
            _ownsHttpClient = true;
        }
        // SSE streams are long-lived; disable the default 100s read timeout so a quiet event
        // source is not aborted while the connection is still open. Per-subscription cancellation
        // is handled by the linked CancellationTokenSource passed to GetAsync.
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;

        _serverBase = serverBaseUrl.EndsWith("/", StringComparison.Ordinal) ? serverBaseUrl : serverBaseUrl + "/";
        _apiPath = apiPath.Trim('/');
        _bearer = bearer ?? string.Empty;
        _reconnect = reconnect;
        _reconnectDelays = reconnectDelays ?? DefaultReconnectDelays;
        if (_reconnectDelays.Length == 0)
            _reconnect = false;
        _onResume = onResume;
    }

    /// <summary>Swap the Bearer (JWT) for future subscribe/resume requests.</summary>
    public void SetBearer(string? bearer) => _bearer = bearer ?? string.Empty;

    /// <summary>SSE is events-only — calls are not supported.</summary>
    public override Task<SleipnirResponse?> Call(SleipnirRequest? request, CancellationToken ct = default)
        => throw new NotSupportedException(
            "SleipnirSseClient is an events-only transport. Use SleipnirRestJsonClient or SleipnirWebSocketClient for calls.");

    /// <summary>SSE is events-only — batch calls are not supported.</summary>
    public override Task<IEnumerable<SleipnirResponse?>?> Call(SleipnirMultiRequest? request, CancellationToken ct = default)
        => throw new NotSupportedException(
            "SleipnirSseClient is an events-only transport. Use SleipnirRestJsonClient or SleipnirWebSocketClient for batch calls.");

    /// <summary>
    /// Subscribes to a server event. The <paramref name="request"/> carries controller/method/params
    /// (built via <see cref="SleipnirCall"/>); the params travel as query params (GET has no body), each
    /// JSON-encoded so the server parses them back type-faithfully. Resolves with the
    /// <see cref="SleipnirSubscription{T}"/> once the server-ack arrives (first SSE block).
    /// </summary>
    public override Task<SleipnirSubscription<T>> SubscribeAsync<T>(SleipnirRequest? request,
        ResumePolicy? resumePolicy = null, CancellationToken ct = default)
    {
        if (request == null)
            throw new ArgumentNullException(nameof(request));
        ThrowIfDisposed();

        var state = new SseSubscriptionState<T>(this, request.Controller, request.Method,
            resumePolicy ?? _onResume, _reconnect, _reconnectDelays, ct);
        var freshUrl = BuildFreshUrl(request.Controller, request.Method, request.Params);
        state.Start(freshUrl, initialResume: false);
        return state.Completion.Task;
    }

    /// <summary>
    /// Resumes a durable subscription by <paramref name="subscriptionId"/> + <paramref name="lastEventId"/>
    /// cursor — the server replays the gap then continues live. Cross-transport: a subscription created
    /// over WebSocket / SignalR is resumable here. No fresh params are available, so a <c>410 Gone</c>
    /// (durable state gone) is terminal (no Fresh fallback).
    /// </summary>
    public override Task<SleipnirSubscription<T>> ResumeAsync<T>(string subscriptionId, long lastEventId,
        ResumePolicy? resumePolicy = null, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(subscriptionId))
            throw new ArgumentException("subscriptionId is required.", nameof(subscriptionId));
        ThrowIfDisposed();

        var state = new SseSubscriptionState<T>(this, "", "",
            resumePolicy ?? _onResume, _reconnect, _reconnectDelays, ct);
        state.ActiveId = subscriptionId;
        state.LastEventId = lastEventId;
        state.ResumeOnly = true;
        var resumeUrl = BuildResumeUrl(subscriptionId, lastEventId);
        state.Start(resumeUrl, initialResume: true);
        return state.Completion.Task;
    }

    // ─── URL + request builders ────────────────────────────────────────────

    internal string BuildFreshUrl(string controller, string method, JsonNode? paramsNode)
    {
        var baseUri = $"{_serverBase}{_apiPath}/events/{Uri.EscapeDataString(controller)}/{Uri.EscapeDataString(method)}";
        if (paramsNode is not JsonArray arr || arr.Count == 0)
            return baseUri;
        var qs = new StringBuilder();
        foreach (var entry in arr)
        {
            if (entry is not JsonObject obj) continue;
            if (!obj.TryGetPropertyValue("parameterName", out var nameNode) || nameNode is null) continue;
            var name = nameNode.GetValue<string>();
            if (string.IsNullOrEmpty(name)) continue;
            var dataNode = obj.TryGetPropertyValue("data", out var d) ? d : null;
            var value = dataNode?.ToJsonString() ?? "null";
            if (qs.Length > 0) qs.Append('&');
            qs.Append(Uri.EscapeDataString(name)).Append('=').Append(Uri.EscapeDataString(value));
        }
        return qs.Length > 0 ? $"{baseUri}?{qs}" : baseUri;
    }

    internal string BuildResumeUrl(string subscriptionId, long lastEventId)
    {
        // lastEventId travels primarily in the Last-Event-Id header; the query is the fallback for
        // environments where setting headers is awkward (mirrors the TS client / native EventSource).
        return $"{_serverBase}{_apiPath}/events/{Uri.EscapeDataString(subscriptionId)}?lastEventId={lastEventId}";
    }

    private HttpRequestMessage BuildGet(string url, bool isResume, long lastEventId)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (isResume && lastEventId > 0)
            req.Headers.TryAddWithoutValidation("Last-Event-Id", lastEventId.ToString());
        if (!string.IsNullOrEmpty(_bearer))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _bearer);
        return req;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SleipnirSseClient));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }

    // ─── Per-subscription state + connect loop ─────────────────────────────

    /// <summary>
    /// Owns one subscription's lifecycle: the ack TaskCompletionSource, the live cursor, the
    /// reconnect loop, and the <see cref="SleipnirSubscription{T}"/> once the ack arrives. Mirrors the
    /// closure-state of the TS <c>sse.ts</c> subscribe/resume methods.
    /// </summary>
    private sealed class SseSubscriptionState<T>
    {
        private readonly SleipnirSseClient _client;
        private readonly string _controller;
        private readonly string _method;
        private readonly ResumePolicy? _policy;
        private readonly bool _reconnect;
        private readonly TimeSpan[] _reconnectDelays;
        private readonly CancellationTokenSource _abortCts;
        private SleipnirSubscription<T>? _subscription;
        private int _attempt;
        private bool _forceFresh;

        public TaskCompletionSource<SleipnirSubscription<T>> Completion { get; }
            = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string ActiveId = "";
        public long LastEventId;
        public bool ResumeOnly;
        public bool Unsubscribed;

        public SseSubscriptionState(SleipnirSseClient client, string controller, string method,
            ResumePolicy? policy, bool reconnect, TimeSpan[] reconnectDelays, CancellationToken callerCt)
        {
            _client = client;
            _controller = controller;
            _method = method;
            _policy = policy;
            _reconnect = reconnect;
            _reconnectDelays = reconnectDelays;
            _abortCts = CancellationTokenSource.CreateLinkedTokenSource(callerCt);
        }

        public void Start(string initialUrl, bool initialResume)
            => _ = Task.Run(() => ConnectLoopAsync(initialUrl, initialResume), _abortCts.Token);

        private async Task ConnectLoopAsync(string freshUrl, bool initialResume)
        {
            bool isResume = initialResume;
            while (!Unsubscribed && !_abortCts.IsCancellationRequested)
            {
                var url = (isResume && !string.IsNullOrEmpty(ActiveId))
                    ? _client.BuildResumeUrl(ActiveId, LastEventId)
                    : freshUrl;
                try
                {
                    var endedClean = await ReadOnceAsync(url, isResume);
                    if (Unsubscribed || _abortCts.IsCancellationRequested)
                        return;
                    // Clean stream end WITHOUT a terminal frame = drop → reconnect (if enabled).
                    if (!endedClean)
                        return; // terminal frame seen (complete/error) → stop
                    if (!ShouldReconnect())
                    {
                        ReportDrop(new SleipnirException("SSE stream ended."));
                        return;
                    }
                    isResume = DecideReconnect(ref _forceFresh, isResume);
                    await DelayAsync();
                }
                catch (OperationCanceledException) when (_abortCts.IsCancellationRequested || Unsubscribed)
                {
                    return;
                }
                catch (Exception ex)
                {
                    if (Unsubscribed || _abortCts.IsCancellationRequested) return;
                    if (!ShouldReconnect())
                    {
                        ReportDrop(ex);
                        return;
                    }
                    isResume = DecideReconnect(ref _forceFresh, isResume);
                    await DelayAsync();
                }
            }
        }

        /// <summary>
        /// Opens one GET, reads the SSE stream to completion, dispatching the ack + event/complete/error
        /// blocks. Returns <c>true</c> if the stream ended cleanly without a terminal frame (drop →
        /// reconnectable); <c>false</c> if a terminal complete/error frame was seen (stop).
        /// </summary>
        private async Task<bool> ReadOnceAsync(string url, bool isResume)
        {
            using var req = _client.BuildGet(url, isResume, LastEventId);
            var resp = await _client._httpClient.SendAsync(req,
                HttpCompletionOption.ResponseHeadersRead, _abortCts.Token);

            if (!resp.IsSuccessStatusCode)
            {
                HandleNonOk(resp, isResume);
                return false;
            }
            var stream = await resp.Content.ReadAsStreamAsync(_abortCts.Token);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
            var blockLines = new List<string>();
            bool ackSeen = _subscription != null;
            bool terminal = false;
            while (true)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(_abortCts.Token);
                }
                catch (OperationCanceledException) when (_abortCts.IsCancellationRequested || Unsubscribed)
                {
                    return false;
                }
                if (line == null)
                    break; // EOF
                if (line.Length == 0)
                {
                    if (blockLines.Count > 0)
                    {
                        terminal = DispatchBlock(blockLines, isResume, ref ackSeen);
                        blockLines.Clear();
                        if (terminal) return false;
                    }
                    continue;
                }
                blockLines.Add(line);
            }
            // Flush a trailing block (no trailing blank line).
            if (blockLines.Count > 0)
                return DispatchBlock(blockLines, isResume, ref ackSeen);
            return true; // clean EOF, no terminal → drop
        }

        /// <summary>Dispatch one SSE block; returns true if it was a terminal complete/error frame.</summary>
        private bool DispatchBlock(List<string> lines, bool isResume, ref bool ackSeen)
        {
            var block = ParseSseBlock(lines);
            if (block == null) return false;

            if (!ackSeen && block.Event == "ack")
            {
                ackSeen = true;
                try
                {
                    using var ackDoc = JsonDocument.Parse(block.Data);
                    var root = ackDoc.RootElement;
                    string? subId = root.TryGetProperty("subscriptionId", out var sid) ? sid.GetString() : null;
                    if (!string.IsNullOrEmpty(subId)) ActiveId = subId!;
                    long? replayedFrom = root.TryGetProperty("replayedFrom", out var rf)
                        && rf.ValueKind == System.Text.Json.JsonValueKind.Number
                        ? rf.GetInt64() : (long?)null;
                    // Resume that returns no replayedFrom = server degraded to fresh → eventId counter resets.
                    if (isResume && replayedFrom == null && !ResumeOnly)
                        LastEventId = 0;
                    else if (isResume && replayedFrom == null && ResumeOnly)
                        LastEventId = 0;

                    _subscription = new SleipnirSubscription<T>(ActiveId, UnsubscribeAsync, _abortCts.Token);
                    Completion.TrySetResult(_subscription);
                }
                catch (Exception ex)
                {
                    Completion.TrySetException(new SleipnirException("Malformed SSE ack block.", ex));
                    Unsubscribed = true;
                }
                return false;
            }

            switch (block.Event)
            {
                case "event":
                    try
                    {
                        using var doc = JsonDocument.Parse(block.Data);
                        var root = doc.RootElement;
                        long? evId = root.TryGetProperty("eventId", out var eid)
                            && eid.ValueKind == System.Text.Json.JsonValueKind.Number
                            && eid.TryGetInt64(out var n) ? n : (long?)null;
                        if (evId.HasValue)
                        {
                            if (evId.Value <= LastEventId) return false; // replay duplicate
                            LastEventId = evId.Value;
                        }
                        if (root.TryGetProperty("data", out var dataEl))
                        {
                            var value = dataEl.Deserialize<T>(_client.JsonOptions);
                            _subscription?.Subject.OnNext(value!);
                        }
                    }
                    catch (Exception ex)
                    {
                        _subscription?.Subject.OnError(ex);
                    }
                    return false;
                case "complete":
                    Unsubscribed = true;
                    _subscription?.Subject.OnCompleted();
                    return true;
                case "error":
                    Unsubscribed = true;
                    string msg = "Subscription error";
                    try
                    {
                        using var doc = JsonDocument.Parse(block.Data);
                        if (doc.RootElement.TryGetProperty("message", out var mp) && mp.ValueKind == JsonValueKind.String)
                            msg = mp.GetString() ?? msg;
                    }
                    catch { /* keep default */ }
                    _subscription?.Subject.OnError(new SleipnirException(msg));
                    return true;
                default:
                    return false; // "message" / unknown — ignored
            }
        }

        private void HandleNonOk(HttpResponseMessage resp, bool wasResume)
        {
            var status = (int)resp.StatusCode;
            // Pre-ack failure: the subscribe itself failed (auth/routing/binding) → reject.
            if (_subscription == null && !Completion.Task.IsCompleted)
            {
                Completion.TrySetException(new SleipnirException(
                    $"SSE subscribe failed (HTTP {status})."));
                Unsubscribed = true;
                return;
            }
            // Post-ack 410 on a resume → degrade to Fresh once (if we have fresh params).
            if (wasResume && status == 410 && !ResumeOnly)
            {
                _forceFresh = true;
                return; // reconnect loop will re-enter in fresh mode
            }
            // Post-ack 410 on a pure resume → terminal (no fresh params to fall back to).
            if (wasResume && status == 410 && ResumeOnly)
            {
                Unsubscribed = true;
                _subscription?.Subject.OnError(
                    new SleipnirException("SSE resume target gone (410): subscription expired."));
                return;
            }
            // Other post-ack non-2xx → drop (reconnect policy decides).
            ReportDrop(new SleipnirException($"SSE stream HTTP {status}."));
        }

        private void ReportDrop(Exception ex)
        {
            if (_subscription == null && !Completion.Task.IsCompleted)
            {
                Completion.TrySetException(ex);
                Unsubscribed = true;
                return;
            }
            _subscription?.Subject.OnError(ex);
        }

        private bool ShouldReconnect()
            => _reconnect && _reconnectDelays.Length > 0 && !Unsubscribed && !_abortCts.IsCancellationRequested;

        /// <summary>
        /// Consult the resume policy (per-subscribe → client-wide). Resume-only subs have no fresh
        /// params, so "fresh" is treated as "resume". Sets <c>forceFresh</c> false once consumed.
        /// Returns the mode for the next connect.
        /// </summary>
        private bool DecideReconnect(ref bool forceFresh, bool currentResume)
        {
            if (ResumeOnly)
            {
                if (forceFresh) { forceFresh = false; return true; } // (resume-only has no fresh, but keep semantics)
                var decision = ConsultPolicy(ResumeDecision.Resume);
                if (decision == ResumeDecision.Drop)
                {
                    Unsubscribed = true;
                    _subscription?.Subject.OnCompleted();
                }
                return true; // resume-only always reconnects in resume mode (or stops above)
            }
            if (forceFresh)
            {
                forceFresh = false;
                return false; // fresh mode
            }
            var dec = ConsultPolicy(ResumeDecision.Fresh);
            if (dec == ResumeDecision.Drop)
            {
                Unsubscribed = true;
                _subscription?.Subject.OnCompleted();
                return currentResume;
            }
            return dec == ResumeDecision.Resume;
        }

        private ResumeDecision ConsultPolicy(ResumeDecision fallback)
        {
            if (_policy == null || string.IsNullOrEmpty(ActiveId)) return fallback;
            var ctx = new SubscriptionResumeContext(_controller, _method, ActiveId,
                LastEventId > 0 ? LastEventId : (long?)null);
            return _policy.Invoke(ctx) ?? fallback;
        }

        private async Task DelayAsync()
        {
            var idx = Math.Min(_attempt, _reconnectDelays.Length - 1);
            _attempt++;
            var delay = _reconnectDelays[idx];
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, _abortCts.Token);
        }

        private Task UnsubscribeAsync(string subscriptionId, CancellationToken ct)
        {
            Unsubscribed = true;
            _abortCts.Cancel();
            return Task.CompletedTask;
        }
    }

    // ─── SSE block parser (port of sse.ts parseSseBlock) ────────────────────

    private sealed class SseBlock
    {
        public string Event = "message";
        public long? Id;
        public string Data = "";
    }

    /// <summary>
    /// Parse the lines of one SSE block (the lines between two blank lines) into
    /// <c>{event,id,data}</c>. Unknown fields (<c>retry:</c>) and comments (<c>:</c>) are ignored.
    /// A block without a <c>data:</c> field returns null. A single leading space after the colon
    /// (SSE convention) is stripped. Multi-line <c>data:</c> is joined with <c>\n</c>.
    /// </summary>
    private static SseBlock? ParseSseBlock(List<string> lines)
    {
        var block = new SseBlock();
        bool hasData = false;
        var data = new StringBuilder();
        foreach (var rawLine in lines)
        {
            var line = rawLine.EndsWith('\r') ? rawLine[..^1] : rawLine;
            if (line.Length == 0 || line[0] == ':') continue; // blank/comment
            var colon = line.IndexOf(':');
            var field = colon == -1 ? line : line[..colon];
            var value = colon == -1 ? "" : line[(colon + 1)..];
            if (value.Length > 0 && value[0] == ' ') value = value[1..]; // strip one leading space
            switch (field)
            {
                case "event":
                    block.Event = value;
                    break;
                case "id":
                    block.Id = long.TryParse(value.Trim(), out var id) ? id : (long?)null;
                    break;
                case "data":
                    if (hasData) data.Append('\n');
                    data.Append(value);
                    hasData = true;
                    break;
                    // retry: + unknown ignored
            }
        }
        if (!hasData) return null;
        block.Data = data.ToString();
        return block;
    }
}