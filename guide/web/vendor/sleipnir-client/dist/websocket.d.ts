import { ExecutionMode, SleipnirConnectionState } from "./types.js";
import type { BearerProvider, SleipnirRequest, SleipnirResponse } from "./types.js";
/** Minimale, browser-kompatible WebSocket-Schnittstelle (auch Node `ws`). */
export interface IWebSocket {
    readonly readyState: number;
    send(data: string): void;
    close(code?: number, reason?: string): void;
    onopen: (() => void) | null;
    onmessage: ((ev: {
        data: string | ArrayBuffer;
    }) => void) | null;
    onclose: ((ev: {
        code?: number;
        reason?: string;
    }) => void) | null;
    onerror: ((ev: unknown) => void) | null;
}
/** Fabrik für einen WebSocket (Browser global, Node `ws`, oder Injektion für Tests). */
export type WsFactory = (url: string, options: {
    headers?: Record<string, string>;
    protocols?: string | string[];
}) => IWebSocket;
/** Per-Call-Optionen für einen WebSocket-Aufruf. */
export interface WsCallOptions {
    signal?: AbortSignal;
    timeout?: number;
}
/**
 * Per-subscribe options (Phase R, resume). Extends {@link WsCallOptions} with an optional
 * per-subscription resume policy that overrides the client-wide `onResume` for this one
 * subscription.
 */
export interface SubscribeOptions extends WsCallOptions {
    /** Per-subscription reconnect decision hook (overrides the client-wide `onResume`). */
    resumePolicy?: ResumePolicy;
}
/**
 * Reconnect decision for a single event subscription (Phase R, resume). Consulted on
 * auto-reconnect, per subscription, before re-subscribing.
 *
 * - `"fresh"` (the default) re-subscribes with a fresh subscription — today's behavior, a new
 *   `subscriptionId`, the `eventId` counter restarts at 1, and events produced during the
 *   disconnect are lost.
 * - `"resume"` sends the durable `subscriptionId` + `lastEventId` so the server replays the gap
 *   from its disconnect buffer (at-least-once within the replay-buffer window; the client dedups
 *   by `eventId`). A Resume on a non-resumable event / expired buffer degrades to fresh (the
 *   server returns a new `subscriptionId`).
 * - `"drop"` ends the subscription without re-subscribing — the consumer's `onComplete` fires.
 */
export type ResumeDecision = "fresh" | "resume" | "drop";
/**
 * Context passed to a {@link ResumePolicy} on reconnect: the controller/method, the (durable)
 * `subscriptionId` of the dropped subscription, and the last `eventId` the client processed
 * (`null` when no event was received yet).
 */
export interface SubscriptionResumeContext {
    readonly controller: string;
    readonly method: string;
    readonly subscriptionId: string;
    readonly lastEventId: number | null;
}
/**
 * Per-subscription reconnect policy. Returns a {@link ResumeDecision} for the given context, or
 * `null` to abstain (the next policy in the fallback chain is consulted; a fully-null chain means
 * `"fresh"`). Wired via the client-wide `onResume` option and overridable per `subscribe` call.
 */
export type ResumePolicy = (ctx: SubscriptionResumeContext) => ResumeDecision | null;
/**
 * Event-Handler für eine Server-Push-Subscription (Phase 3). `onNext` wird pro
 * empfangenem Event-Frame mit dem deserialisierten Payload gerufen; `onComplete`/
 * `onError` beim Terminal-Frame. Ein Terminal-Frame beendet die Subscription
 * server-seitig — danach kommen keine weiteren Events für diese `subscriptionId`.
 */
export interface SubscribeHandlers<T> {
    onNext: (value: T) => void;
    onComplete?: () => void;
    onError?: (err: Error) => void;
}
/**
 * Handle auf eine aktive Server-Push-Subscription (Phase 3). `subscriptionId` ist
 * die server-seitig zugewiesene Correlation-Id der Event-Frames; `unsubscribe()`
 * sendet `kind:"unsubscribe"` und beendet die Lieferung (idempotent).
 *
 * Bei Auto-Reconnect re-subscribed der Client automatisch mit denselben
 * Parametern (neue `subscriptionId`); Gap-Events während des Disconnects gehen
 * verloren (at-most-once-while-disconnected). Ein terminaler `close()` ruft
 * `onError` aller aktiven Subscriptions.
 */
export interface SleipnirSubscription {
    /** Server-seitig zugewiesene Correlation-Id der Event-Frames. */
    readonly subscriptionId: string;
    /**
     * The highest `eventId` processed so far (0 until the first event carrying an `eventId`).
     * Live cursor — read at any time to snapshot progress, e.g. to hand to a cross-transport
     * resume (`SleipnirTransportRouter.resume`) after a transport switch.
     */
    readonly lastEventId: number;
    /** Stoppt die Event-Lieferung; sendet `kind:"unsubscribe"`. Idempotent. */
    unsubscribe(): Promise<void>;
}
/** Optionen für den WebSocket-Client. */
export interface SleipnirWebSocketClientOptions {
    /** WS-Pfad (Default "sleipnirws"). */
    wsPath?: string;
    /** Bearer-Token (Node: als Authorization-Header; Browser: als ?access_token=) — String oder Provider-Funktion (rotierende JWTs). */
    bearer?: BearerProvider;
    /** Call-Timeout in ms. */
    callTimeout?: number;
    /** Connect-Timeout in ms (Default 15000). */
    connectTimeout?: number;
    /** Injizierbare WebSocket-Fabrik (Tests). */
    WebSocketCtor?: WsFactory;
    /** Auto-Reconnect bei unerwartetem Disconnect (Default true). */
    reconnect?: boolean;
    /** Backoff-Intervalle in ms (Default SignalR-Spiegel). Leeres Array schaltet Reconnect aus. */
    reconnectDelays?: number[];
    /** Observer für Zustandswechsel (UI/Logs). */
    onStateChanged?: (state: SleipnirConnectionState) => void;
    /**
     * Client-wide resume policy (Phase R): consulted per subscription on auto-reconnect before
     * re-subscribing. Default (absent) → `"fresh"` for every subscription (non-breaking, today's
     * behavior). Overridable per `subscribe` call via `SubscribeOptions.resumePolicy`.
     */
    onResume?: ResumePolicy;
}
/**
 * WebSocket-Client für Sleipnir (RFC 6455 + JSON-Text-Frames), isomorph.
 *
 * Connect-Race (B1): konkurrierende `call()` erwarten denselben in-flight
 * Connect-Promise statt abgewiesen zu werden. Correlation (B3): jede Antwort
 * wird per `id` (Single) bzw. `requests[0].id` (Batch) zugeordnet; bei keinem
 * Match wird verworfen (kein Last-Resort-Fehl-Zuweisen).
 *
 * `call`/`callBatch` liefern die rohe Response (werfen nur bei Transport/Abbruch);
 * `callJson`/`callBinary` werfen bei logischem Nicht-2xx (Spiegel C#).
 *
 * **Known Limitation:** Der Server authentifiziert den WS-Upgrade nur über den
 * HTTP-Authorization-Header. Browser-WebSocket kann keine Header setzen —
 * authentifizierte Browser-WS-Calls brauchen serverseitige `?access_token=`-
 * Unterstützung (Roadmap) oder REST. Node (`ws`) sendet den Header korrekt.
 */
export declare class SleipnirWebSocketClient {
    private readonly _baseUrl;
    private readonly _wsPath;
    private _bearer?;
    private readonly _callTimeout?;
    private readonly _connectTimeout;
    private readonly _wsCtor?;
    private readonly _reconnect;
    private readonly _reconnectDelays;
    private readonly _onStateChanged?;
    /** Phase R: client-wide resume policy (null → Fresh for every subscription). */
    private readonly _onResume?;
    private _ws?;
    private _connectPromise?;
    private _pending;
    private _pendingSubscribes;
    private _subscriptions;
    private _state;
    private _closedByClient;
    private _disposed;
    private _reconnectPromise?;
    private _reconnectAbort?;
    constructor(baseUrl: string, options?: SleipnirWebSocketClientOptions);
    /** Aktueller Verbindungs-Zustand (Observer-Oberfläche für UI/Logs). */
    get state(): SleipnirConnectionState;
    /**
     * Tauscht den Bearer zur Laufzeit (rotierende JWTs), ohne den Client neu zu
     * bauen. Akzeptiert einen String oder eine Provider-Funktion. **WS:** der neue
     * Token greift ab dem nächsten Connect/Reconnect — eine bereits offene
     * Verbindung behält ihr Upgrade-Token (HTTP-Header sind nur beim Handshake
     * gesetzt).
     */
    setBearer(bearer: BearerProvider): void;
    /** Löst den Bearer auf (Funktion → rufen, sonst Wert). */
    private resolveBearer;
    private setState;
    /** Stellt eine offene Verbindung sicher (B1: concurrent-safe). */
    connect(): Promise<void>;
    /** Sendet einen einzelnen Request. */
    call(req: SleipnirRequest, opts?: WsCallOptions): Promise<SleipnirResponse>;
    /** Sendet einen Batch (Multi-Request). Auto-Setzt leere Ids. */
    callBatch(requests: SleipnirRequest[], mode?: ExecutionMode, opts?: WsCallOptions): Promise<SleipnirResponse[]>;
    /** Ruft auf und deserialisiert `response.data` als T. Wirft bei Nicht-2xx. */
    callJson<T>(req: SleipnirRequest, opts?: WsCallOptions): Promise<T | null>;
    /** Ruft eine byte[]-Methode auf; liefert `response.content` als Uint8Array. Wirft bei Nicht-2xx. */
    callBinary(req: SleipnirRequest, opts?: WsCallOptions): Promise<Uint8Array | null>;
    /**
     * Abonniert ein Server-Push-Event (Phase 3). Sendet `kind:"subscribe"` mit dem
     * übergebenen Request (Controller/Method/Params), wartet auf die Subscribe-
     * Response mit der `subscriptionId` und liefert ein
     * {@link SleipnirSubscription}-Handle. Eingehende Event-/Complete-/Error-Frames
     * werden per `subscriptionId` an `handlers` geroutet.
     *
     * Der Request wird via {@link SleipnirCall} gebaut (`SleipnirCall.init(c,m).with({...})`);
     * `subscribe` setzt `kind:"subscribe"` und (falls fehlt) eine `id`. Auf
     * Auto-Reconnect re-subscribed der Client automatisch mit demselben Request
     * (neue `subscriptionId`, gleiche `handlers`).
     */
    subscribe<T>(req: SleipnirRequest, handlers: SubscribeHandlers<T>, opts?: SubscribeOptions): Promise<SleipnirSubscription>;
    /** Schließt die Verbindung terminal; alle pending Calls werden abgelehnt. Kein Reconnect. */
    close(): void;
    /** Alias für {@link close} (Symmetrie zum REST-Client). */
    dispose(): void;
    /**
     * Rawer Connect ohne Zustandsverwaltung — der Aufrufer setzt Connecting/Connected.
     * Wichtig für den Reconnect-Loop: dieser hält den Zustand `Reconnecting` bei, bis
     * ein Versuch gelingt (Connected) oder der Backoff erschöpft ist (Disconnected).
     * Würde connectSlow selbst auf Connecting wechseln, würde ein fehlgeschlagener
     * Versuch den Zustand auf Connecting belassen und nebenläufige Calls würden den
     * Reconnect-Await-Pfad (state === Reconnecting) verpassen.
     */
    private connectSlow;
    private buildUrl;
    private sendAndAwait;
    private registerPending;
    private rejectPending;
    /** Räumt einen pending Call auf, ohne ihn abzulehnen (Sendefehler-Pfad). */
    private disposePending;
    private resolvePending;
    private rejectAllPending;
    /** Trägt einen pending Subscribe ein (Timeout/Abort analog registerPending). */
    private registerPendingSubscribe;
    /** Lehnt einen pending Subscribe ab (Timeout/Abort/Sendefehler/Disconnect). */
    private rejectPendingSubscribe;
    /** Räumt Timer/Abort-Listener + Map-Eintrag eines pending Subscribe (ohne reject). */
    private disposePendingSubscribe;
    private rejectAllPendingSubscribes;
    /** Terminal: alle aktiven Subscriptions auf onError setzen und verwerfen. */
    private cancelAllSubscriptions;
    /**
     * Sendet `kind:"unsubscribe"` für `subscriptionId` und entfernt die Subscription.
     * Idempotent: ein zweiter Aufruf für dieselbe Id ist ein No-op. Best-effort —
     * ein Sendefehler nach Disconnect wird still ignoriert (die Subscription ist
     * serverseitig ohnehin mit der Connection gestorben).
     */
    private unsubscribe;
    /**
     * Re-subscribes all active subscriptions after a reconnect. Phase R: per subscription the resume
     * policy is consulted (per-subscribe override → client-wide → `"fresh"`): `"fresh"` starts a new
     * subscription (new id, gap lost — today's behavior); `"resume"` sends the durable
     * `subscriptionId` + `lastEventId` so the server replays the gap; `"drop"` ends the subscription
     * without re-subscribing (`onComplete`). A Resume the server cannot satisfy (TTL expired /
     * non-resumable) degrades to fresh — the server returns a new `subscriptionId` and the dedup
     * cursor resets.
     */
    private resubscribeAll;
    /**
     * Phase R resume re-subscribe: sends `kind:"subscribe"` with the durable `subscriptionId` +
     * `lastEventId` so the server replays the disconnect gap, reusing the existing entry (preserving
     * its handlers + dedup cursor). Pre-registers under the durable id so any replay frame arriving
     * before the response is dispatched. On a degraded-to-fresh response (new id), the cursor resets.
     */
    private resubscribeResume;
    private onMessage;
    /** Routes an event/complete/error frame to the active subscription. */
    private dispatchEventFrame;
    /** Processes a subscribe response: extracts subscriptionId, registers the subscription. */
    private handleSubscribeResponse;
    private dropUnmatched;
    private onClosed;
    /** Startet den Hintergrund-Reconnect mit Backoff (idempotent). */
    private startReconnect;
    /** Bricht einen laufenden Hintergrund-Reconnect ab (terminal bei dispose). */
    private stopReconnect;
}
//# sourceMappingURL=websocket.d.ts.map