// Auto-generated Sleipnir controllers. Method names are camelCase; parameter
// names bind case-sensitively on the wire (keys passed verbatim to SleipnirCall).
import { SleipnirCall } from "sleipnir-client";
import { TypedCall } from "./typed-call.js";
import type { Quote } from "./types.js";
import type { QuotePaths } from "./typed-call.js";

export class MarketClient {
  /** @internal */ _build: (controller: string, method: string) => SleipnirCall;
  constructor(build: (controller: string, method: string) => SleipnirCall) {
    this._build = build;
  }
  /** Get a snapshot price quote for a single market symbol. Returns null if the symbol is unknown. */
  getQuote(symbol: string): TypedCall<Quote | null, QuotePaths> {
    return new TypedCall<Quote | null, QuotePaths>(this._build("Market", "GetQuote").with({ symbol: symbol }));
  }
}
