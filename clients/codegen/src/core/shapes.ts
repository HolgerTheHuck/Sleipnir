// Shape model — the type-shape primitives consumed by the DevUI dependency
// checker (parsePath/evalPath/compatible/checkSteps stay in the DevUI for now;
// they import these primitives). This file is the canonical home for the shape
// model. JSON-kind mapping mirrors System.Text.Json:
//   numbers → number, bool → boolean, string/Guid/DateTime/Uri/char/bytes → string,
//   object/dynamic/JsonElement/opaque → unknown (acceptsAny), enums → number
//   (Sleipnir serializes enums as their underlying integer), maps → object (acceptsAny),
//   sets/streams → array (JSON materializes both as arrays).
//
// Sourced from the producer's structured `TypeRef` (docs/discovery-schema.md),
// not from .NET type-name strings — no string re-parsing.

import type {
  DiscoveryInfo,
  MethodMeta,
  ParameterMeta,
  PropertyMeta,
  TypeMeta,
  TypeRef,
} from "sleipnir-client";
import { toCamelCase } from "./casing.js";
import {
  BOOL_NAMES,
  NUMBER_NAMES,
  STRING_NAMES,
} from "./scalars.js";

export type JsonKind = "unknown" | "null" | "boolean" | "number" | "string" | "object" | "array";

export interface TypeShape {
  kind: JsonKind;
  /** For array/set/stream: element shape. */
  element?: TypeShape;
  /** For object: TypeMeta for the property walk (null when opaque). */
  typeMeta?: TypeMeta | null;
  /** .NET friendly name / nativeName (for messages). */
  display?: string;
  /** True when the target type accepts arbitrary JSON (object/dynamic/JsonElement/opaque/map). */
  acceptsAny?: boolean;
}

/** Lookup a TypeMeta by its registry key (the opaque producer-chosen id). */
export function lookupTypeMeta(discovery: DiscoveryInfo | null, key: string): TypeMeta | null {
  if (!discovery) return null;
  return discovery.types[key] ?? null;
}

/** Find a property by its camelCase wire name (case-sensitive against the wire document). */
export function findProperty(tm: TypeMeta, name: string): PropertyMeta | undefined {
  // The server serializes camelCase and JsonPath.Net evaluates case-sensitively,
  // so a PascalCase path (`$.Id`) matches nothing at runtime. Compare against
  // toCamelCase(propertyName) to avoid a false-green that fails at runtime.
  return (tm.properties ?? []).find((p) => toCamelCase(p.propertyName) === name);
}

/** Construct a TypeShape from a wire `TypeRef`. */
export function shapeFromRef(ref: TypeRef, discovery: DiscoveryInfo | null): TypeShape {
  switch (ref.kind) {
    case "scalar":
      return scalarShape(ref.name ?? "", ref);
    case "array":
    case "set":
    case "stream":
      // JSON materializes sets/streams as arrays; the element shape is shared.
      return { kind: "array", element: shapeFromRef(ref.element ?? { kind: "opaque" }, discovery), display: displayOf(ref) };
    case "map":
      // A map is a JSON object of dynamic keys; the checker treats it as opaque-object.
      return { kind: "object", acceptsAny: true, display: displayOf(ref) };
    case "ref": {
      const tm = lookupTypeMeta(discovery, ref.ref ?? "");
      // Enum refs serialize as their underlying integer on the wire.
      if (tm && tm.kind === "enum") return { kind: "number", display: ref.ref };
      if (tm) return { kind: "object", typeMeta: tm, display: ref.ref };
      return { kind: "unknown", display: ref.ref };
    }
    case "opaque":
      return { kind: "unknown", acceptsAny: true, display: ref.nativeName ?? "opaque" };
    case "void":
      return { kind: "null", display: "void" };
    default:
      return { kind: "unknown", display: displayOf(ref) };
  }
}

function scalarShape(name: string, ref: TypeRef): TypeShape {
  const k = (name || "").toLowerCase().trim();
  if (NUMBER_NAMES.has(k)) return { kind: "number", display: name };
  if (BOOL_NAMES.has(k)) return { kind: "boolean", display: name };
  if (STRING_NAMES.has(k) || k === "bytes") return { kind: "string", display: name };
  if (k === "any" || k === "object" || k === "dynamic") return { kind: "unknown", acceptsAny: true, display: name };
  return { kind: "unknown", display: name };
}

function displayOf(ref: TypeRef): string {
  return ref.nativeName ?? ref.name ?? ref.ref ?? ref.kind;
}

/** Shape of a method's return value (null for void / no method). */
export function returnShape(methodMeta: MethodMeta | null, discovery: DiscoveryInfo | null): TypeShape | null {
  if (!methodMeta) return null;
  const rt = methodMeta.returnType;
  if (!rt || rt.kind === "void") return null;
  return shapeFromRef(rt, discovery);
}

/** Shape of a consumer parameter (target of an @alias). */
export function paramShape(param: ParameterMeta, discovery: DiscoveryInfo | null): TypeShape {
  return shapeFromRef(param.parameterType, discovery);
}

/** Shape of an object property. */
export function propertyShape(prop: PropertyMeta, discovery: DiscoveryInfo | null): TypeShape {
  return shapeFromRef(prop.propertyType, discovery);
}