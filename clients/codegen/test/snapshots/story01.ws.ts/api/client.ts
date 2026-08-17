// Auto-generated root Sleipnir client (WebSocket transport). Compose with the sleipnir-client runtime.
import { SleipnirCall, SleipnirWebSocketClient } from "sleipnir-client";
import type { SleipnirWebSocketClientOptions, SleipnirResponse } from "sleipnir-client";
import { Batch, TypedCall } from "./typed-call.js";
import { StockClient } from "./controllers.js";
import { OrderLineClient } from "./controllers.js";
import { ArticleClient } from "./controllers.js";
import { OrderClient } from "./controllers.js";
import { CustomerClient } from "./controllers.js";
import { AddressClient } from "./controllers.js";

/** A SleipnirResponse whose `data` is narrowed to T (the wire shape is unchanged). */
export type TypedResponse<T> = SleipnirResponse & { data: T | null };

export class SleipnirClient {
  private readonly _ws: SleipnirWebSocketClient;
  readonly stock: StockClient;
  readonly orderLine: OrderLineClient;
  readonly article: ArticleClient;
  readonly order: OrderClient;
  readonly customer: CustomerClient;
  readonly address: AddressClient;

  constructor(baseUrl: string, options: SleipnirWebSocketClientOptions = {}) {
    this._ws = new SleipnirWebSocketClient(baseUrl, options);
    const build = (controller: string, method: string) => SleipnirCall.init(controller, method);
    this.stock = new StockClient(build);
    this.orderLine = new OrderLineClient(build);
    this.article = new ArticleClient(build);
    this.order = new OrderClient(build);
    this.customer = new CustomerClient(build);
    this.address = new AddressClient(build);
  }

  /** Execute a single typed call over WebSocket; `response.data` is narrowed to T. */
  async call<T, TPaths extends Record<string, unknown>>(call: TypedCall<T, TPaths>): Promise<TypedResponse<T>> {
    return (await this._ws.call(call.toRequest())) as TypedResponse<T>;
  }

  /** Execute a typed batch over WebSocket (Serial — required for @alias resolution). */
  async batch<A extends Record<string, unknown>>(b: Batch<A>): Promise<SleipnirResponse[]> {
    const multi = b.toMulti();
    return this._ws.callBatch(multi.requests, multi.mode);
  }

  /** The underlying WebSocket client (escape hatch for raw calls / lifecycle). */
  get ws(): SleipnirWebSocketClient { return this._ws; }
}
