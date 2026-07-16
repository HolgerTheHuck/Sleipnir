// Casing helpers — the load-bearing wire-correctness layer.
//
// `toCamelCase` mirrors `System.Text.Json.JsonNamingPolicy.CamelCase` exactly
// (ported verbatim from TrameDeveloperUi/src/lib/utils/params.ts:46-56). Trame
// writes object value properties camelCase on the wire, so generated property
// names MUST match this transform or deserialization silently binds nothing.

/**
 * Convert a .NET PascalCase / acronym-laden identifier to camelCase, matching
 * `System.Text.Json.JsonNamingPolicy.CamelCase` on the server. Rules:
 *  - starts lowercase → unchanged (already camelCase);
 *  - leading run of uppercase letters is lowercased; if that run is longer
 *    than one char AND is followed by a lowercase char, the last uppercase
 *    char stays uppercase (`ID` → `id`, `IPAddress` → `ipAddress`, `Id` → `id`).
 */
export function toCamelCase(name: string): string {
  if (!name) return name;
  const chars = [...name];
  let i = 0;
  while (i < chars.length && chars[i] >= "A" && chars[i] <= "Z") i++;
  if (i === 0) return name; // starts lowercase → unchanged
  let end = i;
  if (i > 1 && i < chars.length && chars[i] >= "a" && chars[i] <= "z") end = i - 1;
  for (let k = 0; k < end; k++) chars[k] = chars[k].toLowerCase();
  return chars.join("");
}

/** Last `.`-segment of a full type name ("MyApp.Foo.Order" → "Order"). */
export function shortName(fullName: string): string {
  return fullName.includes(".") ? fullName.split(".").pop()! : fullName;
}

/** PascalCase ("order" → "Order", "orderLineItem" → "OrderLineItem") for emitted class names. */
export function pascalCase(name: string): string {
  if (!name) return name;
  // Capitalize the first code point; leave the rest untouched so that already
  // PascalCase names and acronym-prefixed names stay readable.
  return name.charAt(0).toUpperCase() + name.slice(1);
}