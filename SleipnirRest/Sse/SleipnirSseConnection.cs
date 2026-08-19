using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging;
using SleipnirCommon.Models;
using SleipnirCore.Events;
using SleipnirCore.Services;
using SleipnirCore.Tracing;

namespace SleipnirRest.Sse;

/// <summary>
/// Per-request handler for the SSE (Server-Sent Events) REST event transport — the REST-only
/// sibling of <c>SleipnirWebSocket.SleipnirSubscriptionManager</c>. A single SSE stream carries
/// exactly one event subscription (one <c>GET</c> = one stream = one subscription). Reuses the
/// transport-agnostic event core (<see cref="SleipnirCore.Events"/>) + the process-wide
/// <see cref="SleipnirSubscriptionStore"/>, so a durable subscription created over WebSocket can
/// be resumed over SSE and vice-versa (cross-transport resume).
/// <para>
/// <b>Wire.</b> Each logical event frame <c>{type,subscriptionId,eventId,data}</c> becomes an SSE
/// block: <c>id: {eventId}</c>, <c>event: {type}</c>, <c>data: {frame-json}</c>, blank line. The
/// subscribe-ack is the first SSE event (<c>event: ack</c>, <c>id: 0</c>,
/// <c>data: {subscriptionId[, replayedFrom]}</c>) — written before any event frame, mirroring the
/// WebSocket ack-before-first-frame invariant.
/// </para>
/// <para>
/// <b>Backpressure.</b> The durable live tap (from <see cref="DurableSubscriptionState.Attach"/>)
/// is unbounded (as on WebSocket); a bounded <see cref="EventBuffer"/> (DropOldest, drops counted
/// via <see cref="SleipnirMetrics.EventDropped"/>) sits between the tap and the slow HTTP response.
/// The pump keeps the tap drained into the buffer; a slow SSE client overflows the buffer (drops
/// oldest live frames — they remain in the replay ring for a later resume), never grows the tap.
/// The ephemeral path writes straight into its per-subscription <see cref="EventBuffer"/> (the
/// same buffer WebSocket uses, with the per-event overflow strategy).
/// </para>
/// <para>
/// <b>Auth.</b> Transport <see cref="ISleipnirCore.RequireAuthentication"/> gate (401) is applied
/// by the endpoint before this handler; per-method <c>[SleipnirAuthorise]</c> runs in
/// <see cref="ISleipnirCore.SubscribeAsync"/> / <see cref="ISleipnirCore.AuthorizeSubscribeAsync"/>.
/// <c>EventSource</c> cannot set a Bearer header — the supported TS client is fetch-based; native
/// <c>EventSource</c> works for cookie-auth hosts.
/// </para>
/// </summary>
internal sealed class SleipnirSseConnection
{
    private readonly HttpContext _context;
    private readonly ISleipnirCore _core;
    private readonly SleipnirSubscriptionStore _store;
    private readonly SleipnirConnectionRegistry _registry;
    private readonly int _defaultBufferCapacity;
    private readonly ILogger? _logger;

    // Prepared in Prepare* (before the stream starts), drained in StreamAsync.
    private EphemeralSubscriptionState? _ephemeral;
    private EventBuffer? _sendBuffer;     // bounded send buffer → response (both paths)
    private Tap? _tap;                     // durable live tap (unbounded)
    private string? _durableId;             // durable id attached to this stream (Detach on end)
    private string _subscriptionId = "";
    private long? _replayedFrom;            // for the ack (resume only)
    private bool _isDurable;                // durable (store owns the gauge) vs ephemeral (direct)
    private Task? _pumpTask;                // durable: tap → sendBuffer

    public SleipnirSseConnection(
        HttpContext context,
        ISleipnirCore core,
        SleipnirSubscriptionStore store,
        SleipnirConnectionRegistry registry,
        int defaultBufferCapacity,
        ILogger? logger)
    {
        _context = context;
        _core = core;
        _store = store;
        _registry = registry;
        _defaultBufferCapacity = defaultBufferCapacity > 0 ? defaultBufferCapacity : 100;
        _logger = logger;
    }

    /// <summary>
    /// Fresh subscribe: resolves the event, subscribes the observer (buffering), and prepares the
    /// stream. Returns <c>null</c> on success (the caller streams via <see cref="StreamAsync"/>),
    /// or an error <see cref="IResult"/> (auth/routing/binding, or 503 on the durable cap) to return
    /// directly — no stream is started.
    /// </summary>
    public async Task<IResult?> PrepareFreshAsync(string controller, string method, IQueryCollection query, CancellationToken ct)
    {
        var request = new SleipnirRequest
        {
            Controller = controller,
            Method = method,
            Params = BuildParams(query),
            Id = "sse",
        };

        var result = await _core.SubscribeAsync(request, _context, ct);
        if (result.Error != null)
            return ToErrorResult(result.Error);

        if (result.Resumable)
        {
            var state = _store.BeginCreate(result.EventBackpressureStrategy);
            if (state is null)
                return Results.Json(
                    new { code = 503, message = "Durable subscription cap reached — retry later." },
                    statusCode: 503);

            state.Controller = controller;
            state.Method = method;
            // Subscribe the observer FIRST so events produced before Attach land in the replay
            // ring (the attach snapshot then replays them — no lost events on the create path).
            state.SourceSubscription = result.Observable!.Subscribe(new DurableEventObserver<object?>(state, _logger));
            _tap = state.Attach(0);
            _durableId = _tap.SubscriptionId;
            _subscriptionId = _durableId;
            _replayedFrom = _tap.ReplayedFrom;   // null on a fresh durable subscribe
            _isDurable = true;
            // Gauge bookkeeping for durable subscriptions is the STORE's job: OnAttached Inc's,
            // Detach Dec's (symmetric). Do NOT also touch the registry here — that would double-count.
            _store.OnAttached();
            // Durable send buffer is always bounded DropOldest — keep the newest live frames for a
            // slow client; evicted-oldest live frames remain in the replay ring for resume. The
            // per-event strategy still governs ephemeral subscriptions + the replay ring eviction.
            _sendBuffer = new EventBuffer(_defaultBufferCapacity, EventBackpressureStrategy.DropOldest, ct);
        }
        else
        {
            _subscriptionId = Guid.NewGuid().ToString("N");
            var capacity = result.EventBufferCapacity > 0 ? result.EventBufferCapacity : _defaultBufferCapacity;
            _ephemeral = new EphemeralSubscriptionState(_subscriptionId, capacity, result.EventBackpressureStrategy, ct);
            _sendBuffer = _ephemeral.Buffer;
            // Subscribe stays BEFORE the ack: synchronous-cold frames land in the buffer (no loss);
            // only the writer (which drains the buffer onto the wire) runs after the ack is written,
            // guaranteeing ack-before-first-frame without a hot-observable event-loss window.
            _ephemeral.Disposable = result.Observable!.Subscribe(new EventObserver<object?>(_ephemeral, _subscriptionId, _logger));
            _replayedFrom = null;
            // Ephemeral subscriptions are not in the store — the gauge is managed directly
            // here (Inc now, Dec in Cleanup). Durable subscriptions go through the store instead.
            _registry.IncSubscription();
        }

        return null;
    }

    /// <summary>
    /// Resume: looks up the durable subscription, re-runs authorization against the original route,
    /// attaches a bounded live tap, and replays the gap. Returns <c>null</c> on success, or an
    /// error <see cref="IResult"/>: 410 Gone (state GC'd/TTL-expired — client re-subscribes fresh),
    /// or the auth error (401/403/404 — the durable subscription is torn down).
    /// </summary>
    public async Task<IResult?> PrepareResumeAsync(string subscriptionId, long? lastEventId, CancellationToken ct)
    {
        var state = _store.Lookup(subscriptionId);
        if (state is null)
            return Results.StatusCode(410);   // Gone — durable state GC'd/TTL-expired; re-subscribe fresh.

        var authError = await _core.AuthorizeSubscribeAsync(state.Controller!, state.Method!, _context);
        if (authError != null)
        {
            _store.Destroy(subscriptionId);
            return ToErrorResult(authError);
        }

        _tap = state.Attach(lastEventId ?? 0);
        _durableId = _tap.SubscriptionId;
        _subscriptionId = _durableId;
        _replayedFrom = _tap.ReplayedFrom;    // first replayed eventId (null when nothing buffered)
        _sendBuffer = new EventBuffer(_defaultBufferCapacity, EventBackpressureStrategy.DropOldest, ct);
        _isDurable = true;
        // Gauge: the store owns it for durable subs (OnAttached Inc / Detach Dec). Do NOT touch the
        // registry directly here — the create path set this durable up with the same OnAttached call.
        _store.OnAttached();
        return null;
    }

    /// <summary>
    /// Streams the prepared subscription to the HTTP response body: writes the ack first, then
    /// drains the send buffer (replayed-gap + live frames) as SSE blocks until the source completes
    /// or the client disconnects. Invoked by <c>Results.Stream</c> after the 200 + text/event-stream
    /// headers are sent.
    /// </summary>
    public async Task StreamAsync(Stream responseBody, CancellationToken ct)
    {
        try
        {
            await WriteAckAsync(responseBody, ct);

            if (_tap is not null)
            {
                // Durable: a pump drains the unbounded live tap into the bounded send buffer (drops
                // oldest on overflow). The writer below drains the send buffer to the response.
                _pumpTask = Task.Run(() => PumpDurableAsync(ct), ct);
            }

            await foreach (var frame in _sendBuffer!.ReadAllAsync(ct))
                await WriteFrameAsync(responseBody, frame, ct);
        }
        catch (OperationCanceledException) { /* client disconnected */ }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "SSE stream failed for subscription {SubscriptionId}", _subscriptionId);
        }
        finally
        {
            // Ensure the pump no longer feeds the buffer, then wait for it to wind down.
            if (_pumpTask is not null)
            {
                try { await _pumpTask; } catch { /* pump errors logged in PumpDurableAsync */ }
            }
            Cleanup();
        }
    }

    /// <summary>Drains the durable live tap into the bounded send buffer; completes the buffer when the tap ends.</summary>
    private async Task PumpDurableAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var frame in _tap!.Reader.ReadAllAsync(ct))
                _sendBuffer!.TryEnqueue(frame, OnDropped);
        }
        catch (OperationCanceledException) { /* disconnect */ }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "SSE durable pump failed for subscription {SubscriptionId}", _subscriptionId);
        }
        finally
        {
            _sendBuffer!.Complete();   // signal the writer to drain remaining frames and end
        }
    }

    private void OnDropped()
    {
        // SleipnirMetrics.EventDropped bumps the registry accumulator (via Current) AND the OTel
        // counter — a single call is enough (mirrors the store's durable OnDropped).
        SleipnirMetrics.EventDropped(_subscriptionId);
        _logger?.LogWarning("SSE event dropped for subscription {SubscriptionId} (send buffer full)", _subscriptionId);
    }

    /// <summary>Writes the subscribe-ack as the first SSE event (ack-before-first-frame invariant).</summary>
    private async Task WriteAckAsync(Stream stream, CancellationToken ct)
    {
        var ackData = JsonSerializer.Serialize(new { subscriptionId = _subscriptionId, replayedFrom = _replayedFrom },
            EventJsonOptions.Default);   // WhenWritingNull → replayedFrom omitted on a fresh subscribe
        var sb = new StringBuilder();
        sb.Append("id: 0\n");
        sb.Append("event: ack\n");
        foreach (var line in ackData.Split('\n'))
            sb.Append("data: ").Append(line).Append('\n');
        sb.Append('\n');
        await WriteRawAsync(stream, sb.ToString(), ct);
    }

    /// <summary>Writes one logical frame as an SSE block: <c>id:</c> (eventId), <c>event:</c> (type), <c>data:</c> (frame).</summary>
    private static async Task WriteFrameAsync(Stream stream, string frame, CancellationToken ct)
    {
        // Extract the SSE id/event meta from the pre-serialized frame so native EventSource can
        // drive Last-Event-Id reconnect (the data payload still carries the full envelope).
        long? id = null;
        var type = "event";
        try
        {
            using var doc = JsonDocument.Parse(frame);
            var root = doc.RootElement;
            if (root.TryGetProperty("eventId", out var eid) && eid.ValueKind == JsonValueKind.Number && eid.TryGetInt64(out var n))
                id = n;
            if (root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String)
                type = t.GetString() ?? "event";
        }
        catch { /* malformed frame — fall back to event/data only */ }

        var sb = new StringBuilder();
        if (id.HasValue)
            sb.Append("id: ").Append(id.Value).Append('\n');
        sb.Append("event: ").Append(type).Append('\n');
        foreach (var line in frame.Split('\n'))
            sb.Append("data: ").Append(line).Append('\n');
        sb.Append('\n');
        await WriteRawAsync(stream, sb.ToString(), ct);
    }

    private static async Task WriteRawAsync(Stream stream, string s, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);   // SSE must flush per event so the client receives it
    }

    /// <summary>Builds the <see cref="SleipnirRequest.Params"/> JsonArray from SSE query params.</summary>
    /// <remarks>
    /// A GET has no body, so method arguments travel as query params. Each value is parsed as JSON
    /// when it is valid JSON (number/bool/null/object/array), otherwise treated as a JSON string —
    /// so <c>?count=3</c> binds to an <c>int</c> and <c>?name=hello</c> binds to a <c>string</c>. A
    /// repeated key becomes a JSON array. This is the SSE/GET limitation: no type hints, so a value
    /// like <c>123</c> binds to a number and would 400 for a <c>string</c> parameter — use the
    /// native WebSocket wire for complex/typed parameters.
    /// </remarks>
    private static JsonNode? BuildParams(IQueryCollection query)
    {
        if (query.Count == 0) return null;
        var arr = new JsonArray();
        foreach (var (name, values) in query)
        {
            if (string.IsNullOrEmpty(name)) continue;
            JsonNode? data;
            if (values.Count == 1)
            {
                data = ParseScalar(values[0]);
            }
            else
            {
                var items = new JsonArray();
                foreach (var v in values)
                    items.Add(ParseScalar(v));
                data = items;
            }
            arr.Add(new JsonObject { ["parameterName"] = name, ["data"] = data });
        }
        return arr.Count == 0 ? null : arr;
    }

    private static JsonNode? ParseScalar(string? value)
    {
        if (value is null) return null;
        try { return JsonNode.Parse(value); }
        catch (JsonException) { /* not valid JSON → fall through to a string */ }
        return JsonValue.Create(value);   // not valid JSON → treat as a string
    }

    private static IResult ToErrorResult(SleipnirResponse error)
    {
        var code = error.Code > 0 ? error.Code : 500;
        return Results.Json(error, statusCode: code);
    }

    private void Cleanup()
    {
        // Ephemeral: dispose the source subscription + complete the buffer (the writer has ended).
        // The gauge was Inc'd in PrepareFresh; Dec it here (the store is not involved).
        if (_ephemeral is not null)
        {
            _ephemeral.Dispose();
            _ephemeral = null;
            _registry.DecSubscription();
        }

        // Durable: DETACH (not destroy) — the source + replay ring persist for a resume on
        // reconnect. store.Detach decrements the gauge (symmetric with OnAttached at subscribe),
        // so do NOT also Dec here — that would double-decrement.
        if (_durableId is not null)
        {
            _store.Detach(_durableId);
            _durableId = null;
        }
    }
}