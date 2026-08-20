// Auto-generated Sleipnir data types. Properties are camelCase (wire) and
// optional (discovery carries no nullability; callers narrow).

export interface Message {
  id?: number;
  text?: string;
  authorId?: number;
}

export interface User {
  id?: number;
  name?: string;
}
