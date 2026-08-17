// Auto-generated root Sleipnir client. Compose with the sleipnir-client runtime.
import { SleipnirCall, SleipnirRestClient } from "sleipnir-client";
import type { SleipnirRestClientOptions, SleipnirResponse } from "sleipnir-client";
import { Batch, TypedCall } from "./typed-call.js";
import { SearchClient } from "./controllers.js";
import { ArticleClient } from "./controllers.js";

/** A SleipnirResponse whose `data` is narrowed to T (the wire shape is unchanged). */
export type TypedResponse<T> = SleipnirResponse & { data: T | null };

export class SleipnirClient {
  private readonly _rest: SleipnirRestClient;
  readonly search: SearchClient;
  readonly article: ArticleClient;

  constructor(baseUrl: string, options: SleipnirRestClientOptions = {}) {
    this._rest = new SleipnirRestClient(baseUrl, options);
    const build = (controller: string, method: string) => SleipnirCall.init(controller, method);
    this.search = new SearchClient(build);
    this.article = new ArticleClient(build);
  }

  /** Execute a single typed call; `response.data` is narrowed to T. */
  async call<T, TPaths extends Record<string, unknown>>(call: TypedCall<T, TPaths>): Promise<TypedResponse<T>> {
    return (await this._rest.call(call.toRequest())) as TypedResponse<T>;
  }

  /** Execute a typed batch (Serial — required for @alias resolution). */
  async batch<A extends Record<string, unknown>>(b: Batch<A>): Promise<SleipnirResponse[]> {
    const multi = b.toMulti();
    return this._rest.callBatch(multi.requests, multi.mode);
  }

  /** The underlying REST client (escape hatch for raw calls). */
  get rest(): SleipnirRestClient { return this._rest; }
}
