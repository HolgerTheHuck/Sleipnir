import { ExecutionMode } from "./types.js";
import type { SleipnirMultiRequest, SleipnirParameter, SleipnirRequest, SleipnirResponse } from "./types.js";

/**
 * Baut das `params`-Feld (Array von SleipnirParameter mit nativen `data`-Werten) aus
 * Aufrufargumenten. Spiegelt den C#-SleipnirCall-Builder.
 *
 * - **Named** (Object): `{parameterName: key, data: value}`.
 *   Sichere, positionsunabhängige Bindung (Server bindet nach Parametername).
 * - **Positional** (Array): `{parameterName: "param{i}", num: i, data: value}`.
 *   Server bindet nach Name ("param{i" passt nie) und fällt dann auf `num` zurück
 *   (PROTOCOL.md:60-66).
 *
 * `data` ist der **native JSON-Wert** (kein JSON-String mehr); `undefined` → `null`.
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

/** Normiert einen Parameterwert: undefined → null (Wire-konsistent), sonst nativ. */
function normalizeValue(value: unknown): unknown {
  if (value === undefined) return null;
  return value;
}

/** Baut einen einzelnen SleipnirRequest. */
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

/** Baut einen SleipnirMultiRequest (Batch). */
export function buildMulti(
  requests: SleipnirRequest[],
  mode: ExecutionMode = ExecutionMode.Parallel,
): SleipnirMultiRequest {
  return { requests, mode };
}

/**
 * Füllt das `isSuccess`-Feld auf, falls der Server es nicht gesendet hat.
 * Server-seitig ist `IsSuccess` `[JsonIgnore]` und wird aus `code` abgeleitet
 * (`Code is >= 200 and <= 299`) — das Wire-Frame enthält es also nie. Der
 * Client spiegelt diese Ableitung, sodass `response.isSuccess` verlässlich ist.
 */
export function normalizeResponse<T extends SleipnirResponse>(resp: T): T {
  if (typeof resp?.isSuccess === "boolean") return resp;
  const code = typeof resp?.code === "number" ? resp.code : 0;
  return { ...resp, isSuccess: code >= 200 && code <= 299 };
}

/** `normalizeResponse` für jedes Element eines Batch-Arrays. */
export function normalizeResponses(arr: SleipnirResponse[]): SleipnirResponse[] {
  return arr.map(normalizeResponse);
}

// --- Isomorphe base64-Helper (Browser + Node) ---

/** true, wenn die Node-Buffer-API zur Verfügung steht. */
function hasNodeBuffer(): boolean {
  return (
    typeof (globalThis as any).Buffer !== "undefined" &&
    typeof (globalThis as any).Buffer.from === "function"
  );
}

/** Kodiert ein Uint8Array als base64-String (isomorph). */
export function toBase64(bytes: Uint8Array): string {
  if (hasNodeBuffer()) {
    return (globalThis as any).Buffer.from(bytes).toString("base64");
  }
  // Browser-Pfad: bytes (0..255) → Latin1-String → btoa.
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

/** Dekodiert einen base64-String als Uint8Array (isomorph). */
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