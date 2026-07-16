// Auto-generated Trame controllers. Method names are camelCase; parameter
// names bind case-sensitively on the wire (keys passed verbatim to TrameCall).
import { TrameCall } from "trame-client";
import { TypedCall } from "./typed-call.js";
import type { Address, Article, Customer, Order, OrderLine, StockInfo } from "./types.js";
import type { AddressPaths, ArticleArrayPaths, CustomerPaths, OrderLineArrayPaths, OrderPaths, StockInfoArrayPaths } from "./typed-call.js";

export class StockClient {
  /** @internal */ _build: (controller: string, method: string) => TrameCall;
  constructor(build: (controller: string, method: string) => TrameCall) {
    this._build = build;
  }
  getByArticles(articleIds: number[]): TypedCall<StockInfo[], StockInfoArrayPaths> {
    return new TypedCall<StockInfo[], StockInfoArrayPaths>(this._build("Stock", "GetByArticles").with({ articleIds: articleIds }));
  }
}

export class OrderLineClient {
  /** @internal */ _build: (controller: string, method: string) => TrameCall;
  constructor(build: (controller: string, method: string) => TrameCall) {
    this._build = build;
  }
  getByOrder(orderId: number): TypedCall<OrderLine[], OrderLineArrayPaths> {
    return new TypedCall<OrderLine[], OrderLineArrayPaths>(this._build("OrderLine", "GetByOrder").with({ orderId: orderId }));
  }
}

export class ArticleClient {
  /** @internal */ _build: (controller: string, method: string) => TrameCall;
  constructor(build: (controller: string, method: string) => TrameCall) {
    this._build = build;
  }
  getByIds(articleIds: number[]): TypedCall<Article[], ArticleArrayPaths> {
    return new TypedCall<Article[], ArticleArrayPaths>(this._build("Article", "GetByIds").with({ articleIds: articleIds }));
  }
}

export class OrderClient {
  /** @internal */ _build: (controller: string, method: string) => TrameCall;
  constructor(build: (controller: string, method: string) => TrameCall) {
    this._build = build;
  }
  getById(id: number): TypedCall<Order, OrderPaths> {
    return new TypedCall<Order, OrderPaths>(this._build("Order", "GetById").with({ id: id }));
  }
}

export class CustomerClient {
  /** @internal */ _build: (controller: string, method: string) => TrameCall;
  constructor(build: (controller: string, method: string) => TrameCall) {
    this._build = build;
  }
  getById(customerId: number): TypedCall<Customer, CustomerPaths> {
    return new TypedCall<Customer, CustomerPaths>(this._build("Customer", "GetById").with({ customerId: customerId }));
  }
}

export class AddressClient {
  /** @internal */ _build: (controller: string, method: string) => TrameCall;
  constructor(build: (controller: string, method: string) => TrameCall) {
    this._build = build;
  }
  getById(addressId: number): TypedCall<Address, AddressPaths> {
    return new TypedCall<Address, AddressPaths>(this._build("Address", "GetById").with({ addressId: addressId }));
  }
}
