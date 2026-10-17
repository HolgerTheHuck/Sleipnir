// Opaque-site scan — the codegen half of "opaque types are silent".
//
// Discovery marks a type `opaque` when it is not modelled: declared outside the
// controller assemblies (typically a domain type from a Core project), the
// non-generic SleipnirResponse envelope, or another framework/BCL type. Every
// emitter maps it to `unknown` / `object` / `Any` and only leaves a TODO comment
// in the generated code, so a consumer used to find out at the first property
// access. The CLI reports the sites on stderr at generation time instead. The
// server runs the same scan at startup (SleipnirCore DiscoveryOpaqueScan).

import type { DiscoveryInfo, TypeRef } from "sleipnir-client";

export interface OpaqueSite {
  /** `Run.Execute → return`, `Run.Execute(summary)`, `Ns.InvoiceDto.Lines`, `Queue.Live → event payload`. */
  site: string;
  /** The unmodelled C# type's short name (diagnostic only, never identity). */
  nativeName: string;
}

/** Every usage of an opaque type: returns, parameters, event payloads, contract-type properties. */
export function findOpaqueSites(discovery: DiscoveryInfo): OpaqueSite[] {
  const sites: OpaqueSite[] = [];
  for (const c of discovery.controllers ?? []) {
    for (const m of c.methods ?? []) {
      const route = `${c.name}.${m.methodName}`;
      const returnSite = m.returnType?.kind === "event" ? `${route} → event payload` : `${route} → return`;
      collect(m.returnType, returnSite, sites);
      for (const p of m.parameters ?? []) collect(p.parameterType, `${route}(${p.parameterName})`, sites);
    }
  }
  for (const [key, t] of Object.entries(discovery.types ?? {})) {
    for (const p of t.properties ?? []) collect(p.propertyType, `${key}.${p.propertyName}`, sites);
  }
  return sites;
}

/** The stderr warning for `sites` (empty string when there are none). */
export function formatOpaqueWarning(sites: OpaqueSite[], max = 20): string {
  if (sites.length === 0) return "";
  const lines = sites.slice(0, max).map((s) => `  ${s.site}: ${s.nativeName}`);
  if (sites.length > max) lines.push(`  … (${sites.length - max} more)`);
  return (
    `sleipnir-gen: warning: ${sites.length} contract usage${sites.length === 1 ? " is" : "s are"} opaque in discovery ` +
    `and emitted as unknown/object/Any:\n${lines.join("\n")}\n` +
    `  A type is opaque when it is declared outside the server's controller assemblies (e.g. a Core/Domain project)\n` +
    `  or is a framework type. Map it to a DTO in the API project, or mark it [SleipnirDataContract] to expand it.\n` +
    `  A non-generic SleipnirResponse return stays opaque — return SleipnirResponse<T> to keep the payload type.\n`
  );
}

function collect(ref: TypeRef | undefined, site: string, out: OpaqueSite[]): void {
  if (!ref) return;
  switch (ref.kind) {
    case "opaque":
      out.push({ site, nativeName: ref.nativeName ?? "?" });
      return;
    case "array":
    case "set":
    case "stream":
    case "event":
      collect(ref.element, site, out);
      return;
    case "map":
      collect(ref.key, site, out);
      collect(ref.value, site, out);
      return;
    default:
      return;
  }
}
