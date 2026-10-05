// Auto-generated Sleipnir data types. Properties are camelCase (wire).
// Required (always present on the wire) unless nullable — nullable properties are
// presence-optional (`?:`) because event frames omit null values (WhenWritingNull);
// the value can still be null, so the `| null` remains.

export interface Holding {
  averagePrice: number;
  quantity: number;
  symbol: string;
}

export interface Order {
  id: number;
  price: number;
  quantity: number;
  symbol: string;
  time: string;
}

export interface PriceTick {
  change: number;
  price: number;
  symbol: string;
  time: string;
}

export interface Profile {
  role: string;
  username: string;
}

export interface Quote {
  change: number;
  price: number;
  symbol: string;
  time: string;
}
