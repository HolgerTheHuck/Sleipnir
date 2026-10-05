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
// conditional. Properties carry wire-presence semantics (see emitTypes), so
// interfaces are no longer universally assignable — but path keys still need
// the explicit record for `$`-syntax and cardinality, so the design stands.

import type { EmitterInput, ResolvedController, ResolvedEnum, ResolvedMethod, ResolvedType, ResolvedTypeRef } from "../core/model.js";
import { toCamelCase } from "../core/casing.js";
import { tsTypeOfRef as tsTypeOfRefBase, enumRefOf, isEventMethod, eventPayloadRef, hasEvents } from "../core/model.js";
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

// The TS emitter's own NamingResolver over every name it declares in types.ts —
// object types AND enums — so a short-name collision between an object and an
// enum is disambiguated consistently for declarations and references alike.
// (Declarations therefore use `resolver.resolve(fullName)`, not the input's
// `emittedName`, which was resolved over object types only.)
function resolverFor(input: EmitterInput): NamingResolver {
  const r = new NamingResolver();
  for (const t of input.types) r.register(t.fullName);
  for (const e of input.enums ?? []) r.register(e.fullName);
  return r;
}

/** TS type of a ref with enum identity: an enum usage renders as the enum type, not `number`. */
function tsTypeOfRef(ref: ResolvedTypeRef, resolver: NamingResolver): string {
  return tsTypeOfRefBase(ref, resolver, { enumIdentity: true });
}

/** A member name as an object-literal key (quoted unless it is a plain identifier). */
function enumKey(name: string): string {
  return /^[A-Za-z_$][A-Za-z0-9_$]*$/.test(name) ? name : JSON.stringify(name);
}

// ---------------------------------------------------------------------------
// types.ts — one `as const` object + literal-union type per enum, one interface
// per ResolvedType (camelCase props, presence-aware).
// ---------------------------------------------------------------------------

function emitEnum(e: ResolvedEnum, resolver: NamingResolver): string {
  const name = resolver.resolve(e.fullName);
  const members = e.members.map((m) => `  ${enumKey(m.name)}: ${m.value},`);
  return `export const ${name} = {\n${members.join("\n")}\n} as const;\nexport type ${name} = (typeof ${name})[keyof typeof ${name}];`;
}

function emitTypes(input: EmitterInput, resolver: NamingResolver): string {
  const enumBlocks = (input.enums ?? []).map((e) => emitEnum(e, resolver));
  const blocks: string[] = [];
  for (const t of input.types) {
    const props = t.properties.map((p) => {
      const ty = tsTypeOfRef(p.typeRef, resolver);
      const doc = p.documentation ? `  /** ${p.documentation} */\n` : "";
      // Presence rule (wire-truthful):
      //  - non-nullable → required (`name: T`): the server serializes call results
      //    without an ignore condition, so every listed property is always written.
      //  - nullable → presence-optional (`name?: T | null`): event frames serialize
      //    with WhenWritingNull, so a null value is omitted (the property is absent).
      //    [JsonIgnore(WhenWritingNull)] properties are equally indistinguishable in
      //    discovery. The `| null` stays — the value, present or not, can BE null.
      const opt = p.typeRef.nullable ? "?" : "";
      return `${doc}  ${p.wireName}${opt}: ${ty};`;
    });
    blocks.push(`export interface ${resolver.resolve(t.fullName)} {\n${props.join("\n")}\n}`);
  }
  if (blocks.length === 0 && enumBlocks.length === 0)
    return "// No structured types declared in discovery.\n";
  // Enums: the wire value stays the number; the `as const` object gives the
  // member names, the same-named type is the union of the member values.
  const enumSection = enumBlocks.length
    ? `// Enums: the wire carries the numeric value. \`X.Member\` names a value; the type\n// \`X\` is the union of the member values (no TS \`enum\` — tree-shakable,\n// isolatedModules-safe).\n\n${enumBlocks.join("\n\n")}\n`
    : "";
  if (blocks.length === 0) return `// Auto-generated Sleipnir data types.\n\n${enumSection}`;
  const header = `// Auto-generated Sleipnir data types. Properties are camelCase (wire) and\n// presence-aware: non-nullable properties are required (always present on\n// the wire); nullable properties are presence-optional (\`?:\`) because event\n// frames omit null values (WhenWritingNull) — the value can still be null,\n// so the \`| null\` remains.\n\n`;
  return `${header}${enumSection}${enumSection ? "\n" : ""}${blocks.join("\n\n")}\n`;
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
  const enums = input.enums ?? [];
  const importedNames = [
    ...input.types.map((t) => resolver.resolve(t.fullName)),
    ...enums.map((e) => resolver.resolve(e.fullName)),
  ];
  const typeImport = importedNames.length
    ? `import type { ${importedNames.join(", ")} } from "./types.js";\n`
    : "";

  // Object types: object + array path records. Paths descend recursively into
  // nested object properties AND nested array-element properties (with a depth
  // cap and a cycle guard), so a chain like `$.hits[*].articleId` is a typed key
  // of TPaths — not just the top-level `$.hits` array. See `descendProps`.
  const typesByFullName = new Map<string, ResolvedType>(input.types.map((t) => [t.fullName, t]));

  for (const t of input.types) {
    const name = resolver.resolve(t.fullName);
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

  // Enums: like scalar path records, but typed with the enum type, so an alias
  // exposed from an enum-returning call binds to an enum-typed parameter.
  for (const e of enums) {
    const name = resolver.resolve(e.fullName);
    pathRecords.push(`export interface ${name}Paths {\n  "$": ${name};\n}`);
    pathRecords.push(
      `export interface ${name}ArrayPaths {\n  "$": ${name}[];\n  "$[0]": ${name};\n  "$[*]": ${name}[];\n}`,
    );
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
 * The scalar path-record kind for a scalar name: its TS type when a record is
 * emitted for it (`SCALAR_KINDS`), else `unknown`. A scalar whose TS type has no
 * record (e.g. the `any` scalar) would otherwise reference an undeclared
 * `_AnyPaths` and break the generated client's compile.
 */
function scalarPathKind(name: string | undefined): string {
  const t = tsTypeOf(name ?? "any");
  return t === "void" || (SCALAR_KINDS as readonly string[]).includes(t) ? t : "unknown";
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
    case "scalar": {
      const enumRef = enumRefOf(ref);
      return enumRef ? resolver.resolve(enumRef) + "Paths" : scalarPathsName(scalarPathKind(ref.name));
    }
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
    case "scalar": {
      const enumRef = enumRefOf(el);
      return enumRef ? resolver.resolve(enumRef) + "ArrayPaths" : scalarArrayPathsName(scalarPathKind(el.name));
    }
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
  // Event controllers need the subscribe types from the runtime client (next to
  // SleipnirCall). Controllers without event methods keep their original imports.
  const subscribeTypeImport = events
    ? `import type { SleipnirRequest, SubscribeHandlers, SleipnirSubscription, SleipnirSubscribeOptions } from "sleipnir-client";\n`
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
    // No event methods → the original build-only form.
    return `export class ${ctrl.className} {
  /** @internal */ _build: (controller: string, method: string) => SleipnirCall;
  constructor(build: (controller: string, method: string) => SleipnirCall) {
    this._build = build;
  }
${methods.join("\n\n")}
}`;
  }
  // With event methods: a second ctor parameter `subscribe` (delegates to the
  // transport router). Event methods call this._subscribe<T>(req, handlers, options).
  return `export class ${ctrl.className} {
  /** @internal */ _build: (controller: string, method: string) => SleipnirCall;
  /** @internal */ _subscribe: <T>(req: SleipnirRequest, handlers: SubscribeHandlers<T>, options?: SleipnirSubscribeOptions) => Promise<SleipnirSubscription>;
  constructor(
    build: (controller: string, method: string) => SleipnirCall,
    subscribe: <T>(req: SleipnirRequest, handlers: SubscribeHandlers<T>, options?: SleipnirSubscribeOptions) => Promise<SleipnirSubscription>,
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
 *
 * The trailing optional `options` (`SleipnirSubscribeOptions`: `signal`,
 * `resumePolicy`, `timeout`, SSE `headers`) is passed through to the router;
 * aborting `signal` ends the subscription (see the root client's `_subscribe`).
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
  const optionsParam = `options?: SleipnirSubscribeOptions`;
  const doc = m.documentation ? `  /** ${m.documentation} */\n` : "";
  return `${doc}  ${m.emittedName}(${[...params, handlerParam, optionsParam].join(", ")}): Promise<SleipnirSubscription> {
    return this._subscribe<${payloadType}>(this._build("${ctrl.name}", "${m.methodName}")${withCall}.toRequest(), handlers, options);
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
  // Event controllers take the `subscribe` callback as a second ctor arg; pure
  // call controllers keep the 1-arg ctor.
  const inits = input.controllers.map((c) => {
    const args = c.methods.some(isEventMethod) ? "build, this._subscribe" : "build";
    return `    this.${c.accessor} = new ${c.className}(${args});`;
  });
  // The `_subscribe` field (only when events exist) delegates to the transport router,
  // which bridges the WS-vs-SSE difference (WS passes the request through; SSE unpacks
  // it into (controller, method, params)). It also makes `options.signal` end an
  // ACTIVE subscription on every backend: the router's SSE/SignalR backends already
  // do that, the WS backend honors the signal only until the subscribe is
  // acknowledged — so the abort is bridged to the idempotent `unsubscribe()` here.
  const subscribeField = events
    ? `  private readonly _subscribe = async <T>(req: SleipnirRequest, handlers: SubscribeHandlers<T>, options?: SleipnirSubscribeOptions): Promise<SleipnirSubscription> => {
    const sub = await this._router.subscribe<T>(req, handlers, options);
    const signal = options?.signal;
    if (signal) {
      // Aborting the signal ends the subscription (unsubscribe() is idempotent).
      const end = (): void => { sub.unsubscribe().catch(() => undefined); };
      if (signal.aborted) end();
      else signal.addEventListener("abort", end, { once: true });
    }
    return sub;
  };
`
    : "";

  return `// Auto-generated root Sleipnir client (capability: ${capability}). Compose with the sleipnir-client runtime.
// Transport is selected at runtime via SleipnirTransportRouter: "auto" (default) probes WebSocket
// and falls back to REST+SSE on failure; useTransport() switches explicitly. The public surface
// is identical across all capabilities — only the bundled backends differ.
import { SleipnirCall, SleipnirTransportRouter } from "sleipnir-client";
import type { SleipnirResponse, SleipnirRequest, SubscribeHandlers, SleipnirSubscription,${events ? " SleipnirSubscribeOptions," : ""} SleipnirTransport, SleipnirRestClient, SleipnirWebSocketClient, SleipnirSseClient, SleipnirSignalrClient, SleipnirRestClientOptions, SleipnirWebSocketClientOptions, SleipnirSseClientOptions, SleipnirSignalrClientOptions } from "sleipnir-client";
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
  for (const t of input.types) used.add(resolver.resolve(t.fullName));
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
    case "scalar": {
      // A collapsed enum usage references the enum type.
      const enumRef = enumRefOf(ref);
      if (enumRef) used.add(resolver.resolve(enumRef));
      break;
    }
    case "array":
    case "set":
    case "stream":
    case "event":
      // An event payload (T of IObservable<T>) can be a ref → import it so the
      // `SubscribeHandlers<PayloadType>` signature resolves the type.
      if (ref.element) collectRefs(ref.element, resolver, used);
      break;
    case "map":
      if (ref.key) collectRefs(ref.key, resolver, used);
      if (ref.value) collectRefs(ref.value, resolver, used);
      break;
    // opaque / void (and plain scalars) → nothing to import.
  }
}