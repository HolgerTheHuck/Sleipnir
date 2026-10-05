// Auto-generated Sleipnir data types. Properties are camelCase (wire) and
// presence-aware: non-nullable properties are required (always present on
// the wire); nullable properties are presence-optional (`?:`) because event
// frames omit null values (WhenWritingNull) — the value can still be null,
// so the `| null` remains.

export interface Message {
  id: number;
  text: string;
  authorId: number;
}

export interface User {
  id: number;
  name: string;
}
