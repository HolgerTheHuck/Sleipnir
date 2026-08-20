// Auto-generated Sleipnir data types. Properties are camelCase (wire) and
// optional (discovery carries no nullability; callers narrow).

export interface Holding {
  symbol?: string;
  quantity?: number;
  averagePrice?: number;
}

export interface Order {
  id?: number;
  symbol?: string;
  quantity?: number;
  price?: number;
  time?: string;
}

export interface Profile {
  username?: string;
  role?: string;
}

export interface Quote {
  symbol?: string;
  price?: number;
  change?: number;
  time?: string;
}
