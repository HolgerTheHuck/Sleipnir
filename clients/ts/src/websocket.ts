import { SleipnirError, CancelledError } from "./errors.js";
import { ExecutionMode, SleipnirConnectionState } from "./types.js";
import type { BearerProvider, SleipnirMultiRequest, SleipnirRequest, SleipnirResponse } from "./types.js";
import { fromBase64, normalizeResponse, normalizeResponses } from "./request.js";

const READY_CONNECTING = 0;
const READY_OPEN = 1;
const READY_CLOSING = 2;
const READY_CLOSED = 3;

/** Default backoff intervals in ms (mirror of SignalR): 2,2,5,5,10,10,30,30s,1,1,5min. */
const DEFAULT_RECONNECT_DELAYS = [
  2_000, 2_000, 5_000, 5_000, 10_000, 10_000, 30_000, 30_000, 60_000, 60_000, 300_000,
];

function sleep(ms: number, signal?: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    if (signal?.aborted) {
      reject(new Error("aborted"));
      return;
    }
    const t = setTimeout(() => {
      signal?.removeEventListener("abort", onAbort);
      resolve();
    }, ms);
    const onAbort = () => {
      clearTimeout(t);
      signal?.removeEventListener("abort", onAbort);
      reject(new Error("aborted"));
    };
    signal?.addEventListener("abort", onAbort, { once: true });
  });
}

/** Minimal, browser-compatible WebSocket interface (also Node `ws`). */
export interface IWebSocket {
  readonly readyState: number;
  send(data: string): void;
  close(code?: number, reason?: string): void;
  onopen: (() => void) | null;
  onmessage: ((ev: { data: string | ArrayBuffer }) => void) | null;
  onclose: ((ev: { code?: number; reason?: string }) => void) | null;
  onerror: ((ev: unknown) => void) | null;
}

/** Factory for a WebSocket (browser global, Node `ws`, or injection for tests). */
export type WsFactory = (
  url: string,
  options: { headers?: Record<string, string>; protocols?: string | string[] },
) => IWebSocket;

/** Per-call options for a WebSocket call. */
export interface WsCallOptions {
  signal?: AbortSignal;
  timeout?: number;
}

/**
 * Per-subscribe options (Phase R, resume). Extends {@link WsCallOptions} with an optional
 * per-subscription resume policy that overrides the client-wide `onResume` for this one
 * subscription.
 */
export interface SubscribeOptions extends WsCallOptions {
  /** Per-subscription reconnect decision hook (overrides the client-wide `onResume`). */
  resumePolicy?: ResumePolicy;
}

/**
 * Reconnect decision for a single event subscription (Phase R, resume). Consulted on
 * auto-reconnect, per subscription, before re-subscribing.
 *
 * - `"fresh"` (the default) re-subscribes with a fresh subscription — today's behavior, a new
 *   `subscriptionId`, the `eventId` counter restarts at 1, and events produced during the
 *   disconnect are lost.
 * - `"resume"` sends the durable `subscriptionId` + `lastEventId` so the server replays the gap
 *   from its disconnect buffer (at-least-once within the replay-buffer window; the client dedups
 *   by `eventId`). A Resume on a non-resumable event / expired buffer degrades to fresh (the
 *   server returns a new `subscriptionId`).
 * - `"drop"` ends the subscription without re-subscribing — the consumer's `onComplete` fires.
 */
export type ResumeDecision = "fresh" | "resume" | "drop";

/**
 * Context passed to a {@link ResumePolicy} on reconnect: the controller/method, the (durable)
 * `subscriptionId` of the dropped subscription, and the last `eventId` the client processed
 * (`null` when no event was received yet).
 */
export interface SubscriptionResumeContext {
  readonly controller: string;
  readonly method: string;
  readonly subscriptionId: string;
  readonly lastEventId: number | null;
}

/**
 * Per-subscription reconnect policy. Returns a {@link ResumeDecision} for the given context, or
 * `null` to abstain (the next policy in the fallback chain is consulted; a fully-null chain means
 * `"fresh"`). Wired via the client-wide `onResume` option and overridable per `subscribe` call.
 */
export type ResumePolicy = (ctx: SubscriptionResumeContext) => ResumeDecision | null;

/**
 * Event handlers for a server-push subscription (Phase 3). `onNext` is called per
 * received event frame with the deserialized payload; `onComplete`/`onError` on the
 * terminal frame. A terminal frame ends the subscription server-side — after that, no
 * further events arrive for this `subscriptionId`.
 */
export interface SubscribeHandlers<T> {
  onNext: (value: T) => void;
  onComplete?: () => void;
  onError?: (err: Error) => void;
}

/**
 * Handle to an active server-push subscription (Phase 3). `subscriptionId` is the
 * server-assigned correlation id of the event frames; `unsubscribe()` sends
 * `kind:"unsubscribe"` and ends delivery (idempotent).
 *
 * On auto-reconnect the client automatically re-subscribes with the same
 * parameters (a new `subscriptionId`); gap events during the disconnect are lost
 * (at-most-once-while-disconnected). A terminal `close()` calls `onError` on
 * every active subscription.
 */
export interface SleipnirSubscription {
  /** Server-assigned correlation id of the event frames. */
  readonly subscriptionId: string;
  /**
   * The highest `eventId` processed so far (0 until the first event carrying an `eventId`).
   * Live cursor — read at any time to snapshot progress, e.g. to hand to a cross-transport
   * resume (`SleipnirTransportRouter.resume`) after a transport switch.
   */
  readonly lastEventId: number;
  /** Stops event delivery; sends `kind:"unsubscribe"`. Idempotent. */
  unsubscribe(): Promise<void>;
  /**
   * Resolves once the subscription has ended for good — by `unsubscribe()`, the caller's
   * `signal`, a terminal `complete`/`error` frame, a `"drop"` resume decision, a failed
   * re-subscribe, or the client closing. Never rejects. Use it to release resources tied to the
   * subscription (e.g. an abort listener of your own). The built-in clients always set it; it is
   * optional only so that hand-written implementations of this interface stay valid.
   */
  readonly ended?: Promise<void>;
}

/** Creates the `ended` promise + its idempotent resolver (shared by WS / SSE / SignalR). */
export function createEnded(): { ended: Promise<void>; end: () => void } {
  let resolve!: () => void;
  const ended = new Promise<void>((r) => {
    resolve = r;
  });
  let done = false;
  return {
    ended,
    end: () => {
      if (done) return;
      done = true;
      resolve();
    },
  };
}

/** Options for the WebSocket client. */
export interface SleipnirWebSocketClientOptions {
  /** WS path (default "sleipnirws"). */
  wsPath?: string;
  /** Bearer token (Node: as the Authorization header; browser: as ?access_token=) — string or provider function (rotating JWTs). */
  bearer?: BearerProvider;
  /** Call timeout in ms. */
  callTimeout?: number;
  /** Connect timeout in ms (default 15000). */
  connectTimeout?: number;
  /** Injectable WebSocket factory (tests). */
  WebSocketCtor?: WsFactory;
  /** Auto-reconnect on unexpected disconnect (default true). */
  reconnect?: boolean;
  /** Backoff intervals in ms (default mirrors SignalR). An empty array disables reconnect. */
  reconnectDelays?: number[];
  /** Observer for state changes (UI/logs). */
  onStateChanged?: (state: SleipnirConnectionState) => void;
  /**
   * Client-wide resume policy (Phase R): consulted per subscription on auto-reconnect before
   * re-subscribing. Default (absent) → `"fresh"` for every subscription (non-breaking, today's
   * behavior). Overridable per `subscribe` call via `SubscribeOptions.resumePolicy`.
   */
  onResume?: ResumePolicy;
}

interface PendingCall {
  resolve: (v: SleipnirResponse | SleipnirResponse[]) => void;
  reject: (e: Error) => void;
  isBatch: boolean;
  timer?: ReturnType<typeof setTimeout>;
  onCallerAbort?: () => void;
  callerSignal?: AbortSignal;
}

interface PendingSubscribe {
  resolve: (sub: SleipnirSubscription) => void;
  reject: (e: Error) => void;
  /** Original request (without `kind`) — retained for re-subscribe on reconnect. */
  request: SleipnirRequest;
  handlers: SubscribeHandlers<unknown>;
  /** Phase R: when set, this pending subscribe is a resume re-subscribe — reuse the existing
   * ActiveSubscription (preserve its dedup cursor) instead of creating a new entry. */
  resumeEntry?: { oldId: string; entry: ActiveSubscription };
  /** Phase R: per-subscribe resume policy override to store on the new ActiveSubscription. */
  resumePolicy?: ResumePolicy;
  timer?: ReturnType<typeof setTimeout>;
  onCallerAbort?: () => void;
  callerSignal?: AbortSignal;
  /** Fresh re-subscribe after a reconnect: the existing handle ref to re-point at the new id. */
  ref?: SubscriptionRef;
}

/**
 * Stable identity of one logical subscription across reconnects. A fresh re-subscribe (or a
 * degraded resume) gets a new server `subscriptionId`; the public handle and the caller's
 * `signal` listener read the CURRENT id through this ref, so `unsubscribe()`/abort keep working
 * after a reconnect.
 */
interface SubscriptionRef {
  sid: string;
  /** Caller signal from `SubscribeOptions.signal` — aborting it unsubscribes. */
  signal?: AbortSignal;
  onAbort?: () => void;
  /** `SleipnirSubscription.ended` + its resolver (called by {@link releaseRef}). */
  ended: Promise<void>;
  end: () => void;
}

interface ActiveSubscription {
  handlers: SubscribeHandlers<unknown>;
  /** Original request (without `kind`) — re-sent verbatim on reconnect. */
  request: SleipnirRequest;
  /** Phase R: highest event `eventId` processed for this subscription (0 = none yet). Used to
   * dedup replayed frames (at-least-once within the replay window) and as the resume cursor. */
  lastEventId: number;
  /** Phase R: per-subscribe resume policy override (null → fall back to the client-wide `onResume`). */
  resumePolicy?: ResumePolicy;
  /** Stable handle identity (see {@link SubscriptionRef}). */
  ref: SubscriptionRef;
}

/** Monotonic client-side id counter for unsubscribe requests (avoids id reuse). */
let _unsubscribeIdSeq = 0;

let _defaultFactoryPromise: Promise<WsFactory> | undefined;

/** Resolves the default WebSocket factory (browser global or Node `ws`) lazily. */
async function resolveDefaultFactory(): Promise<WsFactory> {
  if (_defaultFactoryPromise) return _defaultFactoryPromise;
  _defaultFactoryPromise = (async () => {
    if (typeof (globalThis as any).WebSocket !== "undefined") {
      return (url, opts) =>
        new (globalThis as any).WebSocket(url, opts?.protocols) as IWebSocket;
    }
    // Node: `ws` as an optionalDependency; loaded lazily.
    const mod: any = await import("ws");
    const WS = mod.WebSocket ?? mod.default?.WebSocket ?? mod.default;
    if (typeof WS !== "function") {
      throw new SleipnirError(
        0,
        "No WebSocket implementation found. In Node, install the optional 'ws' package.",
      );
    }
    return (url, opts) => new WS(url, opts?.protocols, { headers: opts?.headers }) as IWebSocket;
  })();
  return _defaultFactoryPromise;
}

/**
 * WebSocket client for Sleipnir (RFC 6455 + JSON text frames), isomorphic.
 *
 * Connect race (B1): concurrent `call()`s await the same in-flight connect promise
 * instead of being rejected. Correlation (B3): every response is matched by `id`
 * (single) or `requests[0].id` (batch); with no match it is discarded (no
 * last-resort mis-assignment).
 *
 * `call`/`callBatch` return the raw response (they throw only on
 * transport/cancellation); `callJson`/`callBinary` throw on logical non-2xx (mirror of C#).
 *
 * **Browser auth:** a browser WebSocket cannot set headers, so the bearer travels as
 * `?access_token=`. The server honors it only with `SleipnirOptions.AcceptAccessTokenQuery`
 * (opt-in; WS upgrade + SSE only). Cookie auth is the recommended browser path. Node (`ws`)
 * sends the `Authorization` header.
 */
export class SleipnirWebSocketClient {
  private readonly _baseUrl: string;
  private readonly _wsPath: string;
  private _bearer?: BearerProvider;
  private readonly _callTimeout?: number;
  private readonly _connectTimeout: number;
  private readonly _wsCtor?: WsFactory;
  private readonly _reconnect: boolean;
  private readonly _reconnectDelays: number[];
  private readonly _onStateChanged?: (state: SleipnirConnectionState) => void;
  /** Phase R: client-wide resume policy (null → Fresh for every subscription). */
  private readonly _onResume?: ResumePolicy;

  private _ws?: IWebSocket;
  private _connectPromise?: Promise<void>;
  private _pending = new Map<string, PendingCall>();
  private _pendingSubscribes = new Map<string, PendingSubscribe>();
  private _subscriptions = new Map<string, ActiveSubscription>();
  private _state: SleipnirConnectionState = SleipnirConnectionState.Disconnected;
  private _closedByClient = false;
  private _disposed = false;
  private _reconnectPromise?: Promise<void>;
  private _reconnectAbort?: AbortController;

  constructor(baseUrl: string, options: SleipnirWebSocketClientOptions = {}) {
    if (!baseUrl || baseUrl.trim().length === 0) {
      throw new Error("SleipnirWebSocketClient: baseUrl must not be empty.");
    }
    this._baseUrl = baseUrl.replace(/\/+$/, "");
    this._wsPath = (options.wsPath ?? "sleipnirws").replace(/^\/+|\/+$/g, "");
    this._bearer = options.bearer;
    this._callTimeout = options.callTimeout;
    this._connectTimeout = options.connectTimeout ?? 15000;
    this._wsCtor = options.WebSocketCtor;
    this._reconnectDelays = options.reconnectDelays ?? DEFAULT_RECONNECT_DELAYS;
    this._reconnect = (options.reconnect ?? true) && this._reconnectDelays.length > 0;
    this._onStateChanged = options.onStateChanged;
    this._onResume = options.onResume;
  }

  /** Current connection state (observer surface for UI/logs). */
  get state(): SleipnirConnectionState {
    return this._state;
  }

  /**
   * Swaps the bearer at runtime (rotating JWTs) without rebuilding the client.
   * Accepts a string or a provider function. **WS:** the new token takes effect
   * from the next connect/reconnect on — an already-open connection keeps its
   * upgrade token (HTTP headers are only sent at the handshake).
   */
  setBearer(bearer: BearerProvider): void {
    this._bearer = bearer;
  }

  /** Resolves the bearer (function → invoke, otherwise the value). */
  private resolveBearer(): string | undefined {
    const b = this._bearer;
    return typeof b === "function" ? b() : b;
  }

  private setState(s: SleipnirConnectionState): void {
    this._state = s;
    try {
      this._onStateChanged?.(s);
    } catch {
      /* observer errors are not fatal */
    }
  }

  /** Ensures an open connection (B1: concurrent-safe). */
  async connect(): Promise<void> {
    if (this._disposed) throw new Error("SleipnirWebSocketClient: disposed.");
    if (this._ws && this._ws.readyState === READY_OPEN) return;

    // Is a background reconnect running? Wait for it (do not connect ourselves),
    // so that parallel calls share the same in-flight reconnect.
    if (this._reconnectPromise && this._state === SleipnirConnectionState.Reconnecting) {
      try {
        await this._reconnectPromise;
      } catch {
        /* re-evaluate the reconnect failure below */
      }
      if (this._ws && this._ws.readyState === READY_OPEN) return;
    }

    if (this._connectPromise) return this._connectPromise;
    this.setState(SleipnirConnectionState.Connecting);
    this._connectPromise = this.connectSlow()
      .then(() => this.setState(SleipnirConnectionState.Connected))
      .finally(() => {
        this._connectPromise = undefined;
      });
    return this._connectPromise;
  }

  /** Sends a single request. */
  async call(req: SleipnirRequest, opts?: WsCallOptions): Promise<SleipnirResponse> {
    if (!req.id) req.id = `${req.controller}.${req.method}`;
    await this.connect();
    return this.sendAndAwait(req, false, opts) as Promise<SleipnirResponse>;
  }

  /** Sends a batch (multi-request). Auto-fills empty ids. */
  async callBatch(
    requests: SleipnirRequest[],
    mode: ExecutionMode = ExecutionMode.Parallel,
    opts?: WsCallOptions,
  ): Promise<SleipnirResponse[]> {
    const normalized = requests.map((r) =>
      r.id ? r : { ...r, id: `${r.controller}.${r.method}` },
    );
    await this.connect();
    const multi: SleipnirMultiRequest = { requests: normalized, mode };
    const key = normalized[0]?.id;
    if (!key) throw new SleipnirError(0, "Batch requires at least one request with an id.");
    return this.sendAndAwait(multi as unknown as SleipnirRequest, true, opts, key) as Promise<
      SleipnirResponse[]
    >;
  }

  /** Calls a method and deserializes `response.data` as T. Throws on non-2xx. */
  async callJson<T>(req: SleipnirRequest, opts?: WsCallOptions): Promise<T | null> {
    const response = await this.call(req, opts);
    return parseData<T>(response);
  }

  /** Calls a byte[] method; returns `response.content` as a Uint8Array. Throws on non-2xx. */
  async callBinary(req: SleipnirRequest, opts?: WsCallOptions): Promise<Uint8Array | null> {
    const response = await this.call(req, opts);
    if (!response.isSuccess) throw SleipnirError.fromResponse(response);
    return response.content ? fromBase64(response.content) : null;
  }

  /**
   * Subscribes to a server-push event (Phase 3). Sends `kind:"subscribe"` with the
   * given request (controller/method/params), waits for the subscribe response with
   * the `subscriptionId`, and returns a {@link SleipnirSubscription} handle.
   * Incoming event/complete/error frames are routed to `handlers` by
   * `subscriptionId`.
   *
   * The request is built via {@link SleipnirCall} (`SleipnirCall.init(c,m).with({...})`);
   * `subscribe` sets `kind:"subscribe"` and (if missing) an `id`. On auto-reconnect the
   * client automatically re-subscribes with the same request (a new `subscriptionId`,
   * the same `handlers`).
   *
   * `opts.signal`: aborting it before the subscribe response rejects with `CancelledError`;
   * aborting it afterwards **unsubscribes** (same as `handle.unsubscribe()`), also across
   * reconnects.
   */
  async subscribe<T>(
    req: SleipnirRequest,
    handlers: SubscribeHandlers<T>,
    opts?: SubscribeOptions,
  ): Promise<SleipnirSubscription> {
    return this.subscribeInternal(req, handlers as SubscribeHandlers<unknown>, opts);
  }

  private async subscribeInternal(
    req: SleipnirRequest,
    handlers: SubscribeHandlers<unknown>,
    opts: SubscribeOptions | undefined,
    ref?: SubscriptionRef,
  ): Promise<SleipnirSubscription> {
    if (this._disposed) throw new Error("SleipnirWebSocketClient: disposed.");
    if (!req.id) req.id = `${req.controller}.${req.method}`;
    const id = req.id;
    await this.connect();

    const promise = this.registerPendingSubscribe(id, req, handlers, opts);
    const pendingEntry = this._pendingSubscribes.get(id);
    if (pendingEntry && ref) pendingEntry.ref = ref;
    try {
      const ws = this._ws;
      if (!ws || ws.readyState !== READY_OPEN) {
        this.disposePendingSubscribe(id);
        throw new SleipnirError(0, "WebSocket is not open.");
      }
      // kind:"subscribe" routes server-side to SubscribeAsync; the rest is a
      // normal SleipnirRequest (controller/method/params/id).
      ws.send(JSON.stringify({ ...req, kind: "subscribe" }));
      return promise;
    } catch (err) {
      this.rejectPendingSubscribe(
        id,
        err instanceof SleipnirError
          ? err
          : new SleipnirError(0, `WebSocket send error: ${(err as Error)?.message ?? err}`),
      );
      throw err instanceof SleipnirError
        ? err
        : new SleipnirError(0, `WebSocket send error: ${(err as Error)?.message ?? err}`);
    }
  }

  /** Closes the connection terminally; every pending call is rejected. No reconnect. */
  close(): void {
    this._closedByClient = true;
    this._disposed = true;
    this.stopReconnect();
    this.rejectAllPending(new SleipnirError(0, "WebSocket closed by client."));
    this.rejectAllPendingSubscribes(new SleipnirError(0, "WebSocket closed by client."));
    this.cancelAllSubscriptions(new SleipnirError(0, "WebSocket closed by client."));
    if (this._ws) {
      try {
        this._ws.close(1000, "client close");
      } catch {
        // ignore
      }
    }
    this._ws = undefined;
    this.setState(SleipnirConnectionState.Disconnected);
  }

  /** Alias for {@link close} (symmetry with the REST client). */
  dispose(): void {
    this.close();
  }

  /**
   * Replaces the connection with a fresh one (non-terminal) — e.g. after the app refreshed its
   * credentials: the server authenticates a WebSocket once, at the upgrade, so a new bearer (or
   * cookie) only takes effect on a new connection. The current socket is closed; calls and
   * subscribes still in flight on it are rejected with a transport error (`code` 0), exactly as on
   * a network drop. Active subscriptions are re-subscribed on the new socket per their
   * {@link ResumePolicy} (default `"fresh"`), keeping their handles and `signal`s valid.
   * State: `Reconnecting` → `Connected` (or `Disconnected` if the new connect fails).
   */
  async reconnect(): Promise<void> {
    if (this._disposed) throw new Error("SleipnirWebSocketClient: disposed.");
    this.stopReconnect();
    const old = this._ws;
    this._ws = undefined;
    this._connectPromise = undefined;
    if (old) {
      // Retire the old socket silently: its close must not trigger the auto-reconnect loop.
      old.onopen = null;
      old.onmessage = null;
      old.onclose = null;
      old.onerror = null;
      try {
        old.close(1000, "client reconnect");
      } catch {
        // ignore
      }
    }
    const err = new SleipnirError(0, "WebSocket reconnecting (connection replaced).");
    this.rejectAllPending(err);
    this.rejectAllPendingSubscribes(err);
    this.setState(SleipnirConnectionState.Reconnecting);
    try {
      this._connectPromise = this.connectSlow().finally(() => {
        this._connectPromise = undefined;
      });
      await this._connectPromise;
    } catch (e) {
      // A failed attempt whose socket closed has already handed over to the auto-reconnect loop
      // (state stays Reconnecting); without one, the client is now disconnected.
      if (!this._disposed && !this._reconnectPromise) this.setState(SleipnirConnectionState.Disconnected);
      throw e;
    }
    this.setState(SleipnirConnectionState.Connected);
    await this.resubscribeAll();
  }

  // --- Internals ---

  /**
   * Raw connect without state management — the caller sets Connecting/Connected.
   * Important for the reconnect loop: it holds the state at `Reconnecting` until
   * an attempt succeeds (Connected) or the backoff is exhausted (Disconnected).
   * If connectSlow itself switched to Connecting, a failed attempt would leave the
   * state at Connecting and concurrent calls would miss the reconnect-await path
   * (state === Reconnecting).
   */
  private async connectSlow(): Promise<void> {
    const factory = this._wsCtor ?? (await resolveDefaultFactory());
    const isBrowserWs = typeof (globalThis as any).WebSocket !== "undefined";
    const url = this.buildUrl(isBrowserWs);
    const token = this.resolveBearer();
    const headers =
      !isBrowserWs && token ? { Authorization: `Bearer ${token}` } : undefined;
    const ws = factory(url, { headers });
    this._ws = ws;

    await new Promise<void>((resolve, reject) => {
      let opened = false;
      let connectTimer: ReturnType<typeof setTimeout> | undefined;

      const fail = (err: Error) => {
        if (connectTimer) clearTimeout(connectTimer);
        if (!opened) reject(err);
      };

      connectTimer = setTimeout(
        () => fail(new SleipnirError(0, "WebSocket connect timed out.")),
        this._connectTimeout,
      );

      ws.onopen = () => {
        opened = true;
        if (connectTimer) clearTimeout(connectTimer);
        resolve();
      };
      ws.onmessage = (ev) => this.onMessage(ev.data);
      ws.onclose = (ev) => {
        this.onClosed();
        if (!opened) {
          fail(new SleipnirError(0, `WebSocket closed before open (code ${ev?.code ?? "n/a"}).`));
        }
      };
      ws.onerror = () => {
        if (!opened) fail(new SleipnirError(0, "WebSocket connection failed."));
        // after open, onclose follows, which rejects every pending call.
      };
    });
  }

  private buildUrl(isBrowserWs: boolean): string {
    let base = this._baseUrl;
    base = base.replace(/^http:/i, "ws:").replace(/^https:/i, "wss:");
    let url = `${base}/${this._wsPath}`;
    const token = this.resolveBearer();
    if (isBrowserWs && token) {
      url += `?access_token=${encodeURIComponent(token)}`;
    }
    return url;
  }

  private sendAndAwait(
    payload: SleipnirRequest,
    isBatch: boolean,
    opts: WsCallOptions | undefined,
    explicitKey?: string,
  ): Promise<SleipnirResponse | SleipnirResponse[]> {
    const key = explicitKey ?? payload.id!;
    const deferred = this.registerPending(key, isBatch, opts);
    try {
      const ws = this._ws;
      if (!ws || ws.readyState !== READY_OPEN) {
        this.disposePending(key);
        return Promise.reject(new SleipnirError(0, "WebSocket is not open."));
      }
      ws.send(JSON.stringify(payload));
      return deferred.promise;
    } catch (err) {
      this.disposePending(key);
      return Promise.reject(
        err instanceof SleipnirError
          ? err
          : new SleipnirError(0, `WebSocket send error: ${(err as Error)?.message ?? err}`),
      );
    }
  }

  private registerPending(
    key: string,
    isBatch: boolean,
    opts: WsCallOptions | undefined,
  ): { promise: Promise<SleipnirResponse | SleipnirResponse[]> } {
    let resolve!: (v: SleipnirResponse | SleipnirResponse[]) => void;
    let reject!: (e: Error) => void;
    const promise = new Promise<SleipnirResponse | SleipnirResponse[]>((res, rej) => {
      resolve = res;
      reject = rej;
    });

    const pending: PendingCall = { resolve, reject, isBatch };
    const timeoutMs = opts?.timeout ?? this._callTimeout;

    if (timeoutMs && timeoutMs > 0) {
      pending.timer = setTimeout(
        () => this.rejectPending(key, new CancelledError("Sleipnir call timed out.", true)),
        timeoutMs,
      );
    }

    if (opts?.signal) {
      if (opts.signal.aborted) {
        // Rejected immediately (unwrapped).
        queueMicrotask(() => this.rejectPending(key, new CancelledError("Sleipnir call was cancelled.")));
      } else {
        pending.callerSignal = opts.signal;
        pending.onCallerAbort = () =>
          this.rejectPending(key, new CancelledError("Sleipnir call was cancelled."));
        opts.signal.addEventListener("abort", pending.onCallerAbort, { once: true });
      }
    }

    this._pending.set(key, pending);
    return { promise };
  }

  private rejectPending(key: string, err: Error): void {
    const pending = this._pending.get(key);
    if (!pending) return;
    if (pending.timer) clearTimeout(pending.timer);
    if (pending.onCallerAbort && pending.callerSignal) {
      pending.callerSignal.removeEventListener("abort", pending.onCallerAbort);
    }
    this._pending.delete(key);
    pending.reject(err);
  }

  /** Cleans up a pending call without rejecting it (send-error path). */
  private disposePending(key: string): void {
    const pending = this._pending.get(key);
    if (!pending) return;
    if (pending.timer) clearTimeout(pending.timer);
    if (pending.onCallerAbort && pending.callerSignal) {
      pending.callerSignal.removeEventListener("abort", pending.onCallerAbort);
    }
    this._pending.delete(key);
  }

  private resolvePending(key: string, value: SleipnirResponse | SleipnirResponse[]): boolean {
    const pending = this._pending.get(key);
    if (!pending) return false;
    if (pending.timer) clearTimeout(pending.timer);
    if (pending.onCallerAbort && pending.callerSignal) {
      pending.callerSignal.removeEventListener("abort", pending.onCallerAbort);
    }
    this._pending.delete(key);
    pending.resolve(value);
    return true;
  }

  private rejectAllPending(err: Error): void {
    for (const key of [...this._pending.keys()]) this.rejectPending(key, err);
  }

  // --- Phase 3: Subscribe / Unsubscribe / Event dispatch (internals) ---

  /** Registers a pending subscribe (timeout/abort analogous to registerPending). */
  private registerPendingSubscribe(
    id: string,
    request: SleipnirRequest,
    handlers: SubscribeHandlers<unknown>,
    opts: WsCallOptions | undefined,
    resumeEntry?: { oldId: string; entry: ActiveSubscription },
  ): Promise<SleipnirSubscription> {
    let resolve!: (sub: SleipnirSubscription) => void;
    let reject!: (e: Error) => void;
    const promise = new Promise<SleipnirSubscription>((res, rej) => {
      resolve = res;
      reject = rej;
    });

    const pending: PendingSubscribe = {
      resolve,
      reject,
      request,
      handlers,
      resumeEntry,
      resumePolicy: (opts as SubscribeOptions | undefined)?.resumePolicy,
    };
    const timeoutMs = opts?.timeout ?? this._callTimeout;
    if (timeoutMs && timeoutMs > 0) {
      pending.timer = setTimeout(
        () => this.rejectPendingSubscribe(id, new CancelledError("Sleipnir subscribe timed out.", true)),
        timeoutMs,
      );
    }
    if (opts?.signal) {
      if (opts.signal.aborted) {
        queueMicrotask(() => this.rejectPendingSubscribe(id, new CancelledError("Sleipnir subscribe was cancelled.")));
      } else {
        pending.callerSignal = opts.signal;
        pending.onCallerAbort = () =>
          this.rejectPendingSubscribe(id, new CancelledError("Sleipnir subscribe was cancelled."));
        opts.signal.addEventListener("abort", pending.onCallerAbort, { once: true });
      }
    }
    this._pendingSubscribes.set(id, pending);
    return promise;
  }

  /** Rejects a pending subscribe (timeout/abort/send error/disconnect). */
  private rejectPendingSubscribe(id: string, err: Error): void {
    const pending = this._pendingSubscribes.get(id);
    if (!pending) return;
    this.disposePendingSubscribe(id);
    pending.reject(err);
  }

  /** Cleans up the timer/abort listener + map entry of a pending subscribe (without reject). */
  private disposePendingSubscribe(id: string): void {
    const pending = this._pendingSubscribes.get(id);
    if (!pending) return;
    if (pending.timer) clearTimeout(pending.timer);
    if (pending.onCallerAbort && pending.callerSignal) {
      pending.callerSignal.removeEventListener("abort", pending.onCallerAbort);
    }
    this._pendingSubscribes.delete(id);
  }

  private rejectAllPendingSubscribes(err: Error): void {
    for (const id of [...this._pendingSubscribes.keys()]) this.rejectPendingSubscribe(id, err);
  }

  /** Terminal: moves every active subscription to onError and discards them. */
  private cancelAllSubscriptions(err: Error): void {
    for (const [, entry] of this._subscriptions) {
      releaseRef(entry.ref);
      try { entry.handlers.onError?.(err); } catch { /* handler errors are not fatal */ }
    }
    this._subscriptions.clear();
  }

  /**
   * Sends `kind:"unsubscribe"` for `subscriptionId` and removes the subscription.
   * Idempotent: a second call for the same id is a no-op. Best-effort — a send
   * error after a disconnect is silently ignored (the subscription has already
   * died server-side with the connection).
   */
  private async unsubscribe(subscriptionId: string): Promise<void> {
    const entry = this._subscriptions.get(subscriptionId);
    if (!entry) return;
    this._subscriptions.delete(subscriptionId);
    releaseRef(entry.ref);
    const ws = this._ws;
    if (ws && ws.readyState === READY_OPEN) {
      try {
        ws.send(JSON.stringify({ kind: "unsubscribe", subscriptionId, id: `unsub.${_unsubscribeIdSeq++}` }));
      } catch {
        // best-effort
      }
    }
  }

  /**
   * Re-subscribes all active subscriptions after a reconnect. Phase R: per subscription the resume
   * policy is consulted (per-subscribe override → client-wide → `"fresh"`): `"fresh"` starts a new
   * subscription (new id, gap lost — today's behavior); `"resume"` sends the durable
   * `subscriptionId` + `lastEventId` so the server replays the gap; `"drop"` ends the subscription
   * without re-subscribing (`onComplete`). A Resume the server cannot satisfy (TTL expired /
   * non-resumable) degrades to fresh — the server returns a new `subscriptionId` and the dedup
   * cursor resets.
   */
  private async resubscribeAll(): Promise<void> {
    if (this._subscriptions.size === 0) return;
    const old = [...this._subscriptions.entries()];
    // `subscribe` registers under the new subscriptionId; the old entries must go or they linger
    // as dead entries (new id != old id).
    this._subscriptions.clear();
    for (const [oldId, entry] of old) {
      // Phase R: resolve the reconnect decision (per-subscribe → client-wide → fresh).
      const policy = entry.resumePolicy ?? this._onResume;
      const ctx: SubscriptionResumeContext = {
        controller: entry.request.controller,
        method: entry.request.method,
        subscriptionId: oldId,
        lastEventId: entry.lastEventId > 0 ? entry.lastEventId : null,
      };
      const decision = policy?.(ctx) ?? "fresh";

      if (decision === "drop") {
        releaseRef(entry.ref);
        try { entry.handlers.onComplete?.(); } catch { /* handler error not fatal */ }
        continue;
      }

      try {
        if (decision === "resume") {
          await this.resubscribeResume(oldId, entry);
        } else {
          // Fresh: the subscribe path, carrying the per-subscribe policy so a later reconnect
          // still consults it, and the stable ref so the caller's handle/signal follow the new
          // id. The cursor resets implicitly (new entry, lastEventId 0).
          await this.subscribeInternal(entry.request, entry.handlers, {
            resumePolicy: entry.resumePolicy,
          }, entry.ref);
        }
      } catch (err) {
        releaseRef(entry.ref);
        const e = err instanceof Error ? err : new Error(String(err));
        try { entry.handlers.onError?.(e); } catch { /* handler error not fatal */ }
      }
    }
  }

  /**
   * Phase R resume re-subscribe: sends `kind:"subscribe"` with the durable `subscriptionId` +
   * `lastEventId` so the server replays the disconnect gap, reusing the existing entry (preserving
   * its handlers + dedup cursor). Pre-registers under the durable id so any replay frame arriving
   * before the response is dispatched. On a degraded-to-fresh response (new id), the cursor resets.
   */
  private async resubscribeResume(oldId: string, entry: ActiveSubscription): Promise<void> {
    if (this._disposed) throw new Error("SleipnirWebSocketClient: disposed.");
    await this.connect();
    const ws = this._ws;
    if (!ws || ws.readyState !== READY_OPEN) {
      throw new SleipnirError(0, "WebSocket is not open.");
    }
    const id = `resume.${oldId}.${_unsubscribeIdSeq++}`;
    const req: SleipnirRequest = { ...entry.request, id };
    const promise = this.registerPendingSubscribe(id, req, entry.handlers, undefined, {
      oldId,
      entry,
    });
    // Pre-register under the durable id so replay frames arriving before the response are dispatched.
    this._subscriptions.set(oldId, entry);
    try {
      ws.send(
        JSON.stringify({ ...req, kind: "subscribe", subscriptionId: oldId, lastEventId: entry.lastEventId }),
      );
      await promise;
    } catch (err) {
      this._subscriptions.delete(oldId); // clean up the pre-registration on failure
      this.rejectPendingSubscribe(
        id,
        err instanceof SleipnirError
          ? err
          : new SleipnirError(0, `WebSocket send error: ${(err as Error)?.message ?? err}`),
      );
      throw err instanceof Error ? err : new Error(String(err));
    }
  }

  private onMessage(data: string | ArrayBuffer): void {
    const text = typeof data === "string" ? data : new TextDecoder().decode(data);
    let parsed: unknown;
    try {
      parsed = JSON.parse(text);
    } catch {
      // Server error frames without an id cannot be correlated -> discard.
      return;
    }

    // Phase 3: event/complete/error frames — an object with `type` + `subscriptionId`,
    // without `code`/`id`. Routed to the active subscription by subscriptionId.
    if (parsed && typeof parsed === "object" && !Array.isArray(parsed)) {
      const obj = parsed as Record<string, unknown>;
      if (typeof obj.type === "string" && typeof obj.subscriptionId === "string") {
        this.dispatchEventFrame(obj.type, obj.subscriptionId, obj);
        return;
      }
    }

    if (Array.isArray(parsed)) {
      // Batch response: correlation via the first element.
      const arr = normalizeResponses(parsed as SleipnirResponse[]);
      const key = arr[0]?.id ?? undefined;
      if (key && this.resolvePending(key, arr)) return;
      this.dropUnmatched(text, key);
      return;
    }

    const resp = normalizeResponse(parsed as SleipnirResponse);
    const key = resp?.id ?? undefined;
    // Subscribe response (a normal SleipnirResponse, correlated by id) — check it
    // before the call-pending, since subscribe keeps its own pending map.
    if (key && this._pendingSubscribes.has(key)) {
      this.handleSubscribeResponse(key, resp);
      return;
    }
    if (key && this.resolvePending(key, resp)) return;
    this.dropUnmatched(text, key);
  }

  /** Routes an event/complete/error frame to the active subscription. */
  private dispatchEventFrame(type: string, subscriptionId: string, obj: Record<string, unknown>): void {
    const entry = this._subscriptions.get(subscriptionId);
    if (!entry) return; // unsubscribe already ran / unknown -> discard.
    if (type === "event") {
      // Phase R: at-least-once dedup. The server replays the disconnect gap from its buffer; drop
      // any frame whose eventId we have already processed (eventId <= last seen). Frames without
      // an eventId (non-resumable sources) are forwarded verbatim — no dedup.
      const evId = typeof obj.eventId === "number" ? obj.eventId : null;
      if (evId !== null) {
        if (evId <= entry.lastEventId) return; // replay duplicate
        entry.lastEventId = evId;
      }
      entry.handlers.onNext(obj.data);
    } else if (type === "complete") {
      this._subscriptions.delete(subscriptionId);
      releaseRef(entry.ref);
      try { entry.handlers.onComplete?.(); } catch { /* handler errors are not fatal */ }
    } else if (type === "error") {
      this._subscriptions.delete(subscriptionId);
      releaseRef(entry.ref);
      const msg = typeof obj.message === "string" ? obj.message : "Subscription error";
      try { entry.handlers.onError?.(new Error(msg)); } catch { /* handler errors are not fatal */ }
    }
  }

  /** Processes a subscribe response: extracts subscriptionId, registers the subscription. */
  private handleSubscribeResponse(key: string, resp: SleipnirResponse): void {
    const pending = this._pendingSubscribes.get(key);
    if (!pending) return;
    // Captured before dispose (which detaches the pre-ack abort listener): a caller signal keeps
    // governing the subscription after the ack — aborting it unsubscribes.
    const callerSignal = pending.callerSignal;
    this.disposePendingSubscribe(key);
    if (!resp.isSuccess) {
      // A failed resume re-subscribe must drop the pre-registered durable id so a later reconnect
      // does not resurrect a dead entry.
      if (pending.resumeEntry) this._subscriptions.delete(pending.resumeEntry.oldId);
      pending.reject(SleipnirError.fromResponse(resp));
      return;
    }
    const sid = extractSubscriptionId(resp);
    if (!sid) {
      if (pending.resumeEntry) this._subscriptions.delete(pending.resumeEntry.oldId);
      pending.reject(new SleipnirError(resp.code ?? 0, "Subscribe response missing subscriptionId."));
      return;
    }
    if (pending.resumeEntry) {
      // Phase R resume: reuse the existing entry (preserve its handlers + dedup cursor).
      const { oldId, entry } = pending.resumeEntry;
      if (sid !== oldId) {
        // Degrade-to-fresh: the server returned a new id (TTL expired / non-resumable). The new
        // server subscription restarts its eventId counter at 1, so the stale cursor must reset or
        // the fresh stream would be deduped away. Drop the pre-registered old id; re-key under sid.
        entry.lastEventId = 0;
        this._subscriptions.delete(oldId);
      }
      entry.ref.sid = sid;
      this._subscriptions.set(sid, entry);
      // A re-subscribe: the caller already holds its handle (backed by entry.ref); this one only
      // completes the internal await.
      pending.resolve(this.makeHandle(entry.ref));
      return;
    }

    // Fresh subscribe — a reconnect re-subscribe reuses the existing ref (the caller's handle and
    // signal listener follow the new id); a first subscribe creates one.
    const isResubscribe = pending.ref !== undefined;
    const ref: SubscriptionRef = pending.ref ?? { sid, ...createEnded() };
    ref.sid = sid;
    this._subscriptions.set(sid, {
      handlers: pending.handlers,
      request: pending.request,
      lastEventId: 0,
      resumePolicy: pending.resumePolicy,
      ref,
    });
    if (!isResubscribe && callerSignal) {
      ref.signal = callerSignal;
      ref.onAbort = () => void this.unsubscribe(ref.sid);
      if (callerSignal.aborted) queueMicrotask(ref.onAbort);
      else callerSignal.addEventListener("abort", ref.onAbort, { once: true });
    }
    pending.resolve(this.makeHandle(ref));
  }

  /** Public handle over a stable {@link SubscriptionRef}. */
  private makeHandle(ref: SubscriptionRef): SleipnirSubscription {
    // Capture the subscription store so the live-cursor getter below can read the dedup
    // cursor without binding `this` (a getter in an object literal does not see the client).
    const store = this._subscriptions;
    return {
      // Live id: follows a reconnect re-subscribe (new server id).
      get subscriptionId() {
        return ref.sid;
      },
      // Live cursor: reads the dedup cursor from the active entry so a caller can snapshot
      // progress for a cross-transport resume after a transport switch.
      get lastEventId() {
        return store.get(ref.sid)?.lastEventId ?? 0;
      },
      unsubscribe: () => this.unsubscribe(ref.sid),
      ended: ref.ended,
    };
  }

  private dropUnmatched(text: string, key: string | undefined): void {
    // B3: no last resort — do not assign, discard. The pending caller expires
    // through its timeout/its signal.
    console.warn(
      `[sleipnir-client] Received WebSocket response with no matching pending request (id=${key ?? "n/a"}). Dropping.`,
    );
    void text;
  }

  private onClosed(): void {
    this._ws = undefined;
    this.rejectAllPending(new SleipnirError(0, "WebSocket connection closed."));
    // Pending subscribes have no subscriptionId yet -> the response will never arrive.
    // Active subscriptions (_subscriptions) survive and are re-subscribed after the
    // reconnect (at-most-once-while-disconnected: gap events lost).
    this.rejectAllPendingSubscribes(new SleipnirError(0, "WebSocket connection closed."));

    // Unexpected disconnect (not triggered by close()/dispose()) -> reconnect.
    if (!this._closedByClient && !this._disposed && this._reconnect) {
      this.startReconnect();
    } else {
      // No reconnect will follow: the server-side subscriptions died with the connection — end
      // them (onError + `ended`) instead of leaving them dangling.
      if (!this._disposed) this.cancelAllSubscriptions(new SleipnirError(0, "WebSocket connection closed."));
      this.setState(SleipnirConnectionState.Disconnected);
    }
  }

  /** Starts the background auto-reconnect with backoff (idempotent). */
  private startReconnect(): void {
    if (this._disposed) return;
    if (this._reconnectPromise && this._state === SleipnirConnectionState.Reconnecting) return;

    this.setState(SleipnirConnectionState.Reconnecting);
    this._reconnectAbort?.abort();
    this._reconnectAbort = new AbortController();
    const signal = this._reconnectAbort.signal;

    this._reconnectPromise = (async () => {
      for (let i = 0; i < this._reconnectDelays.length; i++) {
        if (this._disposed) return;
        try {
          await sleep(this._reconnectDelays[i], signal);
        } catch {
          return; // aborted (dispose / a newer reconnect)
        }
        if (this._disposed) return;
        // connectSlow directly (NOT connect()): the public connect() would, at
        // state === Reconnecting, await the in-flight reconnect — i.e. itself
        // (self-deadlock). connectSlow shares the attempt with concurrent connect()
        // calls via _connectPromise but keeps the state at Reconnecting.
        this._connectPromise = this.connectSlow().finally(() => {
          this._connectPromise = undefined;
        });
        try {
          await this._connectPromise;
          if (this._ws && this._ws.readyState === READY_OPEN) {
            this.setState(SleipnirConnectionState.Connected);
            // Phase 3: re-subscribe active subscriptions on the new socket
            // (new subscriptionIds; same parameters + handlers). Best-effort,
            // fire-and-forget — one failure per subscription -> onError.
            void this.resubscribeAll();
            return; // success
          }
        } catch {
          // continue with the next backoff interval (state stays Reconnecting)
        }
      }
      // Backoff exhausted -> give up. Subscriptions can no longer be re-subscribed -> end them.
      if (!this._disposed) {
        this.cancelAllSubscriptions(new SleipnirError(0, "WebSocket reconnect gave up."));
        this.setState(SleipnirConnectionState.Disconnected);
      }
    })();
  }

  /** Cancels a running background reconnect (terminal on dispose). */
  private stopReconnect(): void {
    this._reconnectAbort?.abort();
    this._reconnectPromise = undefined;
  }
}

/** Detaches a subscription's caller-signal listener (terminal end of the logical subscription). */
function releaseRef(ref: SubscriptionRef): void {
  if (ref.signal && ref.onAbort) ref.signal.removeEventListener("abort", ref.onAbort);
  ref.signal = undefined;
  ref.onAbort = undefined;
  ref.end();
}

// --- Shared (same as rest.ts) ---

function parseData<T>(response: SleipnirResponse): T | null {
  // Since the single-pass fix, data is already a structured value (no JSON string).
  if (response.isSuccess && response.data != null) {
    return response.data as T;
  }
  if (!response.isSuccess) throw SleipnirError.fromResponse(response);
  return null;
}

/**
 * Extracts the `subscriptionId` from a subscribe response. The server sends
 * `data: { subscriptionId: "…" }` (object) — as a fallback, a scalar `data` string
 * is accepted (mirror of the C# `ExtractSubscriptionId`).
 */
function extractSubscriptionId(response: SleipnirResponse): string | undefined {
  const data = response.data;
  if (data && typeof data === "object" && typeof (data as { subscriptionId?: unknown }).subscriptionId === "string") {
    return (data as { subscriptionId: string }).subscriptionId;
  }
  return typeof data === "string" ? data : undefined;
}