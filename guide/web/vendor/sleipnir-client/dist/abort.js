// Isomorphe Helper zur Verknüpfung von Caller-AbortSignal und Call-Timeout.
// (Node 18 hat noch kein AbortSignal.any; daher manuelle Verkettung.)
/**
 * Verknüpft ein optionales Caller-Signal mit einem optionalen Call-Timeout.
 * Löst der Caller ab → Signal aborted. Läuft der Timer ab → Signal aborted.
 * `clear()` muss im finally aufgerufen werden, um den Timer zu stoppen.
 */
export function linkAbortSignal(callerSignal, timeoutMs) {
    const controller = new AbortController();
    let timedOut = false;
    let timer;
    const onCallerAbort = () => {
        if (!controller.signal.aborted)
            controller.abort(callerSignal?.reason);
    };
    if (callerSignal) {
        if (callerSignal.aborted) {
            controller.abort(callerSignal.reason);
        }
        else {
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
        if (timer)
            clearTimeout(timer);
        if (callerSignal)
            callerSignal.removeEventListener("abort", onCallerAbort);
    };
    return { signal: controller.signal, clear, isTimeout: () => timedOut };
}
//# sourceMappingURL=abort.js.map