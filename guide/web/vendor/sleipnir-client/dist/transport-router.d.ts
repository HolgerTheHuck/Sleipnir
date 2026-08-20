import { ExecutionMode } from "./types.js";
import type { BearerProvider, SleipnirMultiRequest, SleipnirRequest, SleipnirResponse } from "./types.js";
import { SleipnirRestClient, type SleipnirRestClientOptions, type CallOptions } from "./rest.js";
import { SleipnirWebSocketClient, type SleipnirWebSocketClientOptions, type WsCallOptions, type SubscribeHandlers, type SleipnirSubscription, type ResumePolicy } from "./websocket.js";
import { SleipnirSseClient, type SleipnirSseClientOptions } from "./sse.js";
import { SleipnirSignalrClient, type SleipnirSignalrClientOptions, type SignalrCallOptions } from "./signalr.js";
/**
 * User-facing transport selection — a *profile* mapping to a {call, event} backend pair, plus
 * `"auto"` for negotiation. Note: `"sse"` is intentionally NOT a standalone profile (SSE cannot
 * carry calls); the HTTP-only profile is `"rest"` (= REST calls + SSE events). The raw SSE
 * backend remains reachable via the `sse` escape hatch.
 */
export type SleipnirTransport = "auto" | "rest" | "ws" | "signalr";
/**
 * Codegen `--transport` capability — which backends to bundle. The public `SleipnirClient`
 * surface is identical across all capabilities; only the bundled backends differ.
 *   - `rest`     -> REST + SSE
 *   - `ws`       -> WS
 *   - `all`      -> REST + WS + SSE (enables `auto`: WS -> REST+SSE fallback)
 *   - `signalr`  -> REST + WS + SSE + SignalR (opt-in add-on; Phase 3 wires the SignalR backend)
 */
export type SleipnirBundleCapability = "rest" | "ws" | "all" | "signalr";
/** Thrown when `useTransport`/`negotiate` selects a backend not present in the bundle. */
export declare class SleipnirTransportNotBundledError extends Error {
    readonly transport: string;
    readonly capability: SleipnirBundleCapability;
    constructor(transport: string, capability: SleipnirBundleCapability);
}
/** Per-transport options for the router. Each sub-object is passed to its backend verbatim. */
export interface SleipnirRouterOptions {
    /** Server base URL (scheme + host [+ port], e.g. `https://localhost:5001`). */
    baseUrl: string;
    /** Which backends to instantiate (codegen-derived from `--transport`). */
    capability: SleipnirBundleCapability;
    /** Default transport profile. Defaults to `"auto"`. */
    defaultTransport?: SleipnirTransport;
    /** Bearer applied to all bundled backends that accept one (overridden by sub-options). */
    bearer?: BearerProvider;
    /** Call timeout (ms) applied to REST + WS (overridden by sub-options). */
    callTimeout?: number;
    /** WS handshake probe timeout (ms) for `auto` negotiation. Default 1500. */
    probeTimeout?: number;
    /** REST backend options (bearer/callTimeout are injected from the shared fields). */
    rest?: Omit<SleipnirRestClientOptions, "bearer" | "callTimeout">;
    /** WebSocket backend options (bearer/callTimeout are injected from the shared fields). */
    ws?: Omit<SleipnirWebSocketClientOptions, "bearer" | "callTimeout">;
    /** SSE backend options (bearer is injected from the shared field). */
    sse?: Omit<SleipnirSseClientOptions, "bearer">;
    /** SignalR backend options (Phase 3; bearer is injected from the shared field). */
    signalr?: Omit<SleipnirSignalrClientOptions, "bearer">;
}
/** Unified subscribe options across event backends (WS / SSE / SignalR). */
export interface SleipnirSubscribeOptions {
    /** Abort signal — ends the subscription without reconnect. */
    signal?: AbortSignal;
    /** Per-subscription resume policy (overrides the client-wide policy). WS + SSE. */
    resumePolicy?: ResumePolicy;
    /** Per-call timeout (WS only; SSE has no call timeout). */
    timeout?: number;
    /** Extra headers for this subscribe request (SSE only). */
    headers?: Record<string, string>;
}
/**
 * Unified transport router. Holds the bundled backends, routes calls and subscriptions to the
 * active profile, and negotiates `auto`. The generated `SleipnirClient` is a thin facade over
 * this class; it never branches on transport itself.
 */
export declare class SleipnirTransportRouter {
    readonly capability: SleipnirBundleCapability;
    private readonly _rest?;
    private readonly _ws?;
    private readonly _sse?;
    private readonly _signalr?;
    private readonly _probeTimeout;
    /** Active profile (set by `negotiate`/`useTransport`). `null` until first resolution. */
    private _profile;
    private _negotiatePromise;
    private _disposed;
    constructor(opts: SleipnirRouterOptions);
    /** Underlying REST client (escape hatch). `undefined` if not bundled. */
    get rest(): SleipnirRestClient | undefined;
    /** Underlying WebSocket client (escape hatch). `undefined` if not bundled. */
    get ws(): SleipnirWebSocketClient | undefined;
    /** Underlying SSE client (escape hatch). `undefined` if not bundled. */
    get sse(): SleipnirSseClient | undefined;
    /** Underlying SignalR client (escape hatch). `undefined` if not bundled. */
    get signalr(): SleipnirSignalrClient | undefined;
    /** Current active profile (the resolved transport). `null` until first use when `auto`. */
    get activeTransport(): Exclude<SleipnirTransport, "auto"> | null;
    /**
     * Maps a user-facing transport to a concrete {call, event} backend pair, validating that both
     * backends are bundled. Throws {@link SleipnirTransportNotBundledError} if a required backend is
     * missing, or `Error` if the profile isn't implemented yet (SignalR pre-Phase-3).
     */
    private resolveProfile;
    /** Backend used for calls under the active profile. */
    private callBackend;
    /** Backend used for events under the active profile. */
    private eventBackend;
    /**
     * Ensures the active profile is resolved. For `"auto"` this runs the WS handshake probe once
     * (lazy, concurrent-safe): on success the WS profile is used; on failure/timeout the rest
     * profile (REST calls + SSE events) is used. Subsequent calls reuse the resolved profile.
     */
    negotiate(): Promise<void>;
    private runAutoNegotiation;
    /**
     * Switches the active transport profile at runtime. Must target a profile whose backends are
     * bundled. `"auto"` re-runs negotiation. Throws on an unbundled profile.
     */
    useTransport(t: SleipnirTransport): Promise<void>;
    /** Execute a single request over the active call backend. */
    call(req: SleipnirRequest, opts?: CallOptions | WsCallOptions | SignalrCallOptions): Promise<SleipnirResponse>;
    /** Execute a batch over the active call backend. */
    callBatch(requests: SleipnirRequest[], mode?: ExecutionMode, opts?: CallOptions | WsCallOptions | SignalrCallOptions): Promise<SleipnirResponse[]>;
    /** Build a multi-request from the fluent batch (transport-agnostic). */
    toMulti(requests: SleipnirRequest[], mode: ExecutionMode): SleipnirMultiRequest;
    /**
     * Subscribe to a server-push event over the active event backend.
     *
     * - WS / SignalR: the pre-built {@link SleipnirRequest} is passed straight through.
     * - SSE: the request is unpacked into `(controller, method, params)` because SSE carries method
     *   arguments as URL query params (no body). Only named params (`parameterName`) are expressible
     *   over SSE; positional/binary params are a WS/SignalR-only capability.
     */
    subscribe<T>(req: SleipnirRequest, handlers: SubscribeHandlers<T>, opts?: SleipnirSubscribeOptions): Promise<SleipnirSubscription>;
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
    resume<T>(subscriptionId: string, lastEventId: number, handlers: SubscribeHandlers<T>, opts?: SleipnirSubscribeOptions): Promise<SleipnirSubscription>;
    /** Fan a bearer swap out to every bundled backend that accepts one. */
    setBearer(bearer: BearerProvider): void;
    /** Dispose all bundled backends (terminal). */
    dispose(): void;
    private ensureProfile;
}
//# sourceMappingURL=transport-router.d.ts.map