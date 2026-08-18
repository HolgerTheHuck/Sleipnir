// Auto-generated root Sleipnir client (JS, WebSocket transport).
import { SleipnirCall, SleipnirWebSocketClient } from "sleipnir-client";
import { ChatClient } from "./controllers.js";
import { TickerClient } from "./controllers.js";
import { UserClient } from "./controllers.js";

export class SleipnirClient {
  /**
   * @param {string} baseUrl
   * @param {SleipnirWebSocketClientOptions} [options]
   */
  constructor(baseUrl, options = {}) {
    this._ws = new SleipnirWebSocketClient(baseUrl, options);
    const build = (controller, method) => SleipnirCall.init(controller, method);
  this._subscribe = (req, handlers) => this._ws.subscribe(req, handlers);
  this.chat = new ChatClient(build, this._subscribe);
  this.ticker = new TickerClient(build, this._subscribe);
  this.user = new UserClient(build);
  }

  /** @param {TypedCall<*>} call @returns {Promise<SleipnirResponse<*|null>>} */
  async call(call) {
    return this._ws.call(call.toRequest());
  }

  /** @param {Batch} b @returns {Promise<SleipnirResponse[]>} */
  async batch(b) {
    const m = b.toMulti();
    return this._ws.callBatch(m.requests, m.mode);
  }

  get ws() {
    return this._ws;
  }
}
