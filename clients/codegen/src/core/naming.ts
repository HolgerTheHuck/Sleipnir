// NamingResolver — maps full .NET type names to emitted identifier names,
// disambiguating collisions by prefixing parent namespace segments.
//
// Discovery keys `types` by full name (`MyApp.Foo.Order`) and carries short
// names via reflection (`Order`). Two distinct types can share a short name
// (`Foo.Order` + `Bar.Order`). Emitting both as `Order` would clash. The
// resolver registers every type up front and, on collision, prefixes parent
// namespace segments until the name is unique within the colliding set:
// `Foo.Order` + `Bar.Order` → `FooOrder` + `BarOrder`. The zero-collision
// case leaves names unchanged (`Order` stays `Order`), so Story-01 (no
// short-name collisions) emits identical names to before.

import type { TypeMeta } from "trame-client";
import { shortName } from "./casing.js";

export class NamingResolver {
  /** fullName → emitted name. */
  private readonly _names = new Map<string, string>();
  /** short name → the set of full names sharing it. */
  private readonly _byShort = new Map<string, Set<string>>();

  /** Register a type by its full name (idempotent). */
  register(fullName: string): void {
    if (this._names.has(fullName)) return;
    const short = shortName(fullName);
    let set = this._byShort.get(short);
    if (!set) { set = new Set(); this._byShort.set(short, set); }
    set.add(fullName);
  }

  /** Convenience: register every type in a discovery `types` map. */
  registerByTypeMeta(types: Record<string, TypeMeta>): void {
    for (const fullName of Object.keys(types)) this.register(fullName);
  }

  /** The emitted name for a full name. Must be registered first. */
  resolve(fullName: string): string {
    const cached = this._names.get(fullName);
    if (cached !== undefined) return cached;

    const short = shortName(fullName);
    const siblings = this._byShort.get(short) ?? new Set([fullName]);
    let name: string;
    if (siblings.size <= 1) {
      name = short;
    } else {
      name = this.disambiguate(fullName, short, siblings);
    }
    this._names.set(fullName, name);
    return name;
  }

  /** Short name (no prefix) when you know there's no collision. */
  short(fullName: string): string {
    return shortName(fullName);
  }

  /** All short names registered so far. */
  declaredTypes(): string[] {
    return [...this._byShort.keys()];
  }

  /**
   * Prefix parent segments of `fullName` until the candidate is unique among
   * the colliding `siblings`. `Foo.Bar.Order` → `BarOrder` → `FooBarOrder`.
   */
  private disambiguate(fullName: string, short: string, siblings: Set<string>): string {
    const parts = fullName.split(".");
    // parts[last] === short. Prepend parents from nearest outward.
    let name = short;
    for (let depth = 1; depth < parts.length; depth++) {
      const parent = parts[parts.length - 1 - depth];
      name = pascalConcat(parent) + name;
      // Unique if no other sibling produces the same candidate at this depth.
      let clashes = false;
      for (const other of siblings) {
        if (other === fullName) continue;
        if (candidateAtDepth(other, depth) === name) { clashes = true; break; }
      }
      if (!clashes) return name;
    }
    return name;
  }
}

/** The disambiguation candidate for `fullName` at `depth` prepended parents. */
function candidateAtDepth(fullName: string, depth: number): string {
  const parts = fullName.split(".");
  let name = parts[parts.length - 1];
  for (let d = 1; d <= depth && d < parts.length; d++) {
    name = pascalConcat(parts[parts.length - 1 - d]) + name;
  }
  return name;
}

/** Concatenate a parent segment onto a PascalCase name, preserving casing. */
function pascalConcat(segment: string): string {
  if (!segment) return "";
  return segment.charAt(0).toUpperCase() + segment.slice(1);
}