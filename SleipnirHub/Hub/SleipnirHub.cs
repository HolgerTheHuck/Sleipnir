using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using SleipnirCommon.Models;
using SleipnirCore.Events;
using SleipnirCore.Services;
using SleipnirCore.Tracing;
using SleipnirHub.Extensions;

namespace SleipnirHub.Hub
{
    /// <summary>
    /// SignalR transport hub. Calls flow through <see cref="DoWork"/>/<see cref="DoWorkMany"/>
    /// (unchanged — JSON / MessagePack). Events flow through the streaming
    /// <see cref="SubscribeAsync"/> method (Phase 3a): it bridges the transport-agnostic
    /// <c>IObservable&lt;T&gt;</c> event pipeline to a SignalR
    /// <c>IAsyncEnumerable&lt;string&gt;</c> hub stream, reusing the same durable subscription
    /// store + event buffer the WebSocket and SSE transports use — so a durable subscription
    /// created over one transport resumes over another (cross-transport resume).
    /// <para>
    /// <b>Wire.</b> Each yielded stream item is one pre-serialized logical event frame string
    /// (<c>{type,subscriptionId,eventId,data}</c> / <c>complete</c> / <c>error</c>) — the SAME
    /// string the WebSocket sends as a text frame and SSE writes as an event block, so the TS
    /// SignalR client parses stream items with the WS frame parser. The FIRST item is an
    /// <c>{type:"ack",subscriptionId,replayedFrom?}</c> frame: unlike WS/SSE, a SignalR stream
    /// has no separate response channel to carry the subscribe-ack, so it rides as the first
    /// stream item. The client resolves <c>subscriptionId</c> + <c>replayedFrom</c> from it
    /// (needed for cross-transport resume).
    /// </para>
    /// <para>
    /// <b>Backpressure.</b> The durable live tap (from
    /// <see cref="DurableSubscriptionState.Attach"/>) is unbounded; a bounded
    /// <see cref="EventBuffer"/> (DropOldest) sits between the tap and the SignalR stream, fed by
    /// a background pump — mirroring the SSE path. A slow SignalR client overflows the send
    /// buffer (drops oldest live frames — they remain in the replay ring for a later resume),
    /// never grows the tap. The ephemeral path's observer writes straight into its per-subscription
    /// <see cref="EventBuffer"/> (already bounded), so no pump is needed.
    /// </para>
    /// <para>
    /// <b>Auth.</b> Per-method <c>[SleipnirAuthorise]</c> runs in
    /// <see cref="ISleipnirCore.SubscribeAsync"/> (fresh) / <see cref="ISleipnirCore.AuthorizeSubscribeAsync"/>
    /// (resume — re-checked against the ORIGINAL route recorded on the durable state, not the
    /// client-claimed one). A pre-stream auth/routing/binding failure throws
    /// <see cref="HubException"/> → the stream rejects on the client (mapped to
    /// <c>onError</c>); a mid-stream source error arrives as an <c>{type:"error",...}</c> frame
    /// (written by the observer as a terminal frame) before the stream ends.
    /// </para>
    /// <para>
    /// Only wired when <c>SleipnirOptions.UseSignalR = true</c> (opt-in, default false) — the hub
    /// is mapped only then. Calls keep using <see cref="DoWork"/>/<see cref="DoWorkMany"/>.
    /// </para>
    /// </summary>
    public class SleipnirHub(
        ISleipnirCore service,
        SleipnirSubscriptionStore store,
        SleipnirConnectionRegistry registry,
        SleipnirOptions options,
        ILogger<SleipnirHub> logger) : Microsoft.AspNetCore.SignalR.Hub
    {
        private readonly int _defaultBufferCapacity =
            options.EventBufferCapacity is { } cap && cap > 0 ? cap : 100;

        public async Task<SleipnirResponse?> DoWork(SleipnirRequest request)
        {
            var user = Context.UserIdentifier;
            return await service.InvokeDi(request, Context.GetHttpContext(), Context.ConnectionAborted);
        }

        public async Task<IEnumerable<SleipnirResponse>> DoWorkMany(SleipnirMultiRequest? request)
        {
            if (request == null)
            {
                return new List<SleipnirResponse>();
            }
            if (request.Requests == null)
            {
                return new List<SleipnirResponse>();
            }

            var result = await service.InvokeDi(
                request.Requests,
                Context.GetHttpContext(),
                request.Mode,
                Context.ConnectionAborted);
            return result;
        }

        /// <summary>
        /// Streaming event subscribe over SignalR. Yields the ack frame first, then the event
        /// stream (replayed gap + live for a resume). Pass a non-empty
        /// <paramref name="resumeSubscriptionId"/> (+ <paramref name="lastEventId"/>) to resume a
        /// durable subscription created on any transport; leave them null/0 for a fresh subscribe
        /// (the <paramref name="request"/> carries controller/method/params then).
        /// </summary>
        /// <remarks>
        /// The <paramref name="ct"/> is the SignalR stream cancellation token (cancelled on client
        /// cancel / disconnect); <c>[EnumeratorCancellation]</c> threads it into the
        /// <c>await foreach</c> loops so the <c>finally</c> cleanup runs on disconnect.
        /// </remarks>
        public async IAsyncEnumerable<string> SubscribeAsync(
            SleipnirRequest request,
            string? resumeSubscriptionId,
            long? lastEventId,
            [EnumeratorCancellation] CancellationToken ct)
        {
            // Per-stream state — disposed in `finally` (runs on completion, client cancel, disconnect).
            EphemeralSubscriptionState? ephemeral = null;
            Tap? tap = null;
            EventBuffer? sendBuffer = null;     // durable: bounded tap→stream buffer (fed by the pump)
            string? durableId = null;            // durable id attached to this stream (Detach on end)
            string subscriptionId = "";
            long? replayedFrom = null;
            bool isDurable = false;
            Task? pumpTask = null;

            try
            {
                if (!string.IsNullOrEmpty(resumeSubscriptionId))
                {
                    // --- Resume a durable subscription (cross-transport) ---
                    var state = store.Lookup(resumeSubscriptionId);
                    if (state is null)
                        throw new HubException("Sleipnir subscription '" + resumeSubscriptionId +
                            "' not found (expired or never created). Re-subscribe fresh.");

                    // Re-run auth against the ORIGINAL route (recorded on the durable state, not the
                    // client-claimed one — a caller cannot lie about the route to land a weaker check).
                    var authError = await service.AuthorizeSubscribeAsync(
                        state.Controller!, state.Method!, Context.GetHttpContext());
                    if (authError != null)
                    {
                        store.Destroy(resumeSubscriptionId);
                        throw new HubException(authError.Error?.Message ?? "Subscribe unauthorized.");
                    }

                    tap = state.Attach(lastEventId ?? 0);
                    durableId = tap.SubscriptionId;
                    subscriptionId = durableId;
                    replayedFrom = tap.ReplayedFrom;    // first replayed eventId (null when nothing buffered)
                    isDurable = true;
                    store.OnAttached();
                    // Bounded send buffer (DropOldest) between the unbounded tap and the stream —
                    // evicted-oldest live frames remain in the replay ring for a later resume.
                    sendBuffer = new EventBuffer(_defaultBufferCapacity, EventBackpressureStrategy.DropOldest, ct);
                }
                else
                {
                    // --- Fresh subscribe ---
                    if (request is null)
                        throw new HubException("Sleipnir subscribe request is required for a fresh subscribe.");
                    request.Id = "signalr";

                    var result = await service.SubscribeAsync(request, Context.GetHttpContext(), ct);
                    if (result.Error != null)
                        throw new HubException(result.Error.Error?.Message ?? "Subscribe failed.");

                    if (result.Resumable)
                    {
                        var state = store.BeginCreate(result.EventBackpressureStrategy);
                        if (state is null)
                            throw new HubException("Sleipnir durable subscription cap reached — retry later.");

                        state.Controller = request.Controller;
                        state.Method = request.Method;
                        // Subscribe the observer BEFORE Attach so events produced before the attach
                        // snapshot land in the replay ring (the attach then replays them — no lost
                        // events on the create path), mirroring the SSE/WS durable create.
                        state.SourceSubscription = result.Observable!.Subscribe(
                            new DurableEventObserver<object?>(state, logger));
                        tap = state.Attach(0);
                        durableId = tap.SubscriptionId;
                        subscriptionId = durableId;
                        replayedFrom = tap.ReplayedFrom;    // null on a fresh durable subscribe
                        isDurable = true;
                        store.OnAttached();
                        sendBuffer = new EventBuffer(_defaultBufferCapacity, EventBackpressureStrategy.DropOldest, ct);
                    }
                    else
                    {
                        subscriptionId = Guid.NewGuid().ToString("N");
                        var capacity = result.EventBufferCapacity > 0 ? result.EventBufferCapacity : _defaultBufferCapacity;
                        ephemeral = new EphemeralSubscriptionState(
                            subscriptionId, capacity, result.EventBackpressureStrategy, ct);
                        // Subscribe BEFORE the ack: synchronous-cold frames land in the buffer (no
                        // loss); the stream (which drains the buffer) only runs after the ack is
                        // yielded — ack-before-first-frame without a hot-observable event-loss window.
                        ephemeral.Disposable = result.Observable!.Subscribe(
                            new EventObserver<object?>(ephemeral, subscriptionId, logger));
                        registry.IncSubscription();
                    }
                }

                // Ack first — ack-before-first-frame invariant (carried as the first stream item).
                yield return EventFrame.Ack(subscriptionId, replayedFrom);

                if (isDurable)
                {
                    // A background pump drains the unbounded live tap into the bounded send buffer
                    // (drops oldest on overflow — they stay in the replay ring). The stream drains
                    // the send buffer. The pump completes the send buffer when the tap ends.
                    var pumpCt = ct;
                    pumpTask = Task.Run(() => PumpDurableAsync(tap!, sendBuffer!, subscriptionId, pumpCt), ct);

                    await foreach (var frame in sendBuffer!.ReadAllAsync(ct))
                        yield return frame;
                }
                else
                {
                    await foreach (var frame in ephemeral!.Buffer.ReadAllAsync(ct))
                        yield return frame;
                }
            }
            finally
            {
                // Ensure the pump no longer feeds the send buffer, then await it before cleanup.
                if (pumpTask is not null)
                {
                    try { await pumpTask; } catch { /* pump errors logged in PumpDurableAsync */ }
                }

                // Ephemeral: dispose the source subscription + complete the buffer. The gauge was
                // Inc'd above; Dec it here (the store is not involved for ephemeral subs).
                if (ephemeral is not null)
                {
                    ephemeral.Dispose();
                    registry.DecSubscription();
                }

                // Durable: DETACH (not destroy) — the source + replay ring persist for a resume on
                // reconnect / cross-transport. store.Detach decrements the gauge (symmetric with
                // OnAttached at subscribe), so do NOT also Dec here — that would double-decrement.
                if (durableId is not null)
                {
                    store.Detach(durableId);
                }
            }
        }

        /// <summary>Drains the durable live tap into the bounded send buffer; completes the buffer when the tap ends.</summary>
        private async Task PumpDurableAsync(Tap tap, EventBuffer sendBuffer, string subscriptionId, CancellationToken ct)
        {
            // SleipnirMetrics.EventDropped bumps the registry accumulator (via Current) AND the OTel
            // counter — a single call is enough (mirrors the SSE/store durable OnDropped).
            void OnDropped()
            {
                SleipnirMetrics.EventDropped(subscriptionId);
                logger.LogWarning("SignalR event dropped for subscription {SubscriptionId} (send buffer full)", subscriptionId);
            }

            try
            {
                await foreach (var frame in tap.Reader.ReadAllAsync(ct))
                    sendBuffer.TryEnqueue(frame, OnDropped);
            }
            catch (OperationCanceledException) { /* disconnect / stream cancel */ }
            catch (Exception ex)
            {
                logger.LogError(ex, "SignalR durable pump failed for subscription {SubscriptionId}", subscriptionId);
            }
            finally
            {
                sendBuffer.Complete();   // signal the stream to drain remaining frames and end
            }
        }
    }
}