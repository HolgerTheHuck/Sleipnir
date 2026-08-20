import type { BearerProvider } from "./types.js";
import type { ResumeDecision, ResumePolicy, SubscriptionResumeContext, SubscribeHandlers, SleipnirSubscription } from "./websocket.js";
export type { ResumeDecision, ResumePolicy, SubscriptionResumeContext, SubscribeHandlers, SleipnirSubscription, };
/** Injizierbares fetch — permissiv, damit Test-Mocks und Node-Lib.fetch passen. */
export type SseFetchLike = (input: string | URL | Request, init?: RequestInit) => Promise<Response>;
/** Optionen für den SSE-Client. */
export interface SleipnirSseClientOptions {
    /** REST-Basispfad (Default "api/sleipnir"); Slashes werden abgeschnitten. */
    apiPath?: string;
    /** Bearer-Token (Authorization-Header) — String oder Provider-Funktion (rotierende JWTs). */
    bearer?: BearerProvider;
    /** Injizierbares fetch (Tests / älteres Node). Default: globales fetch. */
    fetch?: SseFetchLike;
    /** Standard-Header für jeden Request. */
    headers?: Record<string, string>;
    /** Auto-Reconnect bei unerwartetem Disconnect (Default true). */
    reconnect?: boolean;
    /** Backoff-Intervalle in ms (Default SignalR-Spiegel). Leeres Array schaltet Reconnect aus. */
    reconnectDelays?: number[];
    /**
     * Client-wide resume policy (Phase R): consulted per subscription on reconnect before
     * re-subscribing. Default (absent) → `"fresh"` (re-subscribe with a new subscriptionId; gap
     * events lost). `"resume"` reconnects to the durable subscriptionId with Last-Event-Id. A
     * resume on a non-resumable event / expired buffer degrades to fresh (server returns 410).
     */
    onResume?: ResumePolicy;
}
/** Pro-Subscription-Optionen (Spiegel der WS `SubscribeOptions`). */
export interface SseSubscribeOptions {
    /** Abbruch-Signal (Browser/Node); beendet die Subscription ohne Reconnect. */
    signal?: AbortSignal;
    /** Per-subscription resume policy (überschreibt clientweiten `onResume`). */
    resumePolicy?: ResumePolicy;
    /** Zusätzliche Header für diesen Subscribe-Request. */
    headers?: Record<string, string>;
}
/**
 * Optionen für {@link SleipnirSseClient.resume} (Cross-Transport-Resume einer durable
 * Subscription anhand ihrer `subscriptionId`). Im Gegensatz zu {@link SseSubscribeOptions}
 * gibt es keinen Fresh-Modus — ein Drop verbindet immer im Resume-Modus neu (selbe
 * Resume-URL mit aktualisiertem Cursor), bis `complete`/`error`/`410`/Abbruch.
 */
export interface SseResumeOptions {
    /** Abbruch-Signal; beendet die Resume-Subscription ohne Reconnect. */
    signal?: AbortSignal;
    /** Zusätzliche Header für jeden Resume-Request. */
    headers?: Record<string, string>;
    /** Auto-Reconnect bei Drop (Default: clientweiter `reconnect`). */
    reconnect?: boolean;
    /** Backoff-Intervalle in ms (Default: clientweite `reconnectDelays`). */
    reconnectDelays?: number[];
    /** Per-subscription resume policy — `"drop"` beendet, sonst wird fortgesetzt (Default: resume). */
    resumePolicy?: ResumePolicy;
}
/**
 * SSE-Client für Sleipnir-Events (`[SleipnirEvent]` + `IObservable<T>`) über REST
 * (`text/event-stream`). Isomorph via globalem `fetch`. Eine `subscribe`-Aktivierung öffnet
 * genau einen SSE-Stream (ein GET = eine Subscription); auf Disconnect greift der Resume-
 * Mechanismus (sofern `reconnect` an). Siehe `PROTOCOL.md` → "REST Events (SSE)".
 */
export declare class SleipnirSseClient {
    private readonly _baseUrl;
    private readonly _apiPath;
    private readonly _fetch;
    private readonly _headers;
    private _bearer?;
    private readonly _reconnect;
    private readonly _reconnectDelays;
    private _onResume?;
    constructor(baseUrl: string, options?: SleipnirSseClientOptions);
    /** Tauscht den Bearer (String oder Provider-Funktion) für künftige Requests aus. */
    setBearer(bearer: BearerProvider): void;
    /**
     * Öffnet eine SSE-Subscription auf `{controller}.{method}`. Method-Argumente reisen als
     * Query-Parameter (GET hat keinen Body); jeder Wert wird JSON-kodiert gesendet, damit der
     * Server ihn typengetreu zurück-parsed (ein String `"hi"` als `?msg=%22hi%22`). Löst mit dem
     * `SleipnirSubscription`-Handle auf, sobald der Server-Ack eintrifft (erste SSE-Event-Block).
     */
    subscribe<T>(controller: string, method: string, handlers: SubscribeHandlers<T>, params?: Record<string, unknown>, opts?: SseSubscribeOptions): Promise<SleipnirSubscription>;
    /**
     * Setzt eine durable Subscription anhand ihrer server-seitigen `subscriptionId` fort: der
     * Server replayt die Gap ab `lastEventId` und liefert dann live weiter — über einen neuen
     * SSE-Stream. Cross-Transport: der serverseitige `SleipnirSubscriptionStore` ist prozessweit,
     * daher ist eine über WebSocket (oder einen anderen SSE-Stream) erzeugte `subscriptionId`
     * hier resumable. Das ist der Einstiegspunkt, den der Transport-Router beim Auto-Fallback
     * (WS → REST+SSE) nutzt, um eine Event-Subscription an SSE zu übergeben.
     *
     * Im Gegensatz zu {@link subscribe} werden keine Controller/Method/Params benötigt — die
     * Resume-URL ist selbstbeziehend (`GET /events/{subscriptionId}?lastEventId=…`). Bei einem
     * Drop verbindet der Client im Resume-Modus neu (selbe URL, aktualisierter Cursor);
     * `410 Gone` (durable Subscription abgelaufen/geräumt) terminiert mit `onError` — es gibt
     * keinen Fresh-Fallback, da keine Fresh-Params vorliegen.
     */
    resume<T>(subscriptionId: string, lastEventId: number, handlers: SubscribeHandlers<T>, opts?: SseResumeOptions): Promise<SleipnirSubscription>;
    private resolveBearer;
    private buildFreshUrl;
    private buildResumeUrl;
}
//# sourceMappingURL=sse.d.ts.map