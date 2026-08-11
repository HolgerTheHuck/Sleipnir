// Isomorphe Helper zur Verknüpfung von Caller-AbortSignal und Call-Timeout.
// (Node 18 hat noch kein AbortSignal.any; daher manuelle Verkettung.)

export interface LinkedSignal {
  /** Signal, das feuert, wenn Caller-Signal oder Timeout auslöst. */
  signal: AbortSignal;
  /** Hebt den Timeout-Timer auf (aufrufen im finally des Callers). */
  clear: () => void;
  /** True, wenn das Signal wegen eines Timeouts (nicht Caller-Abbruch) feuerte. */
  isTimeout: () => boolean;
}

/**
 * Verknüpft ein optionales Caller-Signal mit einem optionalen Call-Timeout.
 * Löst der Caller ab → Signal aborted. Läuft der Timer ab → Signal aborted.
 * `clear()` muss im finally aufgerufen werden, um den Timer zu stoppen.
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