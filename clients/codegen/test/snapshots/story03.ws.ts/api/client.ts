// Auto-generated root Sleipnir client (WebSocket transport). Compose with the sleipnir-client runtime.
import { SleipnirCall, SleipnirWebSocketClient } from "sleipnir-client";
import type { SleipnirWebSocketClientOptions, SleipnirResponse, SleipnirRequest, SubscribeHandlers, SleipnirSubscription } from "sleipnir-client";
import { Batch, TypedCall } from "./typed-call.js";
import { ChatClient } from "./controllers.js";
import { TickerClient } from "./controllers.js";
import { UserClient } from "./controllers.js";

/** A SleipnirResponse whose `data` is narrowed to T (the wire shape is unchanged). */
export type TypedResponse<T> = SleipnirResponse & { data: T | null };

export class SleipnirClient {
  private readonly _ws: SleipnirWebSocketClient;
  private readonly _subscribe = <T>(req: SleipnirRequest, handlers: SubscribeHandlers<T>): Promise<SleipnirSubscription> => this._ws.subscribe<T>(req, handlers);
  readonly chat: ChatClient;
  readonly ticker: TickerClient;
  readonly user: UserClient;

  constructor(baseUrl: string, options: SleipnirWebSocketClientOptions = {}) {
    this._ws = new SleipnirWebSocketClient(baseUrl, options);
    const build = (controller: string, method: string) => SleipnirCall.init(controller, method);
    this.chat = new ChatClient(build, this._subscribe);
    this.ticker = new TickerClient(build, this._subscribe);
    this.user = new UserClient(build);
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
