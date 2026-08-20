// Unified transport router for the generated Sleipnir client.
//
// The generated `SleipnirClient` (one shape across every `--transport` capability) delegates
// here. The router holds the bundled backends, routes `call`/`callBatch` to the active call
// backend and `subscribe` to the active event backend, and implements the `auto` negotiation
// (try WebSocket, fall back to REST+SSE on failure). The WS-vs-SSE subscribe signature
// mismatch is bridged once, here, so the generated client stays thin and transport-identical.
//
// Capability asymmetry (no single native transport does both calls AND events except WS):
//   REST  -> calls only        SSE  -> events only
//   WS    -> calls + events     SignalR -> calls + events (Phase 3, opt-in)
// A user-facing "transport" is therefore a PROFILE that picks a {call, event} backend pair:
//   "rest"     -> calls=REST,  events=SSE   (HTTP-only, proxy-safe)
//   "ws"       -> calls=WS,    events=WS
//   "signalr"  -> calls=SignalR, events=SignalR (Phase 3)
//   "auto"     -> probe WS; success -> ws profile, failure -> rest profile
import { ExecutionMode } from "./types.js";
import { SleipnirRestClient } from "./rest.js";
import { SleipnirWebSocketClient, } from "./websocket.js";
import { SleipnirSseClient } from "./sse.js";
import { SleipnirSignalrClient, } from "./signalr.js";
/** Backends bundled per capability. */
const CAPABILITY_BACKEDS = {
    rest: ["rest", "sse"],
    ws: ["ws"],
    all: ["rest", "ws", "sse"],
    signalr: ["rest", "ws", "sse", "signalr"],
};
/** Thrown when `useTransport`/`negotiate` selects a backend not present in the bundle. */
export class SleipnirTransportNotBundledError extends Error {
    transport;
    capability;
    constructor(transport, capability) {
        super(`Sleipnir transport '${transport}' is not available: the client was generated with --transport ${capability}, which does not bundle the required backend. Regenerate with a capability that includes it (e.g. --transport all).`);
        this.transport = transport;
        this.capability = capability;
        this.name = "SleipnirTransportNotBundledError";
    }
}
function hasBackend(cap, b) {
    return CAPABILITY_BACKEDS[cap].includes(b);
}
/**
 * Unified transport router. Holds the bundled backends, routes calls and subscriptions to the
 * active profile, and negotiates `auto`. The generated `SleipnirClient` is a thin facade over
 * this class; it never branches on transport itself.
 */
export class SleipnirTransportRouter {
    capability;
    _rest;
    _ws;
    _sse;
    _signalr;
    _probeTimeout;
    /** Active profile (set by `negotiate`/`useTransport`). `null` until first resolution. */
    _profile = null;
    _negotiatePromise = null;
    _disposed = false;
    constructor(opts) {
        if (!opts?.baseUrl)
            throw new Error("SleipnirTransportRouter: baseUrl is required.");
        this.capability = opts.capability;
        this._probeTimeout = opts.probeTimeout ?? 1500;
        const bearer = opts.bearer;
        const callTimeout = opts.callTimeout;
        if (hasBackend(this.capability, "rest")) {
            this._rest = new SleipnirRestClient(opts.baseUrl, { ...(opts.rest ?? {}), bearer, callTimeout });
        }
        if (hasBackend(this.capability, "ws")) {
            this._ws = new SleipnirWebSocketClient(opts.baseUrl, { ...(opts.ws ?? {}), bearer, callTimeout });
        }
        if (hasBackend(this.capability, "sse")) {
            this._sse = new SleipnirSseClient(opts.baseUrl, { ...(opts.sse ?? {}), bearer });
        }
        if (hasBackend(this.capability, "signalr")) {
            this._signalr = new SleipnirSignalrClient(opts.baseUrl, { ...(opts.signalr ?? {}), bearer });
        }
        // Resolve the initial profile. A non-auto default is set immediately; "auto" is probed
        // lazily on first use (avoid constructor side-effects / connect races).
        const initial = opts.defaultTransport ?? "auto";
        if (initial !== "auto") {
            this._profile = this.resolveProfile(initial);
        }
    }
    // --- escape hatches (raw backends; undefined if not bundled) ---
    /** Underlying REST client (escape hatch). `undefined` if not bundled. */
    get rest() {
        return this._rest;
    }
    /** Underlying WebSocket client (escape hatch). `undefined` if not bundled. */
    get ws() {
        return this._ws;
    }
    /** Underlying SSE client (escape hatch). `undefined` if not bundled. */
    get sse() {
        return this._sse;
    }
    /** Underlying SignalR client (escape hatch). `undefined` if not bundled. */
    get signalr() {
        return this._signalr;
    }
    /** Current active profile (the resolved transport). `null` until first use when `auto`. */
    get activeTransport() {
        return this._profile;
    }
    // --- profile resolution ---
    /**
     * Maps a user-facing transport to a concrete {call, event} backend pair, validating that both
     * backends are bundled. Throws {@link SleipnirTransportNotBundledError} if a required backend is
     * missing, or `Error` if the profile isn't implemented yet (SignalR pre-Phase-3).
     */
    resolveProfile(t) {
        if (t === "rest") {
            if (!this._rest || !this._sse)
                throw new SleipnirTransportNotBundledError(t, this.capability);
            return "rest";
        }
        if (t === "ws") {
            if (!this._ws)
                throw new SleipnirTransportNotBundledError(t, this.capability);
            return "ws";
        }
        if (t === "signalr") {
            if (!this._signalr)
                throw new SleipnirTransportNotBundledError(t, this.capability);
            return "signalr";
        }
        // "auto" is resolved via negotiate(), never here.
        throw new Error(`Sleipnir transport '${t}' is not a valid profile.`);
    }
    /** Backend used for calls under the active profile. */
    callBackend() {
        if (this._profile === "ws")
            return "ws";
        if (this._profile === "signalr")
            return "signalr";
        return "rest"; // "rest" profile
    }
    /** Backend used for events under the active profile. */
    eventBackend() {
        if (this._profile === "ws")
            return "ws";
        if (this._profile === "signalr")
            return "signalr";
        return "sse"; // "rest" profile
    }
    /**
     * Ensures the active profile is resolved. For `"auto"` this runs the WS handshake probe once
     * (lazy, concurrent-safe): on success the WS profile is used; on failure/timeout the rest
     * profile (REST calls + SSE events) is used. Subsequent calls reuse the resolved profile.
     */
    async negotiate() {
        if (this._disposed)
            throw new Error("SleipnirTransportRouter: disposed.");
        if (this._profile)
            return;
        if (!this._negotiatePromise) {
            this._negotiatePromise = this.runAutoNegotiation();
        }
        await this._negotiatePromise;
    }
    async runAutoNegotiation() {
        // `auto` needs WS to probe. Without WS bundled, fall back to the rest profile immediately.
        if (!this._ws) {
            if (!this._rest || !this._sse)
                throw new SleipnirTransportNotBundledError("auto", this.capability);
            this._profile = "rest";
            return;
        }
        // Race the WS handshake against the probe timeout. On timeout the background connect is
        // left to complete (or fail on its own connectTimeout) — it is not routed to, so an idle
        // socket is the only cost; a later useTransport("ws") can still adopt it. Phase 2 hardens
        // this (explicit cleanup / reconnect-timeout decoupling).
        let ok = false;
        try {
            const timeout = new Promise((r) => {
                setTimeout(() => r("timeout"), this._probeTimeout);
            });
            const result = await Promise.race([
                this._ws.connect().then(() => "ok"),
                timeout,
            ]);
            ok = result === "ok";
        }
        catch {
            ok = false;
        }
        this._profile = ok ? "ws" : "rest";
        if (this._profile === "rest" && (!this._rest || !this._sse)) {
            // WS failed but the fallback backends aren't bundled (e.g. --transport ws) — nothing to
            // fall back to. Surface a clear error rather than a silent null route.
            throw new SleipnirTransportNotBundledError("auto", this.capability);
        }
    }
    /**
     * Switches the active transport profile at runtime. Must target a profile whose backends are
     * bundled. `"auto"` re-runs negotiation. Throws on an unbundled profile.
     */
    async useTransport(t) {
        if (this._disposed)
            throw new Error("SleipnirTransportRouter: disposed.");
        if (t === "auto") {
            this._profile = null;
            this._negotiatePromise = null;
            await this.negotiate();
            return;
        }
        this._profile = this.resolveProfile(t);
    }
    // --- call routing ---
    /** Execute a single request over the active call backend. */
    async call(req, opts) {
        await this.ensureProfile();
        const backend = this.callBackend();
        if (backend === "ws")
            return this._ws.call(req, opts);
        if (backend === "signalr")
            return this._signalr.call(req, opts);
        return this._rest.call(req, opts);
    }
    /** Execute a batch over the active call backend. */
    async callBatch(requests, mode = ExecutionMode.Parallel, opts) {
        await this.ensureProfile();
        const backend = this.callBackend();
        if (backend === "ws")
            return this._ws.callBatch(requests, mode, opts);
        if (backend === "signalr")
            return this._signalr.callBatch(requests, mode, opts);
        return this._rest.callBatch(requests, mode, opts);
    }
    /** Build a multi-request from the fluent batch (transport-agnostic). */
    toMulti(requests, mode) {
        return { requests, mode };
    }
    // --- subscribe routing (the WS-vs-SSE mismatch bridged here) ---
    /**
     * Subscribe to a server-push event over the active event backend.
     *
     * - WS / SignalR: the pre-built {@link SleipnirRequest} is passed straight through.
     * - SSE: the request is unpacked into `(controller, method, params)` because SSE carries method
     *   arguments as URL query params (no body). Only named params (`parameterName`) are expressible
     *   over SSE; positional/binary params are a WS/SignalR-only capability.
     */
    async subscribe(req, handlers, opts) {
        await this.ensureProfile();
        const backend = this.eventBackend();
        if (backend === "ws") {
            const wsOpts = opts
                ? { signal: opts.signal, timeout: opts.timeout, resumePolicy: opts.resumePolicy }
                : undefined;
            return this._ws.subscribe(req, handlers, wsOpts);
        }
        if (backend === "signalr") {
            const srOpts = opts
                ? { signal: opts.signal, resumePolicy: opts.resumePolicy }
                : undefined;
            return this._signalr.subscribe(req, handlers, srOpts);
        }
        // SSE: unpack the request into (controller, method, params).
        const params = {};
        for (const p of req.params ?? [])
            params[p.parameterName] = p.data;
        const sseOpts = opts
            ? { signal: opts.signal, resumePolicy: opts.resumePolicy, headers: opts.headers }
            : undefined;
        return this._sse.subscribe(req.controller, req.method, handlers, params, sseOpts);
    }
    /**
     * Resume a durable event subscription over the active event backend — the cross-transport
     * bridge used after a transport switch (e.g. `auto` WS→REST+SSE fallback). The server-side
     * `SleipnirSubscriptionStore` is process-wide, so a `subscriptionId` + `lastEventId` obtained
     * from a {@link SleipnirSubscription} on one transport resume live on another.
     *
     * - SSE backend: opens the resume URL directly (`GET /events/{subscriptionId}?lastEventId=…`);
     *   no controller/method/params are needed. The server replays the gap then continues live.
     * - WS backend: cross-transport resume INTO WebSocket needs the original controller/method
     *   (not carried by a subscription handle) — not supported. Switch to the `rest`/`auto`
     *   profile (`useTransport("rest")`) to resume over SSE.
     * - SignalR backend: resumes over the streaming `SubscribeAsync` hub method (the placeholder
     *   request satisfies the non-optional param; the server re-runs auth against the stored route).
     */
    async resume(subscriptionId, lastEventId, handlers, opts) {
        await this.ensureProfile();
        const backend = this.eventBackend();
        if (backend === "ws") {
            throw new Error("Sleipnir cross-transport resume into WebSocket is not supported (the WS resume frame needs the original controller/method). " +
                "Switch to the rest/auto profile via useTransport('rest') to resume over SSE.");
        }
        if (backend === "signalr") {
            const srOpts = opts
                ? { signal: opts.signal, resumePolicy: opts.resumePolicy }
                : undefined;
            return this._signalr.resume(subscriptionId, lastEventId, handlers, srOpts);
        }
        const sseOpts = opts
            ? { signal: opts.signal, resumePolicy: opts.resumePolicy, headers: opts.headers }
            : undefined;
        return this._sse.resume(subscriptionId, lastEventId, handlers, sseOpts);
    }
    // --- shared concerns ---
    /** Fan a bearer swap out to every bundled backend that accepts one. */
    setBearer(bearer) {
        this._rest?.setBearer(bearer);
        this._ws?.setBearer(bearer);
        this._sse?.setBearer(bearer);
        this._signalr?.setBearer(bearer);
    }
    /** Dispose all bundled backends (terminal). */
    dispose() {
        this._disposed = true;
        this._ws?.close();
        this._signalr?.dispose();
        // REST + SSE are stateless / per-subscribe; nothing to dispose.
    }
    async ensureProfile() {
        if (this._profile)
            return;
        await this.negotiate();
    }
}
//# sourceMappingURL=transport-router.js.map