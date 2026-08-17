// Auto-generated root Sleipnir client (JS).
import { SleipnirCall, SleipnirRestClient } from "sleipnir-client";
import { SearchClient } from "./controllers.js";
import { ArticleClient } from "./controllers.js";

export class SleipnirClient {
  /**
   * @param {string} baseUrl
   * @param {SleipnirRestClientOptions} [options]
   */
  constructor(baseUrl, options = {}) {
    this._rest = new SleipnirRestClient(baseUrl, options);
    const build = (controller, method) => SleipnirCall.init(controller, method);
  this.search = new SearchClient(build);
  this.article = new ArticleClient(build);
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
