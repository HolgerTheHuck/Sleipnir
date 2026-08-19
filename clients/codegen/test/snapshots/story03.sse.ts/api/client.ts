// Auto-generated root Sleipnir client (REST + SSE transport). Compose with the sleipnir-client runtime.
import { SleipnirCall, SleipnirRestClient, SleipnirSseClient } from "sleipnir-client";
import type { SleipnirRestClientOptions, SleipnirSseClientOptions, SleipnirResponse, SleipnirRequest, SubscribeHandlers, SleipnirSubscription } from "sleipnir-client";
import { Batch, TypedCall } from "./typed-call.js";
import { ChatClient } from "./controllers.js";
import { TickerClient } from "./controllers.js";
import { UserClient } from "./controllers.js";

/** A SleipnirResponse whose `data` is narrowed to T (the wire shape is unchanged). */
export type TypedResponse<T> = SleipnirResponse & { data: T | null };

/** Per-transport options for the combined REST + SSE client. */
export interface SleipnirClientOptions {
  rest?: SleipnirRestClientOptions;
  sse?: SleipnirSseClientOptions;
}

export class SleipnirClient {
  private readonly _rest: SleipnirRestClient;
  private readonly _sse: SleipnirSseClient;
  private readonly _subscribe = <T>(req: SleipnirRequest, handlers: SubscribeHandlers<T>): Promise<SleipnirSubscription> => {
    const params: Record<string, unknown> = {};
    for (const p of (req.params ?? [])) params[p.parameterName] = p.data;
    return this._sse.subscribe<T>(req.controller, req.method, handlers, params);
  };
  readonly chat: ChatClient;
  readonly ticker: TickerClient;
  readonly user: UserClient;

  constructor(baseUrl: string, options: SleipnirClientOptions = {}) {
    this._rest = new SleipnirRestClient(baseUrl, options.rest ?? {});
    this._sse = new SleipnirSseClient(baseUrl, options.sse ?? {});
    const build = (controller: string, method: string) => SleipnirCall.init(controller, method);
    this.chat = new ChatClient(build, this._subscribe);
    this.ticker = new TickerClient(build, this._subscribe);
    this.user = new UserClient(build);
  }

  /** Execute a single typed call over REST; `response.data` is narrowed to T. */
  async call<T, TPaths extends Record<string, unknown>>(call: TypedCall<T, TPaths>): Promise<TypedResponse<T>> {
    return (await this._rest.call(call.toRequest())) as TypedResponse<T>;
  }

  /** Execute a typed batch over REST (Serial — required for @alias resolution). */
  async batch<A extends Record<string, unknown>>(b: Batch<A>): Promise<SleipnirResponse[]> {
    const multi = b.toMulti();
    return this._rest.callBatch(multi.requests, multi.mode);
  }

  /** The underlying REST client (escape hatch for raw calls). */
  get rest(): SleipnirRestClient { return this._rest; }

  /** The underlying SSE client (escape hatch for raw subscriptions / lifecycle). */
  get sse(): SleipnirSseClient { return this._sse; }
}
