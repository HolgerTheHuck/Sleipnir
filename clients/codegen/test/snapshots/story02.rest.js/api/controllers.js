// Auto-generated Sleipnir controllers (JSDoc-typed JS).
import { SleipnirCall } from "sleipnir-client";
export class SearchClient {
  /** @param {(controller: string, method: string) => SleipnirCall} build */
  constructor(build) {
    this._build = build;
  }
  /**
   * @param {string} query
   * @returns {Promise<SleipnirResponse<SearchResult | null>>}
   */
  async semanticSearch(query) {
    const call = this._build("Search", "SemanticSearch").with({ query: query });
    return call;
  }
}

export class ArticleClient {
  /** @param {(controller: string, method: string) => SleipnirCall} build */
  constructor(build) {
    this._build = build;
  }
  /**
   * @param {number[]} articleIds
   * @returns {Promise<SleipnirResponse<Article[] | null>>}
   */
  async getByIds(articleIds) {
    const call = this._build("Article", "GetByIds").with({ articleIds: articleIds });
    return call;
  }
}
