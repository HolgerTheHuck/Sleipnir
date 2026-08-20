import { ExecutionMode } from "./types.js";
import type { BearerProvider, SleipnirRequest, SleipnirResponse } from "./types.js";
import type { ResumePolicy, SubscribeHandlers, SleipnirSubscription } from "./websocket.js";
export type { ResumePolicy, SubscribeHandlers, SleipnirSubscription };
/** `IStreamSubscriber` — the handle returned by `IStreamResult.subscribe()`. `dispose()` cancels. */
export interface IStreamSubscriber {
    dispose(): void;
}
/** `IStreamResult<T>` — the result of `HubConnection.stream<T>()` (callback-based, NOT async-iterable in JS). */
export interface IStreamResult<T> {
    subscribe(observer: {
        next: (value: T) => void;
        complete?: () => void;
        error?: (err: unknown) => void;
    }): IStreamSubscriber;
}
/** `HubConnection` — the slice this client uses: invoke, stream, lifecycle, reconnect hooks. */
export interface IHubConnection {
    start(): Promise<void>;
    stop(): Promise<void>;
    invoke<T>(methodName: string, ...args: unknown[]): Promise<T>;
    stream<T>(methodName: string, ...args: unknown[]): IStreamResult<T>;
    onreconnecting(handler: (error?: unknown) => void): void;
    onreconnected(handler: (connectionId?: string) => void): void;
    onclose(handler: (error?: unknown) => void): void;
}
/** Options forwarded to the hub-connection builder (the `withUrl` options we care about). */
export interface SignalrBuildOptions {
    /** Bearer-token provider (called per request by SignalR — rotating JWTs work). */
    accessTokenProvider?: () => string | Promise<string> | null;
    /** Extra headers on the negotiate/transport requests. */
    headers?: Record<string, string>;
    /** Reconnect backoff (ms); empty disables auto-reconnect. Default {@link DEFAULT_RECONNECT_DELAYS}. */
    reconnectDelays?: number[];
}
/**
 * Injectable hub-connection factory. The default does the dynamic `@microsoft/signalr` import +
 * build; tests inject a fake `IHubConnection`. Returning a `Promise` lets the default stay async
 * (dynamic import) while a test factory can return a ready instance.
 */
export type SignalrHubFactory = (url: string, options: SignalrBuildOptions) => IHubConnection | Promise<IHubConnection>;
/** Options for {@link SleipnirSignalrClient}. */
export interface SleipnirSignalrClientOptions {
    /** Hub path appended to `baseUrl` (default `"/sleipnirhub"`). */
    hubPath?: string;
    /** Bearer token (Authorization) — string or provider function (rotating JWTs). */
    bearer?: BearerProvider;
    /** Auto-reconnect on unexpected disconnect (default `true`). */
    reconnect?: boolean;
    /** Backoff intervals in ms (default {@link DEFAULT_RECONNECT_DELAYS}). Empty disables reconnect. */
    reconnectDelays?: number[];
    /** Default call timeout (ms); `0`/unset = no timeout. */
    callTimeout?: number;
    /** Client-wide resume policy for reconnect re-subscribe (default `"fresh"`). */
    onResume?: ResumePolicy;
    /** Injectable factory (tests). Default: dynamic-import `@microsoft/signalr` builder. */
    hubFactory?: SignalrHubFactory;
}
/** Per-call options (mirror of WS `WsCallOptions`). */
export interface SignalrCallOptions {
    /** Abort signal — rejects with `CancelledError` (the server invoke completes in the background). */
    signal?: AbortSignal;
    /** Per-call timeout (ms); overrides the client-wide `callTimeout`. */
    timeout?: number;
}
/** Per-subscribe options (mirror of WS `SubscribeOptions`). */
export interface SignalrSubscribeOptions {
    /** Abort signal — ends the subscription without reconnect. */
    signal?: AbortSignal;
    /** Per-subscription resume policy (overrides the client-wide `onResume`). */
    resumePolicy?: ResumePolicy;
}
/**
 * Sleipnir SignalR client. Calls via `.invoke("DoWork"/"DoWorkMany")`; events via
 * `.stream("SubscribeAsync", req, resumeId?, lastEventId?)`. Reuses the shared `SubscribeHandlers` /
 * `SleipnirSubscription` / `ResumePolicy` surface so the transport router treats it identically to
 * the WS backend.
 */
export declare class SleipnirSignalrClient {
    private readonly _hubUrl;
    private readonly _callTimeout;
    private readonly _reconnectDelays;
    private readonly _onResume?;
    private readonly _hubFactory;
    private _bearer;
    private _conn?;
    private _startPromise?;
    private _disposed;
    /** True between `onreconnecting` and `onreconnected`/`onclose` — distinguishes a reconnect stream
     * tear-down (leave the sub for re-stream) from an unexpected stream end (fail the sub). */
    private _reconnecting;
    /** Active subscriptions keyed by an internal id (NOT the server subscriptionId, which can change). */
    private readonly _subs;
    private _subSeq;
    constructor(baseUrl: string, opts?: SleipnirSignalrClientOptions);
    /** Starts the hub connection (idempotent — concurrent callers share one start). */
    connect(): Promise<void>;
    /** Stops the connection (terminal). Active subscriptions are cancelled. No reconnect. */
    close(): Promise<void>;
    /** Alias for {@link close} (parity with WS `dispose()`). */
    dispose(): void;
    /** Swaps the bearer; applied to the NEXT connect (SignalR's `accessTokenProvider` reads it per
     * request, so a live connection picks up the new token without a reconnect). */
    setBearer(bearer: BearerProvider): void;
    /** Execute a single request via `DoWork`. */
    call(req: SleipnirRequest, opts?: SignalrCallOptions): Promise<SleipnirResponse>;
    /** Execute a batch via `DoWorkMany`. */
    callBatch(requests: SleipnirRequest[], mode?: ExecutionMode, opts?: SignalrCallOptions): Promise<SleipnirResponse[]>;
    /** Call and deserialize `response.data` as `T`; throws on non-2xx. */
    callJson<T>(req: SleipnirRequest, opts?: SignalrCallOptions): Promise<T | null>;
    /** Call a `byte[]` method; returns `response.content` as `Uint8Array`. Throws on non-2xx. */
    callBinary(req: SleipnirRequest, opts?: SignalrCallOptions): Promise<Uint8Array | null>;
    /**
     * Subscribe to a server-push event via the streaming `SubscribeAsync` hub method. The first
     * stream item is the `ack` frame (carrying the `subscriptionId`); subsequent items are `event`
     * frames (→ `onNext`), then a terminal `complete`/`error` frame. Resolves the
     * {@link SleipnirSubscription} handle on the ack.
     */
    subscribe<T>(req: SleipnirRequest, handlers: SubscribeHandlers<T>, opts?: SignalrSubscribeOptions): Promise<SleipnirSubscription>;
    /**
     * Resume a durable subscription by `subscriptionId` + `lastEventId` (cross-transport). The server
     * replays the gap from its disconnect buffer then continues live. The original `req` is required
     * by the hub signature but its controller/method are ignored on resume (auth re-runs against the
     * route recorded on the durable state).
     */
    resume<T>(subscriptionId: string, lastEventId: number, handlers: SubscribeHandlers<T>, opts?: SignalrSubscribeOptions): Promise<SleipnirSubscription>;
    /**
     * Opens a `SubscribeAsync` stream (fresh or resume) and wires the frames to the handlers. Returns
     * a promise that resolves on the ack. The internal {@link ActiveSubscription} tracks the cursor +
     * stream-subscriber for reconnect re-streaming.
     */
    private openStream;
    /**
     * Starts (or re-starts) the `SubscribeAsync` stream for a subscription. Fresh:
     * `stream("SubscribeAsync", req, null, null)`; resume: `stream("SubscribeAsync", req, subId, lastEventId)`.
     * The ack updates the `subscriptionId` (and resolves the handle on the first ack); event frames
     * drive `onNext` with `eventId` dedup; `complete`/`error` are terminal.
     */
    private startStream;
    /** Dispatches one frame string to the subscription. */
    private handleFrame;
    /**
     * The SignalR stream ended (complete/error callback). If it ended WITHOUT a terminal frame
     * (`done` is false), this is a transport drop or cancel. During a reconnect (`_reconnecting`),
     * leave the sub in place — `restreamOnReconnect` re-streams it. Otherwise treat the end as
     * unexpected and fail the sub (post-ack → onError; pre-ack → reject the subscribe promise).
     */
    private handleStreamEnd;
    /** Re-streams active subscriptions after a successful reconnect (onreconnected). Parity with the
     * WS reconnect re-subscribe: every acked, non-terminal sub is re-opened, fresh or resume per the
     * `ResumePolicy` (default `"fresh"`). `"drop"` ends the sub. */
    private restreamOnReconnect;
    /** Builds the public handle for a subscription (getter-backed mutable cursor + id). */
    private makeHandle;
    /** Tears down a subscription: dispose the stream-sub (sends a stream Cancel → hub finally →
     * Detach for durable), remove from the map. Idempotent. */
    private teardown;
    /** Fails a subscription with an error (onError) and removes it. */
    private failSub;
    /** Fails ALL active subscriptions (on close / dispose). */
    private failAllSubs;
    /** Races an invoke promise against an optional abort signal / timeout. The server invoke is NOT
     * cancelled (SignalR has no per-invoke cancel) — it completes in the background, result discarded. */
    private raceAbort;
}
//# sourceMappingURL=signalr.d.ts.map