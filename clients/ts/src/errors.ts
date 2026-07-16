import type { TrameErrorBody, TrameResponse } from "./types.js";

/**
 * Fehler bei einem Trame-Aufruf. Spiegelt das C#-Äquivalent (TrameException).
 *
 * - **Logischer Fehler** (non-2xx `code` im Response-Body): `code`/`message`/
 *   `details`/`requestId` aus TrameResponse.error (oder aus Response abgeleitet).
 * - **Transportfehler** (Netzwerk, non-200 HTTP, malformed JSON): `code` = 0,
 *   `message` beschreibt den Transportfehler, ursprüngliche Exception in `cause`.
 *
 * Cancellation (AbortSignal) wird **nicht** als TrameError geworfen, sondern als
 * {@link CancelledError} — konsistent mit der C#-Konvention (OCE unverpackt).
 */
export class TrameError extends Error {
  readonly code: number;
  readonly details?: string | null;
  readonly requestId?: string | null;

  constructor(
    code: number,
    message: string,
    options?: { details?: string | null; requestId?: string | null; cause?: unknown },
  ) {
    super(message, options?.cause !== undefined ? { cause: options.cause } : undefined);
    this.name = "TrameError";
    this.code = code;
    this.details = options?.details;
    this.requestId = options?.requestId;
  }

  /** Baut einen TrameError aus einem TrameErrorBody (z. B. response.error). */
  static fromBody(body: TrameErrorBody): TrameError {
    return new TrameError(body.code, body.message, {
      details: body.details,
      requestId: body.requestId,
    });
  }

  /**
   * Baut einen TrameError aus einer nicht-erfolgreichen Response. Ist ein
   * strukturiertes `error` vorhanden, wird es genutzt; sonst generischer Text
   * aus `code` (Spiegel von C# TrameError.FromResponse — Data trägt seit dem
   * Single-Pass-Fix keine Fehlertexte mehr, die wohnen in error.message).
   */
  static fromResponse(response: TrameResponse): TrameError {
    if (response.error) return TrameError.fromBody(response.error);
    return new TrameError(response.code, `Trame call failed with code ${response.code}.`, {
      requestId: response.id,
    });
  }
}

/**
 * Signalisiert Abbruch eines Aufrufs (AbortSignal/Timeout). Wird — anders als
 * TrameError — unverpackt propagiert, damit Aufrufer Cancellation von echten
 * Fehlern unterscheiden können (Spiegel der C# OperationCanceledException).
 */
export class CancelledError extends Error {
  readonly timedOut: boolean;

  constructor(message = "Trame call was cancelled.", timedOut = false) {
    super(message);
    this.name = "CancelledError";
    this.timedOut = timedOut;
  }
}

/** True, wenn x ein Abbruch ist (CancelledError oder fetch-AbortError/DOMException). */
export function isCancelled(x: unknown): boolean {
  return (
    x instanceof CancelledError ||
    (x instanceof Error &&
      (x.name === "AbortError" || x.name === "CancelledError"))
  );
}