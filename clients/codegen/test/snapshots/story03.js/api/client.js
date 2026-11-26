// Auto-generated root Sleipnir client (JS, capability: all).
// Transport is selected at runtime via SleipnirTransportRouter: "auto" (default) probes
// WebSocket and falls back to REST+SSE on failure; useTransport() switches explicitly.
import { SleipnirCall, SleipnirTransportRouter } from "sleipnir-client";
import { ChatClient } from "./controllers.js";
import { TickerClient } from "./controllers.js";
import { UserClient } from "./controllers.js";

export class SleipnirClient {
  /**
   * @param {string} baseUrl
   * @param {object} [options] per-backend options (rest/ws/sse/signalr) + shared bearer,
   *   callTimeout, probeTimeout, defaultTransport. Passed to SleipnirTransportRouter.
   */
  constructor(baseUrl, options = {}) {
    this._router = new SleipnirTransportRouter({ baseUrl, capability: "all", ...options });
    const build = (controller, method) => SleipnirCall.init(controller, method);
  this._subscribe = (req, handlers) => this._router.subscribe(req, handlers);
  this.chat = new ChatClient(build, this._subscribe);
  this.ticker = new TickerClient(build, this._subscribe);
  this.user = new UserClient(build);
  }

  /** @returns {Promise<void>} resolve the `auto` profile (probe WS → fallback REST+SSE). */
  negotiate() {
    return this._router.negotiate();
  }

  /** @param {string} t @returns {Promise<void>} switch the active transport at runtime. */
  useTransport(t) {
    return this._router.useTransport(t);
  }

  /** @returns {string|null} the resolved transport profile (null until `auto` is negotiated). */
  get activeTransport() {
    return this._router.activeTransport;
  }

  /**
   * Aggregated connection status of the active transport profile, as a store
   * (`connection.state` + `connection.subscribe(listener) → unsubscribe`); one of
   * `connecting | open | reconnecting | closed` on every transport.
   * @returns {{ state: string, subscribe(listener: (state: string) => void): () => void }}
   */
  get connection() {
    return this._router.connection;
  }

  /** @param {TypedCall<*>} call @returns {Promise<SleipnirResponse<*|null>>} */
  async call(call) {
    return this._router.call(call.toRequest());
  }

  /** @param {Batch} b @returns {Promise<SleipnirResponse[]>} */
  async batch(b) {
    const m = b.toMulti();
    return this._router.callBatch(m.requests, m.mode);
  }

  /**
   * Resume a durable event subscription by `subscriptionId` + `lastEventId` (both on the
   * SleipnirSubscription handle): the server replays the gap, then continues live. Runs over
   * the active event backend (SSE or SignalR); resuming into WebSocket is not supported.
   * @param {string} subscriptionId @param {number} lastEventId
   * @param {SubscribeHandlers<*>} handlers @param {object} [options] signal, resumePolicy, timeout, headers
   * @returns {Promise<SleipnirSubscription>}
   */
  resume(subscriptionId, lastEventId, handlers, options) {
    return this._router.resume(subscriptionId, lastEventId, handlers, options);
  }

  /** @returns {SleipnirRestClient|undefined} underlying REST client (escape hatch). */
  get rest() {
    return this._router.rest;
  }

  /** @returns {SleipnirWebSocketClient|undefined} underlying WebSocket client (escape hatch). */
  get ws() {
    return this._router.ws;
  }

  /** @returns {SleipnirSseClient|undefined} underlying SSE client (escape hatch). */
  get sse() {
    return this._router.sse;
  }

  /** @returns {SleipnirSignalrClient|undefined} underlying SignalR client (escape hatch). */
  get signalr() {
    return this._router.signalr;
  }

  /** @param {string|Function} bearer swap the bearer on all bundled backends. */
  setBearer(bearer) {
    this._router.setBearer(bearer);
  }

  /** Dispose all bundled backends (terminal). */
  dispose() {
    this._router.dispose();
  }
}
