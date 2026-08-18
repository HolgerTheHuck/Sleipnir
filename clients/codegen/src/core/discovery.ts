// Discovery loading + shape assertion.
//
// `loadDiscovery` accepts a URL (live server), a file path, or `-` (stdin) and
// returns a parsed DiscoveryInfo. `assertDiscoveryShape` is the no-drift gate's
// ingress check: it enforces `discoveryVersion` (additive-only — accept known
// versions, reject unknown loudly) and validates the full `TypeRef` shape so a
// payload the codegen accepts is, by construction, conformant to the contract
// (docs/discovery-schema.md). Refusing malformed input early prevents emitting
// broken stubs from a structurally-wrong payload.

import { readFile } from "node:fs/promises";
import { SleipnirRestClient } from "sleipnir-client";
import type { DiscoveryInfo, TypeRef } from "sleipnir-client";

export interface LoadDiscoveryOptions {
  /** Bearer token when loading from a URL. */
  bearer?: string;
  /** Request timeout in ms (URL source). */
  timeout?: number;
  /** Abort signal. */
  signal?: AbortSignal;
}

/** Load discovery from a URL (`http(s)://…`), a file path, or `-` (stdin). */
export async function loadDiscovery(
  source: string,
  opts: LoadDiscoveryOptions = {},
): Promise<DiscoveryInfo> {
  if (!source || source === "-") {
    return loadFromStdin();
  }
  if (/^https?:\/\//i.test(source)) {
    return loadFromUrl(source, opts);
  }
  return loadFromFile(source);
}

async function loadFromUrl(url: string, opts: LoadDiscoveryOptions): Promise<DiscoveryInfo> {
  // Derive baseUrl + apiPath from a discovery URL of the form
  // `<origin>/api/sleipnir/discovery` (the trailing `/discovery` is stripped, the
  // leading path becomes apiPath). The SleipnirRestClient normalizes both.
  const withoutDiscovery = url.replace(/\/discovery\/?$/i, "");
  const parsed = new URL(withoutDiscovery);
  const baseUrl = `${parsed.protocol}//${parsed.host}/`;
  const apiPath = parsed.pathname.replace(/^\/+|\/+$/g, "");
  const client = new SleipnirRestClient(baseUrl, {
    apiPath: apiPath || undefined,
    bearer: opts.bearer,
    callTimeout: opts.timeout,
  });
  const payload = await client.discover({ signal: opts.signal });
  // Validate even the URL path so a malformed server response fails loudly.
  return assertDiscoveryShape(payload);
}

async function loadFromFile(path: string): Promise<DiscoveryInfo> {
  const text = await readFile(path, "utf8");
  return parseDiscovery(text, path);
}

async function loadFromStdin(): Promise<DiscoveryInfo> {
  const text = await readAllStdin();
  return parseDiscovery(text, "stdin");
}

function parseDiscovery(text: string, source: string): DiscoveryInfo {
  let obj: unknown;
  try {
    obj = JSON.parse(text);
  } catch (err) {
    throw new DiscoveryShapeError(
      `Failed to parse discovery JSON from ${source}: ${(err as Error).message}`,
    );
  }
  return assertDiscoveryShape(obj);
}

function readAllStdin(): Promise<string> {
  return new Promise((resolve, reject) => {
    const chunks: Buffer[] = [];
    process.stdin.on("data", (c: Buffer) => chunks.push(c));
    process.stdin.on("end", () => resolve(Buffer.concat(chunks).toString("utf8")));
    process.stdin.on("error", reject);
  });
}

/** Raised when the discovery payload does not match the expected shape. */
export class DiscoveryShapeError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "DiscoveryShapeError";
  }
}

/** Known `discoveryVersion` values (additive-only — see docs/discovery-schema.md §11). */
const KNOWN_DISCOVERY_VERSIONS = new Set(["1"]);

const VALID_KINDS = new Set(["scalar", "array", "set", "map", "ref", "stream", "event", "opaque", "void"]);
const SCALAR_NAMES = new Set([
  "string", "char", "bool", "int", "long", "float", "double", "decimal",
  "datetime", "datetimeoffset", "dateonly", "timeonly", "timespan", "guid",
  "uri", "version", "bytes", "any",
]);

/**
 * Runtime shape guard + no-drift ingress gate. Enforces `discoveryVersion`
 * (additive-only: accept known, reject unknown loudly) and validates every
 * `TypeRef` (kind ∈ enum, scalar `name` ∈ table, array/set/stream has `element`,
 * map has `key`+`value`, ref resolves into `types`). Throws DiscoveryShapeError
 * (English) with a precise reason.
 */
export function assertDiscoveryShape(obj: unknown): DiscoveryInfo {
  if (obj === null || typeof obj !== "object") {
    throw new DiscoveryShapeError("Discovery payload is not a JSON object.");
  }
  const o = obj as Record<string, unknown>;

  // discoveryVersion — additive-only gate.
  const version = o.discoveryVersion;
  if (typeof version !== "string" || version.length === 0) {
    throw new DiscoveryShapeError(
      'Discovery payload is missing a string "discoveryVersion" (expected { discoveryVersion: "1", ... }).',
    );
  }
  if (!KNOWN_DISCOVERY_VERSIONS.has(version)) {
    throw new DiscoveryShapeError(
      `Unsupported discoveryVersion "${version}" — known versions: ${[...KNOWN_DISCOVERY_VERSIONS].join(", ")}. ` +
        `Upgrade the codegen (additive-only, see docs/discovery-schema.md §11).`,
    );
  }

  if (!Array.isArray(o.controllers)) {
    throw new DiscoveryShapeError(
      'Discovery payload is missing a "controllers" array (expected { controllers: [...], types: {...} }).',
    );
  }
  if (o.types === null || typeof o.types !== "object" || Array.isArray(o.types)) {
    throw new DiscoveryShapeError(
      'Discovery payload is missing a "types" object (expected { controllers: [...], types: {...} }).',
    );
  }

  const types = o.types as Record<string, unknown>;

  // Validate every TypeRef reachable from controllers + type properties.
  for (const c of o.controllers) {
    if (!c || typeof c !== "object") {
      throw new DiscoveryShapeError('A "controllers" entry is not an object.');
    }
    const ctrl = c as Record<string, unknown>;
    if (typeof ctrl.name !== "string") {
      throw new DiscoveryShapeError('A controller is missing a string "name".');
    }
    if (!Array.isArray(ctrl.methods)) {
      throw new DiscoveryShapeError(`Controller "${ctrl.name}" is missing a "methods" array.`);
    }
    for (const m of ctrl.methods as unknown[]) {
      if (!m || typeof m !== "object") {
        throw new DiscoveryShapeError(`Controller "${ctrl.name}" has a non-object method.`);
      }
      const mm = m as Record<string, unknown>;
      if (typeof mm.methodName !== "string") {
        throw new DiscoveryShapeError(`A method in controller "${ctrl.name}" is missing a string "methodName".`);
      }
      assertTypeRef(mm.returnType, `controller "${ctrl.name}" method "${mm.methodName}" returnType`, types);
      if (mm.parameters !== undefined && mm.parameters !== null) {
        if (!Array.isArray(mm.parameters)) {
          throw new DiscoveryShapeError(`Method "${mm.methodName}" parameters is not an array.`);
        }
        for (const p of mm.parameters as unknown[]) {
          const pp = (p as Record<string, unknown>) ?? {};
          assertTypeRef(pp.parameterType, `method "${mm.methodName}" parameter "${pp.parameterName ?? "?"}"`, types);
        }
      }
    }
  }

  // Validate the types registry entries themselves.
  for (const [key, tm] of Object.entries(types)) {
    if (!tm || typeof tm !== "object") {
      throw new DiscoveryShapeError(`Type registry entry "${key}" is not an object.`);
    }
    const meta = tm as Record<string, unknown>;
    const kind = meta.kind;
    if (kind !== "object" && kind !== "enum") {
      throw new DiscoveryShapeError(`Type registry entry "${key}" has invalid kind "${String(kind)}" (expected "object" or "enum").`);
    }
    if (kind === "object" && !Array.isArray(meta.properties)) {
      throw new DiscoveryShapeError(`Object type "${key}" is missing a "properties" array.`);
    }
    if (kind === "enum") {
      if (!Array.isArray(meta.members) || (meta.members as unknown[]).length === 0) {
        throw new DiscoveryShapeError(`Enum type "${key}" must have a non-empty "members" array.`);
      }
    }
    if (Array.isArray(meta.properties)) {
      for (const p of meta.properties as unknown[]) {
        const pp = (p as Record<string, unknown>) ?? {};
        assertTypeRef(pp.propertyType, `type "${key}" property "${pp.propertyName ?? "?"}"`, types);
      }
    }
  }

  return obj as DiscoveryInfo;
}

/** Validate one TypeRef recursively; refs must resolve into the types registry. */
function assertTypeRef(value: unknown, where: string, types: Record<string, unknown>): void {
  if (!value || typeof value !== "object") {
    throw new DiscoveryShapeError(`${where} is not a TypeRef object.`);
  }
  const ref = value as TypeRef;
  if (!VALID_KINDS.has(ref.kind)) {
    throw new DiscoveryShapeError(`${where} has invalid kind "${String(ref.kind)}".`);
  }
  switch (ref.kind) {
    case "scalar":
      if (typeof ref.name !== "string" || !SCALAR_NAMES.has(ref.name)) {
        throw new DiscoveryShapeError(`${where} has invalid scalar name "${String(ref.name)}".`);
      }
      return;
    case "array":
    case "set":
    case "stream":
    case "event":
      if (!ref.element) throw new DiscoveryShapeError(`${where} (kind "${ref.kind}") is missing "element".`);
      assertTypeRef(ref.element, `${where} element`, types);
      return;
    case "map":
      if (!ref.key || !ref.value) throw new DiscoveryShapeError(`${where} (map) is missing "key" or "value".`);
      assertTypeRef(ref.key, `${where} key`, types);
      assertTypeRef(ref.value, `${where} value`, types);
      return;
    case "ref":
      if (typeof ref.ref !== "string" || !ref.ref) {
        throw new DiscoveryShapeError(`${where} (ref) is missing a "ref" string.`);
      }
      if (!(ref.ref in types)) {
        throw new DiscoveryShapeError(`${where} (ref) "${ref.ref}" does not resolve into the types registry.`);
      }
      return;
    // opaque | void: no further constraints.
  }
}