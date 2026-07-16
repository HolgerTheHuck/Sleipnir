// Auto-generated typed-call + batch machinery. Do not edit by hand.
//
// Each TypedCall carries its own path-type record as a type parameter (TPaths),
// set at the call site by the generated controller method. `exposes` takes
// `path: keyof TPaths` and the alias type is `TPaths[path]` — so path and alias
// validity are compile-checked without a (structurally ambiguous) lookup over T.
import { TrameCall, ExecutionMode } from "trame-client";
import type { TrameRequest, TrameMultiRequest, TrameResponse } from "trame-client";
import type { StockInfo, OrderLine, Article, Order, Customer, Address } from "./types.js";

export interface StockInfoPaths {
  "$": StockInfo;
  "$.articleId": number;
  "$.inStock": number;
}

export interface StockInfoArrayPaths {
  "$": StockInfo[];
  "$[0]": StockInfo;
  "$[0].articleId": number;
  "$[*].articleId": number[];
  "$[0].inStock": number;
  "$[*].inStock": number[];
}

export interface OrderLinePaths {
  "$": OrderLine;
  "$.articleId": number;
  "$.qty": number;
}

export interface OrderLineArrayPaths {
  "$": OrderLine[];
  "$[0]": OrderLine;
  "$[0].articleId": number;
  "$[*].articleId": number[];
  "$[0].qty": number;
  "$[*].qty": number[];
}

export interface ArticlePaths {
  "$": Article;
  "$.id": number;
  "$.name": string;
  "$.price": number;
}

export interface ArticleArrayPaths {
  "$": Article[];
  "$[0]": Article;
  "$[0].id": number;
  "$[*].id": number[];
  "$[0].name": string;
  "$[*].name": string[];
  "$[0].price": number;
  "$[*].price": number[];
}

export interface OrderPaths {
  "$": Order;
  "$.id": number;
  "$.customerId": number;
  "$.shippingAddressId": number;
  "$.status": string;
  "$.placedAt": string;
}

export interface OrderArrayPaths {
  "$": Order[];
  "$[0]": Order;
  "$[0].id": number;
  "$[*].id": number[];
  "$[0].customerId": number;
  "$[*].customerId": number[];
  "$[0].shippingAddressId": number;
  "$[*].shippingAddressId": number[];
  "$[0].status": string;
  "$[*].status": string[];
  "$[0].placedAt": string;
  "$[*].placedAt": string[];
}

export interface CustomerPaths {
  "$": Customer;
  "$.id": number;
  "$.name": string;
}

export interface CustomerArrayPaths {
  "$": Customer[];
  "$[0]": Customer;
  "$[0].id": number;
  "$[*].id": number[];
  "$[0].name": string;
  "$[*].name": string[];
}

export interface AddressPaths {
  "$": Address;
  "$.id": number;
  "$.street": string;
  "$.zip": string;
  "$.city": string;
}

export interface AddressArrayPaths {
  "$": Address[];
  "$[0]": Address;
  "$[0].id": number;
  "$[*].id": number[];
  "$[0].street": string;
  "$[*].street": string[];
  "$[0].zip": string;
  "$[*].zip": string[];
  "$[0].city": string;
  "$[*].city": string[];
}

export interface _NumberPaths {
  "$": number;
}

export interface _NumberArrayPaths {
  "$": number[];
  "$[0]": number;
  "$[*]": number[];
}

export interface _StringPaths {
  "$": string;
}

export interface _StringArrayPaths {
  "$": string[];
  "$[0]": string;
  "$[*]": string[];
}

export interface _BooleanPaths {
  "$": boolean;
}

export interface _BooleanArrayPaths {
  "$": boolean[];
  "$[0]": boolean;
  "$[*]": boolean[];
}

export interface _BigintPaths {
  "$": bigint;
}

export interface _BigintArrayPaths {
  "$": bigint[];
  "$[0]": bigint;
  "$[*]": bigint[];
}

export interface _UnknownPaths {
  "$": unknown;
}

export interface _UnknownArrayPaths {
  "$": unknown[];
  "$[0]": unknown;
  "$[*]": unknown[];
}

export interface _VoidPaths {}

/** A map of valid result-relative $-paths to their extracted type, for a call. */
export type PathTypes = Record<string, unknown>;

/**
 * A typed single call wrapping a trame-client {@link TrameCall}. `TPaths` is
 * the generated path-type record for this call's return type.
 */
export class TypedCall<T, TPaths = PathTypes> {
  constructor(public readonly _call: TrameCall) {}
  /** Set the request id (correlation). */
  named(id: string): this { this._call.named(id); return this; }
  /** Materialize the wire request. */
  toRequest(): TrameRequest { return this._call.toRequest(); }
}

/**
 * A call enrolled in a batch. `exposes` declares an alias the server will
 * resolve from this call's result; the alias type (`TPaths[path]`) is tracked
 * at compile time so `alias("@x")` returns the producer's exposed type.
 */
export class TypedRequest<T, TPaths = PathTypes, A extends Record<string, unknown> = {}> {
  /** @internal */ _call: TrameCall;
  constructor(call: TypedCall<T, TPaths>) { this._call = call._call; }
  /**
   * Declare that this call exposes `path` as `alias`. Compile-time-checked.
   * The wire `dependencyMapping` key is the alias **without** the leading `@`
   * (the server strips `@` from a consumer's `@alias` placeholder before
   * lookup — see TrameInvoker.ReplaceDependencyByAliasCore), so we strip it
   * here. The alias type (`TPaths[path]`) is tracked regardless.
   */
  exposes<P extends string & keyof TPaths, Aname extends string>(path: P, alias: Aname): TypedRequest<T, TPaths, A & Record<Aname, TPaths[P]>> {
    this._call.exposes(path, alias.startsWith("@") ? alias.slice(1) : alias);
    return this as TypedRequest<T, TPaths, A & Record<Aname, TPaths[P]>>;
  }
  /** Set the request id. */
  named(id: string): this { this._call.named(id); return this; }
  /**
   * Resolve a previously-declared alias to its typed value (for a consumer param).
   * At runtime this returns the literal `@alias` placeholder string — the wire
   * value the server substitutes in Serial/topological mode (mirrors
   * `TrameCall.withAlias("@x")`, which sets `data: "@x"`). The compile-time type
   * is the producer's exposed type, so the consumer param typechecks.
   */
  alias<Aname extends string & keyof A>(name: Aname): A[Aname] {
    return name as unknown as A[Aname];
  }
  /** @internal */ toRequest(): TrameRequest { return this._call.toRequest(); }
}

/**
 * Batch builder for dependency-chained calls. Execution mode is Serial (the
 * only mode that resolves `@alias` placeholders). Add calls in topological
 * order: a producer's `exposes` must run before any consumer's `alias`.
 */
export class Batch<A extends Record<string, unknown> = {}> {
  private _requests: TypedRequest<unknown, PathTypes, Record<string, unknown>>[] = [];
  add<T, TPaths = PathTypes>(call: TypedCall<T, TPaths>): TypedRequest<T, TPaths, A> {
    const r = new TypedRequest<T, TPaths, A>(call);
    this._requests.push(r as unknown as TypedRequest<unknown, PathTypes, Record<string, unknown>>);
    return r;
  }
  /** Build the wire multi-request (Serial). */
  toMulti(): TrameMultiRequest {
    return TrameCall.batch(this._requests.map((r) => r.toRequest()), ExecutionMode.Serial);
  }
}
