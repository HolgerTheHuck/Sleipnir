// Auto-generated root Sleipnir client (JS, WebSocket transport).
import { SleipnirCall, SleipnirWebSocketClient } from "sleipnir-client";
import { SearchClient } from "./controllers.js";
import { ArticleClient } from "./controllers.js";

export class SleipnirClient {
  /**
   * @param {string} baseUrl
   * @param {SleipnirWebSocketClientOptions} [options]
   */
  constructor(baseUrl, options = {}) {
    this._ws = new SleipnirWebSocketClient(baseUrl, options);
    const build = (controller, method) => SleipnirCall.init(controller, method);
  this.search = new SearchClient(build);
  this.article = new ArticleClient(build);
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
