import { linkAbortSignal } from "./abort.js";
import { SleipnirError, CancelledError } from "./errors.js";
import { buildSingle, buildMulti, fromBase64, normalizeResponse, normalizeResponses } from "./request.js";
import { ExecutionMode } from "./types.js";
/**
 * REST-Client für Sleipnir (HTTP/1.1 + JSON), isomorph via globalem `fetch`.
 *
 * `call`/`callBatch`/`discover` liefern die rohe {@link SleipnirResponse} bzw. das
 * Array und werfen nur bei **Transportfehlern** (Netzwerk, non-2xx HTTP) bzw.
 * Abbruch. Logische Nicht-2xx-Codes (im 200-Body) werden zurückgegeben — prüfe
 * `response.isSuccess`/`response.error`. `callJson`/`callBinary` werfen bei
 * logischem Nicht-2xx (Spiegel der C#-Methoden Call<T>/CallBinary).
 */
export class SleipnirRestClient {
    _baseUrl;
    _apiPath;
    _fetch;
    _headers;
    _bearer;
    _callTimeout;
    constructor(baseUrl, options = {}) {
        if (!baseUrl || baseUrl.trim().length === 0) {
            throw new Error("SleipnirRestClient: baseUrl darf nicht leer sein.");
        }
        this._baseUrl = baseUrl.endsWith("/") ? baseUrl : baseUrl + "/";
        this._apiPath = (options.apiPath ?? "api/sleipnir").replace(/^\/+|\/+$/g, "");
        // Globales `fetch` unbinded speichern und später als `this._fetch(...)` rufen
        // würde im Browser "Illegal invocation" werfen — Browser-fetch verlangt
        // `window`/`globalThis` als Receiver. An `globalThis` binden macht den Default
        // in Browser und Node (undici prüft den Receiver nicht) gleichermaßen sicher.
        // Injiziertes `options.fetch` (Tests) bleibt unangetastet.
        this._fetch = options.fetch ?? fetch.bind(globalThis);
        this._headers = { ...(options.headers ?? {}) };
        this._bearer = options.bearer;
        this._callTimeout = options.callTimeout;
    }
    async call(reqOrController, methodOrOpts, params, opts) {
        let request;
        let callOpts;
        if (typeof reqOrController === "string") {
            request = buildSingle({
                controller: reqOrController,
                method: methodOrOpts,
                params,
            });
            callOpts = opts;
        }
        else {
            request = reqOrController;
            callOpts = methodOrOpts ?? opts;
        }
        return this.postJson(`${this._baseUrl}${this._apiPath}/json`, request, callOpts);
    }
    async callJson(reqOrController, methodOrOpts, params, opts) {
        const response = typeof reqOrController === "string"
            ? await this.call(reqOrController, methodOrOpts, params, opts)
            : await this.call(reqOrController, methodOrOpts ?? opts);
        return parseData(response);
    }
    async callBinary(reqOrController, methodOrOpts, params, opts) {
        const response = typeof reqOrController === "string"
            ? await this.call(reqOrController, methodOrOpts, params, opts)
            : await this.call(reqOrController, methodOrOpts ?? opts);
        if (!response.isSuccess)
            throw SleipnirError.fromResponse(response);
        return response.content ? fromBase64(response.content) : null;
    }
    /** Sendet einen Batch (Multi-Request). Auto-Setzt leere Ids auf `controller.method`. */
    async callBatch(requests, mode = ExecutionMode.Parallel, opts) {
        const normalized = requests.map((r) => r.id ? r : { ...r, id: `${r.controller}.${r.method}` });
        const multi = buildMulti(normalized, mode);
        const result = await this.postJsonArray(`${this._baseUrl}${this._apiPath}/json/multi`, multi, normalized[0]?.id, opts);
        return result;
    }
    /** Ruft die Discovery-Metadaten ab (GET /api/sleipnir/discovery). */
    async discover(opts) {
        const url = `${this._baseUrl}${this._apiPath}/discovery`;
        const { signal, clear, isTimeout } = linkAbortSignal(opts?.signal, opts?.timeout ?? this._callTimeout);
        try {
            const response = await this._fetch(url, {
                method: "GET",
                headers: this.buildHeaders(opts?.headers),
                signal,
            });
            if (!response.ok) {
                const text = await safeReadText(response);
                throw new SleipnirError(response.status, `HTTP Error: ${response.status}`, {
                    details: text,
                });
            }
            return (await response.json());
        }
        catch (err) {
            throw toTransportError(err, isTimeout);
        }
        finally {
            clear();
        }
    }
    /** Freiressourcen (noop für stateless fetch; vorhanden für Symmetrie). */
    dispose() {
        // nichts zu disposen
    }
    /**
     * Tauscht den Bearer zur Laufzeit (rotierende JWTs), ohne den Client neu zu
     * bauen. Akzeptiert einen String oder eine Provider-Funktion; der Wert wird
     * pro Call frisch aufgelöst.
     */
    setBearer(bearer) {
        this._bearer = bearer;
    }
    // --- Interna ---
    async postJson(url, body, opts) {
        const { signal, clear, isTimeout } = linkAbortSignal(opts?.signal, opts?.timeout ?? this._callTimeout);
        try {
            const response = await this._fetch(url, {
                method: "POST",
                headers: { ...this.buildHeaders(opts?.headers), "Content-Type": "application/json" },
                body: JSON.stringify(body),
                signal,
            });
            const text = await safeReadText(response);
            if (!response.ok) {
                // Transport-Level-Fehler (400/429/499 …) -> synthetische Response
                // (Spiegel C# SleipnirRestJsonClient non-2xx-Pfad).
                return {
                    code: response.status,
                    id: body?.id ?? null,
                    content: null,
                    exposedDependencies: null,
                    error: {
                        code: response.status,
                        message: `HTTP Error: ${response.status}`,
                        details: text,
                        requestId: body?.id ?? null,
                    },
                    isSuccess: false,
                };
            }
            return normalizeResponse(JSON.parse(text));
        }
        catch (err) {
            throw toTransportError(err, isTimeout);
        }
        finally {
            clear();
        }
    }
    async postJsonArray(url, body, firstId, opts) {
        const { signal, clear, isTimeout } = linkAbortSignal(opts?.signal, opts?.timeout ?? this._callTimeout);
        try {
            const response = await this._fetch(url, {
                method: "POST",
                headers: { ...this.buildHeaders(opts?.headers), "Content-Type": "application/json" },
                body: JSON.stringify(body),
                signal,
            });
            const text = await safeReadText(response);
            if (!response.ok) {
                return [
                    {
                        code: response.status,
                        id: firstId ?? null,
                        content: null,
                        exposedDependencies: null,
                        error: {
                            code: response.status,
                            message: `HTTP Error: ${response.status}`,
                            details: text,
                            requestId: firstId ?? null,
                        },
                        isSuccess: false,
                    },
                ];
            }
            const parsed = JSON.parse(text);
            return Array.isArray(parsed) ? normalizeResponses(parsed) : [];
        }
        catch (err) {
            throw toTransportError(err, isTimeout);
        }
        finally {
            clear();
        }
    }
    /** Löst den Bearer auf (Funktion → rufen, sonst Wert). */
    resolveBearer() {
        const b = this._bearer;
        return typeof b === "function" ? b() : b;
    }
    buildHeaders(extra) {
        const headers = { ...this._headers };
        const token = this.resolveBearer();
        if (token)
            headers["Authorization"] = `Bearer ${token}`;
        if (extra)
            Object.assign(headers, extra);
        return headers;
    }
}
// --- Shared Helpers ---
/** Gibt response.data als T zurück; wirft bei Nicht-2xx SleipnirError (Spiegel Call<T>).
 *  Seit dem Single-Pass-Fix ist data bereits ein strukturierter Wert (kein JSON-String
 *  mehr) — kein client-seitiges JSON.parse nötig. */
function parseData(response) {
    if (response.isSuccess && response.data != null) {
        return response.data;
    }
    if (!response.isSuccess)
        throw SleipnirError.fromResponse(response);
    return null; // 204 / void
}
async function safeReadText(response) {
    try {
        return await response.text();
    }
    catch {
        return "";
    }
}
/**
 * Mapt einen fetch-Fehler auf die richtige Client-Exception:
 * - Abbruch (AbortError/aborted) → CancelledError (unverpackt; timedOut, wenn Timeout).
 * - sonst → SleipnirError(0, "Transport error", cause).
 */
function toTransportError(err, isTimeout) {
    if (err instanceof SleipnirError || err instanceof CancelledError)
        return err;
    if (err instanceof Error) {
        const aborted = err.name === "AbortError" ||
            (typeof DOMException !== "undefined" && err instanceof DOMException && err.name === "AbortError");
        if (aborted) {
            const timedOut = isTimeout();
            return new CancelledError(timedOut ? "Sleipnir call timed out." : "Sleipnir call was cancelled.", timedOut);
        }
        return new SleipnirError(0, `Transport error: ${err.message}`, { cause: err });
    }
    return new SleipnirError(0, "Transport error: unknown failure.", { cause: err });
}
//# sourceMappingURL=rest.js.map