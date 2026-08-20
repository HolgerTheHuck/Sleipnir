// Auto-generated Sleipnir data types. Properties are camelCase (wire) and
// optional (discovery carries no nullability; callers narrow).

export interface StockInfo {
  articleId?: number;
  inStock?: number;
}

export interface OrderLine {
  articleId?: number;
  qty?: number;
}

export interface Article {
  id?: number;
  name?: string;
  price?: number;
}

export interface Order {
  id?: number;
  customerId?: number;
  shippingAddressId?: number;
  status?: string;
  placedAt?: string;
}

export interface Customer {
  id?: number;
  name?: string;
}

export interface Address {
  id?: number;
  street?: string;
  zip?: string;
  city?: string;
}
