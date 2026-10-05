// Auto-generated Sleipnir controllers (JSDoc-typed JS).
import { SleipnirCall } from "sleipnir-client";
export class AccountClient {
  /** @param {(controller: string, method: string) => SleipnirCall} build */
  constructor(build) {
    this._build = build;
  }
  /**
   * Exchange username + password for a JWT bearer token. Try customer/customer or admin/admin. The token is sent back as Authorization: Bearer on subsequent calls.
   * @param {string} username
   * @param {string} password
   * @returns {Promise<SleipnirResponse<unknown | null>>}
   */
  async login(username, password) {
    const call = this._build("Account", "Login").with({ username: username, password: password });
    return call;
  }

  /**
   * Return the caller's profile from the bearer token. Requires authentication (any role).

   * @returns {Promise<SleipnirResponse<Profile | null>>}
   */
  async me() {
    const call = this._build("Account", "Me");
    return call;
  }
}

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

  /**
   * Bulk-fetch quotes for many symbols in one call. Unknown symbols are skipped. For composing arbitrary methods in one roundtrip, prefer a SleipnirMultiRequest batch (chapter 5).
   * @param {string[]} symbols
   * @returns {Promise<SleipnirResponse<Quote[] | null>>}
   */
  async getQuotes(symbols) {
    const call = this._build("Market", "GetQuotes").with({ symbols: symbols });
    return call;
  }

  /**
   * Find symbols whose ticker or full name contains the query (case-insensitive). Returns the matching tickers — the chain provider for GetQuotes(@symbols): Search exposes $[*] as 'symbols', GetQuotes consumes @symbols, one roundtrip.
   * @param {string} query
   * @returns {Promise<SleipnirResponse<string[] | null>>}
   */
  async search(query) {
    const call = this._build("Market", "Search").with({ query: query });
    return call;
  }
}

export class PortfolioClient {
  /** @param {(controller: string, method: string) => SleipnirCall} build */
  constructor(build) {
    this._build = build;
  }
  /**
   * Return the caller's portfolio holdings. Requires authentication (any role).

   * @returns {Promise<SleipnirResponse<Holding[] | null>>}
   */
  async getHoldings() {
    const call = this._build("Portfolio", "GetHoldings");
    return call;
  }

  /**
   * Fetch a previously placed order by id. Chain consumer: PlaceOrder exposes $.Id as 'orderId', GetOrder(@orderId) resolves it.
   * @param {number} id
   * @returns {Promise<SleipnirResponse<unknown | null>>}
   */
  async getOrder(id) {
    const call = this._build("Portfolio", "GetOrder").with({ id: id });
    return call;
  }

  /**
   * Place a market order for a symbol + quantity. Returns the filled Order. Chain provider for GetOrder(@orderId): expose $.Id as 'orderId'.
   * @param {string} symbol
   * @param {number} quantity
   * @returns {Promise<SleipnirResponse<Order | null>>}
   */
  async placeOrder(symbol, quantity) {
    const call = this._build("Portfolio", "PlaceOrder").with({ symbol: symbol, quantity: quantity });
    return call;
  }

  /**
   * Start the live price feed (chapter 9). Admin role required — a Customer token gets 403.

   * @returns {Promise<SleipnirResponse<boolean | null>>}
   */
  async startFeed() {
    const call = this._build("Portfolio", "StartFeed");
    return call;
  }

  /**
   * Stop the live price feed (chapter 9). Admin role required — a Customer token gets 403.

   * @returns {Promise<SleipnirResponse<boolean | null>>}
   */
  async stopFeed() {
    const call = this._build("Portfolio", "StopFeed");
    return call;
  }
}

export class PriceFeedClient {
  /**
   * @param {(controller: string, method: string) => SleipnirCall} build
   * @param {(req: SleipnirRequest, handlers: SubscribeHandlers<unknown>) => Promise<SleipnirSubscription>} subscribe
   */
  constructor(build, subscribe) {
    this._build = build;
    this._subscribe = subscribe;
  }
  /**
   * Live price feed. Subscribe to a symbol (e.g. BTC) to receive a PriceTick ~once per second while the feed is running. Resumable: reconnect within 60s and the server replays the missed ticks by eventId. The feed is anonymous (subscribe as anyone); the admin starts/stops it via Portfolio.StartFeed/StopFeed.
   * @param {string} symbol
   * @param {SubscribeHandlers<PriceTick>} handlers
   * @returns {Promise<SleipnirSubscription>}
   */
  async ticks(symbol, handlers) {
    return this._subscribe(this._build("PriceFeed", "Ticks").with({ symbol: symbol }).toRequest(), handlers);
  }
}
