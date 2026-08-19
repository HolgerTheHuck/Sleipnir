// Auto-generated root Sleipnir client (JS, REST + SSE transport).
import { SleipnirCall, SleipnirRestClient, SleipnirSseClient } from "sleipnir-client";
import { ChatClient } from "./controllers.js";
import { TickerClient } from "./controllers.js";
import { UserClient } from "./controllers.js";

export class SleipnirClient {
  /**
   * @param {string} baseUrl
   * @param {{ rest?: SleipnirRestClientOptions, sse?: SleipnirSseClientOptions }} [options]
   */
  constructor(baseUrl, options = {}) {
    this._rest = new SleipnirRestClient(baseUrl, options.rest ?? {});
    this._sse = new SleipnirSseClient(baseUrl, options.sse ?? {});
    const build = (controller, method) => SleipnirCall.init(controller, method);
  this._subscribe = (req, handlers) => {
    const params = {};
    for (const p of (req.params ?? [])) params[p.parameterName] = p.data;
    return this._sse.subscribe(req.controller, req.method, handlers, params);
  };
  this.chat = new ChatClient(build, this._subscribe);
  this.ticker = new TickerClient(build, this._subscribe);
  this.user = new UserClient(build);
  }

  /** @param {TypedCall<*>} call @returns {Promise<SleipnirResponse<*|null>>} */
  async call(call) {
    return this._rest.call(call.toRequest());
  }

  /** @param {Batch} b @returns {Promise<SleipnirResponse[]>} */
  async batch(b) {
    const m = b.toMulti();
    return this._rest.callBatch(m.requests, m.mode);
  }

  get rest() {
    return this._rest;
  }

  get sse() {
    return this._sse;
  }
}
