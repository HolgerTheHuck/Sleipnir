import type { SleipnirErrorBody, SleipnirResponse } from "./types.js";
/**
 * Fehler bei einem Sleipnir-Aufruf. Spiegelt das C#-Äquivalent (SleipnirException).
 *
 * - **Logischer Fehler** (non-2xx `code` im Response-Body): `code`/`message`/
 *   `details`/`requestId` aus SleipnirResponse.error (oder aus Response abgeleitet).
 * - **Transportfehler** (Netzwerk, non-200 HTTP, malformed JSON): `code` = 0,
 *   `message` beschreibt den Transportfehler, ursprüngliche Exception in `cause`.
 *
 * Cancellation (AbortSignal) wird **nicht** als SleipnirError geworfen, sondern als
 * {@link CancelledError} — konsistent mit der C#-Konvention (OCE unverpackt).
 */
export declare class SleipnirError extends Error {
    readonly code: number;
    readonly details?: string | null;
    readonly requestId?: string | null;
    constructor(code: number, message: string, options?: {
        details?: string | null;
        requestId?: string | null;
        cause?: unknown;
    });
    /** Baut einen SleipnirError aus einem SleipnirErrorBody (z. B. response.error). */
    static fromBody(body: SleipnirErrorBody): SleipnirError;
    /**
     * Baut einen SleipnirError aus einer nicht-erfolgreichen Response. Ist ein
     * strukturiertes `error` vorhanden, wird es genutzt; sonst generischer Text
     * aus `code` (Spiegel von C# SleipnirError.FromResponse — Data trägt seit dem
     * Single-Pass-Fix keine Fehlertexte mehr, die wohnen in error.message).
     */
    static fromResponse(response: SleipnirResponse): SleipnirError;
}
/**
 * Signalisiert Abbruch eines Aufrufs (AbortSignal/Timeout). Wird — anders als
 * SleipnirError — unverpackt propagiert, damit Aufrufer Cancellation von echten
 * Fehlern unterscheiden können (Spiegel der C# OperationCanceledException).
 */
export declare class CancelledError extends Error {
    readonly timedOut: boolean;
    constructor(message?: string, timedOut?: boolean);
}
/** True, wenn x ein Abbruch ist (CancelledError oder fetch-AbortError/DOMException). */
export declare function isCancelled(x: unknown): boolean;
//# sourceMappingURL=errors.d.ts.map