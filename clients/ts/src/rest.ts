import { linkAbortSignal } from "./abort.js";
import { SleipnirError, CancelledError } from "./errors.js";
import { buildSingle, buildMulti, fromBase64, normalizeResponse, normalizeResponses } from "./request.js";
import { ExecutionMode } from "./types.js";
import type {
  BearerProvider,
  DiscoveryInfo,
  SleipnirMultiRequest,
  SleipnirRequest,
  SleipnirResponse,
} from "./types.js";

/** Per-call options for a REST call. */
export interface CallOptions {
  /** Abort signal (browser/Node); throws CancelledError, not SleipnirError. */
  signal?: AbortSignal;
  /** Extra headers (e.g. trace ids). */
  headers?: Record<string, string>;
  /** Call timeout in ms ( overrides the client-wide callTimeout). */
  timeout?: number;
}

/** Injectable fetch — permissive, so test mocks and Node lib.fetch both fit. */
export type FetchLike = (
  input: string | URL | Request,
  init?: RequestInit,
) => Promise<Response>;

/** Options for the REST client. */
export interface SleipnirRestClientOptions {
  /** Injectable fetch (tests / older Node). Default: global fetch. */
  fetch?: FetchLike;
  /** Default headers for every request. */
  headers?: Record<string, string>;
  /** Bearer token (Authorization header) — string or provider function (rotating JWTs). */
  bearer?: BearerProvider;
  /** Call timeout in ms. */
  callTimeout?: number;
  /** REST base path (default "api/sleipnir"); slashes are trimmed. */
  apiPath?: string;
}

/**
 * REST client for Sleipnir (HTTP/1.1 + JSON), isomorphic via the global `fetch`.
 *
 * `call`/`callBatch`/`discover` return the raw {@link SleipnirResponse} or the
 * array and throw only on **transport errors** (network, non-2xx HTTP) or
 * cancellation. Logical non-2xx codes (carried in the 200 body) are returned —
 * check `response.isSuccess`/`response.error`. `callJson`/`callBinary` throw on
 * logical non-2xx (mirror of the C# methods Call<T>/CallBinary).
 */
export class SleipnirRestClient {
  private readonly _baseUrl: string;
  private readonly _apiPath: string;
  private readonly _fetch: FetchLike;
  private readonly _headers: Record<string, string>;
  private _bearer?: BearerProvider;
  private readonly _callTimeout?: number;

  constructor(baseUrl: string, options: SleipnirRestClientOptions = {}) {
    if (!baseUrl || baseUrl.trim().length === 0) {
      throw new Error("SleipnirRestClient: baseUrl must not be empty.");
    }
    this._baseUrl = baseUrl.endsWith("/") ? baseUrl : baseUrl + "/";
    this._apiPath = (options.apiPath ?? "api/sleipnir").replace(/^\/+|\/+$/g, "");
    // Storing the global `fetch` unbound and later calling it as `this._fetch(...)`
    // would throw "Illegal invocation" in the browser — browser fetch requires
    // `window`/`globalThis` as the receiver. Binding to `globalThis` makes the default
    // safe in browsers and Node alike (undici does not check the receiver).
    // Injected `options.fetch` (tests) is left untouched.
    this._fetch = options.fetch ?? fetch.bind(globalThis);
    this._headers = { ...(options.headers ?? {}) };
    this._bearer = options.bearer;
    this._callTimeout = options.callTimeout;
  }

  /** Sends a single request (overload: pre-built or (controller, method, params)). */
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

  /** Calls a method and deserializes `response.data` as T. Throws on non-2xx. */
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

  /** Calls a byte[] method and returns `response.content` as a Uint8Array. Throws on non-2xx. */
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

  /** Sends a batch (multi-request). Auto-fills empty ids with `controller.method`. */
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

  /** Fetches the discovery metadata (GET /api/sleipnir/discovery). */
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

  /** Releases resources (a no-op for stateless fetch; present for symmetry). */
  dispose(): void {
    // nothing to dispose
  }

  /**
   * Swaps the bearer at runtime (rotating JWTs) without rebuilding the client.
   * Accepts a string or a provider function; the value is resolved fresh
   * per call.
   */
  setBearer(bearer: BearerProvider): void {
    this._bearer = bearer;
  }

  // --- Internals ---

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
        // Transport-level error (400/429/499 …) -> synthetic response
        // (mirror of the C# SleipnirRestJsonClient non-2xx path).
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

  /** Resolves the bearer (function → invoke, otherwise the value). */
  private resolveBearer(): string | undefined {
    const b = this._bearer;
    return typeof b === "function" ? b() : b;
  }

  private buildHeaders(extra?: Record<string, string>): Record<string, string> {
    const headers: Record<string, string> = { ...this._headers };
    const token = this.resolveBearer();
    if (token) headers["Authorization"] = `Bearer ${token}`;
    if (extra) Object.assign(headers, extra);
    return headers;
  }
}

// --- Shared Helpers ---

/** Returns response.data as T; throws SleipnirError on non-2xx (mirror of Call<T>).
 *  Since the single-pass fix, data is already a structured value (no JSON string
 *  anymore) — no client-side JSON.parse needed. */
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
 * Maps a fetch error to the right client exception:
 * - Cancellation (AbortError/aborted) → CancelledError (unwrapped; timedOut on a timeout).
 * - otherwise → SleipnirError(0, "Transport error", cause).
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