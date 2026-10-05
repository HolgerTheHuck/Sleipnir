import { ExecutionMode, SLEIPNIR_ERROR_CATEGORIES } from "./types.js";
import type {
  SleipnirErrorCategory,
  SleipnirMultiRequest,
  SleipnirParameter,
  SleipnirRequest,
  SleipnirResponse,
} from "./types.js";

/**
 * Builds the `params` field (array of SleipnirParameter with native `data` values)
 * from call arguments. Mirrors the C# SleipnirCall builder.
 *
 * - **Named** (Object): `{parameterName: key, data: value}`.
 *   Safe, position-independent binding (the server binds by parameter name).
 * - **Positional** (Array): `{parameterName: "param{i}", num: i, data: value}`.
 *   The server binds by name ("param{i}" never matches) and then falls back to `num`
 *   (PROTOCOL.md:60-66).
 *
 * `data` is the **native JSON value** (no JSON string anymore); `undefined` → `null`.
 */
export function buildParams(
  params: Record<string, unknown> | unknown[] | undefined,
): SleipnirParameter[] {
  if (params == null) return [];

  if (Array.isArray(params)) {
    return params.map((value, i) => ({
      parameterName: `param${i}`,
      num: i,
      data: normalizeValue(value),
    }));
  }

  const entries = Object.entries(params);
  return entries.map(([key, value], i) => ({
    parameterName: key,
    num: i,
    data: normalizeValue(value),
  }));
}

/** Normalizes a parameter value: undefined → null (wire-consistent), otherwise native. */
function normalizeValue(value: unknown): unknown {
  if (value === undefined) return null;
  return value;
}

/** Builds a single SleipnirRequest. */
export function buildSingle(opts: {
  controller: string;
  method: string;
  params?: Record<string, unknown> | unknown[];
  id?: string;
  dependencyMapping?: Record<string, string> | null;
  binaryData?: Uint8Array | string | null;
}): SleipnirRequest {
  const id = opts.id ?? `${opts.controller}.${opts.method}`;
  const request: SleipnirRequest = {
    controller: opts.controller,
    method: opts.method,
    params: buildParams(opts.params),
    id,
    dependencyMapping: opts.dependencyMapping ?? null,
    binaryData:
      opts.binaryData == null
        ? null
        : opts.binaryData instanceof Uint8Array
          ? toBase64(opts.binaryData)
          : opts.binaryData,
  };
  return request;
}

/** Builds a SleipnirMultiRequest (batch). */
export function buildMulti(
  requests: SleipnirRequest[],
  mode: ExecutionMode = ExecutionMode.Parallel,
): SleipnirMultiRequest {
  return { requests, mode };
}

/**
 * Fills in the `isSuccess` field if the server did not send it.
 * Server-side, `IsSuccess` is `[JsonIgnore]` and derived from `code`
 * (`Code is >= 200 and <= 299`) — the wire frame therefore never contains it. The
 * client mirrors this derivation so that `response.isSuccess` is reliable.
 */
export function normalizeResponse<T extends SleipnirResponse>(resp: T): T {
  const normalized = normalizeErrorCategory(resp);
  if (typeof normalized?.isSuccess === "boolean") return normalized;
  const code = typeof normalized?.code === "number" ? normalized.code : 0;
  return { ...normalized, isSuccess: code >= 200 && code <= 299 };
}

/**
 * Coerces `error.category` to the canonical {@link SleipnirErrorCategory} name. The server writes
 * the PascalCase enum name; a host that overrides the JSON enum handling (a camelCase
 * `JsonStringEnumConverter`, or numeric enums) would otherwise leak a different spelling. An
 * unrecognized value is left untouched (forward-compatible with categories added later).
 */
function normalizeErrorCategory<T extends SleipnirResponse>(resp: T): T {
  const error = resp?.error;
  const raw: unknown = error?.category;
  if (raw == null) return resp;
  if (typeof raw === "string" && (SLEIPNIR_ERROR_CATEGORIES as readonly string[]).includes(raw)) return resp;
  let canonical: SleipnirErrorCategory | undefined;
  if (typeof raw === "number") {
    canonical = SLEIPNIR_ERROR_CATEGORIES[raw];
  } else if (typeof raw === "string") {
    const lower = raw.toLowerCase();
    canonical = SLEIPNIR_ERROR_CATEGORIES.find((c) => c.toLowerCase() === lower);
  }
  if (!canonical || !error) return resp;
  return { ...resp, error: { ...error, category: canonical } };
}

/** `normalizeResponse` for each element of a batch array. */
export function normalizeResponses(arr: SleipnirResponse[]): SleipnirResponse[] {
  return arr.map(normalizeResponse);
}

// --- Isomorphic base64 helpers (browser + Node) ---

/** true when the Node Buffer API is available. */
function hasNodeBuffer(): boolean {
  return (
    typeof (globalThis as any).Buffer !== "undefined" &&
    typeof (globalThis as any).Buffer.from === "function"
  );
}

/** Encodes a Uint8Array as a base64 string (isomorphic). */
export function toBase64(bytes: Uint8Array): string {
  if (hasNodeBuffer()) {
    return (globalThis as any).Buffer.from(bytes).toString("base64");
  }
  // Browser path: bytes (0..255) → Latin1 string → btoa.
  const chunk = 0x8000;
  let binary = "";
  for (let i = 0; i < bytes.length; i += chunk) {
    const end = Math.min(i + chunk, bytes.length);
    let part = "";
    for (let j = i; j < end; j++) part += String.fromCharCode(bytes[j]);
    binary += part;
  }
  return btoa(binary);
}

/** Decodes a base64 string as a Uint8Array (isomorphic). */
export function fromBase64(b64: string): Uint8Array {
  if (hasNodeBuffer()) {
    const buf = (globalThis as any).Buffer.from(b64, "base64");
    return new Uint8Array(buf.buffer as ArrayBuffer, buf.byteOffset, buf.byteLength);
  }
  const binary = atob(b64);
  const out = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) out[i] = binary.charCodeAt(i);
  return out;
}