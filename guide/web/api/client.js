// Auto-generated root Sleipnir client (JS, capability: rest).
// Transport is selected at runtime via SleipnirTransportRouter: "auto" (default) probes
// WebSocket and falls back to REST+SSE on failure; useTransport() switches explicitly.
import { SleipnirCall, SleipnirTransportRouter } from "sleipnir-client";
import { MarketClient } from "./controllers.js";

export class SleipnirClient {
  /**
   * @param {string} baseUrl
   * @param {object} [options] per-backend options (rest/ws/sse/signalr) + shared bearer,
   *   callTimeout, probeTimeout, defaultTransport. Passed to SleipnirTransportRouter.
   */
  constructor(baseUrl, options = {}) {
    this._router = new SleipnirTransportRouter({ baseUrl, capability: "rest", ...options });
    const build = (controller, method) => SleipnirCall.init(controller, method);
  this.market = new MarketClient(build);
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

  /** @param {TypedCall<*>} call @returns {Promise<SleipnirResponse<*|null>>} */
  async call(call) {
    return this._router.call(call.toRequest());
  }

  /** @param {Batch} b @returns {Promise<SleipnirResponse[]>} */
  async batch(b) {
    const m = b.toMulti();
    return this._router.callBatch(m.requests, m.mode);
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
