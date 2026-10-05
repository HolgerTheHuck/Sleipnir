// Auto-generated Sleipnir data types. Properties are camelCase (wire) and
// optional: discovery carries no per-property requiredness yet, so callers
// narrow. Nullability IS honored — a nullable property is typed `T | null`.

export interface Message {
  id?: number;
  text?: string;
  authorId?: number;
}

export interface User {
  id?: number;
  name?: string;
}
