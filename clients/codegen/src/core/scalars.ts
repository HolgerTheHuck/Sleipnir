// Canonical scalar tables + per-language type maps.
//
// Single source of truth (replaces the duplicated maps in
// CodegenPage.svelte:14-41, EditorPane.svelte, ParamEditor.svelte). The
// name sets are lifted from SleipnirDeveloperUi/src/lib/utils/dependencyCheck.ts:69-95
// and are the superset incl. bigint/uint/ulong/short/byte/sbyte/ushort/version/
// timespan/dateonly/timeonly/datetimeoffset — they encode System.Text.Json's
// JSON-kind mapping.

const NUMBER_NAMES = new Set([
  "int", "int32", "int64", "long", "short", "byte", "sbyte",
  "uint", "ulong", "ushort", "double", "decimal", "float", "single", "number", "bigint",
]);
const BOOL_NAMES = new Set(["bool", "boolean"]);
const STRING_NAMES = new Set([
  "string", "guid", "datetime", "datetimeoffset", "dateonly", "timeonly",
  "timespan", "uri", "char", "version",
]);
const ANY_NAMES = new Set([
  "object", "dynamic", "jsonobject", "jsonelement", "jsonnode", "jsonvalue",
  "jsondocument", "dictionary", "idictionary", "expandoobject",
]);
// .NET value types (non-nullable). Missing value-type props silently default
// under duck-typed binding; flagging them is the Weak-mode insidious-case warning.
const VALUE_TYPE_NAMES = new Set([
  "int", "int32", "int64", "long", "short", "byte", "sbyte",
  "uint", "ulong", "ushort", "double", "decimal", "float", "single", "bigint",
  "bool", "boolean",
  "datetime", "datetimeoffset", "dateonly", "timeonly", "timespan", "guid", "char",
]);

export { NUMBER_NAMES, BOOL_NAMES, STRING_NAMES, ANY_NAMES, VALUE_TYPE_NAMES };

/** Lowercased trimmed key into the scalar tables. */
function key(name: string): string {
  return (name || "").toLowerCase().trim();
}

export function isNumberName(name: string): boolean { return NUMBER_NAMES.has(key(name)); }
export function isBoolName(name: string): boolean { return BOOL_NAMES.has(key(name)); }
export function isStringName(name: string): boolean { return STRING_NAMES.has(key(name)); }
export function isAnyName(name: string): boolean { return ANY_NAMES.has(key(name)); }
export function isValueTypeRef(name: string): boolean { return VALUE_TYPE_NAMES.has(key(name)); }

/** A primitive scalar (number/bool/string/bytes) — not `object`/`dynamic` and not a complex type. */
export function isScalar(name: string): boolean {
  return isNumberName(name) || isBoolName(name) || isStringName(name) || (name || "").toLowerCase().trim() === "bytes";
}

/** True for `void`/`Task` (no result) and the System.Threading.Tasks.Task envelopes without `T`. */
export function isVoidReturn(name: string): boolean {
  const k = key(name);
  return k === "void" || k === "task" || k === "valuetask";
}

/** TypeScript type string for a scalar name. Complex names fall back to `shortName`. */
export function tsTypeOf(name: string): string {
  const k = key(name);
  if (NUMBER_NAMES.has(k) || k === "bigint") return k === "bigint" ? "bigint" : "number";
  if (BOOL_NAMES.has(k)) return "boolean";
  if (STRING_NAMES.has(k)) return "string";
  if (k === "bytes") return "string"; // base64 on the wire
  if (ANY_NAMES.has(k)) return "unknown";
  if (k === "void") return "void";
  // Complex type name — caller resolves via NamingResolver; return the short name as-is.
  return name.includes(".") ? name.split(".").pop()! : name;
}

/** C# type string for a scalar name (Increment 2 emitter; canonical home regardless). */
export function csTypeOf(name: string): string {
  const k = key(name);
  const map: Record<string, string> = {
    "string": "string",
    "int": "int", "int32": "int", "int64": "long", "long": "long",
    "short": "short", "byte": "byte", "sbyte": "sbyte",
    "uint": "uint", "ulong": "ulong", "ushort": "ushort",
    "double": "double", "decimal": "decimal", "float": "float", "single": "float",
    "number": "double", "bigint": "long",
    "bool": "bool", "boolean": "bool",
    "datetime": "DateTime", "datetimeoffset": "DateTimeOffset",
    "dateonly": "DateOnly", "timeonly": "TimeOnly", "timespan": "TimeSpan",
    "guid": "Guid", "uri": "Uri", "char": "char", "version": "Version",
    "object": "object", "dynamic": "object",
    "bytes": "byte[]", // base64 on the wire → byte[]
  };
  if (map[k] !== undefined) return map[k];
  return name.includes(".") ? name.split(".").pop()! : name;
}

/** Python type string for a scalar name (Increment 2 emitter; canonical home). */
export function pyTypeOf(name: string): string {
  const k = key(name);
  if (k === "int" || k === "int32" || k === "int64" || k === "long" || k === "short" ||
      k === "byte" || k === "sbyte" || k === "uint" || k === "ulong" || k === "ushort" ||
      k === "bigint") return "int";
  if (k === "double" || k === "decimal" || k === "float" || k === "single" || k === "number") return "float";
  if (BOOL_NAMES.has(k)) return "bool";
  if (STRING_NAMES.has(k)) return "str";
  if (k === "bytes") return "bytes"; // base64 string on the wire → bytes
  if (ANY_NAMES.has(k)) return "Any";
  return name.includes(".") ? name.split(".").pop()! : name;
}

/** Default JSON value for a scalar name (lifted from params.ts:12-21). */
export function defaultValueForType(name: string): unknown {
  const k = key(name);
  if (BOOL_NAMES.has(k)) return false;
  if (NUMBER_NAMES.has(k)) return 0;
  if (STRING_NAMES.has(k)) return "";
  return null;
}