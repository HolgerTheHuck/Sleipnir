// Auto-generated root Sleipnir client (REST + WebSocket). Compose with the sleipnir-client runtime.
import { SleipnirCall, SleipnirRestClient, SleipnirWebSocketClient } from "sleipnir-client";
import type { SleipnirRestClientOptions, SleipnirWebSocketClientOptions, SleipnirResponse } from "sleipnir-client";
import { Batch, TypedCall } from "./typed-call.js";
import { StockClient } from "./controllers.js";
import { OrderLineClient } from "./controllers.js";
import { ArticleClient } from "./controllers.js";
import { OrderClient } from "./controllers.js";
import { CustomerClient } from "./controllers.js";
import { AddressClient } from "./controllers.js";

/** A SleipnirResponse whose `data` is narrowed to T (the wire shape is unchanged). */
export type TypedResponse<T> = SleipnirResponse & { data: T | null };

/** Per-transport options for the combined client. */
export interface SleipnirClientOptions {
  rest?: SleipnirRestClientOptions;
  ws?: SleipnirWebSocketClientOptions;
}

export class SleipnirClient {
  private readonly _rest: SleipnirRestClient;
  private readonly _ws: SleipnirWebSocketClient;
  readonly stock: StockClient;
  readonly orderLine: OrderLineClient;
  readonly article: ArticleClient;
  readonly order: OrderClient;
  readonly customer: CustomerClient;
  readonly address: AddressClient;

  constructor(baseUrl: string, options: SleipnirClientOptions = {}) {
    this._rest = new SleipnirRestClient(baseUrl, options.rest ?? {});
    this._ws = new SleipnirWebSocketClient(baseUrl, options.ws ?? {});
    const build = (controller: string, method: string) => SleipnirCall.init(controller, method);
    this.stock = new StockClient(build);
    this.orderLine = new OrderLineClient(build);
    this.article = new ArticleClient(build);
    this.order = new OrderClient(build);
    this.customer = new CustomerClient(build);
    this.address = new AddressClient(build);
  }

  /** Execute a single typed call over REST (default transport); `response.data` is narrowed to T. */
  async call<T, TPaths extends Record<string, unknown>>(call: TypedCall<T, TPaths>): Promise<TypedResponse<T>> {
    return (await this._rest.call(call.toRequest())) as TypedResponse<T>;
  }

  /** Execute a typed batch over REST (Serial — required for @alias resolution). */
  async batch<A extends Record<string, unknown>>(b: Batch<A>): Promise<SleipnirResponse[]> {
    const multi = b.toMulti();
    return this._rest.callBatch(multi.requests, multi.mode);
  }

  /** Execute a single typed call over WebSocket; `response.data` is narrowed to T. */
  async callWs<T, TPaths extends Record<string, unknown>>(call: TypedCall<T, TPaths>): Promise<TypedResponse<T>> {
    return (await this._ws.call(call.toRequest())) as TypedResponse<T>;
  }

  /** Execute a typed batch over WebSocket (Serial — required for @alias resolution). */
  async batchWs<A extends Record<string, unknown>>(b: Batch<A>): Promise<SleipnirResponse[]> {
    const multi = b.toMulti();
    return this._ws.callBatch(multi.requests, multi.mode);
  }

  /** The underlying REST client (escape hatch for raw calls). */
  get rest(): SleipnirRestClient { return this._rest; }

  /** The underlying WebSocket client (escape hatch for raw calls / lifecycle). */
  get ws(): SleipnirWebSocketClient { return this._ws; }
}
