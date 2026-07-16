import { describe, it, expect, vi } from "vitest";
import { TrameWebSocketClient } from "../../src/websocket.js";
import type { TrameWebSocketClientOptions, IWebSocket, WsFactory } from "../../src/websocket.js";
import { TrameError, CancelledError } from "../../src/errors.js";
import { ExecutionMode, TrameConnectionState } from "../../src/types.js";

const OPEN = 1;

/** Browser-ähnlicher Mock-WebSocket mit steuerbaren Event-Feuern. */
class MockWs implements IWebSocket {
  readyState = 0; // CONNECTING
  onopen: (() => void) | null = null;
  onmessage: ((ev: { data: string | ArrayBuffer }) => void) | null = null;
  onclose: ((ev: { code?: number; reason?: string }) => void) | null = null;
  onerror: ((ev: unknown) => void) | null = null;
  readonly sent: string[] = [];
  readonly url: string;
  readonly opts: any;
  constructor(url: string, opts: any) {
    this.url = url;
    this.opts = opts;
  }
  send(data: string): void {
    this.sent.push(data);
  }
  close(code?: number, reason?: string): void {
    this.readyState = 3;
  }
  fireOpen(): void {
    this.readyState = OPEN;
    this.onopen?.();
  }
  fireMessage(data: string): void {
    this.onmessage?.({ data });
  }
  fireClose(code = 1000): void {
    this.readyState = 3;
    this.onclose?.({ code });
  }
  fireError(): void {
    this.onerror?.({});
  }
  get lastSent(): string | undefined {
    return this.sent[this.sent.length - 1];
  }
}

/**
 * Stabiler Holder: die Factory mutiert `ref.ws`/`ref.created` synchron während
 * `client.call(...)`. Tests lesen die Werte NACH dem Auslösen der Calls, sodass
 * die Mutation sichtbar wird (Destructuring würde den Wert zum falschen Zeitpunkt
 * einfrieren — deshalb Holder statt Getter).
 */
interface ClientCtx {
  client: TrameWebSocketClient;
  ref: { ws: MockWs | undefined; created: number };
}

function makeClient(options: TrameWebSocketClientOptions = {}): ClientCtx {
  const ref: { ws: MockWs | undefined; created: number } = { ws: undefined, created: 0 };
  const factory: WsFactory = (url, opts) => {
    ref.created++;
    ref.ws = new MockWs(url, opts);
    return ref.ws;
  };
  const client = new TrameWebSocketClient("http://127.0.0.1:0", {
    WebSocketCtor: factory,
    ...options,
  });
  return { client, ref };
}

function resp(id: string, data: string): string {
  return JSON.stringify({ code: 200, data, id, isSuccess: true });
}

describe("TrameWebSocketClient", () => {
  it("concurrent calls teilen sich EINEN Connect (B1) und korrelieren per id (B3)", async () => {
    const { client, ref } = makeClient();
    const calls = [0, 1, 2, 3, 4].map((i) =>
      client.call({ controller: "C", method: "M", params: [], id: `r${i}` }),
    );
    expect(ref.created).toBe(1); // nur ein WebSocket trotz 5 paralleler Calls
    const ws = ref.ws!;
    ws.fireOpen();
    await vi.waitFor(() => expect(ws.sent.length).toBe(5), { interval: 1, timeout: 1000 });
    // Antworten in verdrehter Reihenfolge — jeder Call bekommt sein eigenes Resultat.
    for (const i of [4, 2, 0, 3, 1]) ws.fireMessage(resp(`r${i}`, `v${i}`));
    const results = await Promise.all(calls);
    expect(results.map((r) => r.data)).toEqual([
      "v0",
      "v1",
      "v2",
      "v3",
      "v4",
    ]);
  });

  it("verwirft Responses ohne passenden pending Call (B3, kein Last-Resort)", async () => {
    const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
    const { client, ref } = makeClient();
    const p = client.call({ controller: "C", method: "M", params: [], id: "a" });
    const ws = ref.ws!;
    ws.fireOpen();
    await vi.waitFor(() => expect(ws.sent.length).toBe(1), { interval: 1, timeout: 1000 });
    ws.fireMessage(resp("unknown-id", "x")); // kein Match -> drop, kein Reject
    const settled = await Promise.race([
      p.then(() => "resolved"),
      Promise.resolve("pending"),
    ]);
    expect(settled).toBe("pending");
    ws.fireMessage(resp("a", "ok")); // Match -> resolved
    const r = await p;
    expect(r.data).toBe("ok");
    expect(warn).toHaveBeenCalled();
    warn.mockRestore();
  });

  it("korreliert Batch per requests[0].id und liefert Array", async () => {
    const { client, ref } = makeClient();
    const p = client.callBatch(
      [
        { controller: "C", method: "M", params: [], id: "b0" },
        { controller: "C", method: "M", params: [], id: "b1" },
      ],
      ExecutionMode.Serial,
    );
    const ws = ref.ws!;
    ws.fireOpen();
    await vi.waitFor(() => expect(ws.sent.length).toBe(1), { interval: 1, timeout: 1000 });
    // Server liefert Array; Korrelation über erstes Element.
    ws.fireMessage(JSON.stringify([resp("b0", "0"), resp("b1", "1")].map((s) => JSON.parse(s))));
    const arr = await p;
    expect(arr).toHaveLength(2);
    expect(arr[0].data).toBe("0");
    expect(arr[1].data).toBe("1");
  });

  it("Timeout -> CancelledError mit timedOut=true", async () => {
    const { client, ref } = makeClient();
    const p = client.call(
      { controller: "C", method: "M", params: [], id: "t" },
      { timeout: 20 },
    );
    const ws = ref.ws!;
    ws.fireOpen();
    await vi.waitFor(() => expect(ws.sent.length).toBe(1), { interval: 1, timeout: 1000 });
    await expect(p).rejects.toMatchObject({ name: "CancelledError", timedOut: true });
  });

  it("Caller-Abbruch -> CancelledError (unverpackt)", async () => {
    const { client, ref } = makeClient();
    const ac = new AbortController();
    ac.abort();
    const p = client.call(
      { controller: "C", method: "M", params: [], id: "c" },
      { signal: ac.signal },
    );
    // Der Client lehnt bereits abgebrochene Signale per Microtask ab. Im realen
    // Gebrauch hängt `await client.call(...)` den Handler synchron an; hier wird
    // p aber über vi.waitFor gehalten, daher hängen wir früh einen Handler an,
    // damit die Microtask-Rejection nicht als unhandled durchgeht.
    p.catch(() => {});
    const ws = ref.ws!;
    ws.fireOpen();
    await vi.waitFor(() => expect(ws.sent.length).toBe(1), { interval: 1, timeout: 1000 });
    await expect(p).rejects.toBeInstanceOf(CancelledError);
  });

  it("Schließen der Verbindung lehnt pending Calls mit TrameError ab", async () => {
    // reconnect aus — der In-Flight-Drop wird isoliert geprüft (Spiegel SignalR).
    const { client, ref } = makeClient({ reconnect: false });
    const p = client.call({ controller: "C", method: "M", params: [], id: "p" });
    const ws = ref.ws!;
    ws.fireOpen();
    await vi.waitFor(() => expect(ws.sent.length).toBe(1), { interval: 1, timeout: 1000 });
    ws.fireClose(1001);
    await expect(p).rejects.toBeInstanceOf(TrameError);
    client.dispose();
  });

  it("leitet isSuccess aus code ab, wenn der Server es nicht sendet (Wire-Fakt)", async () => {
    const { client, ref } = makeClient();
    const p = client.call({ controller: "C", method: "M", params: [], id: "d" });
    const ws = ref.ws!;
    ws.fireOpen();
    await vi.waitFor(() => expect(ws.sent.length).toBe(1), { interval: 1, timeout: 1000 });
    // Server sendet KEIN isSuccess-Feld ([JsonIgnore], aus code berechnet).
    ws.fireMessage(JSON.stringify({ code: 200, data: "ok", id: "d" }));
    const r = await p;
    expect(r.isSuccess).toBe(true); // vom Client aus code abgeleitet
    expect(r.data).toBe("ok");
  });

  it("baut die WS-URL aus baseUrl + wsPath (http->ws)", async () => {
    let url = "";
    let created = 0;
    let ws!: MockWs;
    const factory: WsFactory = (u) => {
      url = u;
      created++;
      ws = new MockWs(u, {});
      return ws;
    };
    const client = new TrameWebSocketClient("https://host:5001/app/", {
      WebSocketCtor: factory,
      wsPath: "tramews",
    });
    const p = client.call({ controller: "C", method: "M", params: [], id: "x" });
    // factory wird synchron beim Connect angelegt
    expect(created).toBe(1);
    expect(url).toBe("wss://host:5001/app/tramews");
    // aufräumen: Connect ablehnen (Mock-close feuert onclose -> Connect-Promise rejected).
    client.dispose();
    ws.fireClose();
    await p.catch(() => {});
  });

  it("reconnectet im Hintergrund nach Close und nachfolgender Call gelingt", async () => {
    const { client, ref } = makeClient({ reconnectDelays: [10] });
    const p0 = client.call({ controller: "C", method: "M", params: [], id: "r0" });
    const ws1 = ref.ws!;
    ws1.fireOpen();
    await vi.waitFor(() => expect(ws1.sent.length).toBe(1), { interval: 1, timeout: 1000 });
    ws1.fireMessage(resp("r0", "v0"));
    await p0;
    expect(client.state).toBe(TrameConnectionState.Connected);

    // Unerwarteter Drop -> Hintergrund-Reconnect (neuer MockWs via Factory).
    ws1.fireClose(1001);
    await vi.waitFor(() => expect(client.state).toBe(TrameConnectionState.Reconnecting), {
      interval: 1,
      timeout: 1000,
    });
    await vi.waitFor(() => expect(ref.created).toBe(2), { interval: 1, timeout: 1000 });
    const ws2 = ref.ws!;
    ws2.fireOpen(); // Reconnect-Connect auflösen
    await vi.waitFor(() => expect(client.state).toBe(TrameConnectionState.Connected), {
      interval: 1,
      timeout: 1000,
    });

    // Nachfolgender Call geht über die reconnected Verbindung.
    const p1 = client.call({ controller: "C", method: "M", params: [], id: "r1" });
    await vi.waitFor(() => expect(ws2.sent.length).toBe(1), { interval: 1, timeout: 1000 });
    ws2.fireMessage(resp("r1", "v1"));
    const r1 = await p1;
    expect(r1.data).toBe("v1");
    client.dispose();
  });

  it("Backoff-Erschöpfung geht in Disconnected (Reconnect ausgeschöpft)", async () => {
    // Factory: 1. Socket öffnet (via fireOpen im Test), jeder weitere Socket
    // schlägt sofort fehl (fireError) -> connectSlow rejected -> der Reconnect-Loop
    // rückt zum nächsten Backoff-Intervall vor, bis der Backoff erschöpft ist.
    const ref = { ws: undefined as MockWs | undefined, created: 0 };
    const factory: WsFactory = (url, opts) => {
      ref.created++;
      const ws = new MockWs(url, opts);
      ref.ws = ws;
      if (ref.created > 1) {
        // Reconnect-Sockets schlagen sofort fehl (Spiegel C# RejectUpgrade).
        queueMicrotask(() => ws.fireError());
      }
      return ws;
    };
    const client = new TrameWebSocketClient("http://127.0.0.1:0", {
      WebSocketCtor: factory,
      reconnectDelays: [5, 5, 5],
    });
    const p0 = client.call({ controller: "C", method: "M", params: [], id: "x0" });
    ref.ws!.fireOpen();
    await vi.waitFor(() => expect(ref.ws!.sent.length).toBe(1), { interval: 1, timeout: 1000 });
    ref.ws!.fireMessage(resp("x0", "0"));
    await p0;

    ref.ws!.fireClose(1001); // Drop -> 3 Reconnect-Versuche (alle schlagen fehl)
    await vi.waitFor(() => expect(client.state).toBe(TrameConnectionState.Disconnected), {
      interval: 1,
      timeout: 2000,
    });
    expect(ref.created).toBe(4); // 1 initial + 3 Reconnect-Versuche
    client.dispose();
  });

  it("dispose bricht den Reconnect ab (terminal, kein weiterer Versuch)", async () => {
    const { client, ref } = makeClient({ reconnectDelays: [10, 10, 10] });
    const p0 = client.call({ controller: "C", method: "M", params: [], id: "z0" });
    ref.ws!.fireOpen();
    await vi.waitFor(() => expect(ref.ws!.sent.length).toBe(1), { interval: 1, timeout: 1000 });
    ref.ws!.fireMessage(resp("z0", "0"));
    await p0;

    ref.ws!.fireClose(1001);
    await vi.waitFor(() => expect(client.state).toBe(TrameConnectionState.Reconnecting), {
      interval: 1,
      timeout: 1000,
    });
    const createdBeforeDispose = ref.created;
    client.dispose();
    expect(client.state).toBe(TrameConnectionState.Disconnected);

    // Keine weiteren Reconnect-Versuche nach dispose.
    await vi.waitFor(() => expect(ref.created).toBe(createdBeforeDispose), {
      interval: 1,
      timeout: 500,
    });
  });
});