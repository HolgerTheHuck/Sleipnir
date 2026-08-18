// Auto-generated root Sleipnir client (JS).
import { SleipnirCall, SleipnirRestClient } from "sleipnir-client";
import { ChatClient } from "./controllers.js";
import { TickerClient } from "./controllers.js";
import { UserClient } from "./controllers.js";

export class SleipnirClient {
  /**
   * @param {string} baseUrl
   * @param {SleipnirRestClientOptions} [options]
   */
  constructor(baseUrl, options = {}) {
    this._rest = new SleipnirRestClient(baseUrl, options);
    const build = (controller, method) => SleipnirCall.init(controller, method);
  this._subscribe = async (_req, _handlers) => {
    throw new Error("Sleipnir events require WebSocket transport. Regenerate with --transport ws|both to subscribe.");
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
}
