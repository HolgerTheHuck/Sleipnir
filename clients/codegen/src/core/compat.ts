// Contract compatibility check — TS port of Sleipnir.Codegen.Core/ContractCompatibility.cs.
// Classifies every difference between a baseline contract (what clients in the field were built
// against) and the current contract (what the server serves) as breaking / soft / safe for the
// wire. Spec: CONTRACT_EVOLUTION.md. Both engines run the same executable spec
// (test/fixtures/compat/cases.json) and must agree on every field, message included.
//
// Every check is "does the ACCEPTOR take everything the PRODUCER may send?". Inputs (parameters,
// request DTOs) are produced by the client and accepted by the server; outputs (returns, event
// payloads) the other way round. The rollback direction is the same pass with the two contracts
// swapped. Types compare structurally, never by registry key. Output is ASCII-only.

import type { DiscoveryInfo, TypeRef, TypeMeta } from "sleipnir-client";
import { assertDiscoveryShape } from "./discovery.js";

export type CompatSeverity = "breaking" | "soft" | "safe";
export type CompatDirection = "old-clients" | "rollback";

export interface ContractChange {
  id: string;
  severity: CompatSeverity;
  site: string;
  position: "input" | "output" | null;
  direction: CompatDirection;
  message: string;
}

export interface CompatReport {
  summary: { breaking: number; soft: number; safe: number };
  changes: ContractChange[];
}

type Position = "input" | "output";
type Ref = TypeRef & Record<string, unknown>;

interface Side {
  controllers: { name: string; methods: Map<string, Record<string, unknown>>; order: string[] }[];
  types: Map<string, TypeMeta>; // keys compared case-insensitively (lower-cased)
}

/**
 * Compare `baseline` (the contract deployed clients were built against) with `current` (what the
 * server now serves). With `includeRollback` the reverse pass (current clients against the
 * baseline server) is added; its safe changes are omitted.
 */
export function compareContracts(baseline: unknown, current: unknown, includeRollback = false): CompatReport {
  const base = toSide(assertDiscoveryShape(baseline));
  const head = toSide(assertDiscoveryShape(current));

  const changes: ContractChange[] = [];
  new Pass(base, head, "old-clients", changes).run();
  if (includeRollback) new Pass(head, base, "rollback", changes).run();

  changes.sort((a, b) =>
    rank(a.severity) - rank(b.severity)
    || ordinal(a.site, b.site)
    || ordinal(a.id, b.id)
    || ordinal(a.direction, b.direction));

  return {
    summary: {
      breaking: changes.filter((c) => c.severity === "breaking").length,
      soft: changes.filter((c) => c.severity === "soft").length,
      safe: changes.filter((c) => c.severity === "safe").length,
    },
    changes,
  };
}

/** Human-readable report, one line per change, most severe first (same as the C# ToText). */
export function formatCompatReport(report: CompatReport): string {
  const { breaking, soft, safe } = report.summary;
  let out = `Contract compatibility: ${breaking} breaking, ${soft} soft, ${safe} safe\n`;
  for (const c of report.changes) {
    out += `  ${c.severity.toUpperCase().padEnd(8)} ${c.site}  [${c.id}${c.direction === "rollback" ? ", rollback" : ""}]  ${c.message}\n`;
  }
  return out;
}

const rank = (s: CompatSeverity): number => (s === "breaking" ? 0 : s === "soft" ? 1 : 2);
const ordinal = (a: string, b: string): number => (a < b ? -1 : a > b ? 1 : 0);

function toSide(d: DiscoveryInfo): Side {
  const controllers = d.controllers.map((c) => {
    const methods = new Map<string, Record<string, unknown>>();
    const order: string[] = [];
    for (const m of c.methods) {
      methods.set(m.methodName, m as unknown as Record<string, unknown>);
      order.push(m.methodName);
    }
    return { name: c.name, methods, order };
  });
  const types = new Map<string, TypeMeta>();
  for (const [key, meta] of Object.entries(d.types ?? {})) types.set(key.toLowerCase(), meta);
  return { controllers, types };
}

interface Param {
  name: string;
  index: number;
  type: Ref;
  def: unknown;
  optional: boolean;
}

class Pass {
  // Ref pairs on the current recursion path — guards cycles only, so a type reached via two
  // properties (Billing/Shipping: Address) reports both sites.
  private readonly visiting = new Set<string>();

  constructor(
    private readonly client: Side,
    private readonly server: Side,
    private readonly direction: CompatDirection,
    private readonly out: ContractChange[],
  ) {}

  private add(id: string, severity: CompatSeverity, site: string, position: Position | null, message: string): void {
    // The rollback pass reports only what can go wrong.
    if (this.direction === "rollback" && severity === "safe") return;
    this.out.push({ id, severity, site, position, direction: this.direction, message });
  }

  run(): void {
    for (const c of this.client.controllers) {
      const s = this.server.controllers.find((x) => x.name === c.name);
      if (!s) {
        this.add("controller-removed", "breaking", c.name, null,
          `Controller '${c.name}' does not exist on the server - every call to it fails (404).`);
        continue;
      }
      for (const methodName of c.order) {
        const route = `${c.name}.${methodName}`;
        const serverMethod = s.methods.get(methodName);
        if (!serverMethod) {
          this.add("method-removed", "breaking", route, null,
            `Method '${route}' does not exist on the server - calls fail with 400 (method not found).`);
          continue;
        }
        this.compareMethod(route, c.methods.get(methodName)!, serverMethod);
      }
      for (const serverMethodName of s.order) {
        if (!c.methods.has(serverMethodName)) {
          this.add("method-added", "safe", `${c.name}.${serverMethodName}`, null,
            `Method '${c.name}.${serverMethodName}' is new on the server.`);
        }
      }
    }
    for (const s of this.server.controllers) {
      if (!this.client.controllers.some((c) => c.name === s.name)) {
        this.add("controller-added", "safe", s.name, null, `Controller '${s.name}' is new on the server.`);
      }
    }
  }

  private compareMethod(route: string, client: Record<string, unknown>, server: Record<string, unknown>): void {
    const clientReturn = client.returnType as Ref;
    const serverReturn = server.returnType as Ref;
    const clientEvent = kind(clientReturn) === "event";
    const serverEvent = kind(serverReturn) === "event";
    if (clientEvent !== serverEvent) {
      this.add("method-kind-changed", "breaking", route, null,
        `'${route}' is ${clientEvent ? "an event" : "a call"} for the client but ${serverEvent ? "an event" : "a call"} on the server.`);
      return;
    }

    this.compareParameters(route, params(client), params(server));

    if (serverEvent) {
      this.accepts(clientReturn.element as Ref | undefined, serverReturn.element as Ref | undefined, "output", `${route} -> event`, false);
    } else {
      this.accepts(clientReturn, serverReturn, "output", `${route} -> return`, false);
    }
  }

  private compareParameters(route: string, client: Param[], server: Param[]): void {
    const clientByName = new Map(client.map((p) => [p.name, p]));
    const serverByName = new Map(server.map((p) => [p.name, p]));
    const clientOnly = client.filter((p) => !serverByName.has(p.name));
    const serverOnly = server.filter((p) => !clientByName.has(p.name));

    for (const s of serverOnly) {
      const site = `${route}(${s.name})`;
      if (s.optional) {
        this.add("optional-parameter-added", "safe", site, "input",
          `The server accepts the optional parameter '${s.name}' (${describe(s.type)}), which the client does not send - it binds its default.`);
        continue;
      }
      // Rename heuristic: a client-only parameter at the same position whose type the server
      // parameter accepts (a removed bool next to an added string is no rename).
      const renamedAt = clientOnly.findIndex((c) => c.index === s.index && sameShape(s.type, c.type));
      if (renamedAt >= 0) {
        const [renamed] = clientOnly.splice(renamedAt, 1);
        this.add("parameter-renamed", "breaking", site, "input",
          `The client sends '${renamed.name}' where the server expects '${s.name}' (same position) - the name no longer binds: 400 Missing required parameter.`);
        continue;
      }
      this.add("required-parameter-not-sent", "breaking", site, "input",
        `The server requires parameter '${s.name}' (${describe(s.type)}), which the client does not send - 400 Missing required parameter.`);
    }

    for (const c of clientOnly) {
      this.add("parameter-not-accepted", "soft", `${route}(${c.name})`, "input",
        `The client sends '${c.name}', which the server does not have - the value is silently ignored.`);
    }

    for (const c of client) {
      const s = serverByName.get(c.name);
      if (!s) continue;
      const site = `${route}(${c.name})`;
      if (c.optional && !s.optional) {
        this.add("parameter-may-be-omitted", "breaking", site, "input",
          `The client may omit '${c.name}' (optional in its contract), but the server requires it - 400 Missing required parameter when omitted.`);
      } else if (c.optional && s.optional && jsonText(c.def) !== jsonText(s.def)) {
        this.add("parameter-default-changed", "soft", site, "input",
          `The default of '${c.name}' differs (client contract: ${jsonText(c.def)}, server: ${jsonText(s.def)}) - callers that omit it get the server's default.`);
      }
      // Top-level nullability of a parameter is its optionality, handled above.
      this.accepts(s.type, c.type, "input", site, true);
    }

    const commonClientOrder = client.filter((p) => serverByName.has(p.name)).map((p) => p.name);
    const commonServerOrder = server.filter((p) => clientByName.has(p.name)).map((p) => p.name);
    if (commonClientOrder.join("\u0000") !== commonServerOrder.join("\u0000")) {
      this.add("parameter-order-changed", "soft", route, "input",
        `The parameter order differs (client: ${commonClientOrder.join(", ")}; server: ${commonServerOrder.join(", ")}) - positional callers bind values to the wrong parameters; named callers are unaffected.`);
    }
  }

  // --- Type acceptance ------------------------------------------------------------------

  private acceptorSide(position: Position): Side { return position === "input" ? this.server : this.client; }
  private producerSide(position: Position): Side { return position === "input" ? this.client : this.server; }

  private accepts(acceptor: Ref | undefined, producer: Ref | undefined, position: Position, site: string, skipTopNullability: boolean): void {
    if (!acceptor || !producer) return;

    const aKind = normalizeKind(kind(acceptor));
    const pKind = normalizeKind(kind(producer));

    // The client ignores the data of a call it treats as void.
    if (aKind === "void") return;

    if (!skipTopNullability && isNullable(producer) && !isNullable(acceptor)) {
      this.add("null-not-accepted", "breaking", site, position, position === "input"
        ? "The client may send null, but the server does not accept null - 400 for a value type, an unexpected null otherwise."
        : "The server may return null, but the client does not expect null.");
    }

    const aAny = aKind === "scalar" && acceptor.name === "any";
    const pAny = pKind === "scalar" && producer.name === "any";
    if (aAny || aKind === "opaque") return; // accepts anything
    if (pAny || pKind === "opaque") {
      const p = describe(producer);
      const a = describe(acceptor);
      this.add("opaque-changed", "soft", site, position, position === "input"
        ? `The client sends ${p} (not modelled), the server expects ${a} - compatibility cannot be verified.`
        : `The server returns ${p} (not modelled), the client expects ${a} - compatibility cannot be verified.`);
      return;
    }

    if (aKind !== pKind) {
      this.typeIncompatible(acceptor, producer, position, site);
      return;
    }

    switch (aKind) {
      case "scalar":
        if (acceptor.name !== producer.name && !widens(producer.name, acceptor.name)) {
          this.typeIncompatible(acceptor, producer, position, site);
        }
        return;
      case "array":
        this.accepts(acceptor.element as Ref | undefined, producer.element as Ref | undefined, position, `${site}[]`, false);
        return;
      case "map":
        this.accepts(acceptor.key as Ref | undefined, producer.key as Ref | undefined, position, `${site}{key}`, false);
        this.accepts(acceptor.value as Ref | undefined, producer.value as Ref | undefined, position, `${site}{value}`, false);
        return;
      case "ref":
        this.acceptsRef(acceptor, producer, position, site);
        return;
    }
  }

  private typeIncompatible(acceptor: Ref, producer: Ref, position: Position, site: string): void {
    const a = describe(acceptor);
    const p = describe(producer);
    this.add("type-incompatible", "breaking", site, position, position === "input"
      ? `The client sends ${p}, the server expects ${a} - 400.`
      : `The server returns ${p}, the client expects ${a} - deserialization fails.`);
  }

  private acceptsRef(acceptor: Ref, producer: Ref, position: Position, site: string): void {
    const aKey = String(acceptor.ref);
    const pKey = String(producer.ref);
    const aMeta = this.acceptorSide(position).types.get(aKey.toLowerCase());
    const pMeta = this.producerSide(position).types.get(pKey.toLowerCase());
    if (!aMeta || !pMeta) return;

    if (aMeta.kind !== pMeta.kind) {
      this.typeIncompatible(acceptor, producer, position, site);
      return;
    }

    const pathKey = `${aKey}|${pKey}|${position}`;
    if (this.visiting.has(pathKey)) return; // recursive type: already being compared further up
    this.visiting.add(pathKey);
    try {
      if (aMeta.kind === "enum") this.acceptsEnum(aMeta, pMeta, position, site);
      else this.acceptsObject(aMeta, pMeta, position, site);
    } finally {
      this.visiting.delete(pathKey);
    }
  }

  private acceptsObject(aMeta: TypeMeta, pMeta: TypeMeta, position: Position, site: string): void {
    const aProps = aMeta.properties ?? [];
    const pProps = pMeta.properties ?? [];
    const pByName = new Map(pProps.map((p) => [p.propertyName.toLowerCase(), p]));
    const aByName = new Map(aProps.map((p) => [p.propertyName.toLowerCase(), p]));

    for (const a of aProps) {
      const name = a.propertyName;
      const propSite = `${site}.${name}`;
      const produced = pByName.get(name.toLowerCase());
      if (produced) {
        this.accepts(a.propertyType as Ref, produced.propertyType as Ref, position, propSite, false);
        continue;
      }
      const required = !isNullable(a.propertyType as Ref);
      if (position === "output") {
        this.add("property-not-sent", required ? "breaking" : "soft", propSite, position, required
          ? `The server does not send '${name}', which the client requires.`
          : `The server does not send '${name}' (optional for the client) - the client sees it as absent.`);
      } else {
        this.add("property-not-sent", required ? "soft" : "safe", propSite, position, required
          ? `The server requires property '${name}', which the client does not send - it binds a silent default (400 under AliasBindingMode Strict/Paranoid).`
          : `The server has the optional property '${name}', which the client does not send - it binds null.`);
      }
    }

    for (const p of pProps) {
      if (aByName.has(p.propertyName.toLowerCase())) continue;
      const name = p.propertyName;
      const propSite = `${site}.${name}`;
      if (position === "output") {
        this.add("property-not-accepted", "safe", propSite, position,
          `The server sends '${name}', which the client does not know - ignored.`);
      } else {
        this.add("property-not-accepted", "soft", propSite, position,
          `The client sends '${name}', which the server does not have - the value is silently ignored.`);
      }
    }
  }

  private acceptsEnum(aMeta: TypeMeta, pMeta: TypeMeta, position: Position, site: string): void {
    const aMembers = members(aMeta);
    const pMembers = members(pMeta);
    const aByName = new Map(aMembers.map((m) => [m.name, m]));
    const aValues = new Set(aMembers.map((m) => m.value));

    for (const { name, value } of pMembers) {
      const memberSite = `${site}::${name}`;
      const same = aByName.get(name);
      if (same && same.value !== value) {
        this.add("enum-value-changed", "breaking", memberSite, position,
          `Enum member '${name}' is ${same.value} for the ${party(position, true)} but ${value} for the ${party(position, false)} - the numeric wire value changed meaning.`);
        continue;
      }
      if (aValues.has(value)) continue;
      if (position === "output") {
        this.add("enum-value-not-accepted", "soft", memberSite, position,
          `The server may return '${name}' (${value}), which the client's enum does not know.`);
      } else {
        this.add("enum-value-not-accepted", "breaking", memberSite, position,
          `The client may send '${name}' (${value}), which the server's enum does not have - the server binds an undefined enum value.`);
      }
    }
  }
}

// --- Shared helpers ---------------------------------------------------------------------

function params(method: Record<string, unknown>): Param[] {
  const list = (method.parameters as Record<string, unknown>[] | undefined) ?? [];
  return list.map((p, index) => {
    const type = p.parameterType as Ref;
    const def = p.defaultValue === null ? undefined : p.defaultValue;
    return {
      name: String(p.parameterName),
      index,
      type,
      def,
      // Optional = may be omitted: nullable, a C# default, or byte[] (from binaryData).
      optional: isNullable(type) || def !== undefined || (kind(type) === "scalar" && type.name === "bytes"),
    };
  });
}

function members(meta: TypeMeta): { name: string; value: string }[] {
  return (meta.members ?? []).map((m) => ({
    name: m.name,
    value: m.value === undefined || m.value === null ? m.name : JSON.stringify(m.value),
  }));
}

const party = (position: Position, acceptor: boolean): string =>
  (position === "input") === acceptor ? "server" : "client";

const kind = (r: Ref): string => r.kind ?? "opaque";

const isNullable = (r: Ref): boolean => r.nullable === true;

// set/stream are JSON arrays on the wire.
const normalizeKind = (k: string): string => (k === "set" || k === "stream" ? "array" : k);

const jsonText = (v: unknown): string => (v === undefined ? "none" : JSON.stringify(v));

// Lossless JSON widening (producer -> acceptor): the acceptor parses every value the producer writes.
const WIDENINGS = new Set([
  "int>long", "int>float", "int>double", "int>decimal",
  "long>double", "long>decimal", "float>double", "char>string",
]);

const widens = (from: unknown, to: unknown): boolean => WIDENINGS.has(`${from}>${to}`);

// Rename candidate: same wire kind, and for scalars the same or a widened scalar.
function sameShape(acceptor: Ref, producer: Ref): boolean {
  const a = normalizeKind(kind(acceptor));
  if (a !== normalizeKind(kind(producer))) return false;
  if (a !== "scalar") return true;
  return acceptor.name === producer.name || widens(producer.name, acceptor.name);
}

function describe(r: Ref | undefined): string {
  if (!r) return "?";
  switch (kind(r)) {
    case "scalar": return String(r.name ?? "?");
    case "array":
    case "set":
    case "stream":
    case "event": return `${describe(r.element as Ref | undefined)}[]`;
    case "map": return `map<${describe(r.key as Ref | undefined)}, ${describe(r.value as Ref | undefined)}>`;
    case "ref": {
      const key = String(r.ref);
      const cut = Math.max(key.lastIndexOf("."), key.lastIndexOf("+"));
      return cut >= 0 ? key.slice(cut + 1) : key;
    }
    case "opaque": return String(r.nativeName ?? "opaque");
    default: return kind(r);
  }
}
