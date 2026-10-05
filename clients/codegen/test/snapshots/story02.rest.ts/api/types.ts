// Auto-generated Sleipnir data types. Properties are camelCase (wire).
// Required (always present on the wire) unless nullable — nullable properties are
// presence-optional (`?:`) because event frames omit null values (WhenWritingNull);
// the value can still be null, so the `| null` remains.

export interface SearchResult {
  total: number;
  hits: SearchHit[];
}

export interface SearchHit {
  articleId: number;
  title: string;
  score: number;
  author: Author;
}

export interface Author {
  id: number;
  name: string;
}

export interface Article {
  id: number;
  name: string;
  price: number;
}
