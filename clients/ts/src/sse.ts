// Sleipnir SSE (Server-Sent Events) client — fetch-based server-push events over REST.
//
// `EventSource` cannot set the `Authorization` header, so Bearer-auth hosts need a fetch-based
// client (full control over request headers + URL). This client drives `fetch` + a
// `ReadableStream` reader, decodes the `text/event-stream` body block-by-block, and maps each
// SSE block onto the SAME logical event frame the WebSocket transport emits
// (`{type:"event"|"complete"|"error", subscriptionId, eventId[, data][, message]}`). For a
// cookie-auth host, native `EventSource` against the resume URL also works — this client is the
// supported Bearer path.
//
// Resume (Phase R) reuses the WebSocket `ResumeDecision` shape: on a mid-stream drop the
// client consults a `ResumePolicy` (`fresh` | `resume` | `drop`). `resume` reconnects to
// `GET {apiPath}/events/{subscriptionId}` with `Last-Event-Id: {lastEventId}`, so the server
// replays the gap from its disconnect buffer (at-least-once; the client dedups by `eventId`).
// Durable subscriptions are process-wide on the server, so a subscription created over
// WebSocket can be resumed over this SSE client and vice-versa (cross-transport resume).

import type { BearerProvider } from "./types.js";
import type {
  ResumeDecision,
  ResumePolicy,
  SubscriptionResumeContext,
  SubscribeHandlers,
  SleipnirSubscription,
} from "./websocket.js";
import { SleipnirError } from "./errors.js";

// Re-export the shared event types so consumers import everything from one module, and so the
// codegen emitter (S4) can `import { SleipnirSseClient, type SubscribeHandlers } from "..."`.
export type {
  ResumeDecision,
  ResumePolicy,
  SubscriptionResumeContext,
  SubscribeHandlers,
  SleipnirSubscription,
};

/** Injizierbares fetch — permissiv, damit Test-Mocks und Node-Lib.fetch passen. */
export type SseFetchLike = (
  input: string | URL | Request,
  init?: RequestInit,
) => Promise<Response>;

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
export class SleipnirSseClient {
  private readonly _baseUrl: string;
  private readonly _apiPath: string;
  private readonly _fetch: SseFetchLike;
  private readonly _headers: Record<string, string>;
  private _bearer?: BearerProvider;
  private readonly _reconnect: boolean;
  private readonly _reconnectDelays: number[];
  private _onResume?: ResumePolicy;

  constructor(baseUrl: string, options: SleipnirSseClientOptions = {}) {
    if (!baseUrl || baseUrl.trim().length === 0) {
      throw new Error("SleipnirSseClient: baseUrl darf nicht leer sein.");
    }
    this._baseUrl = baseUrl.endsWith("/") ? baseUrl : baseUrl + "/";
    this._apiPath = (options.apiPath ?? "api/sleipnir").replace(/^\/+|\/+$/g, "");
    // Browser-fetch verlangt `globalThis` als Receiver (siehe SleipnirRestClient).
    this._fetch = options.fetch ?? fetch.bind(globalThis);
    this._headers = { ...(options.headers ?? {}) };
    this._bearer = options.bearer;
    this._reconnect = options.reconnect ?? true;
    this._reconnectDelays = options.reconnectDelays ?? [0, 1000, 2000, 5000, 10000, 15000, 30000];
    this._onResume = options.onResume;
  }

  /** Tauscht den Bearer (String oder Provider-Funktion) für künftige Requests aus. */
  setBearer(bearer: BearerProvider): void {
    this._bearer = bearer;
  }

  /**
   * Öffnet eine SSE-Subscription auf `{controller}.{method}`. Method-Argumente reisen als
   * Query-Parameter (GET hat keinen Body); jeder Wert wird JSON-kodiert gesendet, damit der
   * Server ihn typengetreu zurück-parsed (ein String `"hi"` als `?msg=%22hi%22`). Löst mit dem
   * `SleipnirSubscription`-Handle auf, sobald der Server-Ack eintrifft (erste SSE-Event-Block).
   */
  async subscribe<T>(
    controller: string,
    method: string,
    handlers: SubscribeHandlers<T>,
    params?: Record<string, unknown>,
    opts: SseSubscribeOptions = {},
  ): Promise<SleipnirSubscription> {
    const freshUrl = this.buildFreshUrl(controller, method, params);
    const policy = opts.resumePolicy ?? this._onResume;

    // Ein AbortController pro Subscription:unsubscribe() oder das Caller-Signal brechen
    // sowohl den laufenden fetch als auch den Reconnect-Loop ab.
    const ctrl = new AbortController();
    const onCallerAbort = () => ctrl.abort(opts.signal?.reason);
    if (opts.signal) {
      if (opts.signal.aborted) ctrl.abort(opts.signal.reason);
      else opts.signal.addEventListener("abort", onCallerAbort, { once: true });
    }

    let unsubscribed = false;
    let subscriptionId = "";
    let lastEventId = 0;                 // Phase R dedup cursor (0 = noch kein Event)
    let attempt = 0;                     // Backoff-Index
    let forceFresh = false;               // Einmal-Override nach 410 (Server degradiert Resume→Fresh)

    const unsubscribe = async (): Promise<void> => {
      unsubscribed = true;
      ctrl.abort(new Error("SSE subscription unsubscribed."));
    };

    // Der Subscribe-Promise löst auf, sobald der erste Ack-Block gelesen wurde.
    return new Promise<SleipnirSubscription>((resolve, reject) => {
      // Erste Verbindung ist Fresh; jede Reconnect-Verbindung ist Fresh ODER Resume (Policy).
      let mode: "fresh" | "resume" = "fresh";

      const connectOnce = async (): Promise<void> => {
        const url = mode === "resume" && subscriptionId
          ? this.buildResumeUrl(subscriptionId, lastEventId)
          : freshUrl;

        const headers: Record<string, string> = { ...this._headers, Accept: "text/event-stream" };
        if (mode === "resume" && lastEventId > 0) headers["Last-Event-Id"] = String(lastEventId);
        const token = this.resolveBearer();
        if (token) headers["Authorization"] = `Bearer ${token}`;
        if (opts.headers) Object.assign(headers, opts.headers);

        let resp: Response;
        try {
          resp = await this._fetch(url, { method: "GET", headers, signal: ctrl.signal });
        } catch (e) {
          return handleDrop(e);
        }

        if (!resp.ok || !resp.body) {
          return handleNonOk(resp, mode);
        }

        try {
          await readStream(resp.body);
          // Sauberes Stream-Ende OHNE Terminal-Frame (complete/error gesetzt unsubscribed)
          // ist ein Drop: die Verbindung wurde ohne sauberes Ende geschlossen → Reconnect.
          if (!unsubscribed && !ctrl.signal.aborted) handleDrop(new Error("SSE stream ended"));
        } catch (e) {
          // Abort durch unsubscribe → Schleife beenden; sonst Reconnect.
          if (unsubscribed || ctrl.signal.aborted) return;
          handleDrop(e);
        }
      };

      const readStream = async (body: ReadableStream<Uint8Array>): Promise<void> => {
        const reader = body.getReader();
        const decoder = new TextDecoder();
        let buffer = "";
        let ackSeen = false;
        for (;;) {
          const { value, done } = await reader.read();
          if (done) break;
          buffer += decoder.decode(value, { stream: true });
          // SSE-Blöcke sind durch eine Leerzeile getrennt; verarbeite alle vollständigen.
          let sep: number;
          while ((sep = buffer.indexOf("\n\n")) !== -1) {
            const blockText = buffer.slice(0, sep);
            buffer = buffer.slice(sep + 2);
            const block = parseSseBlock(blockText);
            if (!block) continue;
            if (!ackSeen && block.event === "ack") {
              ackSeen = true;
              const ack = JSON.parse(block.data) as { subscriptionId: string; replayedFrom?: number };
              subscriptionId = ack.subscriptionId;
              // Eine Resume, die eine neue id liefert, bedeutet Server-Degraded-to-Fresh
              // (TTL expired / non-resumable) → der eventId-Zähler startet neu bei 1 → Cursor reset.
              if (mode === "resume" && ack.replayedFrom == null) lastEventId = 0;
              resolve({ subscriptionId, get lastEventId() { return lastEventId; }, unsubscribe });
              continue;
            }
            dispatchFrame(block);
          }
        }
      };

      const dispatchFrame = (block: SseBlock): void => {
        if (block.event === "event") {
          const frame = JSON.parse(block.data) as { eventId?: number; data: T };
          const evId = typeof frame.eventId === "number" ? frame.eventId : null;
          if (evId !== null) {
            if (evId <= lastEventId) return;    // Reconnect-Replay-Duplikat verwerfen
            lastEventId = evId;
          }
          try { handlers.onNext(frame.data); } catch { /* Handler-Fehler nicht fatal */ }
        } else if (block.event === "complete") {
          unsubscribed = true;                  // Terminal → kein Reconnect
          try { handlers.onComplete?.(); } catch { /* Handler-Fehler nicht fatal */ }
        } else if (block.event === "error") {
          unsubscribed = true;
          const msg = (JSON.parse(block.data) as { message?: string }).message ?? "Subscription error";
          try { handlers.onError?.(new Error(msg)); } catch { /* Handler-Fehler nicht fatal */ }
        }
      };

      const handleNonOk = (resp: Response, wasMode: "fresh" | "resume"): void => {
        // Erste Fresh-Subscribe: non-2xx → Subscribe scheitert (Auth/Routing/Binding).
        if (subscriptionId === "") {
          reject(new SleipnirError(resp.status, `SSE subscribe failed (HTTP ${resp.status}).`));
          return;
        }
        // Reconnect-Phase: 410 Gone → Resume-Ziel weggefallen → zu Fresh degradieren und neu
        // versuchen. Andere non-2xx → als Drop behandeln (Policy entscheidet über Reconnect).
        if (wasMode === "resume" && resp.status === 410) {
          // Server hat die durable Subscription weggeräumt → Resume-Ziel weg → einmalig zu Fresh
          // degradieren (Policy-Befragung überspringen, sonst würde sie "resume" wieder setzen).
          mode = "fresh";
          forceFresh = true;
          scheduleReconnect();
          return;
        }
        handleDrop(new Error(`SSE stream HTTP ${resp.status}`));
      };

      const handleDrop = (e: unknown): void => {
        if (unsubscribed || !this._reconnect || this._reconnectDelays.length === 0) {
          // Kein Reconnect: ein Drop vor dem ersten Ack → Subscribe scheitert; danach → onError.
          if (subscriptionId === "") {
            reject(e instanceof Error ? e : new Error("SSE stream ended before ack"));
          } else {
            try { handlers.onError?.(e instanceof Error ? e : new Error("SSE stream ended")); } catch { /* noop */ }
          }
          return;
        }
        scheduleReconnect();
      };

      const scheduleReconnect = (): void => {
        if (unsubscribed) return;
        // Policy befragen (nur wenn schon eine subscriptionId vorliegt; vor dem ersten Ack
        // gibt es nichts zu resumen → frisch neu versuchen). forceFresh (nach 410) überspringt
        // die Policy für genau diesen Reconnect — sie würde sonst "resume" zurückgeben.
        let decision: ResumeDecision = "fresh";
        if (forceFresh) {
          forceFresh = false;
        } else if (subscriptionId !== "" && policy) {
          const ctx: SubscriptionResumeContext = {
            controller,
            method,
            subscriptionId,
            lastEventId: lastEventId > 0 ? lastEventId : null,
          };
          const d = policy(ctx);
          if (d) decision = d;
        }
        if (decision === "drop") {
          unsubscribed = true;
          try { handlers.onComplete?.(); } catch { /* noop */ }
          return;
        }
        mode = decision;                        // "fresh" | "resume"
        const delay = this._reconnectDelays[Math.min(attempt, this._reconnectDelays.length - 1)];
        attempt++;
        if (delay > 0) {
          setTimeout(() => { if (!unsubscribed) void connectOnce(); }, delay);
        } else {
          void connectOnce();
        }
      };

      // Start: erste Fresh-Verbindung.
      void connectOnce();
    });
  }

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
  async resume<T>(
    subscriptionId: string,
    lastEventId: number,
    handlers: SubscribeHandlers<T>,
    opts: SseResumeOptions = {},
  ): Promise<SleipnirSubscription> {
    if (!subscriptionId) throw new Error("SleipnirSseClient.resume: subscriptionId is required.");
    const policy = opts.resumePolicy ?? this._onResume;
    const reconnect = opts.reconnect ?? this._reconnect;
    const reconnectDelays = opts.reconnectDelays ?? this._reconnectDelays;

    const ctrl = new AbortController();
    const onCallerAbort = () => ctrl.abort(opts.signal?.reason);
    if (opts.signal) {
      if (opts.signal.aborted) ctrl.abort(opts.signal.reason);
      else opts.signal.addEventListener("abort", onCallerAbort, { once: true });
    }

    let unsubscribed = false;
    let activeId = subscriptionId;       // server may hand back a new id on degraded-to-fresh
    let cursor = lastEventId;
    let attempt = 0;

    const unsubscribe = async (): Promise<void> => {
      unsubscribed = true;
      ctrl.abort(new Error("SSE resume unsubscribed."));
    };

    return new Promise<SleipnirSubscription>((resolve, reject) => {
      let ackSeen = false;

      const connectOnce = async (): Promise<void> => {
        const url = this.buildResumeUrl(activeId, cursor);
        const headers: Record<string, string> = { ...this._headers, Accept: "text/event-stream" };
        if (cursor > 0) headers["Last-Event-Id"] = String(cursor);
        const token = this.resolveBearer();
        if (token) headers["Authorization"] = `Bearer ${token}`;
        if (opts.headers) Object.assign(headers, opts.headers);

        let resp: Response;
        try {
          resp = await this._fetch(url, { method: "GET", headers, signal: ctrl.signal });
        } catch (e) {
          return handleDrop(e);
        }
        if (!resp.ok || !resp.body) {
          // Pre-ack: die durable Subscription ist weg/verweigert → Subscribe scheitert.
          if (!ackSeen) {
            reject(new SleipnirError(resp.status, `SSE resume failed (HTTP ${resp.status}).`));
            return;
          }
          if (resp.status === 410) {
            // Durable Subscription abgelaufen/geräumt → terminal (kein Fresh-Fallback: keine Params).
            unsubscribed = true;
            try { handlers.onError?.(new Error("SSE resume target gone (410): subscription expired.")); } catch { /* non-fatal */ }
            return;
          }
          return handleDrop(new Error(`SSE resume stream HTTP ${resp.status}`));
        }
        try {
          await readStream(resp.body);
          if (!unsubscribed && !ctrl.signal.aborted) handleDrop(new Error("SSE resume stream ended"));
        } catch (e) {
          if (unsubscribed || ctrl.signal.aborted) return;
          handleDrop(e);
        }
      };

      const readStream = async (body: ReadableStream<Uint8Array>): Promise<void> => {
        const reader = body.getReader();
        const decoder = new TextDecoder();
        let buffer = "";
        for (;;) {
          const { value, done } = await reader.read();
          if (done) break;
          buffer += decoder.decode(value, { stream: true });
          let sep: number;
          while ((sep = buffer.indexOf("\n\n")) !== -1) {
            const blockText = buffer.slice(0, sep);
            buffer = buffer.slice(sep + 2);
            const block = parseSseBlock(blockText);
            if (!block) continue;
            if (!ackSeen && block.event === "ack") {
              ackSeen = true;
              const ack = JSON.parse(block.data) as { subscriptionId?: string; replayedFrom?: number };
              if (ack.subscriptionId) activeId = ack.subscriptionId;
              // Degraded-to-fresh (TTL expired / non-resumable): eventId-Zähler startet neu → Cursor reset.
              if (ack.replayedFrom == null) cursor = 0;
              resolve({ subscriptionId: activeId, get lastEventId() { return cursor; }, unsubscribe });
              continue;
            }
            if (block.event === "event") {
              const frame = JSON.parse(block.data) as { eventId?: number; data: T };
              const evId = typeof frame.eventId === "number" ? frame.eventId : null;
              if (evId !== null) {
                if (evId <= cursor) continue;       // Replay-Duplikat verwerfen
                cursor = evId;
              }
              try { handlers.onNext(frame.data); } catch { /* Handler-Fehler nicht fatal */ }
            } else if (block.event === "complete") {
              unsubscribed = true;
              try { handlers.onComplete?.(); } catch { /* non-fatal */ }
            } else if (block.event === "error") {
              unsubscribed = true;
              const msg = (JSON.parse(block.data) as { message?: string }).message ?? "Subscription error";
              try { handlers.onError?.(new Error(msg)); } catch { /* non-fatal */ }
            }
          }
        }
      };

      const handleDrop = (e: unknown): void => {
        if (unsubscribed || !reconnect || reconnectDelays.length === 0) {
          if (!ackSeen) reject(e instanceof Error ? e : new Error("SSE resume ended before ack"));
          else { try { handlers.onError?.(e instanceof Error ? e : new Error("SSE resume stream ended")); } catch { /* non-fatal */ } }
          return;
        }
        scheduleReconnect();
      };

      const scheduleReconnect = (): void => {
        if (unsubscribed) return;
        // Resume-only: die Policy darf ein Reconnect zu "drop" herabstufen; "fresh" ist hier
        // bedeutungslos (keine Fresh-Params) und wird als "resume" behandelt.
        let decision: ResumeDecision = "resume";
        if (policy && cursor > 0) {
          const ctx: SubscriptionResumeContext = {
            controller: "",
            method: "",
            subscriptionId: activeId,
            lastEventId: cursor,
          };
          const d = policy(ctx);
          if (d) decision = d;
        }
        if (decision === "drop") {
          unsubscribed = true;
          try { handlers.onComplete?.(); } catch { /* non-fatal */ }
          return;
        }
        const delay = reconnectDelays[Math.min(attempt, reconnectDelays.length - 1)];
        attempt++;
        if (delay > 0) setTimeout(() => { if (!unsubscribed) void connectOnce(); }, delay);
        else void connectOnce();
      };

      // Start: erste Resume-Verbindung.
      void connectOnce();
    });
  }

  // --- Interna ---

  private resolveBearer(): string | undefined {
    const b = this._bearer;
    return typeof b === "function" ? b() : b;
  }

  private buildFreshUrl(controller: string, method: string, params?: Record<string, unknown>): string {
    const base = `${this._baseUrl}${this._apiPath}/events/${encodeURIComponent(controller)}/${encodeURIComponent(method)}`;
    if (!params) return base;
    const qs = Object.entries(params)
      .map(([k, v]) => `${encodeURIComponent(k)}=${encodeURIComponent(JSON.stringify(v))}`)
      .join("&");
    return qs ? `${base}?${qs}` : base;
  }

  private buildResumeUrl(subscriptionId: string, lastEventId: number): string {
    // lastEventId reist primär im Last-Event-Id-Header; der Query-Param ist Fallback für
    // Umgebungen, die Header-Setzen erschweren (native EventSource kann gar keine Header).
    return `${this._baseUrl}${this._apiPath}/events/${encodeURIComponent(subscriptionId)}?lastEventId=${lastEventId}`;
  }
}

// --- SSE-Block-Parser ---

interface SseBlock {
  /** Der `event:`-Feldwert (Default "message"). */
  event: string;
  /** Der `id:`-Feldwert (Last-Event-Id) oder null. */
  id: number | null;
  /** Die `data:`-Zeilen, mit "\n" verkettet. */
  data: string;
}

/**
 * Parst einen SSE-Block (die Zeilen zwischen zwei Leerzeilen) in `{event,id,data}`. Felder
 * unbekannter Bedeutung (z. B. `retry:`) und Kommentare (`:`) werden ignoriert. Ein Block ohne
 * `event:`-Feld liefert `event = "message"` (SSE-Default) — Sleipnir sendet immer `event:`.
 */
function parseSseBlock(block: string): SseBlock | null {
  let event = "message";
  let id: number | null = null;
  let data = "";
  let hasData = false;
  for (const rawLine of block.split("\n")) {
    // CR am Zeilenende (CRLF-Transporte) abschneiden.
    const line = rawLine.endsWith("\r") ? rawLine.slice(0, -1) : rawLine;
    if (line === "" || line.startsWith(":")) continue;     // Leer-/Kommentarzeile
    const colon = line.indexOf(":");
    const field = colon === -1 ? line : line.slice(0, colon);
    // Ein einzelnes führendes Leerzeichen nach dem Colon ist SSE-Konvention → streifen.
    let value = colon === -1 ? "" : line.slice(colon + 1);
    if (value.startsWith(" ")) value = value.slice(1);
    if (field === "event") event = value;
    else if (field === "id") id = value.trim() === "" ? null : (Number(value) || null);
    else if (field === "data") {
      if (hasData) data += "\n";
      data += value;
      hasData = true;
    }
    // retry: und unbekannte Felder ignorieren.
  }
  return hasData ? { event, id, data } : null;
}