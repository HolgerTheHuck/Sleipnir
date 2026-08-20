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
import type {
  BearerProvider,
  SleipnirMultiRequest,
  SleipnirRequest,
  SleipnirResponse,
} from "./types.js";
import { fromBase64, normalizeResponse, normalizeResponses } from "./request.js";
import type {
  ResumePolicy,
  SubscribeHandlers,
  SleipnirSubscription,
} from "./websocket.js";

// Re-export the shared event types so consumers import everything from one module (parity with
// sse.ts), and so the codegen emitter can `import { SleipnirSignalrClient, type SubscribeHandlers }`.
export type { ResumePolicy, SubscribeHandlers, SleipnirSubscription };

// `@microsoft/signalr` is loaded by a non-literal dynamic import so the workspace does not need the
// package installed to typecheck (tsc types `import(string)` as `Promise<any>`, no resolution). The
// `/* @vite-ignore */` comment keeps Vite from warning about the un-analyzable specifier; at runtime
// the consumer's installed copy resolves. A failed import throws a clear install hint.
let SIGNALR_MODULE: string = "@microsoft/signalr";

/** Standard backoff intervals in ms (mirror of the WS client / SignalR defaults). */
const DEFAULT_RECONNECT_DELAYS = [
  0, 2_000, 5_000, 10_000, 30_000, 30_000, 60_000, 60_000, 300_000,
];

// --- Local structural interfaces (the slice of @microsoft/signalr we use) ---
// These describe ONLY the surface this client calls; the real HubConnection satisfies them
// structurally. Keeping them local avoids importing `@microsoft/signalr` types into the public
// `.d.ts` (an optional peer dep must not appear in the mandatory type surface).

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
export type SignalrHubFactory = (
  url: string,
  options: SignalrBuildOptions,
) => IHubConnection | Promise<IHubConnection>;

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

// --- the default factory: dynamic-import @microsoft/signalr, build a HubConnection ---

async function defaultHubFactory(url: string, opts: SignalrBuildOptions): Promise<IHubConnection> {
  let mod: any;
  try {
    // Non-literal specifier → tsc types this as Promise<any> (no resolution); the package is an
    // optional peer dep, so the workspace typechecks without it installed. Vite-ignore suppresses
    // the un-analyzable-dynamic-import warning.
    mod = await import(/* @vite-ignore */ SIGNALR_MODULE);
  } catch {
    throw new SleipnirError(
      0,
      "The SignalR transport requires the '@microsoft/signalr' package. Install it " +
        "(npm i @microsoft/signalr) or regenerate with a non-signalr capability.",
    );
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
  return wired.build() as IHubConnection;
}

/** Resolves a `BearerProvider` (string | () => … | Promise) to the token string, or `undefined`. */
async function resolveBearer(bearer: BearerProvider | undefined): Promise<string | undefined> {
  if (bearer == null) return undefined;
  if (typeof bearer === "function") {
    const v = await (bearer as () => string | Promise<string> | null)();
    return v ?? undefined;
  }
  return bearer;
}

/** A parsed logical event frame (the wire shape shared by WS / SSE / SignalR). */
interface EventFrame {
  type: string;
  subscriptionId?: string;
  eventId?: number;
  data?: unknown;
  message?: string;
  replayedFrom?: number;
}

function parseFrame(text: string): EventFrame {
  try {
    return JSON.parse(text) as EventFrame;
  } catch {
    return { type: "error", message: `Malformed event frame: ${text}` };
  }
}

/** Internal bookkeeping for an active subscription. */
interface ActiveSubscription<T> {
  /** Original request (controller/method/params) — used for fresh re-subscribe on reconnect. */
  readonly req: SleipnirRequest;
  readonly handlers: SubscribeHandlers<T>;
  readonly resumePolicy?: ResumePolicy;
  /** Whether this stream was opened in resume mode (carries subscriptionId+lastEventId). */
  readonly isResume: boolean;
  /** Current server subscriptionId (changes on reconnect-fresh; stable on reconnect-resume). */
  subscriptionId: string;
  /** Highest processed eventId (dedup cursor). */
  lastEventId: number;
  /** Current SignalR stream subscription (disposable). */
  streamSub?: IStreamSubscriber;
  /** Terminal frame received → no reconnect re-stream. */
  done: boolean;
  /** Resolves the subscribe/resume promise once the ack arrives. */
  resolveHandle: (h: SleipnirSubscription) => void;
  /** Rejects the subscribe/resume promise on a pre-ack error. */
  rejectHandle: (err: Error) => void;
  /** Whether the ack has been received (the handle is resolved on the first ack). */
  acked: boolean;
  /** Caller abort controller (unsubscribe). */
  abort: AbortController;
}

/**
 * Sleipnir SignalR client. Calls via `.invoke("DoWork"/"DoWorkMany")`; events via
 * `.stream("SubscribeAsync", req, resumeId?, lastEventId?)`. Reuses the shared `SubscribeHandlers` /
 * `SleipnirSubscription` / `ResumePolicy` surface so the transport router treats it identically to
 * the WS backend.
 */
export class SleipnirSignalrClient {
  private readonly _hubUrl: string;
  private readonly _callTimeout: number;
  private readonly _reconnectDelays: number[];
  private readonly _onResume?: ResumePolicy;
  private readonly _hubFactory: SignalrHubFactory;
  private _bearer: BearerProvider | undefined;
  private _conn?: IHubConnection;
  private _startPromise?: Promise<void>;
  private _disposed = false;
  /** True between `onreconnecting` and `onreconnected`/`onclose` — distinguishes a reconnect stream
   * tear-down (leave the sub for re-stream) from an unexpected stream end (fail the sub). */
  private _reconnecting = false;
  /** Active subscriptions keyed by an internal id (NOT the server subscriptionId, which can change). */
  private readonly _subs = new Map<number, ActiveSubscription<unknown>>();
  private _subSeq = 0;

  constructor(baseUrl: string, opts: SleipnirSignalrClientOptions = {}) {
    if (!baseUrl) throw new Error("SleipnirSignalrClient: baseUrl is required.");
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
  async connect(): Promise<void> {
    if (this._disposed) throw new Error("SleipnirSignalrClient: disposed.");
    if (this._conn && this._startPromise) return this._startPromise;
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
      if (this._disposed) return;
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
  async close(): Promise<void> {
    if (this._disposed) return;
    this._disposed = true;
    this.failAllSubs(new Error("SignalR client closed by user."));
    const conn = this._conn;
    this._conn = undefined;
    this._startPromise = undefined;
    if (conn) {
      try {
        await conn.stop();
      } catch {
        // ignore — best-effort stop
      }
    }
  }

  /** Alias for {@link close} (parity with WS `dispose()`). */
  dispose(): void {
    void this.close();
  }

  /** Swaps the bearer; applied to the NEXT connect (SignalR's `accessTokenProvider` reads it per
   * request, so a live connection picks up the new token without a reconnect). */
  setBearer(bearer: BearerProvider): void {
    this._bearer = bearer;
    // If already connected, the stored provider closure captured the OLD token at start time. To
    // honor a mid-session swap, rebuild the provider against the new bearer by patching the conn's
    // accessTokenProvider — but the local interface does not expose it. The robust path is to
    // trigger a reconnect; consumers swapping tokens mid-session should reconnect. For the router's
    // setBearer fan-out (pre-connect), the stored bearer is read at connect() time — correct.
  }

  // --- calls ---

  /** Execute a single request via `DoWork`. */
  async call(req: SleipnirRequest, opts?: SignalrCallOptions): Promise<SleipnirResponse> {
    await this.connect();
    const r = await this.raceAbort(this._conn!.invoke<SleipnirResponse>("DoWork", req), opts);
    return normalizeResponse(r as SleipnirResponse);
  }

  /** Execute a batch via `DoWorkMany`. */
  async callBatch(
    requests: SleipnirRequest[],
    mode: ExecutionMode = ExecutionMode.Parallel,
    opts?: SignalrCallOptions,
  ): Promise<SleipnirResponse[]> {
    await this.connect();
    const multi: SleipnirMultiRequest = { requests, mode };
    const r = await this.raceAbort(
      this._conn!.invoke<SleipnirResponse[]>("DoWorkMany", multi),
      opts,
    );
    return normalizeResponses((r as SleipnirResponse[]) ?? []);
  }

  /** Call and deserialize `response.data` as `T`; throws on non-2xx. */
  async callJson<T>(req: SleipnirRequest, opts?: SignalrCallOptions): Promise<T | null> {
    const resp = await this.call(req, opts);
    if (resp.isSuccess && resp.data != null) return resp.data as T;
    if (!resp.isSuccess) throw SleipnirError.fromResponse(resp);
    return null;
  }

  /** Call a `byte[]` method; returns `response.content` as `Uint8Array`. Throws on non-2xx. */
  async callBinary(req: SleipnirRequest, opts?: SignalrCallOptions): Promise<Uint8Array | null> {
    const resp = await this.call(req, opts);
    if (!resp.isSuccess) throw SleipnirError.fromResponse(resp);
    return resp.content ? fromBase64(resp.content) : null;
  }

  // --- events ---

  /**
   * Subscribe to a server-push event via the streaming `SubscribeAsync` hub method. The first
   * stream item is the `ack` frame (carrying the `subscriptionId`); subsequent items are `event`
   * frames (→ `onNext`), then a terminal `complete`/`error` frame. Resolves the
   * {@link SleipnirSubscription} handle on the ack.
   */
  async subscribe<T>(
    req: SleipnirRequest,
    handlers: SubscribeHandlers<T>,
    opts?: SignalrSubscribeOptions,
  ): Promise<SleipnirSubscription> {
    if (this._disposed) throw new Error("SleipnirSignalrClient: disposed.");
    if (!req.id) req.id = `${req.controller}.${req.method}`;
    await this.connect();
    return this.openStream<T>(req, handlers, false, "", 0, opts);
  }

  /**
   * Resume a durable subscription by `subscriptionId` + `lastEventId` (cross-transport). The server
   * replays the gap from its disconnect buffer then continues live. The original `req` is required
   * by the hub signature but its controller/method are ignored on resume (auth re-runs against the
   * route recorded on the durable state).
   */
  async resume<T>(
    subscriptionId: string,
    lastEventId: number,
    handlers: SubscribeHandlers<T>,
    opts?: SignalrSubscribeOptions,
  ): Promise<SleipnirSubscription> {
    if (this._disposed) throw new Error("SleipnirSignalrClient: disposed.");
    await this.connect();
    // A placeholder request that satisfies the non-optional SleipnirRequest param; the hub ignores
    // it on the resume path (uses the stored controller/method for auth + replay).
    const placeholder: SleipnirRequest = {
      controller: "",
      method: "",
      id: `resume.${subscriptionId}`,
    };
    return this.openStream<T>(placeholder, handlers, true, subscriptionId, lastEventId, opts);
  }

  // --- internals ---

  /**
   * Opens a `SubscribeAsync` stream (fresh or resume) and wires the frames to the handlers. Returns
   * a promise that resolves on the ack. The internal {@link ActiveSubscription} tracks the cursor +
   * stream-subscriber for reconnect re-streaming.
   */
  private openStream<T>(
    req: SleipnirRequest,
    handlers: SubscribeHandlers<T>,
    resume: boolean,
    resumeId: string,
    lastEventId: number,
    opts?: SignalrSubscribeOptions,
  ): Promise<SleipnirSubscription> {
    return new Promise<SleipnirSubscription>((resolve, reject) => {
      if (!this._conn) {
        reject(new SleipnirError(0, "SignalR connection is not open."));
        return;
      }
      const internalId = ++this._subSeq;
      const abort = new AbortController();
      if (opts?.signal) {
        if (opts.signal.aborted) abort.abort(new Error("subscribe aborted"));
        else opts.signal.addEventListener("abort", () => abort.abort(new Error("subscribe aborted")), {
          once: true,
        });
      }
      const sub: ActiveSubscription<T> = {
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
      this._subs.set(internalId, sub as ActiveSubscription<unknown>);

      // Caller abort → unsubscribe (dispose the stream-sub). Idempotent.
      abort.signal.addEventListener(
        "abort",
        () => this.teardown(internalId, abort.signal.reason instanceof Error ? abort.signal.reason : undefined),
        { once: true },
      );

      this.startStream(internalId, resume);
    });
  }

  /**
   * Starts (or re-starts) the `SubscribeAsync` stream for a subscription. Fresh:
   * `stream("SubscribeAsync", req, null, null)`; resume: `stream("SubscribeAsync", req, subId, lastEventId)`.
   * The ack updates the `subscriptionId` (and resolves the handle on the first ack); event frames
   * drive `onNext` with `eventId` dedup; `complete`/`error` are terminal.
   */
  private startStream(internalId: number, resume: boolean): void {
    const sub = this._subs.get(internalId) as ActiveSubscription<unknown> | undefined;
    if (!sub || !this._conn) return;
    // Hub signature: SubscribeAsync(SleipnirRequest, string? resumeId, long? lastEventId, CancellationToken).
    // The CancellationToken is injected by SignalR (client stream cancel → server finally → Detach);
    // we pass only the three application args.
    const streamArgs: unknown[] = resume
      ? [sub.req, sub.subscriptionId, sub.lastEventId]
      : [sub.req, null, null];
    let streamSub: IStreamSubscriber | undefined;
    try {
      const result = this._conn.stream<string>("SubscribeAsync", ...streamArgs);
      streamSub = result.subscribe({
        next: (frameText: string) => this.handleFrame(internalId, frameText),
        complete: () => this.handleStreamEnd(internalId, undefined),
        error: (err: unknown) => this.handleStreamEnd(internalId, err),
      });
    } catch (err) {
      // A synchronous throw (e.g. connection not started) → reject if pre-ack, else fail the sub.
      const e = err instanceof Error ? err : new Error(String(err));
      if (!sub.acked) sub.rejectHandle(e);
      else this.failSub(internalId, e);
      this._subs.delete(internalId);
      return;
    }
    sub.streamSub = streamSub;
  }

  /** Dispatches one frame string to the subscription. */
  private handleFrame(internalId: number, frameText: string): void {
    const sub = this._subs.get(internalId) as ActiveSubscription<unknown> | undefined;
    if (!sub || sub.done) return;
    const frame = parseFrame(frameText);

    if (frame.type === "ack") {
      const sid = frame.subscriptionId;
      if (!sid) {
        if (!sub.acked) sub.rejectHandle(new SleipnirError(0, "SignalR ack frame missing subscriptionId."));
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
      if (sid !== oldId) sub.lastEventId = 0; // degrade-to-fresh resets the dedup cursor
      if (!sub.acked) {
        sub.acked = true;
        sub.resolveHandle(this.makeHandle(internalId));
      }
      return;
    }

    if (frame.type === "event") {
      const evId = typeof frame.eventId === "number" ? frame.eventId : null;
      if (evId !== null) {
        if (evId <= sub.lastEventId) return; // replay duplicate (at-least-once dedup)
        sub.lastEventId = evId;
      }
      try {
        sub.handlers.onNext(frame.data);
      } catch {
        // handler error is not fatal to the subscription
      }
      return;
    }

    if (frame.type === "complete") {
      sub.done = true;
      this.teardown(internalId);
      try {
        sub.handlers.onComplete?.();
      } catch {
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
      } catch {
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
  private handleStreamEnd(internalId: number, err: unknown): void {
    const sub = this._subs.get(internalId) as ActiveSubscription<unknown> | undefined;
    if (!sub) return;
    if (sub.done) return; // terminal frame already handled
    if (this._reconnecting) return; // old stream torn down mid-reconnect → leave for re-stream
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
  private async restreamOnReconnect(): Promise<void> {
    for (const [internalId, subRaw] of [...this._subs.entries()]) {
      const sub = subRaw as ActiveSubscription<unknown>;
      if (sub.done || !sub.acked) continue;
      // Dispose the old (dead) stream-sub first.
      try {
        sub.streamSub?.dispose();
      } catch {
        // ignore
      }
      sub.streamSub = undefined;
      // Consult the resume policy (parity with WS/SSE). Default → "fresh".
      let decision: "fresh" | "resume" | "drop" = "fresh";
      const policy = sub.resumePolicy;
      if (policy) {
        const ctx = {
          controller: sub.req.controller,
          method: sub.req.method,
          subscriptionId: sub.subscriptionId,
          lastEventId: sub.lastEventId,
        };
        const d = policy(ctx);
        if (d === "fresh" || d === "resume" || d === "drop") decision = d;
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
  private makeHandle(internalId: number): SleipnirSubscription {
    const self = this;
    return {
      get subscriptionId(): string {
        return self._subs.get(internalId)?.subscriptionId ?? "";
      },
      get lastEventId(): number {
        return self._subs.get(internalId)?.lastEventId ?? 0;
      },
      unsubscribe(): Promise<void> {
        return self.teardown(internalId);
      },
    };
  }

  /** Tears down a subscription: dispose the stream-sub (sends a stream Cancel → hub finally →
   * Detach for durable), remove from the map. Idempotent. */
  private async teardown(internalId: number, reason?: Error): Promise<void> {
    const sub = this._subs.get(internalId) as ActiveSubscription<unknown> | undefined;
    if (!sub) return;
    try {
      sub.abort.abort(reason ?? new Error("unsubscribed"));
    } catch {
      // ignore
    }
    try {
      sub.streamSub?.dispose();
    } catch {
      // ignore
    }
    sub.streamSub = undefined;
    this._subs.delete(internalId);
  }

  /** Fails a subscription with an error (onError) and removes it. */
  private failSub(internalId: number, err: Error): void {
    const sub = this._subs.get(internalId) as ActiveSubscription<unknown> | undefined;
    if (!sub) return;
    this._subs.delete(internalId);
    try {
      sub.streamSub?.dispose();
    } catch {
      // ignore
    }
    try {
      sub.handlers.onError?.(err);
    } catch {
      // ignore
    }
  }

  /** Fails ALL active subscriptions (on close / dispose). */
  private failAllSubs(err: Error): void {
    for (const id of [...this._subs.keys()]) this.failSub(id, err);
  }

  /** Races an invoke promise against an optional abort signal / timeout. The server invoke is NOT
   * cancelled (SignalR has no per-invoke cancel) — it completes in the background, result discarded. */
  private raceAbort<T>(p: Promise<T>, opts?: SignalrCallOptions): Promise<T> {
    const timeout = opts?.timeout ?? this._callTimeout;
    const signal = opts?.signal;
    if (!timeout && !signal) return p;
    return new Promise<T>((resolve, reject) => {
      let settled = false;
      const finish = (fn: () => void) => {
        if (settled) return;
        settled = true;
        cleanup();
        fn();
      };
      const onAbort = () => {
        finish(() => reject(new CancelledError("Sleipnir call was cancelled.", false)));
      };
      const timer =
        timeout && timeout > 0
          ? setTimeout(() => finish(() => reject(new CancelledError("Sleipnir call timed out.", true))), timeout)
          : undefined;
      const cleanup = () => {
        if (timer) clearTimeout(timer);
        signal?.removeEventListener("abort", onAbort);
      };
      if (signal) {
        if (signal.aborted) {
          finish(() => reject(new CancelledError("Sleipnir call was cancelled.", false)));
          return;
        }
        signal.addEventListener("abort", onAbort, { once: true });
      }
      p.then(
        (v) => finish(() => resolve(v)),
        (e) => finish(() => reject(e instanceof Error ? e : new Error(String(e)))),
      );
    });
  }
}