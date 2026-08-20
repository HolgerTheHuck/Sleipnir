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
export declare function buildParams(params: Record<string, unknown> | unknown[] | undefined): SleipnirParameter[];
/** Baut einen einzelnen SleipnirRequest. */
export declare function buildSingle(opts: {
    controller: string;
    method: string;
    params?: Record<string, unknown> | unknown[];
    id?: string;
    dependencyMapping?: Record<string, string> | null;
    binaryData?: Uint8Array | string | null;
}): SleipnirRequest;
/** Baut einen SleipnirMultiRequest (Batch). */
export declare function buildMulti(requests: SleipnirRequest[], mode?: ExecutionMode): SleipnirMultiRequest;
/**
 * Füllt das `isSuccess`-Feld auf, falls der Server es nicht gesendet hat.
 * Server-seitig ist `IsSuccess` `[JsonIgnore]` und wird aus `code` abgeleitet
 * (`Code is >= 200 and <= 299`) — das Wire-Frame enthält es also nie. Der
 * Client spiegelt diese Ableitung, sodass `response.isSuccess` verlässlich ist.
 */
export declare function normalizeResponse<T extends SleipnirResponse>(resp: T): T;
/** `normalizeResponse` für jedes Element eines Batch-Arrays. */
export declare function normalizeResponses(arr: SleipnirResponse[]): SleipnirResponse[];
/** Kodiert ein Uint8Array als base64-String (isomorph). */
export declare function toBase64(bytes: Uint8Array): string;
/** Dekodiert einen base64-String als Uint8Array (isomorph). */
export declare function fromBase64(b64: string): Uint8Array;
//# sourceMappingURL=request.d.ts.map