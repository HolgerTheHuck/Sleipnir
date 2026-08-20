// Auto-generated Sleipnir controllers (JSDoc-typed JS).
import { SleipnirCall } from "sleipnir-client";
export class MarketClient {
  /** @param {(controller: string, method: string) => SleipnirCall} build */
  constructor(build) {
    this._build = build;
  }
  /**
   * Get a snapshot price quote for a single market symbol. Returns null if the symbol is unknown.
   * @param {string} symbol
   * @returns {Promise<SleipnirResponse<Quote | null | null>>}
   */
  async getQuote(symbol) {
    const call = this._build("Market", "GetQuote").with({ symbol: symbol });
    return call;
  }
}
