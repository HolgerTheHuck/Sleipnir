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

import { ExecutionMode, SleipnirConnectionState } from "./types.js";
import { SleipnirError } from "./errors.js";
import type {
  BearerProvider,
  SleipnirConnectionStatus,
  SleipnirMultiRequest,
  SleipnirRequest,
  SleipnirResponse,
} from "./types.js";
import { SleipnirRestClient, type SleipnirRestClientOptions, type CallOptions } from "./rest.js";
import {
  SleipnirWebSocketClient,
  type SleipnirWebSocketClientOptions,
  type WsCallOptions,
  type SubscribeOptions,
  type SubscribeHandlers,
  type SleipnirSubscription,
  type ResumePolicy,
} from "./websocket.js";
import { SleipnirSseClient, type SleipnirSseClientOptions, type SseSubscribeOptions, type SseResumeOptions } from "./sse.js";
import {
  SleipnirSignalrClient,
  type SleipnirSignalrClientOptions,
  type SignalrCallOptions,
  type SignalrSubscribeOptions,
} from "./signalr.js";

/** A single bundled backend client (one of REST / WS / SSE / SignalR). Internal. */
type Backend = "rest" | "ws" | "sse" | "signalr";

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

/** Backends bundled per capability. */
const CAPABILITY_BACKEDS: Record<SleipnirBundleCapability, Backend[]> = {
  rest: ["rest", "sse"],
  ws: ["ws"],
  all: ["rest", "ws", "sse"],
  signalr: ["rest", "ws", "sse", "signalr"],
};

/** Thrown when `useTransport`/`negotiate` selects a backend not present in the bundle. */
export class SleipnirTransportNotBundledError extends Error {
  constructor(readonly transport: string, readonly capability: SleipnirBundleCapability) {
    super(
      `Sleipnir transport '${transport}' is not available: the client was generated with --transport ${capability}, which does not bundle the required backend. Regenerate with a capability that includes it (e.g. --transport all).`,
    );
    this.name = "SleipnirTransportNotBundledError";
  }
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
  /**
   * Called when the server answers **401 Unauthenticated** (a response/error with `code` 401):
   * refresh the credentials here (e.g. renew the token and call `setBearer`, or re-establish the
   * auth cookie); the router then retries the operation **exactly once**. The retry's result is
   * returned as is — a second 401 is not retried again (no loop). Concurrent 401s share one
   * refresh. On the `ws`/`signalr` profiles the connection is re-established after the hook
   * (those transports authenticate once, at connect); calls still in flight on the old connection
   * are then rejected with a transport error (`code` 0), as on a network drop. A batch is retried
   * only when **every** response is 401 (nothing ran); a partially rejected batch is returned as
   * is. If the hook throws, the operation rejects with that error. 403 (`PermissionDenied`) never
   * triggers the hook.
   */
  onUnauthenticated?: (ctx: SleipnirUnauthenticatedContext) => void | Promise<void>;
}

/** Context passed to {@link SleipnirRouterOptions.onUnauthenticated}. */
export interface SleipnirUnauthenticatedContext {
  /** Active profile the 401 came from. */
  readonly transport: Exclude<SleipnirTransport, "auto">;
  /** Which operation got the 401. */
  readonly operation: "call" | "batch" | "subscribe" | "resume";
}

/**
 * Store-shaped view of the router's aggregated connection status — the `subscribe(listener) →
 * unsubscribe` contract of Svelte stores (the listener is called synchronously with the current
 * value, then on every change) and of Tsazor's `observe()`.
 */
export interface SleipnirConnectionStore {
  /** Current status. */
  readonly state: SleipnirConnectionStatus;
  /** Registers a listener; called immediately with the current status, then on each change. */
  subscribe(listener: (state: SleipnirConnectionStatus) => void): () => void;
}

/** Unified subscribe options across event backends (WS / SSE / SignalR). */
export interface SleipnirSubscribeOptions {
  /**
   * Abort signal. Before the subscription is acknowledged the subscribe rejects with
   * `CancelledError`; afterwards aborting ends the subscription (like `unsubscribe()`), without
   * reconnect — on every event backend. The listener is detached when the subscription ends
   * (see `SleipnirSubscription.ended`).
   */
  signal?: AbortSignal;
  /** Per-subscription resume policy (overrides the client-wide policy). WS + SSE + SignalR. */
  resumePolicy?: ResumePolicy;
  /**
   * Subscribe timeout in ms — the time allowed until the server acknowledges the subscription
   * (WS: the subscribe response; SSE: the `ack` block; SignalR: the ack frame). On expiry the
   * subscribe rejects with `CancelledError` (`timedOut: true`). A live subscription has no timeout.
   */
  timeout?: number;
  /** Extra headers for this subscribe request (SSE only — WS/SignalR carry no per-request headers). */
  headers?: Record<string, string>;
}

function hasBackend(cap: SleipnirBundleCapability, b: Backend): boolean {
  return CAPABILITY_BACKEDS[cap].includes(b);
}

/** Maps a backend's connection state onto the router's status vocabulary. */
function toStatus(s: SleipnirConnectionState): SleipnirConnectionStatus {
  switch (s) {
    case SleipnirConnectionState.Connecting:
      return "connecting";
    case SleipnirConnectionState.Connected:
      return "open";
    case SleipnirConnectionState.Reconnecting:
      return "reconnecting";
    default:
      return "closed";
  }
}

/** True for a 401 Unauthenticated error (a thrown {@link SleipnirError} or an error body). */
function isUnauthenticatedError(err: unknown): boolean {
  return err instanceof SleipnirError && err.code === 401;
}

/**
 * Unified transport router. Holds the bundled backends, routes calls and subscriptions to the
 * active profile, and negotiates `auto`. The generated `SleipnirClient` is a thin facade over
 * this class; it never branches on transport itself.
 */
export class SleipnirTransportRouter {
  readonly capability: SleipnirBundleCapability;

  private readonly _rest?: SleipnirRestClient;
  private readonly _ws?: SleipnirWebSocketClient;
  private readonly _sse?: SleipnirSseClient;
  private readonly _signalr?: SleipnirSignalrClient;
  private readonly _probeTimeout: number;
  private readonly _onUnauthenticated?: (ctx: SleipnirUnauthenticatedContext) => void | Promise<void>;

  /** Active profile (set by `negotiate`/`useTransport`). `null` until first resolution. */
  private _profile: Exclude<SleipnirTransport, "auto"> | null = null;
  private _negotiatePromise: Promise<void> | null = null;
  private _negotiating = false;
  private _disposed = false;
  private _refreshPromise: Promise<void> | null = null;

  private _status: SleipnirConnectionStatus = "closed";
  private readonly _statusListeners = new Set<(state: SleipnirConnectionStatus) => void>();

  /**
   * Aggregated connection status of the active transport profile, as a store
   * (`connection.state` + `connection.subscribe(listener) → unsubscribe`).
   *
   * Transitions:
   * - Initial: `"closed"`; `"open"` right away when the default profile is `rest` (stateless).
   * - `auto`: `"connecting"` while the WebSocket probe runs → then the resolved profile's status.
   * - `ws` / `signalr`: follows the backend — `"connecting"` (first connect) → `"open"`;
   *   a drop → `"reconnecting"` → `"open"` again, or `"closed"` when reconnecting gives up.
   *   Before the first call/subscribe the lazily-connecting backend is `"closed"`.
   * - `rest`: `"open"` (REST needs no connection); `"reconnecting"` while any SSE event stream is
   *   reconnecting, back to `"open"` afterwards.
   * - `useTransport(...)` re-evaluates against the new profile; `dispose()` → `"closed"` (final).
   *
   * Listeners are only notified on an actual change. Inactive bundled backends (e.g. the WS socket
   * left over after `auto` fell back to REST) do not influence the status.
   */
  readonly connection: SleipnirConnectionStore;

  constructor(opts: SleipnirRouterOptions) {
    if (!opts?.baseUrl) throw new Error("SleipnirTransportRouter: baseUrl is required.");
    this.capability = opts.capability;
    this._probeTimeout = opts.probeTimeout ?? 1500;
    this._onUnauthenticated = opts.onUnauthenticated;

    const bearer = opts.bearer;
    const callTimeout = opts.callTimeout;
    const recompute = () => this.recomputeStatus();

    if (hasBackend(this.capability, "rest")) {
      this._rest = new SleipnirRestClient(opts.baseUrl, { ...(opts.rest ?? {}), bearer, callTimeout });
    }
    if (hasBackend(this.capability, "ws")) {
      const user = opts.ws?.onStateChanged;
      this._ws = new SleipnirWebSocketClient(opts.baseUrl, {
        ...(opts.ws ?? {}),
        bearer,
        callTimeout,
        onStateChanged: (s) => {
          user?.(s);
          recompute();
        },
      });
    }
    if (hasBackend(this.capability, "sse")) {
      const user = opts.sse?.onStateChanged;
      this._sse = new SleipnirSseClient(opts.baseUrl, {
        ...(opts.sse ?? {}),
        bearer,
        onStateChanged: (s) => {
          user?.(s);
          recompute();
        },
      });
    }
    if (hasBackend(this.capability, "signalr")) {
      const user = opts.signalr?.onStateChanged;
      this._signalr = new SleipnirSignalrClient(opts.baseUrl, {
        ...(opts.signalr ?? {}),
        bearer,
        onStateChanged: (s) => {
          user?.(s);
          recompute();
        },
      });
    }

    const self = this;
    this.connection = {
      get state() {
        return self._status;
      },
      subscribe(listener) {
        self._statusListeners.add(listener);
        try {
          listener(self._status);
        } catch {
          /* listener errors are not fatal */
        }
        return () => {
          self._statusListeners.delete(listener);
        };
      },
    };

    // Resolve the initial profile. A non-auto default is set immediately; "auto" is probed
    // lazily on first use (avoid constructor side-effects / connect races).
    const initial = opts.defaultTransport ?? "auto";
    if (initial !== "auto") {
      this._profile = this.resolveProfile(initial);
    }
    this.recomputeStatus();
  }

  // --- escape hatches (raw backends; undefined if not bundled) ---

  /** Underlying REST client (escape hatch). `undefined` if not bundled. */
  get rest(): SleipnirRestClient | undefined {
    return this._rest;
  }
  /** Underlying WebSocket client (escape hatch). `undefined` if not bundled. */
  get ws(): SleipnirWebSocketClient | undefined {
    return this._ws;
  }
  /** Underlying SSE client (escape hatch). `undefined` if not bundled. */
  get sse(): SleipnirSseClient | undefined {
    return this._sse;
  }
  /** Underlying SignalR client (escape hatch). `undefined` if not bundled. */
  get signalr(): SleipnirSignalrClient | undefined {
    return this._signalr;
  }

  /** Current active profile (the resolved transport). `null` until first use when `auto`. */
  get activeTransport(): Exclude<SleipnirTransport, "auto"> | null {
    return this._profile;
  }

  // --- profile resolution ---

  /**
   * Maps a user-facing transport to a concrete {call, event} backend pair, validating that both
   * backends are bundled. Throws {@link SleipnirTransportNotBundledError} if a required backend is
   * missing, or `Error` if the profile isn't implemented yet (SignalR pre-Phase-3).
   */
  private resolveProfile(t: SleipnirTransport): Exclude<SleipnirTransport, "auto"> {
    if (t === "rest") {
      if (!this._rest || !this._sse) throw new SleipnirTransportNotBundledError(t, this.capability);
      return "rest";
    }
    if (t === "ws") {
      if (!this._ws) throw new SleipnirTransportNotBundledError(t, this.capability);
      return "ws";
    }
    if (t === "signalr") {
      if (!this._signalr) throw new SleipnirTransportNotBundledError(t, this.capability);
      return "signalr";
    }
    // "auto" is resolved via negotiate(), never here.
    throw new Error(`Sleipnir transport '${t}' is not a valid profile.`);
  }

  /** Backend used for calls under the active profile. */
  private callBackend(): "rest" | "ws" | "signalr" {
    if (this._profile === "ws") return "ws";
    if (this._profile === "signalr") return "signalr";
    return "rest"; // "rest" profile
  }

  /** Backend used for events under the active profile. */
  private eventBackend(): "sse" | "ws" | "signalr" {
    if (this._profile === "ws") return "ws";
    if (this._profile === "signalr") return "signalr";
    return "sse"; // "rest" profile
  }

  /**
   * Ensures the active profile is resolved. For `"auto"` this runs the WS handshake probe once
   * (lazy, concurrent-safe): on success the WS profile is used; on failure/timeout the rest
   * profile (REST calls + SSE events) is used. Subsequent calls reuse the resolved profile.
   */
  async negotiate(): Promise<void> {
    if (this._disposed) throw new Error("SleipnirTransportRouter: disposed.");
    if (this._profile) return;
    if (!this._negotiatePromise) {
      this._negotiating = true;
      this.recomputeStatus();
      this._negotiatePromise = this.runAutoNegotiation().finally(() => {
        this._negotiating = false;
        this.recomputeStatus();
      });
    }
    await this._negotiatePromise;
  }

  private async runAutoNegotiation(): Promise<void> {
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
      const timeout = new Promise<"timeout">((r) => {
        setTimeout(() => r("timeout"), this._probeTimeout);
      });
      const result = await Promise.race([
        this._ws.connect().then(() => "ok" as const),
        timeout,
      ]);
      ok = result === "ok";
    } catch {
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
  async useTransport(t: SleipnirTransport): Promise<void> {
    if (this._disposed) throw new Error("SleipnirTransportRouter: disposed.");
    if (t === "auto") {
      this._profile = null;
      this._negotiatePromise = null;
      this.recomputeStatus();
      await this.negotiate();
      return;
    }
    this._profile = this.resolveProfile(t);
    this.recomputeStatus();
  }

  // --- call routing ---

  /** Execute a single request over the active call backend (401 → `onUnauthenticated` → one retry). */
  async call(
    req: SleipnirRequest,
    opts?: CallOptions | WsCallOptions | SignalrCallOptions,
  ): Promise<SleipnirResponse> {
    const first = await this.callOnce(req, opts);
    if (first.code !== 401 || !this._onUnauthenticated) return first;
    await this.refreshCredentials("call");
    return this.callOnce(req, opts);
  }

  private async callOnce(
    req: SleipnirRequest,
    opts?: CallOptions | WsCallOptions | SignalrCallOptions,
  ): Promise<SleipnirResponse> {
    await this.ensureProfile();
    const backend = this.callBackend();
    if (backend === "ws") return this._ws!.call(req, opts as WsCallOptions | undefined);
    if (backend === "signalr") return this._signalr!.call(req, opts as SignalrCallOptions | undefined);
    return this._rest!.call(req, opts as CallOptions | undefined);
  }

  /**
   * Execute a batch over the active call backend. 401 handling: retried once after
   * `onUnauthenticated` only when every response is 401 (nothing executed).
   */
  async callBatch(
    requests: SleipnirRequest[],
    mode: ExecutionMode = ExecutionMode.Parallel,
    opts?: CallOptions | WsCallOptions | SignalrCallOptions,
  ): Promise<SleipnirResponse[]> {
    const first = await this.callBatchOnce(requests, mode, opts);
    const allUnauthenticated = first.length > 0 && first.every((r) => r.code === 401);
    if (!allUnauthenticated || !this._onUnauthenticated) return first;
    await this.refreshCredentials("batch");
    return this.callBatchOnce(requests, mode, opts);
  }

  private async callBatchOnce(
    requests: SleipnirRequest[],
    mode: ExecutionMode,
    opts?: CallOptions | WsCallOptions | SignalrCallOptions,
  ): Promise<SleipnirResponse[]> {
    await this.ensureProfile();
    const backend = this.callBackend();
    if (backend === "ws") return this._ws!.callBatch(requests, mode, opts as WsCallOptions | undefined);
    if (backend === "signalr") return this._signalr!.callBatch(requests, mode, opts as SignalrCallOptions | undefined);
    return this._rest!.callBatch(requests, mode, opts as CallOptions | undefined);
  }

  /** Build a multi-request from the fluent batch (transport-agnostic). */
  toMulti(requests: SleipnirRequest[], mode: ExecutionMode): SleipnirMultiRequest {
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
   *
   * A 401 rejection triggers `onUnauthenticated` and one retry (see {@link SleipnirRouterOptions}).
   */
  async subscribe<T>(
    req: SleipnirRequest,
    handlers: SubscribeHandlers<T>,
    opts?: SleipnirSubscribeOptions,
  ): Promise<SleipnirSubscription> {
    try {
      return await this.subscribeOnce(req, handlers, opts);
    } catch (err) {
      if (!isUnauthenticatedError(err) || !this._onUnauthenticated || opts?.signal?.aborted) throw err;
      await this.refreshCredentials("subscribe");
      return this.subscribeOnce(req, handlers, opts);
    }
  }

  private async subscribeOnce<T>(
    req: SleipnirRequest,
    handlers: SubscribeHandlers<T>,
    opts?: SleipnirSubscribeOptions,
  ): Promise<SleipnirSubscription> {
    await this.ensureProfile();
    const backend = this.eventBackend();
    if (backend === "ws") {
      const wsOpts: SubscribeOptions | undefined = opts
        ? { signal: opts.signal, timeout: opts.timeout, resumePolicy: opts.resumePolicy }
        : undefined;
      return this._ws!.subscribe<T>(req, handlers, wsOpts);
    }
    if (backend === "signalr") {
      const srOpts: SignalrSubscribeOptions | undefined = opts
        ? { signal: opts.signal, resumePolicy: opts.resumePolicy, timeout: opts.timeout }
        : undefined;
      return this._signalr!.subscribe<T>(req, handlers, srOpts);
    }
    // SSE: unpack the request into (controller, method, params).
    const params: Record<string, unknown> = {};
    for (const p of req.params ?? []) params[p.parameterName] = p.data;
    const sseOpts: SseSubscribeOptions | undefined = opts
      ? { signal: opts.signal, resumePolicy: opts.resumePolicy, headers: opts.headers, timeout: opts.timeout }
      : undefined;
    return this._sse!.subscribe<T>(req.controller, req.method, handlers, params, sseOpts);
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
  async resume<T>(
    subscriptionId: string,
    lastEventId: number,
    handlers: SubscribeHandlers<T>,
    opts?: SleipnirSubscribeOptions,
  ): Promise<SleipnirSubscription> {
    try {
      return await this.resumeOnce(subscriptionId, lastEventId, handlers, opts);
    } catch (err) {
      if (!isUnauthenticatedError(err) || !this._onUnauthenticated || opts?.signal?.aborted) throw err;
      await this.refreshCredentials("resume");
      return this.resumeOnce(subscriptionId, lastEventId, handlers, opts);
    }
  }

  private async resumeOnce<T>(
    subscriptionId: string,
    lastEventId: number,
    handlers: SubscribeHandlers<T>,
    opts?: SleipnirSubscribeOptions,
  ): Promise<SleipnirSubscription> {
    await this.ensureProfile();
    const backend = this.eventBackend();
    if (backend === "ws") {
      throw new Error(
        "Sleipnir cross-transport resume into WebSocket is not supported (the WS resume frame needs the original controller/method). " +
          "Switch to the rest/auto profile via useTransport('rest') to resume over SSE.",
      );
    }
    if (backend === "signalr") {
      const srOpts: SignalrSubscribeOptions | undefined = opts
        ? { signal: opts.signal, resumePolicy: opts.resumePolicy, timeout: opts.timeout }
        : undefined;
      return this._signalr!.resume<T>(subscriptionId, lastEventId, handlers, srOpts);
    }
    const sseOpts: SseResumeOptions | undefined = opts
      ? { signal: opts.signal, resumePolicy: opts.resumePolicy, headers: opts.headers, timeout: opts.timeout }
      : undefined;
    return this._sse!.resume<T>(subscriptionId, lastEventId, handlers, sseOpts);
  }

  // --- shared concerns ---

  /** Fan a bearer swap out to every bundled backend that accepts one. */
  setBearer(bearer: BearerProvider): void {
    this._rest?.setBearer(bearer);
    this._ws?.setBearer(bearer);
    this._sse?.setBearer(bearer);
    this._signalr?.setBearer(bearer);
  }

  /** Dispose all bundled backends (terminal). */
  dispose(): void {
    this._disposed = true;
    this._ws?.close();
    this._signalr?.dispose();
    // REST + SSE are stateless / per-subscribe; nothing to dispose.
    this.recomputeStatus();
    this._statusListeners.clear();
  }

  private async ensureProfile(): Promise<void> {
    if (this._profile) return;
    await this.negotiate();
  }

  /**
   * Runs `onUnauthenticated` once for all concurrent 401s, then — on the connection-oriented
   * profiles — re-establishes the connection so the refreshed credentials are presented.
   */
  private refreshCredentials(operation: SleipnirUnauthenticatedContext["operation"]): Promise<void> {
    if (!this._refreshPromise) {
      const transport = this._profile ?? "rest";
      this._refreshPromise = (async () => {
        await this._onUnauthenticated!({ transport, operation });
        if (transport === "ws" && this._ws) await this._ws.reconnect();
        else if (transport === "signalr" && this._signalr) await this._signalr.reconnect();
      })().finally(() => {
        this._refreshPromise = null;
      });
    }
    return this._refreshPromise;
  }

  /** Recomputes the aggregated status; notifies listeners on change. */
  private recomputeStatus(): void {
    let next: SleipnirConnectionStatus;
    if (this._disposed) next = "closed";
    else if (!this._profile) next = this._negotiating ? "connecting" : "closed";
    else if (this._profile === "ws") next = toStatus(this._ws!.state);
    else if (this._profile === "signalr") next = toStatus(this._signalr!.state);
    else next = this._sse?.state === SleipnirConnectionState.Reconnecting ? "reconnecting" : "open";
    if (next === this._status) return;
    this._status = next;
    for (const listener of [...this._statusListeners]) {
      try {
        listener(next);
      } catch {
        /* listener errors are not fatal */
      }
    }
  }
}
