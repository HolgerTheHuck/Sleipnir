// Casing helpers — the load-bearing wire-correctness layer.
//
// `toCamelCase` mirrors `System.Text.Json.JsonNamingPolicy.CamelCase` exactly
// (ported verbatim from SleipnirDeveloperUi/src/lib/utils/params.ts:46-56). Sleipnir
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
  const name = normalizeTypeKey(fullName);
  return name.includes(".") ? name.split(".").pop()! : name;
}

/**
 * Defensive normalization of a `discovery.types` key. Current servers flatten closed
 * generics to `{Namespace}.{Name}Of{Arg1}And{Arg2}` (`MyApp.PageOfTicketDto`); older
 * servers emitted the raw CLR `FullName`, i.e. the assembly-qualified generic form
 * "MyApp.Page`1[[MyApp.TicketDto, MyApp, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null]]".
 * That form is rewritten to the same flat name the server produces today; every other
 * key is returned unchanged. Rules (identical to SleipnirDiscoveryService.MangledName and
 * Casing.NormalizeTypeKey in Sleipnir.Codegen.Core): arguments by short name in
 * declaration order, joined with `And`; `T[]` → `TArray`; arity markers dropped.
 */
export function normalizeTypeKey(key: string): string {
  if (!key || (key.indexOf("`") < 0 && key.indexOf("[") < 0)) return key;
  try {
    const p = new ClrNameParser(key);
    const t = p.parseType();
    return t.prefix + mangle(t);
  } catch {
    return key; // not a CLR name we understand — leave untouched
  }
}

interface ClrType { prefix: string; name: string; ownArity: number; args: ClrType[]; arrayRank: number }

function mangle(t: ClrType): string {
  const own = t.ownArity >= 0 ? t.args.slice(t.args.length - Math.min(t.ownArity, t.args.length)) : t.args;
  let s = t.name;
  if (own.length > 0) s += "Of" + own.map(mangle).join("And");
  for (let i = 0; i < t.arrayRank; i++) s += "Array";
  return s;
}

class ClrNameParser {
  private i = 0;
  constructor(private readonly s: string) {}

  parseType(): ClrType {
    const start = this.i;
    while (this.i < this.s.length && !"[],".includes(this.s[this.i])) this.i++;
    const ident = this.s.slice(start, this.i).trim();
    if (!ident) throw new Error("empty type name");
    const args: ClrType[] = [];
    let arrayRank = 0;
    while (this.peek() === "[") {
      const next = this.s[this.i + 1];
      if (next === "]" || next === ",") {
        // array suffix: [] or [,]
        this.i++;
        while (this.peek() === ",") this.i++;
        this.expect("]");
        arrayRank++;
      } else {
        if (args.length > 0 || arrayRank > 0) throw new Error("unexpected generic argument list");
        this.i++; // [
        for (;;) {
          if (this.peek() === "[") {
            this.i++;
            args.push(this.parseType());
            this.skipAssemblyQualifier();
            this.expect("]");
          } else {
            args.push(this.parseType());
          }
          if (this.peek() === ",") { this.i++; continue; }
          break;
        }
        this.expect("]");
      }
    }
    // Split namespace / enclosing types from the own name; drop arity markers.
    const cut = Math.max(ident.lastIndexOf("."), ident.lastIndexOf("+"));
    const prefix = ident.slice(0, cut + 1).replace(/`\d+/g, "");
    const last = ident.slice(cut + 1);
    const m = /^(.*?)`(\d+)$/.exec(last);
    const nested = ident.includes("+");
    return {
      prefix,
      name: m ? m[1] : last,
      ownArity: nested ? (m ? Number(m[2]) : 0) : -1,
      args,
      arrayRank,
    };
  }

  /** Skips `, Assembly, Version=…, Culture=…, PublicKeyToken=…` up to the closing bracket. */
  private skipAssemblyQualifier(): void {
    while (this.i < this.s.length && this.s[this.i] !== "]") this.i++;
  }

  private peek(): string | undefined { return this.s[this.i]; }

  private expect(c: string): void {
    if (this.s[this.i] !== c) throw new Error(`expected '${c}' at ${this.i}`);
    this.i++;
  }
}

/** PascalCase ("order" → "Order", "orderLineItem" → "OrderLineItem") for emitted class names. */
export function pascalCase(name: string): string {
  if (!name) return name;
  // Capitalize the first code point; leave the rest untouched so that already
  // PascalCase names and acronym-prefixed names stay readable.
  return name.charAt(0).toUpperCase() + name.slice(1);
}