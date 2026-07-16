// Shared helpers for parameter inputs in the DevUI.
//
// Replaces the former .NET-name-string-switching with structured TypeRef.kind
// (docs/discovery-schema.md). The codegen scalar tables are the single source of
// truth — imported from trame-codegen — so the DevUI and the client generator never
// diverge on the type system. The local string-parsing copies that used to live in
// EditorPane.svelte, tabs.svelte.ts and ParamEditor.svelte are consolidated here.

import type { DiscoveryInfo, ParameterMeta, TypeRef } from 'trame-client';
import {
  defaultValueForType,
  isNumberName,
  isBoolName,
  toCamelCase,
} from 'trame-codegen';

export { toCamelCase };

/** Render a TypeRef as a readable, language-neutral type string for the UI. */
export function displayType(ref: TypeRef | null | undefined): string {
  if (!ref) return '';
  switch (ref.kind) {
    case 'scalar': return ref.name ?? 'scalar';
    case 'array':
    case 'set':
    case 'stream': return `${displayType(ref.element)}[]`;
    case 'map': return `map<${displayType(ref.key)}, ${displayType(ref.value)}>`;
    case 'ref': return shortName(ref.ref);
    case 'opaque': return ref.nativeName ?? 'opaque';
    case 'void': return 'void';
    default: return ref.kind;
  }
}

function shortName(full: string | undefined): string {
  if (!full) return '';
  const i = full.lastIndexOf('.');
  return i >= 0 ? full.slice(i + 1) : full;
}

/** True for array/set/stream (the collection kinds that JSON materializes as arrays). */
export function isCollectionRef(ref: TypeRef | null | undefined): boolean {
  return !!ref && (ref.kind === 'array' || ref.kind === 'set' || ref.kind === 'stream');
}

/** Resolve a ref to its object TypeMeta, or null (enums/opaque/unresolved → null). */
function objectMetaOf(ref: TypeRef | null | undefined, discovery: DiscoveryInfo | null) {
  if (!ref || ref.kind !== 'ref' || !discovery) return null;
  const tm = discovery.types[ref.ref ?? ''];
  return tm && tm.kind === 'object' ? tm : null;
}

/** The object TypeMeta a parameter TypeRef resolves to — for a collection, the element's. */
function targetObjectMeta(ref: TypeRef | null | undefined, discovery: DiscoveryInfo | null) {
  return objectMetaOf(isCollectionRef(ref) ? (ref?.element ?? null) : ref, discovery);
}

/** True when the ref (or its collection element) resolves to an expandable object type. */
export function isObjectParam(ref: TypeRef | null | undefined, discovery: DiscoveryInfo | null): boolean {
  return !!targetObjectMeta(ref, discovery);
}

/** Property count of the object a ref (or collection element) resolves to — for textarea sizing. */
export function objectPropertyCount(ref: TypeRef | null | undefined, discovery: DiscoveryInfo | null): number {
  return targetObjectMeta(ref, discovery)?.properties?.length ?? 0;
}

/** True when the ref is a scalar bool. */
export function isBoolParam(ref: TypeRef | null | undefined): boolean {
  return !!ref && ref.kind === 'scalar' && isBoolName(ref.name ?? '');
}

/** True when the ref is a scalar number (int/long/double/decimal/…). */
export function isNumberParam(ref: TypeRef | null | undefined): boolean {
  return !!ref && ref.kind === 'scalar' && isNumberName(ref.name ?? '');
}

/** Default JSON value for a TypeRef (scalar → primitive default; array → []; map → {}; object ref → expanded template; else null). */
export function defaultValueForRef(ref: TypeRef, discovery: DiscoveryInfo | null, depth = 0): unknown {
  switch (ref.kind) {
    case 'scalar': return defaultValueForType(ref.name ?? '');
    case 'array':
    case 'set':
    case 'stream': return [];
    case 'map': return {};
    case 'ref': {
      const tm = objectMetaOf(ref, discovery);
      if (!tm || !tm.properties?.length || depth > 5) return null;
      const obj: Record<string, unknown> = {};
      for (const prop of tm.properties)
        obj[prop.propertyName] = defaultValueForRef(prop.propertyType, discovery, depth + 1);
      return obj;
    }
    default: return null; // opaque, void, unknown
  }
}

/** Default value for a parameter — honors an explicit C# default, else the TypeRef default. */
export function defaultValueForParam(param: ParameterMeta, discovery: DiscoveryInfo | null): unknown {
  if (param.defaultValue !== undefined && param.defaultValue !== null) return param.defaultValue;
  return defaultValueForRef(param.parameterType, discovery);
}

/** Coerce a raw input string to the wire scalar value for a TypeRef (number/bool), else keep the string. */
export function inferValue(input: string, ref: TypeRef | null | undefined): unknown {
  if (!ref || ref.kind !== 'scalar') return input;
  const name = (ref.name ?? '').toLowerCase();
  if (isNumberName(name)) {
    const n = Number(input);
    return Number.isNaN(n) ? input : n;
  }
  if (isBoolName(name)) {
    if (input.toLowerCase() === 'true') return true;
    if (input.toLowerCase() === 'false') return false;
  }
  return input;
}

/** Serialize an editor value to the native JSON wire value for a TypeRef. */
export function serializeValueByRef(value: unknown, ref: TypeRef, discovery: DiscoveryInfo | null): unknown {
  switch (ref.kind) {
    case 'scalar': {
      const name = (ref.name ?? '').toLowerCase();
      if (name === 'string') return value ?? '';
      if (isBoolName(name)) return value === '' ? false : !!value;
      if (isNumberName(name)) {
        const n = value === '' ? 0 : Number(value);
        return Number.isNaN(n) ? 0 : n;
      }
      return value ?? null;
    }
    case 'array':
    case 'set':
    case 'stream': {
      let v = value;
      if (typeof v === 'string') {
        try { v = JSON.parse(v); } catch { v = []; }
      }
      return Array.isArray(v) ? v : (v == null ? [] : v);
    }
    case 'map':
    case 'ref': {
      const props = ref.kind === 'ref' ? (objectMetaOf(ref, discovery)?.properties ?? []) : [];
      let v = value;
      if (typeof v === 'string') {
        try { v = JSON.parse(v); } catch { v = {}; }
      }
      if (v === null || typeof v !== 'object' || Array.isArray(v)) v = {};
      const obj = v as Record<string, unknown>;
      for (const prop of props) {
        if (!(prop.propertyName in obj) || obj[prop.propertyName] === '')
          obj[prop.propertyName] = defaultValueForRef(prop.propertyType, discovery);
      }
      return obj;
    }
    default:
      return value ?? null;
  }
}

/** Default literal value (string form) for a dependency-builder param input. */
export function defaultLiteralValue(ref: TypeRef, discovery: DiscoveryInfo | null): string {
  const v = defaultValueForRef(ref, discovery);
  if (typeof v === 'string') return v;
  return v == null ? '' : JSON.stringify(v, null, 2);
}

/** JsonPath suggestions for an Expose field against a method return TypeRef. */
export function jsonPathSuggestions(ref: TypeRef | null | undefined, discovery: DiscoveryInfo | null): string[] {
  const opts: string[] = ['$'];
  if (!ref) return opts;
  const isList = isCollectionRef(ref);
  const prefix = isList ? '$[0].' : '$.';
  if (isList) opts.push('$[0]');
  const tm = targetObjectMeta(ref, discovery);
  if (tm) {
    for (const p of tm.properties ?? []) opts.push(`${prefix}${toCamelCase(p.propertyName)}`);
  }
  return opts;
}