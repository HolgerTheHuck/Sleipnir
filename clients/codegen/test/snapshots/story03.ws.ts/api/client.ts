// Auto-generated root Sleipnir client (capability: ws). Compose with the sleipnir-client runtime.
// Transport is selected at runtime via SleipnirTransportRouter: "auto" (default) probes WebSocket
// and falls back to REST+SSE on failure; useTransport() switches explicitly. The public surface
// is identical across all capabilities — only the bundled backends differ.
import { SleipnirCall, SleipnirTransportRouter } from "sleipnir-client";
import type { SleipnirResponse, SleipnirRequest, SubscribeHandlers, SleipnirSubscription, SleipnirTransport, SleipnirRestClient, SleipnirWebSocketClient, SleipnirSseClient, SleipnirSignalrClient, SleipnirRestClientOptions, SleipnirWebSocketClientOptions, SleipnirSseClientOptions, SleipnirSignalrClientOptions } from "sleipnir-client";
import { Batch, TypedCall } from "./typed-call.js";
import { ChatClient } from "./controllers.js";
import { TickerClient } from "./controllers.js";
import { UserClient } from "./controllers.js";

/** A SleipnirResponse whose `data` is narrowed to T (the wire shape is unchanged). */
export type TypedResponse<T> = SleipnirResponse & { data: T | null };

/** Options for the generated SleipnirClient — a strict superset across all capabilities.
 *  Fields for unbundled backends are accepted but ignored by the router (the capability
 *  decides which backends are instantiated). */
export interface SleipnirClientOptions {
  /** REST backend options (used when REST is bundled). */
  rest?: SleipnirRestClientOptions;
  /** WebSocket backend options (used when WS is bundled). */
  ws?: SleipnirWebSocketClientOptions;
  /** SSE backend options (used when SSE is bundled). */
  sse?: SleipnirSseClientOptions;
  /** SignalR backend options (opt-in add-on; Phase 3). Used when SignalR is bundled. */
  signalr?: SleipnirSignalrClientOptions;
  /** Bearer token (or provider) applied to all bundled backends. */
  bearer?: string | (() => string);
  /** Call timeout (ms) for REST + WS. */
  callTimeout?: number;
  /** WS handshake probe timeout (ms) for `auto` negotiation. Default 1500. */
  probeTimeout?: number;
  /** Default transport profile. Defaults to `auto`. */
  defaultTransport?: SleipnirTransport;
}

export class SleipnirClient {
  private readonly _router: SleipnirTransportRouter;
  private readonly _subscribe = <T>(req: SleipnirRequest, handlers: SubscribeHandlers<T>): Promise<SleipnirSubscription> => this._router.subscribe<T>(req, handlers);
  readonly chat: ChatClient;
  readonly ticker: TickerClient;
  readonly user: UserClient;

  constructor(baseUrl: string, options: SleipnirClientOptions = {}) {
    this._router = new SleipnirTransportRouter({ baseUrl, capability: "ws", ...options });
    const build = (controller: string, method: string) => SleipnirCall.init(controller, method);
    this.chat = new ChatClient(build, this._subscribe);
    this.ticker = new TickerClient(build, this._subscribe);
    this.user = new UserClient(build);
  }

  /** Resolve the `auto` profile (probe WS → fallback REST+SSE). No-op for a fixed profile. */
  negotiate(): Promise<void> { return this._router.negotiate(); }

  /** Switch the active transport at runtime. Throws if the backend isn't bundled. */
  useTransport(t: SleipnirTransport): Promise<void> { return this._router.useTransport(t); }

  /** The resolved transport profile (`null` until `auto` is negotiated). */
  get activeTransport(): Exclude<SleipnirTransport, "auto"> | null { return this._router.activeTransport; }

  /** Execute a single typed call over the active call backend; `response.data` is narrowed to T. */
  async call<T, TPaths extends Record<string, unknown>>(call: TypedCall<T, TPaths>): Promise<TypedResponse<T>> {
    return (await this._router.call(call.toRequest())) as TypedResponse<T>;
  }

  /** Execute a typed batch over the active call backend (Serial — required for @alias resolution). */
  async batch<A extends Record<string, unknown>>(b: Batch<A>): Promise<SleipnirResponse[]> {
    const multi = b.toMulti();
    return this._router.callBatch(multi.requests, multi.mode);
  }

  /** The underlying REST client (escape hatch). `undefined` if not bundled. */
  get rest(): SleipnirRestClient | undefined { return this._router.rest; }
  /** The underlying WebSocket client (escape hatch). `undefined` if not bundled. */
  get ws(): SleipnirWebSocketClient | undefined { return this._router.ws; }
  /** The underlying SSE client (escape hatch). `undefined` if not bundled. */
  get sse(): SleipnirSseClient | undefined { return this._router.sse; }
  /** The underlying SignalR client (escape hatch). `undefined` if not bundled. */
  get signalr(): SleipnirSignalrClient | undefined { return this._router.signalr; }

  /** Swap the bearer on all bundled backends. */
  setBearer(bearer: string | (() => string)): void { this._router.setBearer(bearer); }

  /** Dispose all bundled backends (terminal). */
  dispose(): void { this._router.dispose(); }
}
