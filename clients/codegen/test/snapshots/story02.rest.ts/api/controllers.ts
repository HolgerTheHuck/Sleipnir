// Auto-generated Sleipnir controllers. Method names are camelCase; parameter
// names bind case-sensitively on the wire (keys passed verbatim to SleipnirCall).
import { SleipnirCall } from "sleipnir-client";
import { TypedCall } from "./typed-call.js";
import type { Article, Author, SearchHit, SearchResult } from "./types.js";
import type { ArticleArrayPaths, SearchResultPaths } from "./typed-call.js";

export class SearchClient {
  /** @internal */ _build: (controller: string, method: string) => SleipnirCall;
  constructor(build: (controller: string, method: string) => SleipnirCall) {
    this._build = build;
  }
  semanticSearch(query: string): TypedCall<SearchResult, SearchResultPaths> {
    return new TypedCall<SearchResult, SearchResultPaths>(this._build("Search", "SemanticSearch").with({ query: query }));
  }
}

export class ArticleClient {
  /** @internal */ _build: (controller: string, method: string) => SleipnirCall;
  constructor(build: (controller: string, method: string) => SleipnirCall) {
    this._build = build;
  }
  getByIds(articleIds: number[]): TypedCall<Article[], ArticleArrayPaths> {
    return new TypedCall<Article[], ArticleArrayPaths>(this._build("Article", "GetByIds").with({ articleIds: articleIds }));
  }
}
