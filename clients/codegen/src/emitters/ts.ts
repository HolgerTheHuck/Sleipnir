// TypeScript emitter — emits a typed client from an EmitterInput.
//
// Output is a multi-file tree (Record<relativePath, contents>) so `tsc --noEmit`
// can exercise it and the DevUI can concatenate it with file banners.
//
// Headline value: a typed batch builder. Each generated method returns a
// `TypedCall<T, TPaths>` where `TPaths` is a generated record mapping valid
// result-relative `$`-paths to their extracted type. `exposes("$.Id", "@x")`
// (PascalCase — the wire is camelCase) is a *compile error* because `"$.Id"` is
// not a key of `TPaths`. `batch.alias("@x")` returns `TPaths[path]`, so the
// consumer's parameter typechecks.
//
// The path-type is carried explicitly per call (set by the generated controller
// method) rather than looked up from the data type via a distributive
// conditional — which would be ambiguous because all generated properties are
// optional (every interface would be structurally assignable to every other).

import type { EmitterInput, ResolvedController, ResolvedMethod, ResolvedType, ResolvedTypeRef } from "../core/model.js";
import { toCamelCase } from "../core/casing.js";
import { tsTypeOfRef, isEventMethod, eventPayloadRef, hasEvents } from "../core/model.js";
import { tsTypeOf } from "../core/scalars.js";
import { NamingResolver } from "../core/naming.js";

/** Which backends the generated `SleipnirClient` bundles. The public surface is
 * identical across all capabilities — only the bundled backends differ. */
export type SleipnirBundleCapability = "rest" | "ws" | "all" | "signalr";

export interface EmitTsOptions {
  /** Base URL hint rendered into the client header comment. */
  baseUrl?: string;
  /**
   * Codegen capability — which backends the generated `SleipnirClient` bundles. The public
   * `SleipnirClient` surface is identical across all capabilities; only the bundled backends
   * (and thus the runtime transport choices) differ. Transport is selected at runtime via
   * `SleipnirTransportRouter` (`auto` default probes WS → falls back to REST+SSE).
   * - `rest`: REST + SSE (HTTP-only, proxy-safe). `auto` resolves to REST/SSE immediately.
   * - `ws`: WebSocket only (calls + events). No fallback backend is bundled.
   * - `all` (default): REST + WS + SSE — enables `auto` (WS → REST+SSE fallback).
   * - `signalr`: REST + WS + SSE + SignalR (opt-in add-on; the SignalR backend lands in Phase 3,
   *   but the capability is accepted now so the generated client is forward-compatible).
   */
  capability?: SleipnirBundleCapability;
  /**
   * DEPRECATED alias for `capability`, kept one minor version for upgrade compat. Canonicalized:
   * `rest`→`rest`, `sse`→`rest`, `ws`→`ws`, `both`→`all`. Use `capability` instead. If both are
   * given, `capability` wins.
   */
  transport?: "rest" | "sse" | "ws" | "both";
}

/** Emit the full TS client as a file tree. */
export function emitTsClient(input: EmitterInput, opts: EmitTsOptions = {}): Record<string, string> {
  const resolver = resolverFor(input);
  return {
    "api/types.ts": emitTypes(input, resolver),
    "api/typed-call.ts": emitTypedCall(input, resolver),
    "api/controllers.ts": emitControllers(input, resolver),
    "api/client.ts": emitClient(input, opts),
    "api/index.ts": emitIndex(input),
  };
}

// We need the NamingResolver used to build the input. Re-derive it from emitted
// names via a thin wrapper — the input already carries emittedName per type.
function resolverFor(input: EmitterInput): NamingResolver {
  const r = new NamingResolver();
  for (const t of input.types) r.register(t.fullName);
  return r;
}

// ---------------------------------------------------------------------------
// types.ts — one interface per ResolvedType (camelCase props, all optional).
// ---------------------------------------------------------------------------

function emitTypes(input: EmitterInput, _resolver: NamingResolver): string {
  const blocks: string[] = [];
  for (const t of input.types) {
    const props = t.properties.map((p) => {
      const ty = tsTypeOfRef(p.typeRef, _resolver);
      const doc = p.documentation ? `  /** ${p.documentation} */\n` : "";
      return `${doc}  ${p.wireName}?: ${ty};`;
    });
    blocks.push(`export interface ${t.emittedName} {\n${props.join("\n")}\n}`);
  }
  if (blocks.length === 0) return "// No structured types declared in discovery.\n";
  return `// Auto-generated Sleipnir data types. Properties are camelCase (wire) and\n// optional (discovery carries no nullability; callers narrow).\n\n${blocks.join("\n\n")}\n`;
}

// ---------------------------------------------------------------------------
// typed-call.ts — path-type records + TypedCall<T, TPaths> + TypedRequest + Batch.
// ---------------------------------------------------------------------------

const SCALAR_KINDS = ["number", "string", "boolean", "bigint", "unknown"] as const;

/**
 * Maximum number of nested-type descents when building path records. The root
 * type is depth 0; each descent into a property's object type or an array
 * property's element type costs one level. Caps path-record explosion for deep
 * / mutually-recursive graphs; the cycle guard (`seen`) handles true cycles.
 */
const MAX_PATH_DEPTH = 3;

type Cardinality = "single" | "array";

/** Apply array cardinality to a rendered type: `number` → `number[]`. */
function withCard(baseType: string, card: Cardinality): string {
  return card === "array" ? `${baseType}[]` : baseType;
}

/**
 * Recursively emit path-record entries for the properties of `type` reachable
 * at `prefix` (a full `$`-path like `$`, `$.x`, `$[0]`, `$[*].hits[0]`).
 *
 * `card` is the cardinality of the path so far: `"array"` when any `[*]`
 * segment is on the path (the leaf selects multiple matches → its type gets
 * `[]`); `"single"` otherwise. For an array-valued property we emit both a
 * `[0]` (one element, keeps outer cardinality) and a `[*]` (collected, always
 * array cardinality) entry, and descend into the element type under each. For a
 * nested object property we descend under the property name with the same
 * cardinality. `map`-valued properties emit only the leaf (no clean path syntax
 * for map values). Depth-capped and cycle-guarded via `seen` (fullNames on the
 * current path).
 */
function descendProps(
  prefix: string,
  type: ResolvedType,
  card: Cardinality,
  depth: number,
  seen: Set<string>,
  entries: string[],
  resolver: NamingResolver,
  typesByFullName: Map<string, ResolvedType>,
): void {
  for (const p of type.properties) {
    const propPrefix = `${prefix}.${p.wireName}`;
    const ref = p.typeRef;
    // The property itself at this path.
    entries.push(`  "${propPrefix}": ${withCard(tsTypeOfRef(ref, resolver), card)};`);

    if (ref.kind === "array" || ref.kind === "set" || ref.kind === "stream") {
      const element = ref.element ?? { kind: "opaque" as const };
      const elemType = tsTypeOfRef(element, resolver);
      // [0]: one element of the inner array (outer cardinality applies).
      entries.push(`  "${propPrefix}[0]": ${withCard(elemType, card)};`);
      // [*]: all elements collected (always array cardinality).
      entries.push(`  "${propPrefix}[*]": ${withCard(elemType, "array")};`);
      // Descend into the element's properties under each selector, if the
      // element is a structured object type and we haven't hit the caps.
      if (element.kind === "ref" && element.ref && depth < MAX_PATH_DEPTH) {
        const elemResolved = typesByFullName.get(element.ref);
        if (elemResolved && !seen.has(element.ref)) {
          const nextSeen = new Set(seen).add(element.ref);
          descendProps(`${propPrefix}[0]`, elemResolved, card, depth + 1, nextSeen, entries, resolver, typesByFullName);
          descendProps(`${propPrefix}[*]`, elemResolved, "array", depth + 1, nextSeen, entries, resolver, typesByFullName);
        }
      }
    } else if (ref.kind === "ref" && ref.ref && depth < MAX_PATH_DEPTH) {
      const nested = typesByFullName.get(ref.ref);
      if (nested && !seen.has(ref.ref)) {
        descendProps(propPrefix, nested, card, depth + 1, new Set(seen).add(ref.ref), entries, resolver, typesByFullName);
      }
    }
    // scalar / opaque / void / map: leaf only, no descent.
  }
}

function emitTypedCall(input: EmitterInput, resolver: NamingResolver): string {
  const pathRecords: string[] = [];
  // typed-call.ts references every emitted type name in path records.
  const typeImport = input.types.length
    ? `import type { ${input.types.map((t) => t.emittedName).join(", ")} } from "./types.js";\n`
    : "";

  // Object types: object + array path records. Paths descend recursively into
  // nested object properties AND nested array-element properties (with a depth
  // cap and a cycle guard), so a chain like `$.hits[*].articleId` is a typed key
  // of TPaths — not just the top-level `$.hits` array. See `descendProps`.
  const typesByFullName = new Map<string, ResolvedType>(input.types.map((t) => [t.fullName, t]));

  for (const t of input.types) {
    const name = t.emittedName;
    // XPaths: "$" → X, then descend "$.prop", "$.prop.sub", "$.arr[*].sub", …
    const objEntries: string[] = [`  "$": ${name};`];
    descendProps("$", t, "single", 0, new Set([t.fullName]), objEntries, resolver, typesByFullName);
    pathRecords.push(`export interface ${name}Paths {\n${objEntries.join("\n")}\n}`);

    // XArrayPaths: "$" → X[], "$[0]" → X; descend both the single-element root
    // ($[0].prop, single cardinality) and the collected root ($[*].prop, array
    // cardinality → leaf types get `[]`).
    const arrEntries: string[] = [`  "$": ${name}[];`, `  "$[0]": ${name};`];
    descendProps("$[0]", t, "single", 0, new Set([t.fullName]), arrEntries, resolver, typesByFullName);
    descendProps("$[*]", t, "array", 0, new Set([t.fullName]), arrEntries, resolver, typesByFullName);
    pathRecords.push(`export interface ${name}ArrayPaths {\n${arrEntries.join("\n")}\n}`);
  }

  // Scalar kinds: scalar + scalar-array path records (for scalar-returning methods).
  for (const s of SCALAR_KINDS) {
    pathRecords.push(`export interface ${scalarPathsName(s)} {\n  "$": ${s};\n}`);
    pathRecords.push(
      `export interface ${scalarArrayPathsName(s)} {\n  "$": ${s}[];\n  "$[0]": ${s};\n  "$[*]": ${s}[];\n}`,
    );
  }
  // void / Task return: no paths (cannot expose from a void result).
  pathRecords.push(`export interface _VoidPaths {}`);

  return `// Auto-generated typed-call + batch machinery. Do not edit by hand.
//
// Each TypedCall carries its own path-type record as a type parameter (TPaths),
// set at the call site by the generated controller method. \`exposes\` takes
// \`path: keyof TPaths\` and the alias type is \`TPaths[path]\` — so path and alias
// validity are compile-checked without a (structurally ambiguous) lookup over T.
import { SleipnirCall, ExecutionMode } from "sleipnir-client";
import type { SleipnirRequest, SleipnirMultiRequest, SleipnirResponse } from "sleipnir-client";
${typeImport}
${pathRecords.join("\n\n")}

/** A map of valid result-relative $-paths to their extracted type, for a call. */
export type PathTypes = Record<string, unknown>;

/**
 * A typed single call wrapping a sleipnir-client {@link SleipnirCall}. \`TPaths\` is
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
 * A call enrolled in a batch. \`exposes\` declares an alias the server will
 * resolve from this call's result; the alias type (\`TPaths[path]\`) is tracked
 * at compile time so \`alias("@x")\` returns the producer's exposed type.
 */
export class TypedRequest<T, TPaths = PathTypes, A extends Record<string, unknown> = {}> {
  /** @internal */ _call: SleipnirCall;
  constructor(call: TypedCall<T, TPaths>) { this._call = call._call; }
  /**
   * Declare that this call exposes \`path\` as \`alias\`. Compile-time-checked.
   * The wire \`dependencyMapping\` key is the alias **without** the leading \`@\`
   * (the server strips \`@\` from a consumer's \`@alias\` placeholder before
   * lookup — see SleipnirInvoker.ReplaceDependencyByAliasCore), so we strip it
   * here. The alias type (\`TPaths[path]\`) is tracked regardless.
   */
  exposes<P extends string & keyof TPaths, Aname extends string>(path: P, alias: Aname): TypedRequest<T, TPaths, A & Record<Aname, TPaths[P]>> {
    this._call.exposes(path, alias.startsWith("@") ? alias.slice(1) : alias);
    return this as TypedRequest<T, TPaths, A & Record<Aname, TPaths[P]>>;
  }
  /** Set the request id. */
  named(id: string): this { this._call.named(id); return this; }
  /**
   * Resolve a previously-declared alias to its typed value (for a consumer param).
   * At runtime this returns the literal \`@alias\` placeholder string — the wire
   * value the server substitutes in Serial/topological mode (mirrors
   * \`SleipnirCall.withAlias("@x")\`, which sets \`data: "@x"\`). The compile-time type
   * is the producer's exposed type, so the consumer param typechecks.
   *
   * \`@\`-normalization is symmetric with \`exposes\`: \`exposes\` STRIPS a leading \`@\`
   * (the wire \`dependencyMapping\` key is the bare name — the server strips the
   * consumer's \`@alias\` placeholder before lookup), while \`alias\` ENSURES a leading
   * \`@\` (the consumer sends \`data: "@alias"\`). So both call styles work:
   * \`alias("ids")\` → \`"@ids"\` and \`alias("@ids")\` → \`"@ids"\`. Returning the bare
   * name here (the 1.2.1 bug) sent \`"ids"\` on the wire, which the server's
   * \`ReplaceDependencyByAlias\` never matched — the typed chain compiled but the
   * dependent call received an unresolved literal instead of the alias value.
   */
  alias<Aname extends string & keyof A>(name: Aname): A[Aname] {
    return (name.startsWith("@") ? name : "@" + name) as unknown as A[Aname];
  }
  /** @internal */ toRequest(): SleipnirRequest { return this._call.toRequest(); }
}

/**
 * Batch builder for dependency-chained calls. Execution mode is Serial (the
 * only mode that resolves \`@alias\` placeholders). Add calls in topological
 * order: a producer's \`exposes\` must run before any consumer's \`alias\`.
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
`;
}

function scalarPathsName(s: string): string {
  return "_" + s.charAt(0).toUpperCase() + s.slice(1) + "Paths";
}
function scalarArrayPathsName(s: string): string {
  return "_" + s.charAt(0).toUpperCase() + s.slice(1) + "ArrayPaths";
}

/**
 * The generated path-record interface name for a return type ref — the `TPaths`
 * carried by the method's `TypedCall<T, TPaths>`. Arrays/sets/streams use
 * `XArrayPaths` (or `_ScalarArrayPaths`); object refs use `XPaths`; scalars use
 * `_ScalarPaths`; opaque/void → `_VoidPaths` (no exposable paths).
 */
function pathRecordForRef(ref: ResolvedTypeRef, resolver: NamingResolver): string {
  switch (ref.kind) {
    case "array":
    case "set":
    case "stream":
      // JSON materializes sets/streams as arrays, so path records are array-shaped.
      return arrayPathsNameFor(ref.element, resolver);
    case "ref":
      return resolver.resolve(ref.ref ?? "") + "Paths";
    case "scalar":
      return scalarPathsName(tsTypeOf(ref.name ?? "any"));
    case "opaque":
    case "void":
      return "_VoidPaths"; // opaque has no structured paths to expose
    case "event":
      // Events are not chainable (no exposes/@alias on a subscription) → no paths.
      return "_VoidPaths";
    case "map":
    default:
      return scalarPathsName("unknown");
  }
}

/** Path-record name for an array element: object → XArrayPaths, scalar → _XArrayPaths. */
function arrayPathsNameFor(element: ResolvedTypeRef | undefined, resolver: NamingResolver): string {
  const el = element ?? { kind: "opaque" as const };
  switch (el.kind) {
    case "ref": return resolver.resolve(el.ref ?? "") + "ArrayPaths";
    case "scalar": return scalarArrayPathsName(tsTypeOf(el.name ?? "any"));
    case "opaque": return scalarArrayPathsName("unknown");
    default: return scalarArrayPathsName("unknown"); // nested array/map element — opaque-ish
  }
}

// ---------------------------------------------------------------------------
// controllers.ts — one class per controller; methods return TypedCall<T, TPaths>.
// ---------------------------------------------------------------------------

function emitControllers(input: EmitterInput, resolver: NamingResolver): string {
  const typeImports = collectTypeImports(input, resolver);
  const pathImports = collectPathRecordImports(input, resolver);
  const events = hasEvents(input);
  const classes = input.controllers.map((c) => emitControllerClass(c, resolver));
  // Event-Controller brauchen die Subscribe-Typen aus dem Runtime-Client (neben
  // SleipnirCall). Controller ohne Event-Method bleiben unverändert (kein Import-
  // Schwenk → story01/story02-Snapshots byte-identisch).
  const subscribeTypeImport = events
    ? `import type { SleipnirRequest, SubscribeHandlers, SleipnirSubscription } from "sleipnir-client";\n`
    : "";
  return `// Auto-generated Sleipnir controllers. Method names are camelCase; parameter
// names bind case-sensitively on the wire (keys passed verbatim to SleipnirCall).
import { SleipnirCall } from "sleipnir-client";
${subscribeTypeImport}import { TypedCall } from "./typed-call.js";
${typeImports ? typeImports + "\n" : ""}${pathImports ? pathImports + "\n" : ""}
${classes.join("\n\n")}
`;
}

function emitControllerClass(ctrl: ResolvedController, resolver: NamingResolver): string {
  const events = ctrl.methods.some(isEventMethod);
  const methods = ctrl.methods.map((m) =>
    isEventMethod(m) ? emitEventMethod(ctrl, m, resolver) : emitMethod(ctrl, m, resolver),
  );
  if (!events) {
    // Keine Event-Methoden → ursprüngliche Form (build-only), Snapshots stabil.
    return `export class ${ctrl.className} {
  /** @internal */ _build: (controller: string, method: string) => SleipnirCall;
  constructor(build: (controller: string, method: string) => SleipnirCall) {
    this._build = build;
  }
${methods.join("\n\n")}
}`;
  }
  // Mit Event-Methoden: zweiter ctor-Parameter `subscribe` (delegiert an den
  // WS-Client). Event-Methoden rufen this._subscribe<T>(req, handlers) auf.
  return `export class ${ctrl.className} {
  /** @internal */ _build: (controller: string, method: string) => SleipnirCall;
  /** @internal */ _subscribe: <T>(req: SleipnirRequest, handlers: SubscribeHandlers<T>) => Promise<SleipnirSubscription>;
  constructor(
    build: (controller: string, method: string) => SleipnirCall,
    subscribe: <T>(req: SleipnirRequest, handlers: SubscribeHandlers<T>) => Promise<SleipnirSubscription>,
  ) {
    this._build = build;
    this._subscribe = subscribe;
  }
${methods.join("\n\n")}
}`;
}

/**
 * Emit a typed `subscribe` method for a `[SleipnirEvent]` (IObservable<T>) method.
 * Builds the wire request via `SleipnirCall` (named params, case-sensitive) and
 * delegates to the root client's `_subscribe<T>`, which sends `kind:"subscribe"`
 * over WebSocket and routes the returned `SleipnirSubscription`'s event frames to
 * the caller's handlers. Events are NOT chainable (no `exposes`/`@alias`).
 */
function emitEventMethod(ctrl: ResolvedController, m: ResolvedMethod, resolver: NamingResolver): string {
  const payloadType = tsTypeOfRef(eventPayloadRef(m), resolver);
  const params = m.parameters.map((p) => {
    const tsName = toCamelCase(p.name);
    const ty = tsTypeOfRef(p.typeRef, resolver);
    return `${tsName}: ${ty}`;
  });
  const withEntries = m.parameters.map((p) => {
    const tsName = toCamelCase(p.name);
    // Wire key is the exact discovery parameter name (case-sensitive binding).
    return `${p.name}: ${tsName}`;
  });
  const withCall = withEntries.length
    ? `.with({ ${withEntries.join(", ")} })`
    : "";
  const handlerParam = `handlers: SubscribeHandlers<${payloadType}>`;
  const doc = m.documentation ? `  /** ${m.documentation} */\n` : "";
  return `${doc}  ${m.emittedName}(${[...params, handlerParam].join(", ")}): Promise<SleipnirSubscription> {
    return this._subscribe<${payloadType}>(this._build("${ctrl.name}", "${m.methodName}")${withCall}.toRequest(), handlers);
  }`;
}

function emitMethod(ctrl: ResolvedController, m: ResolvedMethod, resolver: NamingResolver): string {
  const retType = m.isVoid ? "void" : tsTypeOfRef(m.returnType, resolver);
  const pathsName = m.isVoid ? "_VoidPaths" : pathRecordForRef(m.returnType, resolver);
  const params = m.parameters.map((p) => {
    const tsName = toCamelCase(p.name);
    const ty = tsTypeOfRef(p.typeRef, resolver);
    return `${tsName}: ${ty}`;
  });
  const withEntries = m.parameters.map((p) => {
    const tsName = toCamelCase(p.name);
    // Wire key is the exact discovery parameter name (case-sensitive binding).
    return `${p.name}: ${tsName}`;
  });
  const withCall = withEntries.length
    ? `.with({ ${withEntries.join(", ")} })`
    : "";
  const doc = m.documentation ? `  /** ${m.documentation} */\n` : "";
  const todo = m.returnType.kind === "opaque" && !m.isVoid
    ? `  // TODO: return type "${m.returnType.nativeName ?? "?"}" is an opaque framework/BCL type not modelled in discovery; emitted as unknown.\n`
    : "";
  return `${doc}${todo}  ${m.emittedName}(${params.join(", ")}): TypedCall<${retType}, ${pathsName}> {
    return new TypedCall<${retType}, ${pathsName}>(this._build("${ctrl.name}", "${m.methodName}")${withCall});
  }`;
}

// ---------------------------------------------------------------------------
// client.ts — root client with per-controller accessors + call/batch helpers.
// ---------------------------------------------------------------------------

/** Canonicalize the capability option. `capability` wins; the deprecated `transport`
 * alias is mapped (sse→rest, both→all); default is `all`. */
function resolveCapability(opts: EmitTsOptions): SleipnirBundleCapability {
  if (opts.capability) return opts.capability;
  switch (opts.transport) {
    case "rest": return "rest";
    case "sse": return "rest";
    case "ws": return "ws";
    case "both": return "all";
    default: return "all";
  }
}

function emitClient(input: EmitterInput, opts: EmitTsOptions): string {
  const capability = resolveCapability(opts);
  const events = hasEvents(input);
  const imports = input.controllers.map((c) => `import { ${c.className} } from "./controllers.js";`).join("\n");
  const accessors = input.controllers.map((c) => `  readonly ${c.accessor}: ${c.className};`);
  // Event-Controller brauchen den `subscribe`-Callback als zweiten ctor-Arg; reine
  // Call-Controller bleiben beim 1-arg-ctor (Snapshots von story01/story02 stabil).
  const inits = input.controllers.map((c) => {
    const args = c.methods.some(isEventMethod) ? "build, this._subscribe" : "build";
    return `    this.${c.accessor} = new ${c.className}(${args});`;
  });
  // Typ-Import-Fragment + `_subscribe`-Feld, nur wenn Events vorhanden. Das `_subscribe`-
  // Feld delegiert an den Transport-Router, der den WS-vs-SSE-Unterschied kapselt
  // (WS reicht das Request durch; SSE entpackt es zu (controller, method, params)).
  const subscribeTypes = events
    ? ", SleipnirRequest, SubscribeHandlers, SleipnirSubscription"
    : "";
  const subscribeField = events
    ? `  private readonly _subscribe = <T>(req: SleipnirRequest, handlers: SubscribeHandlers<T>): Promise<SleipnirSubscription> => this._router.subscribe<T>(req, handlers);\n`
    : "";

  return `// Auto-generated root Sleipnir client (capability: ${capability}). Compose with the sleipnir-client runtime.
// Transport is selected at runtime via SleipnirTransportRouter: "auto" (default) probes WebSocket
// and falls back to REST+SSE on failure; useTransport() switches explicitly. The public surface
// is identical across all capabilities — only the bundled backends differ.
import { SleipnirCall, SleipnirTransportRouter } from "sleipnir-client";
import type { SleipnirResponse, SleipnirRequest, SubscribeHandlers, SleipnirSubscription, SleipnirTransport, SleipnirRestClient, SleipnirWebSocketClient, SleipnirSseClient, SleipnirSignalrClient, SleipnirRestClientOptions, SleipnirWebSocketClientOptions, SleipnirSseClientOptions, SleipnirSignalrClientOptions } from "sleipnir-client";
import { Batch, TypedCall } from "./typed-call.js";
${imports}

/** A SleipnirResponse whose \`data\` is narrowed to T (the wire shape is unchanged). */
export type TypedResponse<T> = SleipnirResponse & { data: T | null };

/** Options for the generated SleipnirClient — a strict superset across all capabilities.
 *  Fields for unbundled backends are accepted but ignored by the router (the capability
 *  decides which backends are instantiated). */
export interface SleipnirClientOptions {
  /** REST backend options (used when REST is bundled). */
  rest?: SleipnirRestClientOptions;
  /** WebSocket backend options (used when WS is bundled). */
  ws?: SleipnirWebSocketClientOptions;
  /** SSE backend options (used when SSE is bundled). */
  sse?: SleipnirSseClientOptions;
  /** SignalR backend options (opt-in add-on; Phase 3). Used when SignalR is bundled. */
  signalr?: SleipnirSignalrClientOptions;
  /** Bearer token (or provider) applied to all bundled backends. */
  bearer?: string | (() => string);
  /** Call timeout (ms) for REST + WS. */
  callTimeout?: number;
  /** WS handshake probe timeout (ms) for \`auto\` negotiation. Default 1500. */
  probeTimeout?: number;
  /** Default transport profile. Defaults to \`auto\`. */
  defaultTransport?: SleipnirTransport;
}

export class SleipnirClient {
  private readonly _router: SleipnirTransportRouter;
${subscribeField}${accessors.join("\n")}

  constructor(baseUrl: string, options: SleipnirClientOptions = {}) {
    this._router = new SleipnirTransportRouter({ baseUrl, capability: "${capability}", ...options });
    const build = (controller: string, method: string) => SleipnirCall.init(controller, method);
${inits.join("\n")}
  }

  /** Resolve the \`auto\` profile (probe WS → fallback REST+SSE). No-op for a fixed profile. */
  negotiate(): Promise<void> { return this._router.negotiate(); }

  /** Switch the active transport at runtime. Throws if the backend isn't bundled. */
  useTransport(t: SleipnirTransport): Promise<void> { return this._router.useTransport(t); }

  /** The resolved transport profile (\`null\` until \`auto\` is negotiated). */
  get activeTransport(): Exclude<SleipnirTransport, "auto"> | null { return this._router.activeTransport; }

  /** Execute a single typed call over the active call backend; \`response.data\` is narrowed to T. */
  async call<T, TPaths extends Record<string, unknown>>(call: TypedCall<T, TPaths>): Promise<TypedResponse<T>> {
    return (await this._router.call(call.toRequest())) as TypedResponse<T>;
  }

  /** Execute a typed batch over the active call backend (Serial — required for @alias resolution). */
  async batch<A extends Record<string, unknown>>(b: Batch<A>): Promise<SleipnirResponse[]> {
    const multi = b.toMulti();
    return this._router.callBatch(multi.requests, multi.mode);
  }

  /** The underlying REST client (escape hatch). \`undefined\` if not bundled. */
  get rest(): SleipnirRestClient | undefined { return this._router.rest; }
  /** The underlying WebSocket client (escape hatch). \`undefined\` if not bundled. */
  get ws(): SleipnirWebSocketClient | undefined { return this._router.ws; }
  /** The underlying SSE client (escape hatch). \`undefined\` if not bundled. */
  get sse(): SleipnirSseClient | undefined { return this._router.sse; }
  /** The underlying SignalR client (escape hatch). \`undefined\` if not bundled. */
  get signalr(): SleipnirSignalrClient | undefined { return this._router.signalr; }

  /** Swap the bearer on all bundled backends. */
  setBearer(bearer: string | (() => string)): void { this._router.setBearer(bearer); }

  /** Dispose all bundled backends (terminal). */
  dispose(): void { this._router.dispose(); }
}
`;
}
function emitIndex(_input: EmitterInput): string {
  return `// Auto-generated barrel.
export * from "./types.js";
export * from "./typed-call.js";
export * from "./controllers.js";
export { SleipnirClient } from "./client.js";
`;
}

// ---------------------------------------------------------------------------
// helpers
// ---------------------------------------------------------------------------

/** Collect the import line for all emitted type names referenced by controllers. */
function collectTypeImports(input: EmitterInput, resolver: NamingResolver): string {
  const used = new Set<string>();
  for (const t of input.types) used.add(t.emittedName);
  for (const c of input.controllers) {
    for (const m of c.methods) {
      if (!m.isVoid) collectRefs(m.returnType, resolver, used);
      for (const p of m.parameters) collectRefs(p.typeRef, resolver, used);
    }
  }
  if (used.size === 0) return "";
  const names = [...used].sort().join(", ");
  return `import type { ${names} } from "./types.js";`;
}

/** Collect the import line for all path-record interfaces referenced by methods. */
function collectPathRecordImports(input: EmitterInput, resolver: NamingResolver): string {
  const used = new Set<string>();
  for (const c of input.controllers) {
    for (const m of c.methods) {
      // Event-Methoden verwenden keinen TypedCall/path-record (sie sind nicht
      // chainbar) → kein Import. Void-Methoden referenzieren _VoidPaths.
      if (isEventMethod(m)) continue;
      if (m.isVoid) { used.add("_VoidPaths"); continue; }
      used.add(pathRecordForRef(m.returnType, resolver));
    }
  }
  if (used.size === 0) return "";
  const names = [...used].sort().join(", ");
  return `import type { ${names} } from "./typed-call.js";`;
}

function collectRefs(ref: ResolvedTypeRef, resolver: NamingResolver, used: Set<string>): void {
  switch (ref.kind) {
    case "ref":
      used.add(resolver.resolve(ref.ref ?? ""));
      break;
    case "array":
    case "set":
    case "stream":
    case "event":
      // Event-Payload (T aus IObservable<T>) kann ein ref sein → importieren,
      // damit die `SubscribeHandlers<PayloadType>`-Signatur den Typ auflöst.
      if (ref.element) collectRefs(ref.element, resolver, used);
      break;
    case "map":
      if (ref.key) collectRefs(ref.key, resolver, used);
      if (ref.value) collectRefs(ref.value, resolver, used);
      break;
    // scalar / opaque / void → nothing to import.
  }
}