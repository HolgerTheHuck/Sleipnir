import { SleipnirError, CancelledError } from "./errors.js";
import { ExecutionMode, SleipnirConnectionState } from "./types.js";
import type { BearerProvider, SleipnirMultiRequest, SleipnirRequest, SleipnirResponse } from "./types.js";
import { fromBase64, normalizeResponse, normalizeResponses } from "./request.js";

const READY_CONNECTING = 0;
const READY_OPEN = 1;
const READY_CLOSING = 2;
const READY_CLOSED = 3;

/** Standard-Backoff-Intervalle in ms (Spiegel von SignalR): 2,2,5,5,10,10,30,30s,1,1,5min. */
const DEFAULT_RECONNECT_DELAYS = [
  2_000, 2_000, 5_000, 5_000, 10_000, 10_000, 30_000, 30_000, 60_000, 60_000, 300_000,
];

function sleep(ms: number, signal?: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    if (signal?.aborted) {
      reject(new Error("aborted"));
      return;
    }
    const t = setTimeout(() => {
      signal?.removeEventListener("abort", onAbort);
      resolve();
    }, ms);
    const onAbort = () => {
      clearTimeout(t);
      signal?.removeEventListener("abort", onAbort);
      reject(new Error("aborted"));
    };
    signal?.addEventListener("abort", onAbort, { once: true });
  });
}

/** Minimale, browser-kompatible WebSocket-Schnittstelle (auch Node `ws`). */
export interface IWebSocket {
  readonly readyState: number;
  send(data: string): void;
  close(code?: number, reason?: string): void;
  onopen: (() => void) | null;
  onmessage: ((ev: { data: string | ArrayBuffer }) => void) | null;
  onclose: ((ev: { code?: number; reason?: string }) => void) | null;
  onerror: ((ev: unknown) => void) | null;
}

/** Fabrik für einen WebSocket (Browser global, Node `ws`, oder Injektion für Tests). */
export type WsFactory = (
  url: string,
  options: { headers?: Record<string, string>; protocols?: string | string[] },
) => IWebSocket;

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

interface PendingCall {
  resolve: (v: SleipnirResponse | SleipnirResponse[]) => void;
  reject: (e: Error) => void;
  isBatch: boolean;
  timer?: ReturnType<typeof setTimeout>;
  onCallerAbort?: () => void;
  callerSignal?: AbortSignal;
}

interface PendingSubscribe {
  resolve: (sub: SleipnirSubscription) => void;
  reject: (e: Error) => void;
  /** Original request (without `kind`) — retained for re-subscribe on reconnect. */
  request: SleipnirRequest;
  handlers: SubscribeHandlers<unknown>;
  /** Phase R: when set, this pending subscribe is a resume re-subscribe — reuse the existing
   * ActiveSubscription (preserve its dedup cursor) instead of creating a new entry. */
  resumeEntry?: { oldId: string; entry: ActiveSubscription };
  /** Phase R: per-subscribe resume policy override to store on the new ActiveSubscription. */
  resumePolicy?: ResumePolicy;
  timer?: ReturnType<typeof setTimeout>;
  onCallerAbort?: () => void;
  callerSignal?: AbortSignal;
}

interface ActiveSubscription {
  handlers: SubscribeHandlers<unknown>;
  /** Original request (without `kind`) — re-sent verbatim on reconnect. */
  request: SleipnirRequest;
  /** Phase R: highest event `eventId` processed for this subscription (0 = none yet). Used to
   * dedup replayed frames (at-least-once within the replay window) and as the resume cursor. */
  lastEventId: number;
  /** Phase R: per-subscribe resume policy override (null → fall back to the client-wide `onResume`). */
  resumePolicy?: ResumePolicy;
}

/** Monotonic client-side id counter for unsubscribe requests (avoids id reuse). */
let _unsubscribeIdSeq = 0;

let _defaultFactoryPromise: Promise<WsFactory> | undefined;

/** Löst die Standard-WebSocket-Fabrik (Browser global bzw. Node `ws`) lazily auf. */
async function resolveDefaultFactory(): Promise<WsFactory> {
  if (_defaultFactoryPromise) return _defaultFactoryPromise;
  _defaultFactoryPromise = (async () => {
    if (typeof (globalThis as any).WebSocket !== "undefined") {
      return (url, opts) =>
        new (globalThis as any).WebSocket(url, opts?.protocols) as IWebSocket;
    }
    // Node: `ws` als optionalDependency; lazy geladen.
    const mod: any = await import("ws");
    const WS = mod.WebSocket ?? mod.default?.WebSocket ?? mod.default;
    if (typeof WS !== "function") {
      throw new SleipnirError(
        0,
        "No WebSocket implementation found. In Node, install the optional 'ws' package.",
      );
    }
    return (url, opts) => new WS(url, opts?.protocols, { headers: opts?.headers }) as IWebSocket;
  })();
  return _defaultFactoryPromise;
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
export class SleipnirWebSocketClient {
  private readonly _baseUrl: string;
  private readonly _wsPath: string;
  private _bearer?: BearerProvider;
  private readonly _callTimeout?: number;
  private readonly _connectTimeout: number;
  private readonly _wsCtor?: WsFactory;
  private readonly _reconnect: boolean;
  private readonly _reconnectDelays: number[];
  private readonly _onStateChanged?: (state: SleipnirConnectionState) => void;
  /** Phase R: client-wide resume policy (null → Fresh for every subscription). */
  private readonly _onResume?: ResumePolicy;

  private _ws?: IWebSocket;
  private _connectPromise?: Promise<void>;
  private _pending = new Map<string, PendingCall>();
  private _pendingSubscribes = new Map<string, PendingSubscribe>();
  private _subscriptions = new Map<string, ActiveSubscription>();
  private _state: SleipnirConnectionState = SleipnirConnectionState.Disconnected;
  private _closedByClient = false;
  private _disposed = false;
  private _reconnectPromise?: Promise<void>;
  private _reconnectAbort?: AbortController;

  constructor(baseUrl: string, options: SleipnirWebSocketClientOptions = {}) {
    if (!baseUrl || baseUrl.trim().length === 0) {
      throw new Error("SleipnirWebSocketClient: baseUrl darf nicht leer sein.");
    }
    this._baseUrl = baseUrl.replace(/\/+$/, "");
    this._wsPath = (options.wsPath ?? "sleipnirws").replace(/^\/+|\/+$/g, "");
    this._bearer = options.bearer;
    this._callTimeout = options.callTimeout;
    this._connectTimeout = options.connectTimeout ?? 15000;
    this._wsCtor = options.WebSocketCtor;
    this._reconnectDelays = options.reconnectDelays ?? DEFAULT_RECONNECT_DELAYS;
    this._reconnect = (options.reconnect ?? true) && this._reconnectDelays.length > 0;
    this._onStateChanged = options.onStateChanged;
    this._onResume = options.onResume;
  }

  /** Aktueller Verbindungs-Zustand (Observer-Oberfläche für UI/Logs). */
  get state(): SleipnirConnectionState {
    return this._state;
  }

  /**
   * Tauscht den Bearer zur Laufzeit (rotierende JWTs), ohne den Client neu zu
   * bauen. Akzeptiert einen String oder eine Provider-Funktion. **WS:** der neue
   * Token greift ab dem nächsten Connect/Reconnect — eine bereits offene
   * Verbindung behält ihr Upgrade-Token (HTTP-Header sind nur beim Handshake
   * gesetzt).
   */
  setBearer(bearer: BearerProvider): void {
    this._bearer = bearer;
  }

  /** Löst den Bearer auf (Funktion → rufen, sonst Wert). */
  private resolveBearer(): string | undefined {
    const b = this._bearer;
    return typeof b === "function" ? b() : b;
  }

  private setState(s: SleipnirConnectionState): void {
    this._state = s;
    try {
      this._onStateChanged?.(s);
    } catch {
      /* Observer-Fehler nicht fatal */
    }
  }

  /** Stellt eine offene Verbindung sicher (B1: concurrent-safe). */
  async connect(): Promise<void> {
    if (this._disposed) throw new Error("SleipnirWebSocketClient: disposed.");
    if (this._ws && this._ws.readyState === READY_OPEN) return;

    // Läuft ein Hintergrund-Reconnect? Darauf warten (nicht selbst verbinden),
    // damit parallele Calls denselben in-flight Reconnect teilen.
    if (this._reconnectPromise && this._state === SleipnirConnectionState.Reconnecting) {
      try {
        await this._reconnectPromise;
      } catch {
        /* reconnect-Fehler unten neu bewerten */
      }
      if (this._ws && this._ws.readyState === READY_OPEN) return;
    }

    if (this._connectPromise) return this._connectPromise;
    this.setState(SleipnirConnectionState.Connecting);
    this._connectPromise = this.connectSlow()
      .then(() => this.setState(SleipnirConnectionState.Connected))
      .finally(() => {
        this._connectPromise = undefined;
      });
    return this._connectPromise;
  }

  /** Sendet einen einzelnen Request. */
  async call(req: SleipnirRequest, opts?: WsCallOptions): Promise<SleipnirResponse> {
    if (!req.id) req.id = `${req.controller}.${req.method}`;
    await this.connect();
    return this.sendAndAwait(req, false, opts) as Promise<SleipnirResponse>;
  }

  /** Sendet einen Batch (Multi-Request). Auto-Setzt leere Ids. */
  async callBatch(
    requests: SleipnirRequest[],
    mode: ExecutionMode = ExecutionMode.Parallel,
    opts?: WsCallOptions,
  ): Promise<SleipnirResponse[]> {
    const normalized = requests.map((r) =>
      r.id ? r : { ...r, id: `${r.controller}.${r.method}` },
    );
    await this.connect();
    const multi: SleipnirMultiRequest = { requests: normalized, mode };
    const key = normalized[0]?.id;
    if (!key) throw new SleipnirError(0, "Batch requires at least one request with an id.");
    return this.sendAndAwait(multi as unknown as SleipnirRequest, true, opts, key) as Promise<
      SleipnirResponse[]
    >;
  }

  /** Ruft auf und deserialisiert `response.data` als T. Wirft bei Nicht-2xx. */
  async callJson<T>(req: SleipnirRequest, opts?: WsCallOptions): Promise<T | null> {
    const response = await this.call(req, opts);
    return parseData<T>(response);
  }

  /** Ruft eine byte[]-Methode auf; liefert `response.content` als Uint8Array. Wirft bei Nicht-2xx. */
  async callBinary(req: SleipnirRequest, opts?: WsCallOptions): Promise<Uint8Array | null> {
    const response = await this.call(req, opts);
    if (!response.isSuccess) throw SleipnirError.fromResponse(response);
    return response.content ? fromBase64(response.content) : null;
  }

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
  async subscribe<T>(
    req: SleipnirRequest,
    handlers: SubscribeHandlers<T>,
    opts?: SubscribeOptions,
  ): Promise<SleipnirSubscription> {
    if (this._disposed) throw new Error("SleipnirWebSocketClient: disposed.");
    if (!req.id) req.id = `${req.controller}.${req.method}`;
    const id = req.id;
    await this.connect();

    const promise = this.registerPendingSubscribe(
      id,
      req,
      handlers as SubscribeHandlers<unknown>,
      opts,
    );
    try {
      const ws = this._ws;
      if (!ws || ws.readyState !== READY_OPEN) {
        this.disposePendingSubscribe(id);
        throw new SleipnirError(0, "WebSocket is not open.");
      }
      // kind:"subscribe" routet serverseitig nach SubscribeAsync; der Rest ist ein
      // normaler SleipnirRequest (Controller/Method/Params/id).
      ws.send(JSON.stringify({ ...req, kind: "subscribe" }));
      return promise;
    } catch (err) {
      this.rejectPendingSubscribe(
        id,
        err instanceof SleipnirError
          ? err
          : new SleipnirError(0, `WebSocket send error: ${(err as Error)?.message ?? err}`),
      );
      throw err instanceof SleipnirError
        ? err
        : new SleipnirError(0, `WebSocket send error: ${(err as Error)?.message ?? err}`);
    }
  }

  /** Schließt die Verbindung terminal; alle pending Calls werden abgelehnt. Kein Reconnect. */
  close(): void {
    this._closedByClient = true;
    this._disposed = true;
    this.stopReconnect();
    this.rejectAllPending(new SleipnirError(0, "WebSocket closed by client."));
    this.rejectAllPendingSubscribes(new SleipnirError(0, "WebSocket closed by client."));
    this.cancelAllSubscriptions(new SleipnirError(0, "WebSocket closed by client."));
    if (this._ws) {
      try {
        this._ws.close(1000, "client close");
      } catch {
        // ignore
      }
    }
    this._ws = undefined;
    this.setState(SleipnirConnectionState.Disconnected);
  }

  /** Alias für {@link close} (Symmetrie zum REST-Client). */
  dispose(): void {
    this.close();
  }

  // --- Interna ---

  /**
   * Rawer Connect ohne Zustandsverwaltung — der Aufrufer setzt Connecting/Connected.
   * Wichtig für den Reconnect-Loop: dieser hält den Zustand `Reconnecting` bei, bis
   * ein Versuch gelingt (Connected) oder der Backoff erschöpft ist (Disconnected).
   * Würde connectSlow selbst auf Connecting wechseln, würde ein fehlgeschlagener
   * Versuch den Zustand auf Connecting belassen und nebenläufige Calls würden den
   * Reconnect-Await-Pfad (state === Reconnecting) verpassen.
   */
  private async connectSlow(): Promise<void> {
    const factory = this._wsCtor ?? (await resolveDefaultFactory());
    const isBrowserWs = typeof (globalThis as any).WebSocket !== "undefined";
    const url = this.buildUrl(isBrowserWs);
    const token = this.resolveBearer();
    const headers =
      !isBrowserWs && token ? { Authorization: `Bearer ${token}` } : undefined;
    const ws = factory(url, { headers });
    this._ws = ws;

    await new Promise<void>((resolve, reject) => {
      let opened = false;
      let connectTimer: ReturnType<typeof setTimeout> | undefined;

      const fail = (err: Error) => {
        if (connectTimer) clearTimeout(connectTimer);
        if (!opened) reject(err);
      };

      connectTimer = setTimeout(
        () => fail(new SleipnirError(0, "WebSocket connect timed out.")),
        this._connectTimeout,
      );

      ws.onopen = () => {
        opened = true;
        if (connectTimer) clearTimeout(connectTimer);
        resolve();
      };
      ws.onmessage = (ev) => this.onMessage(ev.data);
      ws.onclose = (ev) => {
        this.onClosed();
        if (!opened) {
          fail(new SleipnirError(0, `WebSocket closed before open (code ${ev?.code ?? "n/a"}).`));
        }
      };
      ws.onerror = () => {
        if (!opened) fail(new SleipnirError(0, "WebSocket connection failed."));
        // nach open folgt onclose, das alle pending ablehnt.
      };
    });
  }

  private buildUrl(isBrowserWs: boolean): string {
    let base = this._baseUrl;
    base = base.replace(/^http:/i, "ws:").replace(/^https:/i, "wss:");
    let url = `${base}/${this._wsPath}`;
    const token = this.resolveBearer();
    if (isBrowserWs && token) {
      url += `?access_token=${encodeURIComponent(token)}`;
    }
    return url;
  }

  private sendAndAwait(
    payload: SleipnirRequest,
    isBatch: boolean,
    opts: WsCallOptions | undefined,
    explicitKey?: string,
  ): Promise<SleipnirResponse | SleipnirResponse[]> {
    const key = explicitKey ?? payload.id!;
    const deferred = this.registerPending(key, isBatch, opts);
    try {
      const ws = this._ws;
      if (!ws || ws.readyState !== READY_OPEN) {
        this.disposePending(key);
        return Promise.reject(new SleipnirError(0, "WebSocket is not open."));
      }
      ws.send(JSON.stringify(payload));
      return deferred.promise;
    } catch (err) {
      this.disposePending(key);
      return Promise.reject(
        err instanceof SleipnirError
          ? err
          : new SleipnirError(0, `WebSocket send error: ${(err as Error)?.message ?? err}`),
      );
    }
  }

  private registerPending(
    key: string,
    isBatch: boolean,
    opts: WsCallOptions | undefined,
  ): { promise: Promise<SleipnirResponse | SleipnirResponse[]> } {
    let resolve!: (v: SleipnirResponse | SleipnirResponse[]) => void;
    let reject!: (e: Error) => void;
    const promise = new Promise<SleipnirResponse | SleipnirResponse[]>((res, rej) => {
      resolve = res;
      reject = rej;
    });

    const pending: PendingCall = { resolve, reject, isBatch };
    const timeoutMs = opts?.timeout ?? this._callTimeout;

    if (timeoutMs && timeoutMs > 0) {
      pending.timer = setTimeout(
        () => this.rejectPending(key, new CancelledError("Sleipnir call timed out.", true)),
        timeoutMs,
      );
    }

    if (opts?.signal) {
      if (opts.signal.aborted) {
        // Sofort abgelehnt (unverpackt).
        queueMicrotask(() => this.rejectPending(key, new CancelledError("Sleipnir call was cancelled.")));
      } else {
        pending.callerSignal = opts.signal;
        pending.onCallerAbort = () =>
          this.rejectPending(key, new CancelledError("Sleipnir call was cancelled."));
        opts.signal.addEventListener("abort", pending.onCallerAbort, { once: true });
      }
    }

    this._pending.set(key, pending);
    return { promise };
  }

  private rejectPending(key: string, err: Error): void {
    const pending = this._pending.get(key);
    if (!pending) return;
    if (pending.timer) clearTimeout(pending.timer);
    if (pending.onCallerAbort && pending.callerSignal) {
      pending.callerSignal.removeEventListener("abort", pending.onCallerAbort);
    }
    this._pending.delete(key);
    pending.reject(err);
  }

  /** Räumt einen pending Call auf, ohne ihn abzulehnen (Sendefehler-Pfad). */
  private disposePending(key: string): void {
    const pending = this._pending.get(key);
    if (!pending) return;
    if (pending.timer) clearTimeout(pending.timer);
    if (pending.onCallerAbort && pending.callerSignal) {
      pending.callerSignal.removeEventListener("abort", pending.onCallerAbort);
    }
    this._pending.delete(key);
  }

  private resolvePending(key: string, value: SleipnirResponse | SleipnirResponse[]): boolean {
    const pending = this._pending.get(key);
    if (!pending) return false;
    if (pending.timer) clearTimeout(pending.timer);
    if (pending.onCallerAbort && pending.callerSignal) {
      pending.callerSignal.removeEventListener("abort", pending.onCallerAbort);
    }
    this._pending.delete(key);
    pending.resolve(value);
    return true;
  }

  private rejectAllPending(err: Error): void {
    for (const key of [...this._pending.keys()]) this.rejectPending(key, err);
  }

  // --- Phase 3: Subscribe / Unsubscribe / Event-Dispatch (Interna) ---

  /** Trägt einen pending Subscribe ein (Timeout/Abort analog registerPending). */
  private registerPendingSubscribe(
    id: string,
    request: SleipnirRequest,
    handlers: SubscribeHandlers<unknown>,
    opts: WsCallOptions | undefined,
    resumeEntry?: { oldId: string; entry: ActiveSubscription },
  ): Promise<SleipnirSubscription> {
    let resolve!: (sub: SleipnirSubscription) => void;
    let reject!: (e: Error) => void;
    const promise = new Promise<SleipnirSubscription>((res, rej) => {
      resolve = res;
      reject = rej;
    });

    const pending: PendingSubscribe = {
      resolve,
      reject,
      request,
      handlers,
      resumeEntry,
      resumePolicy: (opts as SubscribeOptions | undefined)?.resumePolicy,
    };
    const timeoutMs = opts?.timeout ?? this._callTimeout;
    if (timeoutMs && timeoutMs > 0) {
      pending.timer = setTimeout(
        () => this.rejectPendingSubscribe(id, new CancelledError("Sleipnir subscribe timed out.", true)),
        timeoutMs,
      );
    }
    if (opts?.signal) {
      if (opts.signal.aborted) {
        queueMicrotask(() => this.rejectPendingSubscribe(id, new CancelledError("Sleipnir subscribe was cancelled.")));
      } else {
        pending.callerSignal = opts.signal;
        pending.onCallerAbort = () =>
          this.rejectPendingSubscribe(id, new CancelledError("Sleipnir subscribe was cancelled."));
        opts.signal.addEventListener("abort", pending.onCallerAbort, { once: true });
      }
    }
    this._pendingSubscribes.set(id, pending);
    return promise;
  }

  /** Lehnt einen pending Subscribe ab (Timeout/Abort/Sendefehler/Disconnect). */
  private rejectPendingSubscribe(id: string, err: Error): void {
    const pending = this._pendingSubscribes.get(id);
    if (!pending) return;
    this.disposePendingSubscribe(id);
    pending.reject(err);
  }

  /** Räumt Timer/Abort-Listener + Map-Eintrag eines pending Subscribe (ohne reject). */
  private disposePendingSubscribe(id: string): void {
    const pending = this._pendingSubscribes.get(id);
    if (!pending) return;
    if (pending.timer) clearTimeout(pending.timer);
    if (pending.onCallerAbort && pending.callerSignal) {
      pending.callerSignal.removeEventListener("abort", pending.onCallerAbort);
    }
    this._pendingSubscribes.delete(id);
  }

  private rejectAllPendingSubscribes(err: Error): void {
    for (const id of [...this._pendingSubscribes.keys()]) this.rejectPendingSubscribe(id, err);
  }

  /** Terminal: alle aktiven Subscriptions auf onError setzen und verwerfen. */
  private cancelAllSubscriptions(err: Error): void {
    for (const [, entry] of this._subscriptions) {
      try { entry.handlers.onError?.(err); } catch { /* Handler-Fehler nicht fatal */ }
    }
    this._subscriptions.clear();
  }

  /**
   * Sendet `kind:"unsubscribe"` für `subscriptionId` und entfernt die Subscription.
   * Idempotent: ein zweiter Aufruf für dieselbe Id ist ein No-op. Best-effort —
   * ein Sendefehler nach Disconnect wird still ignoriert (die Subscription ist
   * serverseitig ohnehin mit der Connection gestorben).
   */
  private async unsubscribe(subscriptionId: string): Promise<void> {
    if (!this._subscriptions.delete(subscriptionId)) return;
    const ws = this._ws;
    if (ws && ws.readyState === READY_OPEN) {
      try {
        ws.send(JSON.stringify({ kind: "unsubscribe", subscriptionId, id: `unsub.${_unsubscribeIdSeq++}` }));
      } catch {
        // best-effort
      }
    }
  }

  /**
   * Re-subscribes all active subscriptions after a reconnect. Phase R: per subscription the resume
   * policy is consulted (per-subscribe override → client-wide → `"fresh"`): `"fresh"` starts a new
   * subscription (new id, gap lost — today's behavior); `"resume"` sends the durable
   * `subscriptionId` + `lastEventId` so the server replays the gap; `"drop"` ends the subscription
   * without re-subscribing (`onComplete`). A Resume the server cannot satisfy (TTL expired /
   * non-resumable) degrades to fresh — the server returns a new `subscriptionId` and the dedup
   * cursor resets.
   */
  private async resubscribeAll(): Promise<void> {
    if (this._subscriptions.size === 0) return;
    const old = [...this._subscriptions.entries()];
    // `subscribe` registers under the new subscriptionId; the old entries must go or they linger
    // as dead entries (new id != old id).
    this._subscriptions.clear();
    for (const [oldId, entry] of old) {
      // Phase R: resolve the reconnect decision (per-subscribe → client-wide → fresh).
      const policy = entry.resumePolicy ?? this._onResume;
      const ctx: SubscriptionResumeContext = {
        controller: entry.request.controller,
        method: entry.request.method,
        subscriptionId: oldId,
        lastEventId: entry.lastEventId > 0 ? entry.lastEventId : null,
      };
      const decision = policy?.(ctx) ?? "fresh";

      if (decision === "drop") {
        try { entry.handlers.onComplete?.(); } catch { /* handler error not fatal */ }
        continue;
      }

      try {
        if (decision === "resume") {
          await this.resubscribeResume(oldId, entry);
        } else {
          // Fresh: reuse the public path, carrying the per-subscribe policy so a later reconnect
          // still consults it. The cursor resets implicitly (new entry, lastEventId 0).
          await this.subscribe<unknown>(entry.request, entry.handlers, {
            resumePolicy: entry.resumePolicy,
          });
        }
      } catch (err) {
        const e = err instanceof Error ? err : new Error(String(err));
        try { entry.handlers.onError?.(e); } catch { /* handler error not fatal */ }
      }
    }
  }

  /**
   * Phase R resume re-subscribe: sends `kind:"subscribe"` with the durable `subscriptionId` +
   * `lastEventId` so the server replays the disconnect gap, reusing the existing entry (preserving
   * its handlers + dedup cursor). Pre-registers under the durable id so any replay frame arriving
   * before the response is dispatched. On a degraded-to-fresh response (new id), the cursor resets.
   */
  private async resubscribeResume(oldId: string, entry: ActiveSubscription): Promise<void> {
    if (this._disposed) throw new Error("SleipnirWebSocketClient: disposed.");
    await this.connect();
    const ws = this._ws;
    if (!ws || ws.readyState !== READY_OPEN) {
      throw new SleipnirError(0, "WebSocket is not open.");
    }
    const id = `resume.${oldId}.${_unsubscribeIdSeq++}`;
    const req: SleipnirRequest = { ...entry.request, id };
    const promise = this.registerPendingSubscribe(id, req, entry.handlers, undefined, {
      oldId,
      entry,
    });
    // Pre-register under the durable id so replay frames arriving before the response are dispatched.
    this._subscriptions.set(oldId, entry);
    try {
      ws.send(
        JSON.stringify({ ...req, kind: "subscribe", subscriptionId: oldId, lastEventId: entry.lastEventId }),
      );
      await promise;
    } catch (err) {
      this._subscriptions.delete(oldId); // clean up the pre-registration on failure
      this.rejectPendingSubscribe(
        id,
        err instanceof SleipnirError
          ? err
          : new SleipnirError(0, `WebSocket send error: ${(err as Error)?.message ?? err}`),
      );
      throw err instanceof Error ? err : new Error(String(err));
    }
  }

  private onMessage(data: string | ArrayBuffer): void {
    const text = typeof data === "string" ? data : new TextDecoder().decode(data);
    let parsed: unknown;
    try {
      parsed = JSON.parse(text);
    } catch {
      // Server-Fehlerframes ohne id können nicht korreliert werden -> verwerfen.
      return;
    }

    // Phase 3: Event-/Complete-/Error-Frames — Objekt mit `type` + `subscriptionId`,
    // ohne `code`/`id`. Werden per subscriptionId an die aktive Subscription geroutet.
    if (parsed && typeof parsed === "object" && !Array.isArray(parsed)) {
      const obj = parsed as Record<string, unknown>;
      if (typeof obj.type === "string" && typeof obj.subscriptionId === "string") {
        this.dispatchEventFrame(obj.type, obj.subscriptionId, obj);
        return;
      }
    }

    if (Array.isArray(parsed)) {
      // Batch-Response: Korrelation über das erste Element.
      const arr = normalizeResponses(parsed as SleipnirResponse[]);
      const key = arr[0]?.id ?? undefined;
      if (key && this.resolvePending(key, arr)) return;
      this.dropUnmatched(text, key);
      return;
    }

    const resp = normalizeResponse(parsed as SleipnirResponse);
    const key = resp?.id ?? undefined;
    // Subscribe-Response (normale SleipnirResponse, correlated by id) — vor den
    // Call-Pending prüfen, da subscribe einen eigenen Pending-Map führt.
    if (key && this._pendingSubscribes.has(key)) {
      this.handleSubscribeResponse(key, resp);
      return;
    }
    if (key && this.resolvePending(key, resp)) return;
    this.dropUnmatched(text, key);
  }

  /** Routes an event/complete/error frame to the active subscription. */
  private dispatchEventFrame(type: string, subscriptionId: string, obj: Record<string, unknown>): void {
    const entry = this._subscriptions.get(subscriptionId);
    if (!entry) return; // unsubscribe schon gelaufen / unbekannt -> verwerfen.
    if (type === "event") {
      // Phase R: at-least-once dedup. The server replays the disconnect gap from its buffer; drop
      // any frame whose eventId we have already processed (eventId <= last seen). Frames without
      // an eventId (non-resumable sources) are forwarded verbatim — no dedup.
      const evId = typeof obj.eventId === "number" ? obj.eventId : null;
      if (evId !== null) {
        if (evId <= entry.lastEventId) return; // replay duplicate
        entry.lastEventId = evId;
      }
      entry.handlers.onNext(obj.data);
    } else if (type === "complete") {
      this._subscriptions.delete(subscriptionId);
      try { entry.handlers.onComplete?.(); } catch { /* Handler-Fehler nicht fatal */ }
    } else if (type === "error") {
      this._subscriptions.delete(subscriptionId);
      const msg = typeof obj.message === "string" ? obj.message : "Subscription error";
      try { entry.handlers.onError?.(new Error(msg)); } catch { /* Handler-Fehler nicht fatal */ }
    }
  }

  /** Processes a subscribe response: extracts subscriptionId, registers the subscription. */
  private handleSubscribeResponse(key: string, resp: SleipnirResponse): void {
    const pending = this._pendingSubscribes.get(key);
    if (!pending) return;
    this.disposePendingSubscribe(key);
    if (!resp.isSuccess) {
      // A failed resume re-subscribe must drop the pre-registered durable id so a later reconnect
      // does not resurrect a dead entry.
      if (pending.resumeEntry) this._subscriptions.delete(pending.resumeEntry.oldId);
      pending.reject(SleipnirError.fromResponse(resp));
      return;
    }
    const sid = extractSubscriptionId(resp);
    if (!sid) {
      if (pending.resumeEntry) this._subscriptions.delete(pending.resumeEntry.oldId);
      pending.reject(new SleipnirError(resp.code ?? 0, "Subscribe response missing subscriptionId."));
      return;
    }
    if (pending.resumeEntry) {
      // Phase R resume: reuse the existing entry (preserve its handlers + dedup cursor).
      const { oldId, entry } = pending.resumeEntry;
      if (sid !== oldId) {
        // Degrade-to-fresh: the server returned a new id (TTL expired / non-resumable). The new
        // server subscription restarts its eventId counter at 1, so the stale cursor must reset or
        // the fresh stream would be deduped away. Drop the pre-registered old id; re-key under sid.
        entry.lastEventId = 0;
        this._subscriptions.delete(oldId);
      }
      this._subscriptions.set(sid, entry);
    } else {
      this._subscriptions.set(sid, {
        handlers: pending.handlers,
        request: pending.request,
        lastEventId: 0,
        resumePolicy: pending.resumePolicy,
      });
    }
    pending.resolve({
      subscriptionId: sid,
      unsubscribe: () => this.unsubscribe(sid),
    });
  }

  private dropUnmatched(text: string, key: string | undefined): void {
    // B3: kein Last-Resort — nicht zuordnen, verwerfen. Der pending Caller läuft
    // über seinen Timeout/sein Signal ab.
    console.warn(
      `[sleipnir-client] Received WebSocket response with no matching pending request (id=${key ?? "n/a"}). Dropping.`,
    );
    void text;
  }

  private onClosed(): void {
    this._ws = undefined;
    this.rejectAllPending(new SleipnirError(0, "WebSocket connection closed."));
    // Pending subscribes haben noch keine subscriptionId -> die Response kommt nie.
    // Aktive Subscriptions (_subscriptions) bleiben bestehen und werden nach dem
    // Reconnect re-subscribed (at-most-once-while-disconnected: Gap-Events verloren).
    this.rejectAllPendingSubscribes(new SleipnirError(0, "WebSocket connection closed."));

    // Unerwarteter Disconnect (nicht durch close()/dispose() ausgelöst) -> Reconnect.
    if (!this._closedByClient && !this._disposed && this._reconnect) {
      this.startReconnect();
    } else {
      this.setState(SleipnirConnectionState.Disconnected);
    }
  }

  /** Startet den Hintergrund-Reconnect mit Backoff (idempotent). */
  private startReconnect(): void {
    if (this._disposed) return;
    if (this._reconnectPromise && this._state === SleipnirConnectionState.Reconnecting) return;

    this.setState(SleipnirConnectionState.Reconnecting);
    this._reconnectAbort?.abort();
    this._reconnectAbort = new AbortController();
    const signal = this._reconnectAbort.signal;

    this._reconnectPromise = (async () => {
      for (let i = 0; i < this._reconnectDelays.length; i++) {
        if (this._disposed) return;
        try {
          await sleep(this._reconnectDelays[i], signal);
        } catch {
          return; // abgebrochen (dispose / neuer Reconnect)
        }
        if (this._disposed) return;
        // connectSlow direkt (NICHT connect()): der öffentliche connect() würde bei
        // state === Reconnectings den in-flight Reconnect awaiten — also sich selbst
        // (Self-Deadlock). connectSlow teilt den Versuch über _connectPromise mit
        // nebenläufigen connect()-Calls, hält aber den Zustand auf Reconnecting.
        this._connectPromise = this.connectSlow().finally(() => {
          this._connectPromise = undefined;
        });
        try {
          await this._connectPromise;
          if (this._ws && this._ws.readyState === READY_OPEN) {
            this.setState(SleipnirConnectionState.Connected);
            // Phase 3: aktive Subscriptions mit dem neuen Socket re-abonnieren
            // (neue subscriptionIds; gleiche Parameter + Handler). Best-effort,
            // fire-and-forget — ein Fehlschlag pro Subscription -> onError.
            void this.resubscribeAll();
            return; // Erfolg
          }
        } catch {
          // weiter zum nächsten Backoff-Intervall (Zustand bleibt Reconnecting)
        }
      }
      // Backoff erschöpft -> aufgeben.
      if (!this._disposed) this.setState(SleipnirConnectionState.Disconnected);
    })();
  }

  /** Bricht einen laufenden Hintergrund-Reconnect ab (terminal bei dispose). */
  private stopReconnect(): void {
    this._reconnectAbort?.abort();
    this._reconnectPromise = undefined;
  }
}

// --- Shared (gleichlautend mit rest.ts) ---

function parseData<T>(response: SleipnirResponse): T | null {
  // Seit dem Single-Pass-Fix ist data bereits ein strukturierter Wert (kein JSON-String).
  if (response.isSuccess && response.data != null) {
    return response.data as T;
  }
  if (!response.isSuccess) throw SleipnirError.fromResponse(response);
  return null;
}

/**
 * Extrahiert die `subscriptionId` aus einer Subscribe-Response. Der Server sendet
 * `data: { subscriptionId: "…" }` (object) — als Fallback wird ein skalarer
 * `data`-String akzeptiert (Spiegel des C# `ExtractSubscriptionId`).
 */
function extractSubscriptionId(response: SleipnirResponse): string | undefined {
  const data = response.data;
  if (data && typeof data === "object" && typeof (data as { subscriptionId?: unknown }).subscriptionId === "string") {
    return (data as { subscriptionId: string }).subscriptionId;
  }
  return typeof data === "string" ? data : undefined;
}