import { ExecutionMode } from "./types.js";
import type { SleipnirMultiRequest, SleipnirParameter, SleipnirRequest } from "./types.js";
import { toBase64 } from "./request.js";

/**
 * Fluent builder for a SleipnirRequest — mirror of the C# SleipnirCall.
 * Transport-agnostic: yields a {@link SleipnirRequest} that any client
 * (REST/WebSocket) can send.
 *
 * ```ts
 * SleipnirCall.init("Customer", "Add")
 *   .with({ name: "Alice" })        // named
 *   .with([42, "x"])                // or positional
 *   .withBinary(blob)               // -> binaryData (base64)
 *   .named("step1")                 // -> id
 *   .exposes("$", "newId")         // -> dependencyMapping (result-relative path)
 *   .withAlias("@newId")            // placeholder — the server resolves @newId
 *   .toRequest();
 * ```
 */
export class SleipnirCall {
  private readonly _controller: string;
  private readonly _method: string;
  private _id?: string;
  private _params: SleipnirParameter[] = [];
  private _num = 0;
  private _exposed = new Map<string, string>();
  private _binary?: Uint8Array;

  private constructor(controller: string, method: string) {
    this._controller = controller;
    this._method = method;
  }

  /** Starts a builder for `controller.method`. */
  static init(controller: string, method: string): SleipnirCall {
    return new SleipnirCall(controller, method);
  }

  /** Sets the request id (correlation). Default: `${controller}.${method}`. */
  named(id: string): this {
    this._id = id;
    return this;
  }

  /**
   * Adds named (Object) or positional (Array) parameters.
   * - Object → `{parameterName: key, data: value}` (safe binding).
   * - Array  → `{parameterName: "param{i}", num: i, data: value}` (positional via `num`).
   */
  with(params: Record<string, unknown> | unknown[]): this {
    if (Array.isArray(params)) {
      for (const value of params) this.pushPositional(value);
    } else {
      for (const [key, value] of Object.entries(params)) this.pushNamed(key, value);
    }
    return this;
  }

  /** Adds a named parameter (the name must match server-side). */
  param(name: string, value: unknown): this {
    this.pushNamed(name, value);
    return this;
  }

  /**
   * Declares that this response exposes the value under `jsonPath` as `alias`
   * (for dependency chaining). Resolved server-side; follow-up requests use
   * `@alias` in their `data`.
   *
   * `jsonPath` is **result-relative** — the root `$` is the serialized result
   * (e.g. an `int` or a `Customer` object), not the response envelope. There is
   * therefore no `data` node level: use `$` for the whole result, `$.Id`/`$.Name`
   * for properties, `$[0].Id` for a list element. A path like `$.data` never
   * matches (unless the result itself has a `data` property).
   */
  exposes(jsonPath: string, alias: string): this {
    this._exposed.set(alias, jsonPath);
    return this;
  }

  /**
   * Adds a parameter carrying a dependency placeholder, e.g. `withAlias("@newId")`.
   * The server replaces `@newId` from a previously exposed dependency. If the alias
   * is unresolvable, the call fails — there is no implicit fallback in v1.
   */
  withAlias(dependencyPlaceholder: string): this {
    const alias = dependencyPlaceholder.startsWith("@")
      ? dependencyPlaceholder.slice(1)
      : dependencyPlaceholder;
    this._params.push({
      parameterName: alias,
      num: this._num,
      data: dependencyPlaceholder,
    });
    this._num++;
    return this;
  }

  /** Sets the binary payload (for byte[] parameters of the target method). */
  withBinary(bytes: Uint8Array): this {
    this._binary = bytes;
    return this;
  }

  /** Turns the builder into a ready-to-send SleipnirRequest. */
  toRequest(): SleipnirRequest {
    const id = this._id ?? `${this._controller}.${this._method}`;
    return {
      controller: this._controller,
      method: this._method,
      params: this._params,
      id,
      dependencyMapping: this._exposed.size > 0 ? Object.fromEntries(this._exposed) : null,
      binaryData: this._binary ? toBase64(this._binary) : null,
    };
  }

  /**
   * Batch factory: builds a SleipnirMultiRequest from several (pre-built)
   * SleipnirRequests. `mode` Serial enables @alias dependency resolution.
   */
  static batch(requests: SleipnirRequest[], mode: ExecutionMode = ExecutionMode.Serial): SleipnirMultiRequest {
    return { requests, mode };
  }

  private pushNamed(name: string, value: unknown): void {
    this._params.push({
      parameterName: name,
      num: this._num,
      data: value === undefined ? null : value,
    });
    this._num++;
  }

  private pushPositional(value: unknown): void {
    this._params.push({
      parameterName: `param${this._num}`,
      num: this._num,
      data: value === undefined ? null : value,
    });
    this._num++;
  }
}