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
// Enum refs: Sleipnir serializes enums as their underlying integer on the wire
// (no global JsonStringEnumConverter), so an enum usage is rendered as its
// numeric wire type: enum usages collapse to a wide integer scalar (`long`)
// that is lossless for every C# enum backing type. The collapsed scalar keeps
// the enum's registry key as `enumRef` (see `EnumAnnotatedTypeRef`), and the
// enum members are exposed as `EmitterInput.enums`, so an emitter can opt into
// an enum identity. The TypeScript emitter does (an `as const` object plus a
// literal-union type of the member values); the JS / C# / Python emitters
// ignore `enumRef` and keep rendering the numeric scalar.
//
// Sets and streams also collapse: JSON materializes both as arrays (the invoker
// consumes IAsyncEnumerable<T> into List<T>; STJ writes a HashSet<T> as a JSON
// array), so the client's deser target is an array.

import type {
  ControllerMeta,
  DiscoveryInfo,
  MethodMeta,
  ParameterMeta,
  PropertyMeta,
  TypeMeta,
  TypeRef,
} from "sleipnir-client";
import { toCamelCase } from "./casing.js";
import { NamingResolver } from "./naming.js";
import { csTypeOf, pyTypeOf, tsTypeOf } from "./scalars.js";

/** A reference to a type — the wire `TypeRef`, consumed directly (passthrough). */
export type ResolvedTypeRef = TypeRef;

/**
 * A collapsed enum usage: the numeric wire scalar, annotated with the enum's
 * registry key. Emitters that do not model enums read it as a plain scalar.
 */
export type EnumAnnotatedTypeRef = TypeRef & { enumRef?: string };

/** The enum registry key of a collapsed enum usage, or undefined for any other ref. */
export function enumRefOf(ref: ResolvedTypeRef): string | undefined {
  return ref.kind === "scalar" ? (ref as EnumAnnotatedTypeRef).enumRef : undefined;
}

export interface ResolvedEnumMember {
  /** Member name as declared in C# (`Shipped`). */
  name: string;
  /** Numeric wire value of the member. */
  value: number;
}

export interface ResolvedEnum {
  /** Registry key (the `types` key). */
  fullName: string;
  members: ResolvedEnumMember[];
}

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
  /** void / Task (no result) → the emitter still returns SleipnirResponse<unknown>. */
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
  /**
   * Enum types whose members all carry a finite numeric value. Usages of these
   * enums are scalars annotated with `enumRef`. Enums with a non-numeric member
   * value are left out (their usages stay plain numeric scalars).
   */
  enums: ResolvedEnum[];
  /** Raw discovery, retained for emitters that need the example payloads. */
  discovery: DiscoveryInfo;
}

/**
 * Passthrough/normalizer of the wire `TypeRef`. The producer already builds the
 * neutral IR; we only collapse enum refs to their numeric wire scalar so the
 * emitters never see an enum ref. When `enumIdentity` contains the enum's key,
 * the collapsed scalar is annotated with `enumRef` (see `EnumAnnotatedTypeRef`).
 * All other kinds pass through unchanged.
 */
export function resolveTypeRef(
  ref: TypeRef,
  enumKeys: ReadonlySet<string>,
  enumIdentity: ReadonlySet<string> = new Set(),
): ResolvedTypeRef {
  return normalizeRef(ref, enumKeys, enumIdentity);
}

/** Recursively collapse enum refs; recurse into element/key/value. */
function normalizeRef(
  ref: TypeRef,
  enumKeys: ReadonlySet<string>,
  enumIdentity: ReadonlySet<string>,
): ResolvedTypeRef {
  if (ref.kind === "ref" && ref.ref != null && enumKeys.has(ref.ref)) {
    // Enum serializes as its underlying integer on the wire; `long` is lossless
    // for every C# enum backing type (int/long/short/byte/…).
    const collapsed: EnumAnnotatedTypeRef = { kind: "scalar", name: "long", nullable: ref.nullable ?? undefined };
    if (enumIdentity.has(ref.ref)) collapsed.enumRef = ref.ref;
    return collapsed;
  }
  switch (ref.kind) {
    case "array":
    case "set":
    case "stream":
    case "event":
      // Events carry their payload as `element` (IObservable<T> → T); recurse so an
      // enum-typed event payload also collapses to its numeric wire scalar.
      return { ...ref, element: ref.element ? normalizeRef(ref.element, enumKeys, enumIdentity) : undefined };
    case "map":
      return {
        ...ref,
        key: ref.key ? normalizeRef(ref.key, enumKeys, enumIdentity) : undefined,
        value: ref.value ? normalizeRef(ref.value, enumKeys, enumIdentity) : undefined,
      };
    default:
      return ref;
  }
}

/** Enum bookkeeping threaded through the resolve walk. */
interface EnumSets {
  /** Every enum registry key (usages collapse to the numeric scalar). */
  keys: ReadonlySet<string>;
  /** Enum keys with a representable identity (usages also carry `enumRef`). */
  identity: ReadonlySet<string>;
}

/**
 * The members of an enum TypeMeta as numeric wire values, or undefined when any
 * member value is not a finite number (no identity can be emitted for it then).
 */
function resolveEnumMembers(tm: TypeMeta): ResolvedEnumMember[] | undefined {
  const members: ResolvedEnumMember[] = [];
  for (const m of tm.members ?? []) {
    if (typeof m.value !== "number" || !Number.isFinite(m.value)) return undefined;
    members.push({ name: m.name, value: m.value });
  }
  return members.length > 0 ? members : undefined;
}

/** Walk DiscoveryInfo once into a ResolvedEmitterInput. */
export function buildEmitterInput(
  discovery: DiscoveryInfo,
  resolver: NamingResolver,
): EmitterInput {
  // Register all *object* type names first so collision detection sees the full
  // set. Enum TypeMetas are not emitted as structured types (their usages
  // collapse to a numeric scalar); they are surfaced separately as `enums`.
  const enumKeys = new Set<string>();
  const enumIdentity = new Set<string>();
  const enums: ResolvedEnum[] = [];
  for (const [key, tm] of Object.entries(discovery.types)) {
    if ((tm as TypeMeta).kind === "enum") {
      enumKeys.add(key);
      const members = resolveEnumMembers(tm as TypeMeta);
      if (members) {
        enumIdentity.add(key);
        enums.push({ fullName: key, members });
      }
    } else {
      resolver.register(key);
    }
  }
  const sets: EnumSets = { keys: enumKeys, identity: enumIdentity };

  const types: ResolvedType[] = [];
  for (const [fullName, tm] of Object.entries(discovery.types)) {
    if ((tm as TypeMeta).kind === "enum") continue;
    types.push({
      fullName,
      emittedName: resolver.resolve(fullName),
      properties: ((tm as TypeMeta).properties ?? []).map((p) => resolveProperty(p, sets)),
    });
  }

  const controllers: ResolvedController[] = (discovery.controllers ?? []).map((c) =>
    resolveController(c, sets),
  );

  return { controllers, types, enums, discovery };
}

function resolveProperty(prop: PropertyMeta, sets: EnumSets): ResolvedProperty {
  return {
    wireName: toCamelCase(prop.propertyName),
    declaredName: prop.propertyName,
    typeRef: resolveTypeRef(prop.propertyType, sets.keys, sets.identity),
  };
}

function resolveController(ctrl: ControllerMeta, sets: EnumSets): ResolvedController {
  return {
    name: ctrl.name,
    accessor: toCamelCase(ctrl.name),
    className: ctrl.name.charAt(0).toUpperCase() + ctrl.name.slice(1) + "Client",
    methods: (ctrl.methods ?? []).map((m) => resolveMethod(ctrl.name, m, sets)),
  };
}

function resolveMethod(
  controllerName: string,
  method: MethodMeta,
  sets: EnumSets,
): ResolvedMethod {
  const isVoid = method.returnType?.kind === "void";
  return {
    methodName: method.methodName,
    emittedName: toCamelCase(method.methodName),
    controller: controllerName,
    parameters: (method.parameters ?? []).map((p) => resolveParameter(p, sets)),
    returnType: resolveTypeRef(method.returnType ?? { kind: "void" }, sets.keys, sets.identity),
    isVoid,
    documentation: method.documentation,
  };
}

function resolveParameter(param: ParameterMeta, sets: EnumSets): ResolvedParameter {
  return {
    name: param.parameterName,
    typeRef: resolveTypeRef(param.parameterType, sets.keys, sets.identity),
    defaultValue: param.defaultValue,
    documentation: param.documentation,
  };
}

/** The element TypeRef of an array/set/stream, or a fallback opaque ref. */
function elementOf(ref: ResolvedTypeRef): ResolvedTypeRef {
  return (ref as { element?: ResolvedTypeRef }).element ?? { kind: "opaque" };
}

/** True for a `[SleipnirEvent]` method — `returnType.kind === "event"` (IObservable<T>). */
export function isEventMethod(m: ResolvedMethod): boolean {
  return m.returnType.kind === "event";
}

/**
 * The pushed-payload TypeRef of an event method (the `T` in `IObservable<T>`).
 * Falls back to `opaque` for a malformed event ref with no element.
 */
export function eventPayloadRef(m: ResolvedMethod): ResolvedTypeRef {
  return (m.returnType as { element?: ResolvedTypeRef }).element ?? { kind: "opaque" };
}

/** True if any method across the input controllers is a server-push event. */
export function hasEvents(input: EmitterInput): boolean {
  return input.controllers.some((c) => c.methods.some(isEventMethod));
}

/** Options for {@link tsTypeOfRef}. */
export interface TsTypeOfRefOptions {
  /**
   * Render a collapsed enum usage (a scalar carrying `enumRef`) as the enum's
   * emitted type name instead of `number`. The resolver must have the enum key
   * registered. Used by the TS emitter; the JS emitter keeps `number`.
   */
  enumIdentity?: boolean;
}

/** TS type string for a resolved ref (used by the TS + JS emitters). */
export function tsTypeOfRef(ref: ResolvedTypeRef, resolver: NamingResolver, opts: TsTypeOfRefOptions = {}): string {
  const base = tsTypeOfRefInner(ref, resolver, opts);
  return ref.nullable ? `${base} | null` : base;
}

function tsTypeOfRefInner(ref: ResolvedTypeRef, resolver: NamingResolver, opts: TsTypeOfRefOptions): string {
  switch (ref.kind) {
    case "scalar": {
      const enumRef = opts.enumIdentity ? enumRefOf(ref) : undefined;
      return enumRef ? resolver.resolve(enumRef) : tsTypeOf(ref.name ?? "any");
    }
    // JSON materializes sets and streams as arrays — the deser target is T[].
    case "array":
    case "set":
    case "stream":
      return tsTypeOfRefInner(elementOf(ref), resolver, opts) + "[]";
    // Event: the payload type T of IObservable<T>. The generated client emits a
    // typed `subscribe<T>` surface (not a call), so the element is the handler's
    // `onNext` value type — a scalar, not an array.
    case "event":
      return tsTypeOfRefInner(elementOf(ref), resolver, opts);
    case "map":
      return `Record<string, ${tsTypeOfRefInner((ref as { value?: ResolvedTypeRef }).value ?? { kind: "opaque" }, resolver, opts)}>`;
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
    // Event: the pushed payload type T of IObservable<T>. The REST-only generated
    // C# client cannot subscribe (events are WS-only); the payload type is still
    // resolved so a TODO marker can name it.
    case "event": return csTypeOfRef(elementOf(ref), resolver);
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
    // Event: the pushed payload type T of IObservable<T>. The REST-only generated
    // Python client cannot subscribe (events are WS-only); the payload type is
    // still resolved so a TODO marker can name it.
    case "event":
      return pyTypeOfRefInner(elementOf(ref), resolver);
    case "map":
      return `dict[${pyTypeOfRefInner((ref as { key?: ResolvedTypeRef }).key ?? { kind: "scalar", name: "string" }, resolver)}, ${pyTypeOfRefInner((ref as { value?: ResolvedTypeRef }).value ?? { kind: "opaque" }, resolver)}]`;
    case "ref": return resolver.resolve(ref.ref ?? "");
    case "opaque": return "Any";
    case "void": return "None";
    default: return "Any";
  }
}