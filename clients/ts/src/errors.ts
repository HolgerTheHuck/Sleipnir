import type { SleipnirErrorBody, SleipnirErrorCategory, SleipnirResponse } from "./types.js";

/**
 * Error from a Sleipnir call. Mirrors the C# equivalent (SleipnirException).
 *
 * - **Logical error** (non-2xx `code` in the response body): `code`/`message`/
 *   `details`/`requestId` from SleipnirResponse.error (or derived from the response).
 * - **Transport error** (network, non-200 HTTP, malformed JSON): `code` = 0,
 *   `message` describes the transport failure, the original exception in `cause`.
 *
 * Cancellation (AbortSignal) is **not** thrown as a SleipnirError but as a
 * {@link CancelledError} — consistent with the C# convention (OCE unwrapped).
 */
export class SleipnirError extends Error {
  readonly code: number;
  readonly details?: string | null;
  readonly requestId?: string | null;
  /** Semantic category from `error.category` (absent for transport errors / older servers). */
  readonly category?: SleipnirErrorCategory;

  constructor(
    code: number,
    message: string,
    options?: {
      details?: string | null;
      requestId?: string | null;
      category?: SleipnirErrorCategory;
      cause?: unknown;
    },
  ) {
    super(message, options?.cause !== undefined ? { cause: options.cause } : undefined);
    this.name = "SleipnirError";
    this.code = code;
    this.details = options?.details;
    this.requestId = options?.requestId;
    this.category = options?.category;
  }

  /** Builds a SleipnirError from a SleipnirErrorBody (e.g. response.error). */
  static fromBody(body: SleipnirErrorBody): SleipnirError {
    return new SleipnirError(body.code, body.message, {
      details: body.details,
      requestId: body.requestId,
      category: body.category,
    });
  }

  /**
   * Builds a SleipnirError from a non-successful response. If a structured `error`
   * is present it is used; otherwise generic text derived from `code`
   * (mirror of C# SleipnirError.FromResponse — since the single-pass fix Data carries
   * no error text anymore; that lives in error.message).
   */
  static fromResponse(response: SleipnirResponse): SleipnirError {
    if (response.error) return SleipnirError.fromBody(response.error);
    return new SleipnirError(response.code, `Sleipnir call failed with code ${response.code}.`, {
      requestId: response.id,
    });
  }
}

/**
 * Signals cancellation of a call (AbortSignal/timeout). Unlike SleipnirError it is
 * propagated unwrapped, so callers can distinguish cancellation from real errors
 * (mirror of the C# OperationCanceledException).
 */
export class CancelledError extends Error {
  readonly timedOut: boolean;

  constructor(message = "Sleipnir call was cancelled.", timedOut = false) {
    super(message);
    this.name = "CancelledError";
    this.timedOut = timedOut;
  }
}

/** True when x is a cancellation (CancelledError or fetch AbortError/DOMException). */
export function isCancelled(x: unknown): boolean {
  return (
    x instanceof CancelledError ||
    (x instanceof Error &&
      (x.name === "AbortError" || x.name === "CancelledError"))
  );
}