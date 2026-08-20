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
export declare function linkAbortSignal(callerSignal?: AbortSignal, timeoutMs?: number): LinkedSignal;
//# sourceMappingURL=abort.d.ts.map