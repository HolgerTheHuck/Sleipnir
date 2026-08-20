import { ExecutionMode } from "./types.js";
import type { SleipnirMultiRequest, SleipnirRequest } from "./types.js";
/**
 * Fluent Builder für einen SleipnirRequest — Spiegel des C#-SleipnirCall.
 * Transport-agnostisch: liefert einen {@link SleipnirRequest}, den jeder Client
 * (REST/WebSocket) senden kann.
 *
 * ```ts
 * SleipnirCall.init("Customer", "Add")
 *   .with({ name: "Alice" })        // benannt
 *   .with([42, "x"])                // oder positional
 *   .withBinary(blob)               // -> binaryData (base64)
 *   .named("step1")                 // -> id
 *   .exposes("$", "newId")         // -> dependencyMapping (ergebnisrelativer Pfad)
 *   .withAlias("@newId")            // Platzhalter, Server löst @newId auf
 *   .toRequest();
 * ```
 */
export declare class SleipnirCall {
    private readonly _controller;
    private readonly _method;
    private _id?;
    private _params;
    private _num;
    private _exposed;
    private _binary?;
    private constructor();
    /** Startet einen Builder für `controller.method`. */
    static init(controller: string, method: string): SleipnirCall;
    /** Setzt die Request-Id (Korrelation). Default: `${controller}.${method}`. */
    named(id: string): this;
    /**
     * Fügt benannte (Object) oder positionale (Array) Parameter hinzu.
     * - Object → `{parameterName: key, data: value}` (sichere Bindung).
     * - Array  → `{parameterName: "param{i}", num: i, data: value}` (Positional via `num`).
     */
    with(params: Record<string, unknown> | unknown[]): this;
    /** Fügt einen benannten Parameter hinzu (Name muss server-seitig passen). */
    param(name: string, value: unknown): this;
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
    exposes(jsonPath: string, alias: string): this;
    /**
     * Fügt einen Parameter mit einem Dependency-Platzhalter hinzu, z. B.
     * `withAlias("@newId")`. Der Server ersetzt `@newId` anhand einer zuvor
     * exposed Dependency. Ist der Alias nicht auflösbar, schlägt der Aufruf fehl
     * (kein impliziter Fallback in v1).
     */
    withAlias(dependencyPlaceholder: string): this;
    /** Setzt das Binary-Payload (für byte[]-Parameter der Zielmethode). */
    withBinary(bytes: Uint8Array): this;
    /** Wandelt den Builder in einen versandfertigen SleipnirRequest um. */
    toRequest(): SleipnirRequest;
    /**
     * Batch-Factory: baut einen SleipnirMultiRequest aus mehreren (vorab gebauten)
     * SleipnirRequests. `mode` Serial aktiviert @alias-Abhängigkeitsauflösung.
     */
    static batch(requests: SleipnirRequest[], mode?: ExecutionMode): SleipnirMultiRequest;
    private pushNamed;
    private pushPositional;
}
//# sourceMappingURL=fluent.d.ts.map