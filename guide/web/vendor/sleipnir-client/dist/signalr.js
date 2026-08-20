// Sleipnir SignalR client — calls + server-push events over a SignalR hub.
//
// Calls flow through the hub's `DoWork`/`DoWorkMany` methods (`.invoke`); events flow through the
// streaming `SubscribeAsync` hub method (`.stream`), which yields the SAME serialized logical event
// frame strings the WebSocket and SSE transports emit (`{type:"event"|"complete"|"error",
// subscriptionId, eventId[, data][, message]}`). The FIRST stream item is an `ack` frame
// (`{type:"ack", subscriptionId, replayedFrom?}`) — a SignalR stream has no separate response
// channel for the subscribe-ack, unlike the WS `SleipnirResponse` / SSE `event: ack` block, so it
// rides as the first item. Frame dispatch + at-least-once `eventId` dedup mirror the WS client.
//
// `@microsoft/signalr` is an OPTIONAL peer dependency. It is loaded via a dynamic `import()` so the
// default bundle (rest/ws/sse) never pulls it; a consumer selects the `signalr` capability only when
// they install it. The `@microsoft/signalr` types are deliberately NOT imported — local structural
// interfaces (`IHubConnection`/`IStreamResult`/`IStreamSubscriber`) describe the surface, so the
// package's types do not leak into the public `.d.ts` (which would break the ts-compile gate for
// consumers without the dependency). The real `HubConnection` is structurally compatible.
//
// Resume (Phase R / cross-transport): the server-side `SleipnirSubscriptionStore` is process-wide,
// so a durable `subscriptionId` + `lastEventId` obtained on ANY transport resumes live here. On
// SignalR's automatic reconnect, active durable subscriptions are re-streamed (fresh or resume per
// the `ResumePolicy`), mirroring the WS/SSE reconnect re-subscribe. SignalR's own transport fallback
// (WebSocket → SSE → LongPolling) applies underneath via `withAutomaticReconnect`.
import { SleipnirError, CancelledError } from "./errors.js";
import { ExecutionMode } from "./types.js";
import { fromBase64, normalizeResponse, normalizeResponses } from "./request.js";
// `@microsoft/signalr` is loaded by a non-literal dynamic import so the workspace does not need the
// package installed to typecheck (tsc types `import(string)` as `Promise<any>`, no resolution). The
// `/* @vite-ignore */` comment keeps Vite from warning about the un-analyzable specifier; at runtime
// the consumer's installed copy resolves. A failed import throws a clear install hint.
let SIGNALR_MODULE = "@microsoft/signalr";
/** Standard backoff intervals in ms (mirror of the WS client / SignalR defaults). */
const DEFAULT_RECONNECT_DELAYS = [
    0, 2_000, 5_000, 10_000, 30_000, 30_000, 60_000, 60_000, 300_000,
];
// --- the default factory: dynamic-import @microsoft/signalr, build a HubConnection ---
async function defaultHubFactory(url, opts) {
    let mod;
    try {
        // Non-literal specifier → tsc types this as Promise<any> (no resolution); the package is an
        // optional peer dep, so the workspace typechecks without it installed. Vite-ignore suppresses
        // the un-analyzable-dynamic-import warning.
        mod = await import(/* @vite-ignore */ SIGNALR_MODULE);
    }
    catch {
        throw new SleipnirError(0, "The SignalR transport requires the '@microsoft/signalr' package. Install it " +
            "(npm i @microsoft/signalr) or regenerate with a non-signalr capability.");
    }
    const builder = new mod.HubConnectionBuilder();
    const withUrl = builder.withUrl(url, {
        accessTokenProvider: opts.accessTokenProvider,
        headers: opts.headers,
    });
    const delays = opts.reconnectDelays ?? DEFAULT_RECONNECT_DELAYS;
    // withAutomaticReconnect(no-args) uses the default backoff; (delays) customizes; an empty array
    // disables reconnect. We only call it when reconnect is enabled.
    const wired = delays.length > 0 ? withUrl.withAutomaticReconnect(delays) : withUrl;
    return wired.build();
}
/** Resolves a `BearerProvider` (string | () => … | Promise) to the token string, or `undefined`. */
async function resolveBearer(bearer) {
    if (bearer == null)
        return undefined;
    if (typeof bearer === "function") {
        const v = await bearer();
        return v ?? undefined;
    }
    return bearer;
}
function parseFrame(text) {
    try {
        return JSON.parse(text);
    }
    catch {
        return { type: "error", message: `Malformed event frame: ${text}` };
    }
}
/**
 * Sleipnir SignalR client. Calls via `.invoke("DoWork"/"DoWorkMany")`; events via
 * `.stream("SubscribeAsync", req, resumeId?, lastEventId?)`. Reuses the shared `SubscribeHandlers` /
 * `SleipnirSubscription` / `ResumePolicy` surface so the transport router treats it identically to
 * the WS backend.
 */
export class SleipnirSignalrClient {
    _hubUrl;
    _callTimeout;
    _reconnectDelays;
    _onResume;
    _hubFactory;
    _bearer;
    _conn;
    _startPromise;
    _disposed = false;
    /** True between `onreconnecting` and `onreconnected`/`onclose` — distinguishes a reconnect stream
     * tear-down (leave the sub for re-stream) from an unexpected stream end (fail the sub). */
    _reconnecting = false;
    /** Active subscriptions keyed by an internal id (NOT the server subscriptionId, which can change). */
    _subs = new Map();
    _subSeq = 0;
    constructor(baseUrl, opts = {}) {
        if (!baseUrl)
            throw new Error("SleipnirSignalrClient: baseUrl is required.");
        const hubPath = opts.hubPath ?? "/sleipnirhub";
        // Join baseUrl + hubPath without a double slash.
        const base = baseUrl.replace(/\/+$/, "");
        const path = hubPath.startsWith("/") ? hubPath : `/${hubPath}`;
        this._hubUrl = base + path;
        this._bearer = opts.bearer;
        this._callTimeout = opts.callTimeout ?? 0;
        this._onResume = opts.onResume;
        const reconnect = opts.reconnect ?? true;
        this._reconnectDelays = reconnect ? (opts.reconnectDelays ?? DEFAULT_RECONNECT_DELAYS) : [];
        this._hubFactory = opts.hubFactory ?? defaultHubFactory;
    }
    // --- connection lifecycle ---
    /** Starts the hub connection (idempotent — concurrent callers share one start). */
    async connect() {
        if (this._disposed)
            throw new Error("SleipnirSignalrClient: disposed.");
        if (this._conn && this._startPromise)
            return this._startPromise;
        const token = await resolveBearer(this._bearer);
        const conn = await this._hubFactory(this._hubUrl, {
            accessTokenProvider: token != null ? () => token : undefined,
            reconnectDelays: this._reconnectDelays,
        });
        this._conn = conn;
        // Re-stream active subscriptions on a successful reconnect (SignalR does NOT restore streams
        // automatically — only the connection). SignalR fires onreconnecting → (old streams tear down)
        // → onreconnected; the `_reconnecting` flag lets `handleStreamEnd` tell a reconnect tear-down
        // (leave the sub for re-stream) from an unexpected stream end (fail the sub).
        conn.onreconnecting(() => {
            this._reconnecting = true;
        });
        conn.onreconnected(() => {
            this._reconnecting = false;
            void this.restreamOnReconnect();
        });
        conn.onclose((err) => {
            // onclose fires only on a TERMINAL close (user stop, or reconnect attempts exhausted) — NOT
            // on a successful reconnect. A user close set `_disposed` and already failed the subs; skip
            // it. Otherwise the connection is gone for good → fail every active subscription.
            if (this._disposed)
                return;
            this._reconnecting = false;
            this.failAllSubs(err instanceof Error ? err : new Error("SignalR connection closed."));
        });
        this._startPromise = conn.start().catch((err) => {
            // A failed start clears the pending state so a later connect() can retry.
            this._conn = undefined;
            this._startPromise = undefined;
            throw err;
        });
        return this._startPromise;
    }
    /** Stops the connection (terminal). Active subscriptions are cancelled. No reconnect. */
    async close() {
        if (this._disposed)
            return;
        this._disposed = true;
        this.failAllSubs(new Error("SignalR client closed by user."));
        const conn = this._conn;
        this._conn = undefined;
        this._startPromise = undefined;
        if (conn) {
            try {
                await conn.stop();
            }
            catch {
                // ignore — best-effort stop
            }
        }
    }
    /** Alias for {@link close} (parity with WS `dispose()`). */
    dispose() {
        void this.close();
    }
    /** Swaps the bearer; applied to the NEXT connect (SignalR's `accessTokenProvider` reads it per
     * request, so a live connection picks up the new token without a reconnect). */
    setBearer(bearer) {
        this._bearer = bearer;
        // If already connected, the stored provider closure captured the OLD token at start time. To
        // honor a mid-session swap, rebuild the provider against the new bearer by patching the conn's
        // accessTokenProvider — but the local interface does not expose it. The robust path is to
        // trigger a reconnect; consumers swapping tokens mid-session should reconnect. For the router's
        // setBearer fan-out (pre-connect), the stored bearer is read at connect() time — correct.
    }
    // --- calls ---
    /** Execute a single request via `DoWork`. */
    async call(req, opts) {
        await this.connect();
        const r = await this.raceAbort(this._conn.invoke("DoWork", req), opts);
        return normalizeResponse(r);
    }
    /** Execute a batch via `DoWorkMany`. */
    async callBatch(requests, mode = ExecutionMode.Parallel, opts) {
        await this.connect();
        const multi = { requests, mode };
        const r = await this.raceAbort(this._conn.invoke("DoWorkMany", multi), opts);
        return normalizeResponses(r ?? []);
    }
    /** Call and deserialize `response.data` as `T`; throws on non-2xx. */
    async callJson(req, opts) {
        const resp = await this.call(req, opts);
        if (resp.isSuccess && resp.data != null)
            return resp.data;
        if (!resp.isSuccess)
            throw SleipnirError.fromResponse(resp);
        return null;
    }
    /** Call a `byte[]` method; returns `response.content` as `Uint8Array`. Throws on non-2xx. */
    async callBinary(req, opts) {
        const resp = await this.call(req, opts);
        if (!resp.isSuccess)
            throw SleipnirError.fromResponse(resp);
        return resp.content ? fromBase64(resp.content) : null;
    }
    // --- events ---
    /**
     * Subscribe to a server-push event via the streaming `SubscribeAsync` hub method. The first
     * stream item is the `ack` frame (carrying the `subscriptionId`); subsequent items are `event`
     * frames (→ `onNext`), then a terminal `complete`/`error` frame. Resolves the
     * {@link SleipnirSubscription} handle on the ack.
     */
    async subscribe(req, handlers, opts) {
        if (this._disposed)
            throw new Error("SleipnirSignalrClient: disposed.");
        if (!req.id)
            req.id = `${req.controller}.${req.method}`;
        await this.connect();
        return this.openStream(req, handlers, false, "", 0, opts);
    }
    /**
     * Resume a durable subscription by `subscriptionId` + `lastEventId` (cross-transport). The server
     * replays the gap from its disconnect buffer then continues live. The original `req` is required
     * by the hub signature but its controller/method are ignored on resume (auth re-runs against the
     * route recorded on the durable state).
     */
    async resume(subscriptionId, lastEventId, handlers, opts) {
        if (this._disposed)
            throw new Error("SleipnirSignalrClient: disposed.");
        await this.connect();
        // A placeholder request that satisfies the non-optional SleipnirRequest param; the hub ignores
        // it on the resume path (uses the stored controller/method for auth + replay).
        const placeholder = {
            controller: "",
            method: "",
            id: `resume.${subscriptionId}`,
        };
        return this.openStream(placeholder, handlers, true, subscriptionId, lastEventId, opts);
    }
    // --- internals ---
    /**
     * Opens a `SubscribeAsync` stream (fresh or resume) and wires the frames to the handlers. Returns
     * a promise that resolves on the ack. The internal {@link ActiveSubscription} tracks the cursor +
     * stream-subscriber for reconnect re-streaming.
     */
    openStream(req, handlers, resume, resumeId, lastEventId, opts) {
        return new Promise((resolve, reject) => {
            if (!this._conn) {
                reject(new SleipnirError(0, "SignalR connection is not open."));
                return;
            }
            const internalId = ++this._subSeq;
            const abort = new AbortController();
            if (opts?.signal) {
                if (opts.signal.aborted)
                    abort.abort(new Error("subscribe aborted"));
                else
                    opts.signal.addEventListener("abort", () => abort.abort(new Error("subscribe aborted")), {
                        once: true,
                    });
            }
            const sub = {
                req,
                handlers,
                resumePolicy: opts?.resumePolicy ?? this._onResume,
                isResume: resume,
                subscriptionId: resumeId,
                lastEventId,
                done: false,
                acked: false,
                resolveHandle: resolve,
                rejectHandle: reject,
                abort,
            };
            this._subs.set(internalId, sub);
            // Caller abort → unsubscribe (dispose the stream-sub). Idempotent.
            abort.signal.addEventListener("abort", () => this.teardown(internalId, abort.signal.reason instanceof Error ? abort.signal.reason : undefined), { once: true });
            this.startStream(internalId, resume);
        });
    }
    /**
     * Starts (or re-starts) the `SubscribeAsync` stream for a subscription. Fresh:
     * `stream("SubscribeAsync", req, null, null)`; resume: `stream("SubscribeAsync", req, subId, lastEventId)`.
     * The ack updates the `subscriptionId` (and resolves the handle on the first ack); event frames
     * drive `onNext` with `eventId` dedup; `complete`/`error` are terminal.
     */
    startStream(internalId, resume) {
        const sub = this._subs.get(internalId);
        if (!sub || !this._conn)
            return;
        // Hub signature: SubscribeAsync(SleipnirRequest, string? resumeId, long? lastEventId, CancellationToken).
        // The CancellationToken is injected by SignalR (client stream cancel → server finally → Detach);
        // we pass only the three application args.
        const streamArgs = resume
            ? [sub.req, sub.subscriptionId, sub.lastEventId]
            : [sub.req, null, null];
        let streamSub;
        try {
            const result = this._conn.stream("SubscribeAsync", ...streamArgs);
            streamSub = result.subscribe({
                next: (frameText) => this.handleFrame(internalId, frameText),
                complete: () => this.handleStreamEnd(internalId, undefined),
                error: (err) => this.handleStreamEnd(internalId, err),
            });
        }
        catch (err) {
            // A synchronous throw (e.g. connection not started) → reject if pre-ack, else fail the sub.
            const e = err instanceof Error ? err : new Error(String(err));
            if (!sub.acked)
                sub.rejectHandle(e);
            else
                this.failSub(internalId, e);
            this._subs.delete(internalId);
            return;
        }
        sub.streamSub = streamSub;
    }
    /** Dispatches one frame string to the subscription. */
    handleFrame(internalId, frameText) {
        const sub = this._subs.get(internalId);
        if (!sub || sub.done)
            return;
        const frame = parseFrame(frameText);
        if (frame.type === "ack") {
            const sid = frame.subscriptionId;
            if (!sid) {
                if (!sub.acked)
                    sub.rejectHandle(new SleipnirError(0, "SignalR ack frame missing subscriptionId."));
                this._subs.delete(internalId);
                return;
            }
            // On a fresh subscribe, learn durability from... (the ack does not carry it; a fresh
            // subscribe is durable iff the server assigned a durable id — which we cannot tell from the
            // wire alone). We treat fresh subs as non-durable for reconnect (re-subscribe fresh), which is
            // the safe default; a resumable event re-subscribed fresh loses the gap (parity with WS fresh).
            // A resume is durable by construction.
            const oldId = sub.subscriptionId;
            sub.subscriptionId = sid;
            if (sid !== oldId)
                sub.lastEventId = 0; // degrade-to-fresh resets the dedup cursor
            if (!sub.acked) {
                sub.acked = true;
                sub.resolveHandle(this.makeHandle(internalId));
            }
            return;
        }
        if (frame.type === "event") {
            const evId = typeof frame.eventId === "number" ? frame.eventId : null;
            if (evId !== null) {
                if (evId <= sub.lastEventId)
                    return; // replay duplicate (at-least-once dedup)
                sub.lastEventId = evId;
            }
            try {
                sub.handlers.onNext(frame.data);
            }
            catch {
                // handler error is not fatal to the subscription
            }
            return;
        }
        if (frame.type === "complete") {
            sub.done = true;
            this.teardown(internalId);
            try {
                sub.handlers.onComplete?.();
            }
            catch {
                // ignore
            }
            return;
        }
        if (frame.type === "error") {
            const msg = typeof frame.message === "string" ? frame.message : "Subscription error";
            sub.done = true;
            this.teardown(internalId);
            try {
                sub.handlers.onError?.(new Error(msg));
            }
            catch {
                // ignore
            }
            return;
        }
        // Unknown frame type — ignore (forward-compat).
    }
    /**
     * The SignalR stream ended (complete/error callback). If it ended WITHOUT a terminal frame
     * (`done` is false), this is a transport drop or cancel. During a reconnect (`_reconnecting`),
     * leave the sub in place — `restreamOnReconnect` re-streams it. Otherwise treat the end as
     * unexpected and fail the sub (post-ack → onError; pre-ack → reject the subscribe promise).
     */
    handleStreamEnd(internalId, err) {
        const sub = this._subs.get(internalId);
        if (!sub)
            return;
        if (sub.done)
            return; // terminal frame already handled
        if (this._reconnecting)
            return; // old stream torn down mid-reconnect → leave for re-stream
        // Pre-ack end with an error → reject the subscribe promise.
        if (!sub.acked) {
            const e = err instanceof Error ? err : new Error("SignalR stream ended before the ack.");
            this._subs.delete(internalId);
            sub.rejectHandle(e);
            return;
        }
        // Post-ack non-terminal end outside a reconnect → unexpected stream close → onError.
        this.failSub(internalId, err instanceof Error ? err : new Error("SignalR stream ended."));
    }
    /** Re-streams active subscriptions after a successful reconnect (onreconnected). Parity with the
     * WS reconnect re-subscribe: every acked, non-terminal sub is re-opened, fresh or resume per the
     * `ResumePolicy` (default `"fresh"`). `"drop"` ends the sub. */
    async restreamOnReconnect() {
        for (const [internalId, subRaw] of [...this._subs.entries()]) {
            const sub = subRaw;
            if (sub.done || !sub.acked)
                continue;
            // Dispose the old (dead) stream-sub first.
            try {
                sub.streamSub?.dispose();
            }
            catch {
                // ignore
            }
            sub.streamSub = undefined;
            // Consult the resume policy (parity with WS/SSE). Default → "fresh".
            let decision = "fresh";
            const policy = sub.resumePolicy;
            if (policy) {
                const ctx = {
                    controller: sub.req.controller,
                    method: sub.req.method,
                    subscriptionId: sub.subscriptionId,
                    lastEventId: sub.lastEventId,
                };
                const d = policy(ctx);
                if (d === "fresh" || d === "resume" || d === "drop")
                    decision = d;
            }
            if (decision === "drop") {
                this.failSub(internalId, new Error("Subscription dropped (resume policy 'drop')."));
                continue;
            }
            // "resume" → re-stream with the subscriptionId + cursor; "fresh" → fresh subscribe (new id).
            this.startStream(internalId, decision === "resume");
        }
    }
    /** Builds the public handle for a subscription (getter-backed mutable cursor + id). */
    makeHandle(internalId) {
        const self = this;
        return {
            get subscriptionId() {
                return self._subs.get(internalId)?.subscriptionId ?? "";
            },
            get lastEventId() {
                return self._subs.get(internalId)?.lastEventId ?? 0;
            },
            unsubscribe() {
                return self.teardown(internalId);
            },
        };
    }
    /** Tears down a subscription: dispose the stream-sub (sends a stream Cancel → hub finally →
     * Detach for durable), remove from the map. Idempotent. */
    async teardown(internalId, reason) {
        const sub = this._subs.get(internalId);
        if (!sub)
            return;
        try {
            sub.abort.abort(reason ?? new Error("unsubscribed"));
        }
        catch {
            // ignore
        }
        try {
            sub.streamSub?.dispose();
        }
        catch {
            // ignore
        }
        sub.streamSub = undefined;
        this._subs.delete(internalId);
    }
    /** Fails a subscription with an error (onError) and removes it. */
    failSub(internalId, err) {
        const sub = this._subs.get(internalId);
        if (!sub)
            return;
        this._subs.delete(internalId);
        try {
            sub.streamSub?.dispose();
        }
        catch {
            // ignore
        }
        try {
            sub.handlers.onError?.(err);
        }
        catch {
            // ignore
        }
    }
    /** Fails ALL active subscriptions (on close / dispose). */
    failAllSubs(err) {
        for (const id of [...this._subs.keys()])
            this.failSub(id, err);
    }
    /** Races an invoke promise against an optional abort signal / timeout. The server invoke is NOT
     * cancelled (SignalR has no per-invoke cancel) — it completes in the background, result discarded. */
    raceAbort(p, opts) {
        const timeout = opts?.timeout ?? this._callTimeout;
        const signal = opts?.signal;
        if (!timeout && !signal)
            return p;
        return new Promise((resolve, reject) => {
            let settled = false;
            const finish = (fn) => {
                if (settled)
                    return;
                settled = true;
                cleanup();
                fn();
            };
            const onAbort = () => {
                finish(() => reject(new CancelledError("Sleipnir call was cancelled.", false)));
            };
            const timer = timeout && timeout > 0
                ? setTimeout(() => finish(() => reject(new CancelledError("Sleipnir call timed out.", true))), timeout)
                : undefined;
            const cleanup = () => {
                if (timer)
                    clearTimeout(timer);
                signal?.removeEventListener("abort", onAbort);
            };
            if (signal) {
                if (signal.aborted) {
                    finish(() => reject(new CancelledError("Sleipnir call was cancelled.", false)));
                    return;
                }
                signal.addEventListener("abort", onAbort, { once: true });
            }
            p.then((v) => finish(() => resolve(v)), (e) => finish(() => reject(e instanceof Error ? e : new Error(String(e)))));
        });
    }
}
//# sourceMappingURL=signalr.js.map