// Auto-generated root Sleipnir client (JS, WebSocket transport).
import { SleipnirCall, SleipnirWebSocketClient } from "sleipnir-client";
import { StockClient } from "./controllers.js";
import { OrderLineClient } from "./controllers.js";
import { ArticleClient } from "./controllers.js";
import { OrderClient } from "./controllers.js";
import { CustomerClient } from "./controllers.js";
import { AddressClient } from "./controllers.js";

export class SleipnirClient {
  /**
   * @param {string} baseUrl
   * @param {SleipnirWebSocketClientOptions} [options]
   */
  constructor(baseUrl, options = {}) {
    this._ws = new SleipnirWebSocketClient(baseUrl, options);
    const build = (controller, method) => SleipnirCall.init(controller, method);
  this.stock = new StockClient(build);
  this.orderLine = new OrderLineClient(build);
  this.article = new ArticleClient(build);
  this.order = new OrderClient(build);
  this.customer = new CustomerClient(build);
  this.address = new AddressClient(build);
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
