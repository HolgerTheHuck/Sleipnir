import { linkAbortSignal } from "./abort.js";
import { SleipnirError, CancelledError } from "./errors.js";
import { buildSingle, buildMulti, fromBase64, normalizeResponse, normalizeResponses } from "./request.js";
import { ExecutionMode } from "./types.js";
import type {
  DiscoveryInfo,
  SleipnirMultiRequest,
  SleipnirRequest,
  SleipnirResponse,
} from "./types.js";

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
export type FetchLike = (
  input: string | URL | Request,
  init?: RequestInit,
) => Promise<Response>;

/** Optionen für den REST-Client. */
export interface SleipnirRestClientOptions {
  /** Injizierbares fetch (Tests / älteres Node). Default: globales fetch. */
  fetch?: FetchLike;
  /** Standard-Header für jeden Request. */
  headers?: Record<string, string>;
  /** Bearer-Token (Authorization-Header). */
  bearer?: string;
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
export class SleipnirRestClient {
  private readonly _baseUrl: string;
  private readonly _apiPath: string;
  private readonly _fetch: FetchLike;
  private readonly _headers: Record<string, string>;
  private readonly _bearer?: string;
  private readonly _callTimeout?: number;

  constructor(baseUrl: string, options: SleipnirRestClientOptions = {}) {
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

  /** Sendet einen einzelnen Request (überlädt: pre-built oder (controller,method,params)). */
  async call(req: SleipnirRequest, opts?: CallOptions): Promise<SleipnirResponse>;
  async call(
    controller: string,
    method: string,
    params?: Record<string, unknown> | unknown[],
    opts?: CallOptions,
  ): Promise<SleipnirResponse>;
  async call(
    reqOrController: SleipnirRequest | string,
    methodOrOpts?: string | CallOptions,
    params?: Record<string, unknown> | unknown[],
    opts?: CallOptions,
  ): Promise<SleipnirResponse> {
    let request: SleipnirRequest;
    let callOpts: CallOptions | undefined;
    if (typeof reqOrController === "string") {
      request = buildSingle({
        controller: reqOrController,
        method: methodOrOpts as string,
        params,
      });
      callOpts = opts;
    } else {
      request = reqOrController;
      callOpts = (methodOrOpts as CallOptions) ?? opts;
    }
    return this.postJson(`${this._baseUrl}${this._apiPath}/json`, request, callOpts);
  }

  /** Ruft eine Methode auf und deserialisiert `response.data` als T. Wirft bei Nicht-2xx. */
  async callJson<T>(
    controller: string,
    method: string,
    params?: Record<string, unknown> | unknown[],
    opts?: CallOptions,
  ): Promise<T | null>;
  async callJson<T>(req: SleipnirRequest, opts?: CallOptions): Promise<T | null>;
  async callJson<T>(
    reqOrController: SleipnirRequest | string,
    methodOrOpts?: string | CallOptions,
    params?: Record<string, unknown> | unknown[],
    opts?: CallOptions,
  ): Promise<T | null> {
    const response =
      typeof reqOrController === "string"
        ? await this.call(reqOrController, methodOrOpts as string, params, opts)
        : await this.call(reqOrController, (methodOrOpts as CallOptions) ?? opts);
    return parseData<T>(response);
  }

  /** Ruft eine byte[]-Methode auf und liefert `response.content` als Uint8Array. Wirft bei Nicht-2xx. */
  async callBinary(
    controller: string,
    method: string,
    params?: Record<string, unknown> | unknown[],
    opts?: CallOptions,
  ): Promise<Uint8Array | null>;
  async callBinary(req: SleipnirRequest, opts?: CallOptions): Promise<Uint8Array | null>;
  async callBinary(
    reqOrController: SleipnirRequest | string,
    methodOrOpts?: string | CallOptions,
    params?: Record<string, unknown> | unknown[],
    opts?: CallOptions,
  ): Promise<Uint8Array | null> {
    const response =
      typeof reqOrController === "string"
        ? await this.call(reqOrController, methodOrOpts as string, params, opts)
        : await this.call(reqOrController, (methodOrOpts as CallOptions) ?? opts);
    if (!response.isSuccess) throw SleipnirError.fromResponse(response);
    return response.content ? fromBase64(response.content) : null;
  }

  /** Sendet einen Batch (Multi-Request). Auto-Setzt leere Ids auf `controller.method`. */
  async callBatch(
    requests: SleipnirRequest[],
    mode: ExecutionMode = ExecutionMode.Parallel,
    opts?: CallOptions,
  ): Promise<SleipnirResponse[]> {
    const normalized = requests.map((r) =>
      r.id ? r : { ...r, id: `${r.controller}.${r.method}` },
    );
    const multi: SleipnirMultiRequest = buildMulti(normalized, mode);
    const result = await this.postJsonArray(
      `${this._baseUrl}${this._apiPath}/json/multi`,
      multi,
      normalized[0]?.id,
      opts,
    );
    return result;
  }

  /** Ruft die Discovery-Metadaten ab (GET /api/sleipnir/discovery). */
  async discover(opts?: CallOptions): Promise<DiscoveryInfo> {
    const url = `${this._baseUrl}${this._apiPath}/discovery`;
    const { signal, clear, isTimeout } = linkAbortSignal(
      opts?.signal,
      opts?.timeout ?? this._callTimeout,
    );
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
      return (await response.json()) as DiscoveryInfo;
    } catch (err) {
      throw toTransportError(err, isTimeout);
    } finally {
      clear();
    }
  }

  /** Freiressourcen (noop für stateless fetch; vorhanden für Symmetrie). */
  dispose(): void {
    // nichts zu disposen
  }

  // --- Interna ---

  private async postJson(
    url: string,
    body: unknown,
    opts?: CallOptions,
  ): Promise<SleipnirResponse> {
    const { signal, clear, isTimeout } = linkAbortSignal(
      opts?.signal,
      opts?.timeout ?? this._callTimeout,
    );
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
          id: (body as SleipnirRequest)?.id ?? null,
          content: null,
          exposedDependencies: null,
          error: {
            code: response.status,
            message: `HTTP Error: ${response.status}`,
            details: text,
            requestId: (body as SleipnirRequest)?.id ?? null,
          },
          isSuccess: false,
        };
      }
      return normalizeResponse(JSON.parse(text) as SleipnirResponse);
    } catch (err) {
      throw toTransportError(err, isTimeout);
    } finally {
      clear();
    }
  }

  private async postJsonArray(
    url: string,
    body: SleipnirMultiRequest,
    firstId: string | undefined,
    opts?: CallOptions,
  ): Promise<SleipnirResponse[]> {
    const { signal, clear, isTimeout } = linkAbortSignal(
      opts?.signal,
      opts?.timeout ?? this._callTimeout,
    );
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
      return Array.isArray(parsed) ? normalizeResponses(parsed as SleipnirResponse[]) : [];
    } catch (err) {
      throw toTransportError(err, isTimeout);
    } finally {
      clear();
    }
  }

  private buildHeaders(extra?: Record<string, string>): Record<string, string> {
    const headers: Record<string, string> = { ...this._headers };
    if (this._bearer) headers["Authorization"] = `Bearer ${this._bearer}`;
    if (extra) Object.assign(headers, extra);
    return headers;
  }
}

// --- Shared Helpers ---

/** Gibt response.data als T zurück; wirft bei Nicht-2xx SleipnirError (Spiegel Call<T>).
 *  Seit dem Single-Pass-Fix ist data bereits ein strukturierter Wert (kein JSON-String
 *  mehr) — kein client-seitiges JSON.parse nötig. */
function parseData<T>(response: SleipnirResponse): T | null {
  if (response.isSuccess && response.data != null) {
    return response.data as T;
  }
  if (!response.isSuccess) throw SleipnirError.fromResponse(response);
  return null; // 204 / void
}

async function safeReadText(response: Response): Promise<string> {
  try {
    return await response.text();
  } catch {
    return "";
  }
}

/**
 * Mapt einen fetch-Fehler auf die richtige Client-Exception:
 * - Abbruch (AbortError/aborted) → CancelledError (unverpackt; timedOut, wenn Timeout).
 * - sonst → SleipnirError(0, "Transport error", cause).
 */
function toTransportError(err: unknown, isTimeout: () => boolean): Error {
  if (err instanceof SleipnirError || err instanceof CancelledError) return err;
  if (err instanceof Error) {
    const aborted =
      err.name === "AbortError" ||
      (typeof DOMException !== "undefined" && err instanceof DOMException && err.name === "AbortError");
    if (aborted) {
      const timedOut = isTimeout();
      return new CancelledError(
        timedOut ? "Sleipnir call timed out." : "Sleipnir call was cancelled.",
        timedOut,
      );
    }
    return new SleipnirError(0, `Transport error: ${err.message}`, { cause: err });
  }
  return new SleipnirError(0, "Transport error: unknown failure.", { cause: err });
}