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

import type { EmitterInput, ResolvedController, ResolvedMethod, ResolvedTypeRef } from "../core/model.js";
import { toCamelCase } from "../core/casing.js";
import { tsTypeOfRef } from "../core/model.js";
import { tsTypeOf } from "../core/scalars.js";
import { NamingResolver } from "../core/naming.js";

export interface EmitTsOptions {
  /** Base URL hint rendered into the client header comment. */
  baseUrl?: string;
}

/** Emit the full TS client as a file tree. */
export function emitTsClient(input: EmitterInput, _opts: EmitTsOptions = {}): Record<string, string> {
  const resolver = resolverFor(input);
  return {
    "api/types.ts": emitTypes(input, resolver),
    "api/typed-call.ts": emitTypedCall(input, resolver),
    "api/controllers.ts": emitControllers(input, resolver),
    "api/client.ts": emitClient(input, resolver),
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
  return `// Auto-generated Trame data types. Properties are camelCase (wire) and\n// optional (discovery carries no nullability; callers narrow).\n\n${blocks.join("\n\n")}\n`;
}

// ---------------------------------------------------------------------------
// typed-call.ts — path-type records + TypedCall<T, TPaths> + TypedRequest + Batch.
// ---------------------------------------------------------------------------

const SCALAR_KINDS = ["number", "string", "boolean", "bigint", "unknown"] as const;

function emitTypedCall(input: EmitterInput, resolver: NamingResolver): string {
  const pathRecords: string[] = [];
  // typed-call.ts references every emitted type name in path records.
  const typeImport = input.types.length
    ? `import type { ${input.types.map((t) => t.emittedName).join(", ")} } from "./types.js";\n`
    : "";

  // Object types: object + array path records.
  for (const t of input.types) {
    const name = t.emittedName;
    // XPaths: "$" → X, "$.prop" → propType (one level).
    const objEntries: string[] = [`  "$": ${name};`];
    for (const p of t.properties) {
      objEntries.push(`  "$.${p.wireName}": ${tsTypeOfRef(p.typeRef, resolver)};`);
    }
    pathRecords.push(`export interface ${name}Paths {\n${objEntries.join("\n")}\n}`);

    // XArrayPaths: "$" → X[], "$[0]" → X, "$[0].prop" → propType, "$[*].prop" → propType[].
    const arrEntries: string[] = [`  "$": ${name}[];`, `  "$[0]": ${name};`];
    for (const p of t.properties) {
      const pt = tsTypeOfRef(p.typeRef, resolver);
      arrEntries.push(`  "$[0].${p.wireName}": ${pt};`, `  "$[*].${p.wireName}": ${pt}[];`);
    }
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
import { TrameCall, ExecutionMode } from "trame-client";
import type { TrameRequest, TrameMultiRequest, TrameResponse } from "trame-client";
${typeImport}
${pathRecords.join("\n\n")}

/** A map of valid result-relative $-paths to their extracted type, for a call. */
export type PathTypes = Record<string, unknown>;

/**
 * A typed single call wrapping a trame-client {@link TrameCall}. \`TPaths\` is
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
 * A call enrolled in a batch. \`exposes\` declares an alias the server will
 * resolve from this call's result; the alias type (\`TPaths[path]\`) is tracked
 * at compile time so \`alias("@x")\` returns the producer's exposed type.
 */
export class TypedRequest<T, TPaths = PathTypes, A extends Record<string, unknown> = {}> {
  /** @internal */ _call: TrameCall;
  constructor(call: TypedCall<T, TPaths>) { this._call = call._call; }
  /**
   * Declare that this call exposes \`path\` as \`alias\`. Compile-time-checked.
   * The wire \`dependencyMapping\` key is the alias **without** the leading \`@\`
   * (the server strips \`@\` from a consumer's \`@alias\` placeholder before
   * lookup — see TrameInvoker.ReplaceDependencyByAliasCore), so we strip it
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
   * \`TrameCall.withAlias("@x")\`, which sets \`data: "@x"\`). The compile-time type
   * is the producer's exposed type, so the consumer param typechecks.
   */
  alias<Aname extends string & keyof A>(name: Aname): A[Aname] {
    return name as unknown as A[Aname];
  }
  /** @internal */ toRequest(): TrameRequest { return this._call.toRequest(); }
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
  toMulti(): TrameMultiRequest {
    return TrameCall.batch(this._requests.map((r) => r.toRequest()), ExecutionMode.Serial);
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
  const classes = input.controllers.map((c) => emitControllerClass(c, resolver));
  return `// Auto-generated Trame controllers. Method names are camelCase; parameter
// names bind case-sensitively on the wire (keys passed verbatim to TrameCall).
import { TrameCall } from "trame-client";
import { TypedCall } from "./typed-call.js";
${typeImports ? typeImports + "\n" : ""}${pathImports ? pathImports + "\n" : ""}
${classes.join("\n\n")}
`;
}

function emitControllerClass(ctrl: ResolvedController, resolver: NamingResolver): string {
  const methods = ctrl.methods.map((m) => emitMethod(ctrl, m, resolver));
  return `export class ${ctrl.className} {
  /** @internal */ _build: (controller: string, method: string) => TrameCall;
  constructor(build: (controller: string, method: string) => TrameCall) {
    this._build = build;
  }
${methods.join("\n\n")}
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

function emitClient(input: EmitterInput, _resolver: NamingResolver): string {
  const imports = input.controllers.map((c) => `import { ${c.className} } from "./controllers.js";`).join("\n");
  const accessors = input.controllers.map((c) => `  readonly ${c.accessor}: ${c.className};`);
  const inits = input.controllers.map((c) => `    this.${c.accessor} = new ${c.className}(build);`);
  return `// Auto-generated root Trame client. Compose with the trame-client runtime.
import { TrameCall, TrameRestClient, ExecutionMode } from "trame-client";
import type { TrameRestClientOptions, TrameResponse } from "trame-client";
import { Batch, TypedCall } from "./typed-call.js";
${imports}

/** A TrameResponse whose \`data\` is narrowed to T (the wire shape is unchanged). */
export type TypedResponse<T> = TrameResponse & { data: T | null };

export class TrameClient {
  private readonly _rest: TrameRestClient;
${accessors.join("\n")}

  constructor(baseUrl: string, options: TrameRestClientOptions = {}) {
    this._rest = new TrameRestClient(baseUrl, options);
    const build = (controller: string, method: string) => TrameCall.init(controller, method);
${inits.join("\n")}
  }

  /** Execute a single typed call; \`response.data\` is narrowed to T. */
  async call<T, TPaths extends Record<string, unknown>>(call: TypedCall<T, TPaths>): Promise<TypedResponse<T>> {
    return (await this._rest.call(call.toRequest())) as TypedResponse<T>;
  }

  /** Execute a typed batch (Serial — required for @alias resolution). */
  async batch<A extends Record<string, unknown>>(b: Batch<A>): Promise<TrameResponse[]> {
    const multi = b.toMulti();
    return this._rest.callBatch(multi.requests, multi.mode);
  }

  /** The underlying REST client (escape hatch for raw calls). */
  get rest(): TrameRestClient { return this._rest; }
}
`;
}

// ---------------------------------------------------------------------------
// index.ts — re-exports.
// ---------------------------------------------------------------------------

function emitIndex(_input: EmitterInput): string {
  return `// Auto-generated barrel.
export * from "./types.js";
export * from "./typed-call.js";
export * from "./controllers.js";
export { TrameClient } from "./client.js";
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
      if (ref.element) collectRefs(ref.element, resolver, used);
      break;
    case "map":
      if (ref.key) collectRefs(ref.key, resolver, used);
      if (ref.value) collectRefs(ref.value, resolver, used);
      break;
    // scalar / opaque / void → nothing to import.
  }
}