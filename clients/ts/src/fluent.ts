import { ExecutionMode } from "./types.js";
import type { TrameMultiRequest, TrameParameter, TrameRequest } from "./types.js";
import { toBase64 } from "./request.js";

/**
 * Fluent Builder für einen TrameRequest — Spiegel des C#-TrameCall.
 * Transport-agnostisch: liefert einen {@link TrameRequest}, den jeder Client
 * (REST/WebSocket) senden kann.
 *
 * ```ts
 * TrameCall.init("Customer", "Add")
 *   .with({ name: "Alice" })        // benannt
 *   .with([42, "x"])                // oder positional
 *   .withBinary(blob)               // -> binaryData (base64)
 *   .named("step1")                 // -> id
 *   .exposes("$", "newId")         // -> dependencyMapping (ergebnisrelativer Pfad)
 *   .withAlias("@newId")            // Platzhalter, Server löst @newId auf
 *   .toRequest();
 * ```
 */
export class TrameCall {
  private readonly _controller: string;
  private readonly _method: string;
  private _id?: string;
  private _params: TrameParameter[] = [];
  private _num = 0;
  private _exposed = new Map<string, string>();
  private _binary?: Uint8Array;

  private constructor(controller: string, method: string) {
    this._controller = controller;
    this._method = method;
  }

  /** Startet einen Builder für `controller.method`. */
  static init(controller: string, method: string): TrameCall {
    return new TrameCall(controller, method);
  }

  /** Setzt die Request-Id (Korrelation). Default: `${controller}.${method}`. */
  named(id: string): this {
    this._id = id;
    return this;
  }

  /**
   * Fügt benannte (Object) oder positionale (Array) Parameter hinzu.
   * - Object → `{parameterName: key, data: value}` (sichere Bindung).
   * - Array  → `{parameterName: "param{i}", num: i, data: value}` (Positional via `num`).
   */
  with(params: Record<string, unknown> | unknown[]): this {
    if (Array.isArray(params)) {
      for (const value of params) this.pushPositional(value);
    } else {
      for (const [key, value] of Object.entries(params)) this.pushNamed(key, value);
    }
    return this;
  }

  /** Fügt einen benannten Parameter hinzu (Name muss server-seitig passen). */
  param(name: string, value: unknown): this {
    this.pushNamed(name, value);
    return this;
  }

  /**
   * Deklariert, dass diese Response den Wert unter `jsonPath` als `alias`
   * exposed (für Dependency-Chaining). Server-seitig aufgelöst; Folgerequests
   * nutzen `@alias` in ihrem `data`.
   *
   * `jsonPath` ist **ergebnisrelativ** — die Wurzel `$` ist das serialisierte
   * Resultat (z. B. ein `int` oder ein `Customer`-Objekt), nicht der
   * Response-Umschlag. Es gibt also keine `data`-Knoten-Ebene: nutze `$` für das
   * ganze Resultat, `$.Id`/`$.Name` für Eigenschaften, `$[0].Id` für ein
   * Listenelement. Ein Pfad wie `$.data` trifft nie (außer das Resultat hat
   * selbst eine `data`-Eigenschaft).
   */
  exposes(jsonPath: string, alias: string): this {
    this._exposed.set(alias, jsonPath);
    return this;
  }

  /**
   * Fügt einen Parameter mit einem Dependency-Platzhalter hinzu, z. B.
   * `withAlias("@newId")`. Der Server ersetzt `@newId` anhand einer zuvor
   * exposed Dependency. Ist der Alias nicht auflösbar, schlägt der Aufruf fehl
   * (kein impliziter Fallback in v1).
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

  /** Setzt das Binary-Payload (für byte[]-Parameter der Zielmethode). */
  withBinary(bytes: Uint8Array): this {
    this._binary = bytes;
    return this;
  }

  /** Wandelt den Builder in einen versandfertigen TrameRequest um. */
  toRequest(): TrameRequest {
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
   * Batch-Factory: baut einen TrameMultiRequest aus mehreren (vorab gebauten)
   * TrameRequests. `mode` Serial aktiviert @alias-Abhängigkeitsauflösung.
   */
  static batch(requests: TrameRequest[], mode: ExecutionMode = ExecutionMode.Serial): TrameMultiRequest {
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