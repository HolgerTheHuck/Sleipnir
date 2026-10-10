// Auto-generated Sleipnir data types. Properties are camelCase (wire) and
// presence-aware: non-nullable properties are required (always present on
// the wire); nullable properties are presence-optional (`?:`) because event
// frames omit null values (WhenWritingNull) — the value can still be null,
// so the `| null` remains.

export interface StockInfo {
  articleId: number;
  inStock: number;
}

export interface OrderLine {
  articleId: number;
  qty: number;
}

export interface Article {
  id: number;
  name: string;
  price: number;
}

export interface Order {
  id: number;
  customerId: number;
  shippingAddressId: number;
  status: string;
  placedAt: string;
  note?: string | null;
}

export interface Customer {
  id: number;
  name: string;
  score?: number | null;
}

export interface Address {
  id: number;
  street: string;
  zip: string;
  city: string;
}
