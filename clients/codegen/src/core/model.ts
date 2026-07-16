// EmitterInput — the resolved intermediate the emitters consume. The producer
// now emits a language-neutral `TypeRef` IR (docs/discovery-schema.md), so this
// layer is a *passthrough*: it walks the raw DiscoveryInfo once to apply the
// wire-correctness fixes (camelCase property names, enum-ref→scalar collapse,
// opaque handling) and keeps the language emitters thin.
//
// This is the layer that fixes CodegenPage.svelte's PascalCase-property bug:
// discovery carries PascalCase property names but the wire is camelCase, so
// every emitted property name runs through toCamelCase here.
//
// Enum refs: Trame serializes enums as their underlying integer on the wire
// (no global JsonStringEnumConverter), so an enum usage is rendered as its
// numeric wire type. The enum `TypeMeta` (with members) is still emitted by the
// producer for documentation/DevUI, but the codegen does not emit language-
// native enum declarations — enum usages collapse to a wide integer scalar
// (`long`) that is lossless for every C# enum backing type. The enum identity
// is therefore not preserved in generated clients; a future increment can emit
// native enums. Sets and streams also collapse: JSON materializes both as
// arrays (the invoker consumes IAsyncEnumerable<T> into List<T>; STJ writes a
// HashSet<T> as a JSON array), so the client's deser target is an array.

import type {
  ControllerMeta,
  DiscoveryInfo,
  MethodMeta,
  ParameterMeta,
  PropertyMeta,
  TypeMeta,
  TypeRef,
} from "trame-client";
import { toCamelCase } from "./casing.js";
import { NamingResolver } from "./naming.js";
import { csTypeOf, pyTypeOf, tsTypeOf } from "./scalars.js";

/** A reference to a type — the wire `TypeRef`, consumed directly (passthrough). */
export type ResolvedTypeRef = TypeRef;

export interface ResolvedProperty {
  /** camelCase wire name (matches the server's CamelCase policy). */
  wireName: string;
  /** Original PascalCase name from discovery (for comments / C# emitter). */
  declaredName: string;
  typeRef: ResolvedTypeRef;
  documentation?: string | null;
}

export interface ResolvedType {
  fullName: string;
  /** Emitted identifier (collision-disambiguated via NamingResolver). */
  emittedName: string;
  properties: ResolvedProperty[];
}

export interface ResolvedParameter {
  /** Parameter name — bound case-sensitively on the wire, kept as-is. */
  name: string;
  typeRef: ResolvedTypeRef;
  /** C# default value (compile-time constant), or null/absent when none. */
  defaultValue?: unknown;
  documentation?: string | null;
}

export interface ResolvedMethod {
  methodName: string;
  /** camelCase emitted method name (`GetById` → `getById`). */
  emittedName: string;
  controller: string;
  parameters: ResolvedParameter[];
  returnType: ResolvedTypeRef;
  /** void / Task (no result) → the emitter still returns TrameResponse<unknown>. */
  isVoid: boolean;
  documentation?: string | null;
}

export interface ResolvedController {
  name: string;
  /** camelCase accessor name on the root client (`Order` → `order`). */
  accessor: string;
  /** PascalCase emitted class name (`Order` → `OrderClient`). */
  className: string;
  methods: ResolvedMethod[];
}

export interface EmitterInput {
  controllers: ResolvedController[];
  types: ResolvedType[];
  /** Raw discovery, retained for emitters that need the example payloads. */
  discovery: DiscoveryInfo;
}

/**
 * Passthrough/normalizer of the wire `TypeRef`. The producer already builds the
 * neutral IR; we only collapse enum refs to their numeric wire scalar so the
 * emitters never see an enum ref (and need no enum-type plumbing). All other
 * kinds pass through unchanged.
 */
export function resolveTypeRef(
  ref: TypeRef,
  enumKeys: ReadonlySet<string>,
): ResolvedTypeRef {
  return normalizeRef(ref, enumKeys);
}

/** Recursively collapse enum refs; recurse into element/key/value. */
function normalizeRef(ref: TypeRef, enumKeys: ReadonlySet<string>): ResolvedTypeRef {
  if (ref.kind === "ref" && ref.ref != null && enumKeys.has(ref.ref)) {
    // Enum serializes as its underlying integer on the wire; `long` is lossless
    // for every C# enum backing type (int/long/short/byte/…).
    return { kind: "scalar", name: "long", nullable: ref.nullable ?? undefined };
  }
  switch (ref.kind) {
    case "array":
    case "set":
    case "stream":
      return { ...ref, element: ref.element ? normalizeRef(ref.element, enumKeys) : undefined };
    case "map":
      return {
        ...ref,
        key: ref.key ? normalizeRef(ref.key, enumKeys) : undefined,
        value: ref.value ? normalizeRef(ref.value, enumKeys) : undefined,
      };
    default:
      return ref;
  }
}

/** Walk DiscoveryInfo once into a ResolvedEmitterInput. */
export function buildEmitterInput(
  discovery: DiscoveryInfo,
  resolver: NamingResolver,
): EmitterInput {
  // Register all *object* type names first so collision detection sees the full
  // set. Enum TypeMetas stay in discovery.types for documentation but are not
  // emitted as structured types (their usages collapse to a numeric scalar).
  const enumKeys = new Set<string>();
  for (const [key, tm] of Object.entries(discovery.types)) {
    if ((tm as TypeMeta).kind === "enum") enumKeys.add(key);
    else resolver.register(key);
  }

  const types: ResolvedType[] = [];
  for (const [fullName, tm] of Object.entries(discovery.types)) {
    if ((tm as TypeMeta).kind === "enum") continue;
    types.push({
      fullName,
      emittedName: resolver.resolve(fullName),
      properties: ((tm as TypeMeta).properties ?? []).map((p) => resolveProperty(p, enumKeys)),
    });
  }

  const controllers: ResolvedController[] = (discovery.controllers ?? []).map((c) =>
    resolveController(c, enumKeys),
  );

  return { controllers, types, discovery };
}

function resolveProperty(prop: PropertyMeta, enumKeys: ReadonlySet<string>): ResolvedProperty {
  return {
    wireName: toCamelCase(prop.propertyName),
    declaredName: prop.propertyName,
    typeRef: resolveTypeRef(prop.propertyType, enumKeys),
  };
}

function resolveController(ctrl: ControllerMeta, enumKeys: ReadonlySet<string>): ResolvedController {
  return {
    name: ctrl.name,
    accessor: toCamelCase(ctrl.name),
    className: ctrl.name.charAt(0).toUpperCase() + ctrl.name.slice(1) + "Client",
    methods: (ctrl.methods ?? []).map((m) => resolveMethod(ctrl.name, m, enumKeys)),
  };
}

function resolveMethod(
  controllerName: string,
  method: MethodMeta,
  enumKeys: ReadonlySet<string>,
): ResolvedMethod {
  const isVoid = method.returnType?.kind === "void";
  return {
    methodName: method.methodName,
    emittedName: toCamelCase(method.methodName),
    controller: controllerName,
    parameters: (method.parameters ?? []).map((p) => resolveParameter(p, enumKeys)),
    returnType: resolveTypeRef(method.returnType ?? { kind: "void" }, enumKeys),
    isVoid,
    documentation: method.documentation,
  };
}

function resolveParameter(param: ParameterMeta, enumKeys: ReadonlySet<string>): ResolvedParameter {
  return {
    name: param.parameterName,
    typeRef: resolveTypeRef(param.parameterType, enumKeys),
    defaultValue: param.defaultValue,
    documentation: param.documentation,
  };
}

/** The element TypeRef of an array/set/stream, or a fallback opaque ref. */
function elementOf(ref: ResolvedTypeRef): ResolvedTypeRef {
  return (ref as { element?: ResolvedTypeRef }).element ?? { kind: "opaque" };
}

/** TS type string for a resolved ref (used by the TS + JS emitters). */
export function tsTypeOfRef(ref: ResolvedTypeRef, resolver: NamingResolver): string {
  const base = tsTypeOfRefInner(ref, resolver);
  return ref.nullable ? `${base} | null` : base;
}

function tsTypeOfRefInner(ref: ResolvedTypeRef, resolver: NamingResolver): string {
  switch (ref.kind) {
    case "scalar": return tsTypeOf(ref.name ?? "any");
    // JSON materializes sets and streams as arrays — the deser target is T[].
    case "array":
    case "set":
    case "stream":
      return tsTypeOfRefInner(elementOf(ref), resolver) + "[]";
    case "map":
      return `Record<string, ${tsTypeOfRefInner((ref as { value?: ResolvedTypeRef }).value ?? { kind: "opaque" }, resolver)}>`;
    case "ref": return resolver.resolve(ref.ref ?? "");
    case "opaque": return "unknown";
    case "void": return "void";
    default: return "unknown";
  }
}

/** C# type string for a resolved ref (used by the C# emitter). */
export function csTypeOfRef(ref: ResolvedTypeRef, resolver: NamingResolver): string {
  switch (ref.kind) {
    case "scalar": return csTypeOf(ref.name ?? "object");
    case "array": return `List<${csTypeOfRef(elementOf(ref), resolver)}>`;
    case "set": return `HashSet<${csTypeOfRef(elementOf(ref), resolver)}>`;
    // stream: the invoker materializes IAsyncEnumerable<T> to a list before
    // serialization, so the client receives a JSON array → List<T>.
    case "stream": return `List<${csTypeOfRef(elementOf(ref), resolver)}>`;
    case "map":
      return `Dictionary<${csTypeOfRef((ref as { key?: ResolvedTypeRef }).key ?? { kind: "scalar", name: "string" }, resolver)}, ${csTypeOfRef((ref as { value?: ResolvedTypeRef }).value ?? { kind: "opaque" }, resolver)}>`;
    case "ref": return resolver.resolve(ref.ref ?? "");
    case "opaque": return "object";
    case "void": return "void";
    default: return "object";
  }
}

/** Python type string for a resolved ref (used by the Python emitter). */
export function pyTypeOfRef(ref: ResolvedTypeRef, resolver: NamingResolver): string {
  const base = pyTypeOfRefInner(ref, resolver);
  return ref.nullable ? `Optional[${base}]` : base;
}

function pyTypeOfRefInner(ref: ResolvedTypeRef, resolver: NamingResolver): string {
  switch (ref.kind) {
    case "scalar": return pyTypeOf(ref.name ?? "Any");
    case "array":
    case "set":
    case "stream":
      return `list[${pyTypeOfRefInner(elementOf(ref), resolver)}]`;
    case "map":
      return `dict[${pyTypeOfRefInner((ref as { key?: ResolvedTypeRef }).key ?? { kind: "scalar", name: "string" }, resolver)}, ${pyTypeOfRefInner((ref as { value?: ResolvedTypeRef }).value ?? { kind: "opaque" }, resolver)}]`;
    case "ref": return resolver.resolve(ref.ref ?? "");
    case "opaque": return "Any";
    case "void": return "None";
    default: return "Any";
  }
}