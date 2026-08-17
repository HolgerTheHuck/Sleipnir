// Auto-generated root Sleipnir client (JS, REST + WebSocket).
import { SleipnirCall, SleipnirRestClient, SleipnirWebSocketClient } from "sleipnir-client";
import { StockClient } from "./controllers.js";
import { OrderLineClient } from "./controllers.js";
import { ArticleClient } from "./controllers.js";
import { OrderClient } from "./controllers.js";
import { CustomerClient } from "./controllers.js";
import { AddressClient } from "./controllers.js";

export class SleipnirClient {
  /**
   * @param {string} baseUrl
   * @param {{ rest?: SleipnirRestClientOptions, ws?: SleipnirWebSocketClientOptions }} [options]
   */
  constructor(baseUrl, options = {}) {
    this._rest = new SleipnirRestClient(baseUrl, options.rest ?? {});
    this._ws = new SleipnirWebSocketClient(baseUrl, options.ws ?? {});
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
    return this._rest.call(call.toRequest());
  }

  /** @param {Batch} b @returns {Promise<SleipnirResponse[]>} */
  async batch(b) {
    const m = b.toMulti();
    return this._rest.callBatch(m.requests, m.mode);
  }

  /** @param {TypedCall<*>} call @returns {Promise<SleipnirResponse<*|null>>} */
  async callWs(call) {
    return this._ws.call(call.toRequest());
  }

  /** @param {Batch} b @returns {Promise<SleipnirResponse[]>} */
  async batchWs(b) {
    const m = b.toMulti();
    return this._ws.callBatch(m.requests, m.mode);
  }

  get rest() {
    return this._rest;
  }

  get ws() {
    return this._ws;
  }
}
