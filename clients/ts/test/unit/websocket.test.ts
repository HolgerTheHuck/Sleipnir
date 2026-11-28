import { describe, it, expect, vi } from "vitest";
import { SleipnirWebSocketClient } from "../../src/websocket.js";
import type { SleipnirWebSocketClientOptions, IWebSocket, WsFactory } from "../../src/websocket.js";
import { SleipnirError, CancelledError } from "../../src/errors.js";
import { ExecutionMode, SleipnirConnectionState } from "../../src/types.js";

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
  client: SleipnirWebSocketClient;
  ref: { ws: MockWs | undefined; created: number };
}

function makeClient(options: SleipnirWebSocketClientOptions = {}): ClientCtx {
  const ref: { ws: MockWs | undefined; created: number } = { ws: undefined, created: 0 };
  const factory: WsFactory = (url, opts) => {
    ref.created++;
    ref.ws = new MockWs(url, opts);
    return ref.ws;
  };
  const client = new SleipnirWebSocketClient("http://127.0.0.1:0", {
    WebSocketCtor: factory,
    ...options,
  });
  return { client, ref };
}

function resp(id: string, data: string): string {
  return JSON.stringify({ code: 200, data, id, isSuccess: true });
}

describe("SleipnirWebSocketClient", () => {
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

  // --- Regression: shared pending key `Controller.Method` (unique correlation ids) ---

  it("two concurrent identical auto-id calls get unique correlation ids (no pending collision)", async () => {
    const { client, ref } = makeClient();
    // Both twins go out without an id (and the builder default id is the readable form) — the
    // regression: the shared key `Projects.ListProjects` let the second `_pending.set` drop the
    // first, so a got the second's answer and b waited until timeout ("Dropping" on the server
    // frame that came back for it).
    const a = client.call({ controller: "Projects", method: "ListProjects", params: [] });
    const b = client.call({ controller: "Projects", method: "ListProjects", params: [] });
    ref.ws!.fireOpen();
    await vi.waitFor(() => expect(ref.ws!.sent.length).toBe(2), { interval: 1, timeout: 1000 });
    const fa = JSON.parse(ref.ws!.sent[0]);
    const fb = JSON.parse(ref.ws!.sent[1]);
    expect(fa.id).toBe("Projects.ListProjects#1"); // readable prefix, unique per-client suffix
    expect(fb.id).toBe("Projects.ListProjects#2");
    // Answers in reverse order — every caller must receive its own result.
    ref.ws!.fireMessage(resp(fa.id, "v-first"));
    ref.ws!.fireMessage(resp(fb.id, "v-second"));
    const [ra, rb] = await Promise.all([a, b]);
    expect(ra.data).toBe("v-first");
    expect(rb.data).toBe("v-second");
    client.dispose();
  });

  it("replaces the builder default id `Controller.Method` but keeps caller-chosen explicit ids", async () => {
    const { client, ref } = makeClient();
    // `id:"C.M"` is exactly the buildSingle/SleipnirCall default — it must be replaced like a
    // missing id, or two builder-built concurrent calls to the same method still collide.
    const a = client.call({ controller: "C", method: "M", params: [], id: "C.M" });
    const b = client.call({ controller: "C", method: "M", params: [] });
    ref.ws!.fireOpen();
    await vi.waitFor(() => expect(ref.ws!.sent.length).toBe(2), { interval: 1, timeout: 1000 });
    const [fa, fb] = ref.ws!.sent.map((s) => JSON.parse(s));
    expect(fa.id).toBe("C.M#1");
    expect(fb.id).toBe("C.M#2");
    ref.ws!.fireMessage(resp(fa.id, "x"));
    ref.ws!.fireMessage(resp(fb.id, "y"));
    const [ra, rb] = await Promise.all([a, b]);
    expect(ra.data).toBe("x");
    expect(rb.data).toBe("y");

    // A caller-chosen id is the caller's correlation handle — untouched (JSON-RPC semantics).
    const chosen = client.call({ controller: "C", method: "M", params: [], id: "my-call-42" });
    await vi.waitFor(() => expect(ref.ws!.sent.length).toBe(3), { interval: 1, timeout: 1000 });
    const f = JSON.parse(ref.ws!.sent[2]);
    expect(f.id).toBe("my-call-42");
    ref.ws!.fireMessage(resp(f.id, "z"));
    expect((await chosen).data).toBe("z");
    client.dispose();
  });

  it("two concurrent identical batches get unique keys and each resolves with its own response array", async () => {
    const { client, ref } = makeClient();
    const mkBatch = () => [
      { controller: "Projects", method: "ListProjects", params: [] },
      { controller: "Projects", method: "GetNames", params: [] },
    ];
    const batchA = client.callBatch(mkBatch());
    const batchB = client.callBatch(mkBatch());
    ref.ws!.fireOpen();
    await vi.waitFor(() => expect(ref.ws!.sent.length).toBe(2), { interval: 1, timeout: 1000 });
    const fa = JSON.parse(ref.ws!.sent[0]);
    const fb = JSON.parse(ref.ws!.sent[1]);
    // Every auto-filled member id is unique — including ACROSS the two batches (the batch
    // correlates via the FIRST element's id, so that is the pending key).
    const idsA = fa.requests.map((r: { id: string }) => r.id);
    const idsB = fb.requests.map((r: { id: string }) => r.id);
    expect(new Set([...idsA, ...idsB]).size).toBe(4);
    expect(idsA[0]).toBe("Projects.ListProjects#1");
    expect(idsB[0]).toBe("Projects.ListProjects#3"); // seq continues past batch A's members
    ref.ws!.fireMessage(JSON.stringify([{ code: 200, id: idsA[0], data: "rA", isSuccess: true }]));
    ref.ws!.fireMessage(JSON.stringify([{ code: 200, id: idsB[0], data: "rB", isSuccess: true }]));
    const [ra, rb] = await Promise.all([batchA, batchB]);
    expect(ra[0].data).toBe("rA");
    expect(rb[0].data).toBe("rB");
    client.dispose();
  });

  it("setzt den Bearer beim Connect (Browser-Pfad: ?access_token= in der URL)", async () => {
    const { client, ref } = makeClient({ bearer: "tok" });
    const p = client.connect();
    ref.ws!.fireOpen();
    await p;
    expect(ref.ws!.url).toContain("access_token=tok");
    client.dispose();
  });

  it("löst einen Function-Bearer beim Connect frisch auf", async () => {
    let token = "v1";
    const { client, ref } = makeClient({ bearer: () => token });
    const p = client.connect();
    ref.ws!.fireOpen();
    await p;
    expect(ref.ws!.url).toContain("access_token=v1");
    client.dispose();
  });

  it("setBearer greift beim nächsten Connect (Token-Tausch zur Laufzeit)", async () => {
    const { client, ref } = makeClient({ bearer: "a", reconnect: false });
    const p1 = client.connect();
    ref.ws!.fireOpen();
    await p1;
    expect(ref.ws!.url).toContain("access_token=a");
    // Tausch zur Laufzeit — bestehende Verbindung unberührt, neuer Token ab nächstem Connect.
    client.setBearer("b");
    ref.ws!.fireClose(1001); // Drop -> kein Reconnect (reconnect:false) -> Disconnected
    await vi.waitFor(() => expect(client.state).toBe(SleipnirConnectionState.Disconnected), {
      interval: 1,
      timeout: 1000,
    });
    const p2 = client.connect();
    ref.ws!.fireOpen();
    await p2;
    expect(ref.ws!.url).toContain("access_token=b");
    client.dispose();
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

  it("Schließen der Verbindung lehnt pending Calls mit SleipnirError ab", async () => {
    // reconnect aus — der In-Flight-Drop wird isoliert geprüft (Spiegel SignalR).
    const { client, ref } = makeClient({ reconnect: false });
    const p = client.call({ controller: "C", method: "M", params: [], id: "p" });
    const ws = ref.ws!;
    ws.fireOpen();
    await vi.waitFor(() => expect(ws.sent.length).toBe(1), { interval: 1, timeout: 1000 });
    ws.fireClose(1001);
    await expect(p).rejects.toBeInstanceOf(SleipnirError);
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
    const client = new SleipnirWebSocketClient("https://host:5001/app/", {
      WebSocketCtor: factory,
      wsPath: "sleipnirws",
    });
    const p = client.call({ controller: "C", method: "M", params: [], id: "x" });
    // factory wird synchron beim Connect angelegt
    expect(created).toBe(1);
    expect(url).toBe("wss://host:5001/app/sleipnirws");
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
    expect(client.state).toBe(SleipnirConnectionState.Connected);

    // Unerwarteter Drop -> Hintergrund-Reconnect (neuer MockWs via Factory).
    ws1.fireClose(1001);
    await vi.waitFor(() => expect(client.state).toBe(SleipnirConnectionState.Reconnecting), {
      interval: 1,
      timeout: 1000,
    });
    await vi.waitFor(() => expect(ref.created).toBe(2), { interval: 1, timeout: 1000 });
    const ws2 = ref.ws!;
    ws2.fireOpen(); // Reconnect-Connect auflösen
    await vi.waitFor(() => expect(client.state).toBe(SleipnirConnectionState.Connected), {
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
    const client = new SleipnirWebSocketClient("http://127.0.0.1:0", {
      WebSocketCtor: factory,
      reconnectDelays: [5, 5, 5],
    });
    const p0 = client.call({ controller: "C", method: "M", params: [], id: "x0" });
    ref.ws!.fireOpen();
    await vi.waitFor(() => expect(ref.ws!.sent.length).toBe(1), { interval: 1, timeout: 1000 });
    ref.ws!.fireMessage(resp("x0", "0"));
    await p0;

    ref.ws!.fireClose(1001); // Drop -> 3 Reconnect-Versuche (alle schlagen fehl)
    await vi.waitFor(() => expect(client.state).toBe(SleipnirConnectionState.Disconnected), {
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
    await vi.waitFor(() => expect(client.state).toBe(SleipnirConnectionState.Reconnecting), {
      interval: 1,
      timeout: 1000,
    });
    const createdBeforeDispose = ref.created;
    client.dispose();
    expect(client.state).toBe(SleipnirConnectionState.Disconnected);

    // Keine weiteren Reconnect-Versuche nach dispose.
    await vi.waitFor(() => expect(ref.created).toBe(createdBeforeDispose), {
      interval: 1,
      timeout: 500,
    });
  });
});

// --- Phase 3: server-push events (subscribe / event / complete / error / unsubscribe / reconnect) ---

describe("SleipnirWebSocketClient — Phase 3 subscribe", () => {
  function subResp(id: string, subscriptionId: string): string {
    return JSON.stringify({ code: 200, data: { subscriptionId }, id, isSuccess: true });
  }
  // Phase R: the real server stamps a monotonic eventId per subscription (from 1). The helper
  // auto-increments by default so distinct events carry distinct ids (otherwise client-side dedup
  // would correctly collapse them); pass an explicit eventId to test dedup/replay behavior.
  let _eventFrameSeq = 0;
  function eventFrame(sid: string, data: unknown, eventId?: number): string {
    const id = eventId ?? ++_eventFrameSeq;
    return JSON.stringify({ type: "event", subscriptionId: sid, eventId: id, data });
  }
  function completeFrame(sid: string): string {
    return JSON.stringify({ type: "complete", subscriptionId: sid });
  }
  function errorFrame(sid: string, message: string): string {
    return JSON.stringify({ type: "error", subscriptionId: sid, message });
  }
  const tick = () => new Promise<void>((r) => setTimeout(r, 10));

  // Regression: shared pending key `Controller.Method`. The `_pendingSubscribes` key was the
  // library default id, so the second twin overwrote the first — the first subscribe promise
  // never resolved (neither resolve nor reject) and the second ack frame was "Dropped".
  it("two concurrent identical auto-id subscribes both resolve and route their own frames", async () => {
    const { client, ref } = makeClient();
    const seenA: number[] = [];
    const seenB: number[] = [];
    const pa = client.subscribe<number>(
      { controller: "Tickets", method: "TicketChanged", params: [] },
      { onNext: (n) => seenA.push(n) },
    );
    const pb = client.subscribe<number>(
      { controller: "Tickets", method: "TicketChanged", params: [] },
      { onNext: (n) => seenB.push(n) },
    );
    const ws = ref.ws!;
    ws.fireOpen();
    await vi.waitFor(() => expect(ws.sent.length).toBe(2), { interval: 1, timeout: 1000 });
    const fa = JSON.parse(ws.sent[0]);
    const fb = JSON.parse(ws.sent[1]);
    expect(fa.kind).toBe("subscribe");
    expect(fa.id).toBe("Tickets.TicketChanged#1");
    expect(fb.id).toBe("Tickets.TicketChanged#2");
    ws.fireMessage(subResp(fa.id, "sid-a"));
    ws.fireMessage(subResp(fb.id, "sid-b"));
    const [sa, sb] = await Promise.all([pa, pb]);
    expect(sa.subscriptionId).toBe("sid-a");
    expect(sb.subscriptionId).toBe("sid-b");
    // Each handle routes exactly its own subscription's frames.
    ws.fireMessage(eventFrame("sid-a", 1));
    await vi.waitFor(() => expect(seenA).toEqual([1]), { interval: 1, timeout: 1000 });
    ws.fireMessage(eventFrame("sid-b", 2));
    await vi.waitFor(() => expect(seenB).toEqual([2]), { interval: 1, timeout: 1000 });
    await tick();
    expect(seenA).toEqual([1]); // no leakage from sid-b
    expect(seenB).toEqual([2]); // no leakage from sid-a
    client.dispose();
  });

  it("subscribe sends kind:\"subscribe\", resolves the subscriptionId, and routes event frames to onNext", async () => {
    const { client, ref } = makeClient();
    const seen: number[] = [];
    const p = client.subscribe<{ value: number }>(
      { controller: "Chat", method: "MessageReceived", params: [{ parameterName: "chatId", data: 1 }], id: "sub1" },
      { onNext: (m) => seen.push(m.value) },
    );
    const ws = ref.ws!;
    ws.fireOpen();
    await vi.waitFor(() => expect(ws.sent.length).toBe(1), { interval: 1, timeout: 1000 });
    // kind:"subscribe" routes server-side; controller/method ride on the same frame.
    const sent = JSON.parse(ws.lastSent!);
    expect(sent.kind).toBe("subscribe");
    expect(sent.controller).toBe("Chat");
    expect(sent.method).toBe("MessageReceived");

    ws.fireMessage(subResp("sub1", "s1"));
    const sub = await p;
    expect(sub.subscriptionId).toBe("s1");

    // Event frames route per subscriptionId → onNext(payload).
    ws.fireMessage(eventFrame("s1", { value: 10 }));
    ws.fireMessage(eventFrame("s1", { value: 20 }));
    await vi.waitFor(() => expect(seen).toEqual([10, 20]), { interval: 1, timeout: 1000 });
    client.dispose();
  });

  it("complete frame calls onComplete and removes the subscription (later events dropped)", async () => {
    const { client, ref } = makeClient();
    let completed = false;
    let next = 0;
    const p = client.subscribe<number>(
      { controller: "Ticker", method: "Ticks", params: [], id: "sub2" },
      { onNext: (n) => { next = n; }, onComplete: () => { completed = true; } },
    );
    const ws = ref.ws!;
    ws.fireOpen();
    await vi.waitFor(() => expect(ws.sent.length).toBe(1), { interval: 1, timeout: 1000 });
    ws.fireMessage(subResp("sub2", "s2"));
    await p;
    ws.fireMessage(eventFrame("s2", 1));
    await vi.waitFor(() => expect(next).toBe(1), { interval: 1, timeout: 1000 });
    ws.fireMessage(completeFrame("s2"));
    await vi.waitFor(() => expect(completed).toBe(true), { interval: 1, timeout: 1000 });
    // After complete the subscription is gone — a late event frame is silently dropped.
    ws.fireMessage(eventFrame("s2", 99));
    await tick();
    expect(next).toBe(1); // unchanged
    client.dispose();
  });

  it("error frame calls onError with the message and removes the subscription", async () => {
    const { client, ref } = makeClient();
    let errMsg: string | undefined;
    const p = client.subscribe<number>(
      { controller: "C", method: "M", params: [], id: "sub3" },
      { onNext: () => {}, onError: (e) => { errMsg = e.message; } },
    );
    const ws = ref.ws!;
    ws.fireOpen();
    await vi.waitFor(() => expect(ws.sent.length).toBe(1), { interval: 1, timeout: 1000 });
    ws.fireMessage(subResp("sub3", "s3"));
    await p;
    ws.fireMessage(errorFrame("s3", "boom"));
    await vi.waitFor(() => expect(errMsg).toBe("boom"), { interval: 1, timeout: 1000 });
    // subscription removed — a late event frame is dropped.
    ws.fireMessage(eventFrame("s3", 5));
    await tick();
    client.dispose();
  });

  it("unsubscribe sends kind:\"unsubscribe\" and stops delivery (idempotent)", async () => {
    const { client, ref } = makeClient();
    let next = 0;
    const p = client.subscribe<number>(
      { controller: "C", method: "M", params: [], id: "sub4" },
      { onNext: (n) => { next = n; } },
    );
    const ws = ref.ws!;
    ws.fireOpen();
    await vi.waitFor(() => expect(ws.sent.length).toBe(1), { interval: 1, timeout: 1000 });
    ws.fireMessage(subResp("sub4", "s4"));
    const sub = await p;
    await sub.unsubscribe();
    const unsub = ws.sent.map((s) => JSON.parse(s)).find((o) => o.kind === "unsubscribe");
    expect(unsub).toBeDefined();
    expect(unsub.subscriptionId).toBe("s4");
    // Delivery stopped; a second unsubscribe is a no-op (no second frame).
    await sub.unsubscribe();
    const unsubs = ws.sent.map((s) => JSON.parse(s)).filter((o) => o.kind === "unsubscribe");
    expect(unsubs).toHaveLength(1);
    ws.fireMessage(eventFrame("s4", 7));
    await tick();
    expect(next).toBe(0);
    client.dispose();
  });

  it("reconnect re-subscribes with the same request (new subscriptionId) and delivery resumes", async () => {
    const { client, ref } = makeClient({ reconnectDelays: [10] });
    const seen: number[] = [];
    const p = client.subscribe<number>(
      { controller: "C", method: "M", params: [], id: "sub5" },
      { onNext: (n) => seen.push(n) },
    );
    const ws1 = ref.ws!;
    ws1.fireOpen();
    await vi.waitFor(() => expect(ws1.sent.length).toBe(1), { interval: 1, timeout: 1000 });
    ws1.fireMessage(subResp("sub5", "s5a"));
    await p;
    ws1.fireMessage(eventFrame("s5a", 1));
    await vi.waitFor(() => expect(seen).toEqual([1]), { interval: 1, timeout: 1000 });

    // Unexpected drop → background reconnect → new socket → resubscribeAll fires.
    ws1.fireClose(1001);
    await vi.waitFor(() => expect(ref.created).toBe(2), { interval: 1, timeout: 1000 });
    const ws2 = ref.ws!;
    ws2.fireOpen(); // reconnect connect resolves; resubscribeAll runs once Connected.
    // A re-subscribe frame (same request, kind:"subscribe") appears on the new socket.
    await vi.waitFor(
      () => expect(ws2.sent.some((s) => JSON.parse(s).kind === "subscribe")).toBe(true),
      { interval: 1, timeout: 1000 },
    );
    const subFrame = ws2.sent.map((s) => JSON.parse(s)).find((o) => o.kind === "subscribe");
    expect(subFrame.controller).toBe("C");
    // Server hands out a NEW subscriptionId for the new connection.
    ws2.fireMessage(subResp(subFrame.id, "s5b"));
    // Delivery resumes under the new subscriptionId (at-most-once: gap events lost).
    ws2.fireMessage(eventFrame("s5b", 2));
    await vi.waitFor(() => expect(seen).toEqual([1, 2]), { interval: 1, timeout: 1000 });
    client.dispose();
  });

  // --- Phase R: resume decision hook (Fresh / Resume / Drop) + eventId dedup ---

  it("Phase R: dedup drops replayed eventId, forwards fresh ids only", async () => {
    const { client, ref } = makeClient({ reconnect: false });
    const seen: string[] = [];
    const p = client.subscribe<string>(
      { controller: "C", method: "M", params: [], id: "sub-dedup" },
      { onNext: (v) => seen.push(v) },
    );
    const ws = ref.ws!;
    ws.fireOpen();
    await vi.waitFor(() => expect(ws.sent.length).toBe(1), { interval: 1, timeout: 1000 });
    ws.fireMessage(subResp("sub-dedup", "sd"));
    await p;

    ws.fireMessage(eventFrame("sd", "a", 1));
    ws.fireMessage(eventFrame("sd", "a-again", 1)); // replayed eventId → deduped
    ws.fireMessage(eventFrame("sd", "b", 2));
    await vi.waitFor(() => expect(seen).toEqual(["a", "b"]), { interval: 1, timeout: 1000 });
    client.dispose();
  });

  it("Phase R: default policy (fresh) re-subscribes without resume fields", async () => {
    const { client, ref } = makeClient({ reconnectDelays: [10] });
    const p = client.subscribe<number>(
      { controller: "C", method: "M", params: [], id: "sub-fresh" },
      { onNext: () => {} },
    );
    const ws1 = ref.ws!;
    ws1.fireOpen();
    await vi.waitFor(() => expect(ws1.sent.length).toBe(1), { interval: 1, timeout: 1000 });
    ws1.fireMessage(subResp("sub-fresh", "sf"));
    await p;

    ws1.fireClose(1001);
    await vi.waitFor(() => expect(ref.created).toBe(2), { interval: 1, timeout: 1000 });
    const ws2 = ref.ws!;
    ws2.fireOpen();
    await vi.waitFor(
      () => expect(ws2.sent.some((s) => JSON.parse(s).kind === "subscribe")).toBe(true),
      { interval: 1, timeout: 1000 },
    );
    const subFrame = ws2.sent.map((s) => JSON.parse(s)).find((o) => o.kind === "subscribe");
    expect(subFrame.subscriptionId).toBeUndefined();
    expect(subFrame.lastEventId).toBeUndefined();
    client.dispose();
  });

  it("Phase R: resume policy re-subscribes with the durable subscriptionId + lastEventId", async () => {
    const { client, ref } = makeClient({ reconnectDelays: [10], onResume: () => "resume" as const });
    const seen: number[] = [];
    const p = client.subscribe<number>(
      { controller: "C", method: "M", params: [], id: "sub-resume" },
      { onNext: (n) => seen.push(n) },
    );
    const ws1 = ref.ws!;
    ws1.fireOpen();
    await vi.waitFor(() => expect(ws1.sent.length).toBe(1), { interval: 1, timeout: 1000 });
    ws1.fireMessage(subResp("sub-resume", "sr"));
    await p;
    // Process one event so the client's lastEventId cursor becomes 1.
    ws1.fireMessage(eventFrame("sr", 100, 1));
    await vi.waitFor(() => expect(seen).toEqual([100]), { interval: 1, timeout: 1000 });

    ws1.fireClose(1001);
    await vi.waitFor(() => expect(ref.created).toBe(2), { interval: 1, timeout: 1000 });
    const ws2 = ref.ws!;
    ws2.fireOpen();
    await vi.waitFor(
      () => expect(ws2.sent.some((s) => JSON.parse(s).kind === "subscribe")).toBe(true),
      { interval: 1, timeout: 1000 },
    );
    const subFrame = ws2.sent.map((s) => JSON.parse(s)).find((o) => o.kind === "subscribe");
    expect(subFrame.subscriptionId).toBe("sr");
    expect(subFrame.lastEventId).toBe(1);
    client.dispose();
  });

  it("Phase R: drop policy does not re-subscribe and fires onComplete", async () => {
    const { client, ref } = makeClient({ reconnectDelays: [10], onResume: () => "drop" as const });
    let completed = false;
    const p = client.subscribe<number>(
      { controller: "C", method: "M", params: [], id: "sub-drop" },
      { onNext: () => {}, onComplete: () => { completed = true; } },
    );
    const ws1 = ref.ws!;
    ws1.fireOpen();
    await vi.waitFor(() => expect(ws1.sent.length).toBe(1), { interval: 1, timeout: 1000 });
    ws1.fireMessage(subResp("sub-drop", "sdrop"));
    await p;

    ws1.fireClose(1001);
    await vi.waitFor(() => expect(ref.created).toBe(2), { interval: 1, timeout: 1000 });
    const ws2 = ref.ws!;
    ws2.fireOpen();
    await tick();
    // No re-subscribe frame on the new socket.
    expect(ws2.sent.some((s) => JSON.parse(s).kind === "subscribe")).toBe(false);
    await vi.waitFor(() => expect(completed).toBe(true), { interval: 1, timeout: 1000 });
    client.dispose();
  });

  it("Phase R: resume not honored by the server degrades to fresh (cursor resets, delivery resumes)", async () => {
    const { client, ref } = makeClient({ reconnectDelays: [10], onResume: () => "resume" as const });
    const seen: number[] = [];
    const p = client.subscribe<number>(
      { controller: "C", method: "M", params: [], id: "sub-degrade" },
      { onNext: (n) => seen.push(n) },
    );
    const ws1 = ref.ws!;
    ws1.fireOpen();
    await vi.waitFor(() => expect(ws1.sent.length).toBe(1), { interval: 1, timeout: 1000 });
    ws1.fireMessage(subResp("sub-degrade", "sdg"));
    await p;
    ws1.fireMessage(eventFrame("sdg", 100, 1));
    await vi.waitFor(() => expect(seen).toEqual([100]), { interval: 1, timeout: 1000 });

    ws1.fireClose(1001);
    await vi.waitFor(() => expect(ref.created).toBe(2), { interval: 1, timeout: 1000 });
    const ws2 = ref.ws!;
    ws2.fireOpen();
    await vi.waitFor(
      () => expect(ws2.sent.some((s) => JSON.parse(s).kind === "subscribe")).toBe(true),
      { interval: 1, timeout: 1000 },
    );
    const subFrame = ws2.sent.map((s) => JSON.parse(s)).find((o) => o.kind === "subscribe");
    expect(subFrame.subscriptionId).toBe("sdg"); // client requested resume with the durable id
    // Server degrades: returns a NEW subscriptionId (not "sdg").
    ws2.fireMessage(subResp(subFrame.id, "sdg-fresh"));
    // The fresh server stream restarts its eventId at 1; the cursor was reset → delivered.
    ws2.fireMessage(eventFrame("sdg-fresh", 200, 1));
    await vi.waitFor(() => expect(seen).toEqual([100, 200]), { interval: 1, timeout: 1000 });
    client.dispose();
  });
});