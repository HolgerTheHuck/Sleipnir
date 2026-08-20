import { ExecutionMode } from "./types.js";
import type { BearerProvider, DiscoveryInfo, SleipnirRequest, SleipnirResponse } from "./types.js";
/** Pro-Call-Optionen für einen REST-Aufruf. */
export interface CallOptions {
    /** Abbruch-Signal (Browser/Node); wirft CancelledError, nicht SleipnirError. */
    signal?: AbortSignal;
    /** Zusätzliche Header (z. B. Trace-Ids). */
    headers?: Record<string, string>;
    /** Call-Timeout in ms ( überschreibt clientweiten callTimeout). */
    timeout?: number;
}
/** Injizierbares fetch — permissiv, damit Test-Mocks und Node-Lib.fetch passen. */
export type FetchLike = (input: string | URL | Request, init?: RequestInit) => Promise<Response>;
/** Optionen für den REST-Client. */
export interface SleipnirRestClientOptions {
    /** Injizierbares fetch (Tests / älteres Node). Default: globales fetch. */
    fetch?: FetchLike;
    /** Standard-Header für jeden Request. */
    headers?: Record<string, string>;
    /** Bearer-Token (Authorization-Header) — String oder Provider-Funktion (rotierende JWTs). */
    bearer?: BearerProvider;
    /** Call-Timeout in ms. */
    callTimeout?: number;
    /** REST-Basispfad (Default "api/sleipnir"); Slashes werden abgeschnitten. */
    apiPath?: string;
}
/**
 * REST-Client für Sleipnir (HTTP/1.1 + JSON), isomorph via globalem `fetch`.
 *
 * `call`/`callBatch`/`discover` liefern die rohe {@link SleipnirResponse} bzw. das
 * Array und werfen nur bei **Transportfehlern** (Netzwerk, non-2xx HTTP) bzw.
 * Abbruch. Logische Nicht-2xx-Codes (im 200-Body) werden zurückgegeben — prüfe
 * `response.isSuccess`/`response.error`. `callJson`/`callBinary` werfen bei
 * logischem Nicht-2xx (Spiegel der C#-Methoden Call<T>/CallBinary).
 */
export declare class SleipnirRestClient {
    private readonly _baseUrl;
    private readonly _apiPath;
    private readonly _fetch;
    private readonly _headers;
    private _bearer?;
    private readonly _callTimeout?;
    constructor(baseUrl: string, options?: SleipnirRestClientOptions);
    /** Sendet einen einzelnen Request (überlädt: pre-built oder (controller,method,params)). */
    call(req: SleipnirRequest, opts?: CallOptions): Promise<SleipnirResponse>;
    call(controller: string, method: string, params?: Record<string, unknown> | unknown[], opts?: CallOptions): Promise<SleipnirResponse>;
    /** Ruft eine Methode auf und deserialisiert `response.data` als T. Wirft bei Nicht-2xx. */
    callJson<T>(controller: string, method: string, params?: Record<string, unknown> | unknown[], opts?: CallOptions): Promise<T | null>;
    callJson<T>(req: SleipnirRequest, opts?: CallOptions): Promise<T | null>;
    /** Ruft eine byte[]-Methode auf und liefert `response.content` als Uint8Array. Wirft bei Nicht-2xx. */
    callBinary(controller: string, method: string, params?: Record<string, unknown> | unknown[], opts?: CallOptions): Promise<Uint8Array | null>;
    callBinary(req: SleipnirRequest, opts?: CallOptions): Promise<Uint8Array | null>;
    /** Sendet einen Batch (Multi-Request). Auto-Setzt leere Ids auf `controller.method`. */
    callBatch(requests: SleipnirRequest[], mode?: ExecutionMode, opts?: CallOptions): Promise<SleipnirResponse[]>;
    /** Ruft die Discovery-Metadaten ab (GET /api/sleipnir/discovery). */
    discover(opts?: CallOptions): Promise<DiscoveryInfo>;
    /** Freiressourcen (noop für stateless fetch; vorhanden für Symmetrie). */
    dispose(): void;
    /**
     * Tauscht den Bearer zur Laufzeit (rotierende JWTs), ohne den Client neu zu
     * bauen. Akzeptiert einen String oder eine Provider-Funktion; der Wert wird
     * pro Call frisch aufgelöst.
     */
    setBearer(bearer: BearerProvider): void;
    private postJson;
    private postJsonArray;
    /** Löst den Bearer auf (Funktion → rufen, sonst Wert). */
    private resolveBearer;
    private buildHeaders;
}
//# sourceMappingURL=rest.d.ts.map