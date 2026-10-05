// Isomorphic helpers for combining a caller abort signal with a call timeout.
// (Node 18 has no AbortSignal.any yet; hence the manual linking.)

export interface LinkedSignal {
  /** Signal that fires when the caller signal or the timeout trips. */
  signal: AbortSignal;
  /** Cancels the timeout timer (call in the caller's finally). */
  clear: () => void;
  /** True when the signal fired because of a timeout (not a caller abort). */
  isTimeout: () => boolean;
}

/**
 * Links an optional caller signal with an optional call timeout.
 * If the caller aborts → the signal aborts. If the timer expires → the signal aborts.
 * `clear()` must be called in a finally so the timer is stopped.
 */
export function linkAbortSignal(
  callerSignal?: AbortSignal,
  timeoutMs?: number,
): LinkedSignal {
  const controller = new AbortController();
  let timedOut = false;
  let timer: ReturnType<typeof setTimeout> | undefined;

  const onCallerAbort = () => {
    if (!controller.signal.aborted) controller.abort(callerSignal?.reason);
  };

  if (callerSignal) {
    if (callerSignal.aborted) {
      controller.abort(callerSignal.reason);
    } else {
      callerSignal.addEventListener("abort", onCallerAbort, { once: true });
    }
  }

  if (timeoutMs && timeoutMs > 0 && !controller.signal.aborted) {
    timer = setTimeout(() => {
      timedOut = true;
      controller.abort(new Error("Sleipnir call timed out."));
    }, timeoutMs);
  }

  const clear = () => {
    if (timer) clearTimeout(timer);
    if (callerSignal) callerSignal.removeEventListener("abort", onCallerAbort);
  };

  return { signal: controller.signal, clear, isTimeout: () => timedOut };
}