// Auto-generated typed-call + batch machinery. Do not edit by hand.
//
// Each TypedCall carries its own path-type record as a type parameter (TPaths),
// set at the call site by the generated controller method. `exposes` takes
// `path: keyof TPaths` and the alias type is `TPaths[path]` — so path and alias
// validity are compile-checked without a (structurally ambiguous) lookup over T.
import { SleipnirCall, ExecutionMode } from "sleipnir-client";
import type { SleipnirRequest, SleipnirMultiRequest, SleipnirResponse } from "sleipnir-client";
import type { PriceTick, Holding, Order, Profile, Quote } from "./types.js";

export interface PriceTickPaths {
  "$": PriceTick;
  "$.symbol": string;
  "$.price": number;
  "$.change": number;
  "$.time": string;
}

export interface PriceTickArrayPaths {
  "$": PriceTick[];
  "$[0]": PriceTick;
  "$[0].symbol": string;
  "$[0].price": number;
  "$[0].change": number;
  "$[0].time": string;
  "$[*].symbol": string[];
  "$[*].price": number[];
  "$[*].change": number[];
  "$[*].time": string[];
}

export interface HoldingPaths {
  "$": Holding;
  "$.symbol": string;
  "$.quantity": number;
  "$.averagePrice": number;
}

export interface HoldingArrayPaths {
  "$": Holding[];
  "$[0]": Holding;
  "$[0].symbol": string;
  "$[0].quantity": number;
  "$[0].averagePrice": number;
  "$[*].symbol": string[];
  "$[*].quantity": number[];
  "$[*].averagePrice": number[];
}

export interface OrderPaths {
  "$": Order;
  "$.id": number;
  "$.symbol": string;
  "$.quantity": number;
  "$.price": number;
  "$.time": string;
}

export interface OrderArrayPaths {
  "$": Order[];
  "$[0]": Order;
  "$[0].id": number;
  "$[0].symbol": string;
  "$[0].quantity": number;
  "$[0].price": number;
  "$[0].time": string;
  "$[*].id": number[];
  "$[*].symbol": string[];
  "$[*].quantity": number[];
  "$[*].price": number[];
  "$[*].time": string[];
}

export interface ProfilePaths {
  "$": Profile;
  "$.username": string;
  "$.role": string;
}

export interface ProfileArrayPaths {
  "$": Profile[];
  "$[0]": Profile;
  "$[0].username": string;
  "$[0].role": string;
  "$[*].username": string[];
  "$[*].role": string[];
}

export interface QuotePaths {
  "$": Quote;
  "$.symbol": string;
  "$.price": number;
  "$.change": number;
  "$.time": string;
}

export interface QuoteArrayPaths {
  "$": Quote[];
  "$[0]": Quote;
  "$[0].symbol": string;
  "$[0].price": number;
  "$[0].change": number;
  "$[0].time": string;
  "$[*].symbol": string[];
  "$[*].price": number[];
  "$[*].change": number[];
  "$[*].time": string[];
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
 * A typed single call wrapping a sleipnir-client {@link SleipnirCall}. `TPaths` is
 * the generated path-type record for this call's return type.
 */
export class TypedCall<T, TPaths = PathTypes> {
  constructor(public readonly _call: SleipnirCall) {}
  /** Set the request id (correlation). */
  named(id: string): this { this._call.named(id); return this; }
  /** Materialize the wire request. */
  toRequest(): SleipnirRequest { return this._call.toRequest(); }
}

/**
 * A call enrolled in a batch. `exposes` declares an alias the server will
 * resolve from this call's result; the alias type (`TPaths[path]`) is tracked
 * at compile time so `alias("@x")` returns the producer's exposed type.
 */
export class TypedRequest<T, TPaths = PathTypes, A extends Record<string, unknown> = {}> {
  /** @internal */ _call: SleipnirCall;
  constructor(call: TypedCall<T, TPaths>) { this._call = call._call; }
  /**
   * Declare that this call exposes `path` as `alias`. Compile-time-checked.
   * The wire `dependencyMapping` key is the alias **without** the leading `@`
   * (the server strips `@` from a consumer's `@alias` placeholder before
   * lookup — see SleipnirInvoker.ReplaceDependencyByAliasCore), so we strip it
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
   * `SleipnirCall.withAlias("@x")`, which sets `data: "@x"`). The compile-time type
   * is the producer's exposed type, so the consumer param typechecks.
   *
   * `@`-normalization is symmetric with `exposes`: `exposes` STRIPS a leading `@`
   * (the wire `dependencyMapping` key is the bare name — the server strips the
   * consumer's `@alias` placeholder before lookup), while `alias` ENSURES a leading
   * `@` (the consumer sends `data: "@alias"`). So both call styles work:
   * `alias("ids")` → `"@ids"` and `alias("@ids")` → `"@ids"`. Returning the bare
   * name here (the 1.2.1 bug) sent `"ids"` on the wire, which the server's
   * `ReplaceDependencyByAlias` never matched — the typed chain compiled but the
   * dependent call received an unresolved literal instead of the alias value.
   */
  alias<Aname extends string & keyof A>(name: Aname): A[Aname] {
    return (name.startsWith("@") ? name : "@" + name) as unknown as A[Aname];
  }
  /** @internal */ toRequest(): SleipnirRequest { return this._call.toRequest(); }
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
  toMulti(): SleipnirMultiRequest {
    return SleipnirCall.batch(this._requests.map((r) => r.toRequest()), ExecutionMode.Serial);
  }
}
