// Auto-generated Sleipnir data types. Properties are camelCase (wire) and
// optional: discovery carries no per-property requiredness yet, so callers
// narrow. Nullability IS honored — a nullable property is typed `T | null`.

export interface SearchResult {
  total?: number;
  hits?: SearchHit[];
}

export interface SearchHit {
  articleId?: number;
  title?: string;
  score?: number;
  author?: Author;
}

export interface Author {
  id?: number;
  name?: string;
}

export interface Article {
  id?: number;
  name?: string;
  price?: number;
}
