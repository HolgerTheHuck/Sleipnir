// Auto-generated Sleipnir controllers. Method names are camelCase; parameter
// names bind case-sensitively on the wire (keys passed verbatim to SleipnirCall).
import { SleipnirCall } from "sleipnir-client";
import { TypedCall } from "./typed-call.js";
import type { Holding, Order, Profile, Quote } from "./types.js";
import type { HoldingArrayPaths, OrderPaths, ProfilePaths, QuoteArrayPaths, QuotePaths, _BooleanPaths, _StringArrayPaths, _VoidPaths } from "./typed-call.js";

export class AccountClient {
  /** @internal */ _build: (controller: string, method: string) => SleipnirCall;
  constructor(build: (controller: string, method: string) => SleipnirCall) {
    this._build = build;
  }
  /** Exchange username + password for a JWT bearer token. Try customer/customer or admin/admin. The token is sent back as Authorization: Bearer on subsequent calls. */
  // TODO: return type "SleipnirResponse" is an opaque framework/BCL type not modelled in discovery; emitted as unknown.
  login(username: string, password: string): TypedCall<unknown, _VoidPaths> {
    return new TypedCall<unknown, _VoidPaths>(this._build("Account", "Login").with({ username: username, password: password }));
  }

  /** Return the caller's profile from the bearer token. Requires authentication (any role). */
  me(): TypedCall<Profile, ProfilePaths> {
    return new TypedCall<Profile, ProfilePaths>(this._build("Account", "Me"));
  }
}

export class MarketClient {
  /** @internal */ _build: (controller: string, method: string) => SleipnirCall;
  constructor(build: (controller: string, method: string) => SleipnirCall) {
    this._build = build;
  }
  /** Get a snapshot price quote for a single market symbol. Returns null if the symbol is unknown. */
  getQuote(symbol: string): TypedCall<Quote | null, QuotePaths> {
    return new TypedCall<Quote | null, QuotePaths>(this._build("Market", "GetQuote").with({ symbol: symbol }));
  }

  /** Bulk-fetch quotes for many symbols in one call. Unknown symbols are skipped. For composing arbitrary methods in one roundtrip, prefer a SleipnirMultiRequest batch (chapter 5). */
  getQuotes(symbols: string[]): TypedCall<Quote[], QuoteArrayPaths> {
    return new TypedCall<Quote[], QuoteArrayPaths>(this._build("Market", "GetQuotes").with({ symbols: symbols }));
  }

  /** Find symbols whose ticker or full name contains the query (case-insensitive). Returns the matching tickers — the chain provider for GetQuotes(@symbols): Search exposes $[*] as 'symbols', GetQuotes consumes @symbols, one roundtrip. */
  search(query: string): TypedCall<string[], _StringArrayPaths> {
    return new TypedCall<string[], _StringArrayPaths>(this._build("Market", "Search").with({ query: query }));
  }
}

export class PortfolioClient {
  /** @internal */ _build: (controller: string, method: string) => SleipnirCall;
  constructor(build: (controller: string, method: string) => SleipnirCall) {
    this._build = build;
  }
  /** Return the caller's portfolio holdings. Requires authentication (any role). */
  getHoldings(): TypedCall<Holding[], HoldingArrayPaths> {
    return new TypedCall<Holding[], HoldingArrayPaths>(this._build("Portfolio", "GetHoldings"));
  }

  /** Place a market order for a symbol + quantity. Returns the filled Order. Chain provider for GetOrder(@orderId): expose $.Id as 'orderId'. */
  placeOrder(symbol: string, quantity: number): TypedCall<Order, OrderPaths> {
    return new TypedCall<Order, OrderPaths>(this._build("Portfolio", "PlaceOrder").with({ symbol: symbol, quantity: quantity }));
  }

  /** Fetch a previously placed order by id. Chain consumer: PlaceOrder exposes $.Id as 'orderId', GetOrder(@orderId) resolves it. */
  // TODO: return type "SleipnirResponse" is an opaque framework/BCL type not modelled in discovery; emitted as unknown.
  getOrder(id: number): TypedCall<unknown, _VoidPaths> {
    return new TypedCall<unknown, _VoidPaths>(this._build("Portfolio", "GetOrder").with({ id: id }));
  }

  /** Start the live price feed (chapter 8). Admin role required — a Customer token gets 403. */
  startFeed(): TypedCall<boolean, _BooleanPaths> {
    return new TypedCall<boolean, _BooleanPaths>(this._build("Portfolio", "StartFeed"));
  }

  /** Stop the live price feed (chapter 8). Admin role required — a Customer token gets 403. */
  stopFeed(): TypedCall<boolean, _BooleanPaths> {
    return new TypedCall<boolean, _BooleanPaths>(this._build("Portfolio", "StopFeed"));
  }
}
