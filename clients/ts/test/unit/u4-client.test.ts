// U4 — connection state, error category, the 401 hook, and signal/timeout/ended on subscriptions.
import { describe, it, expect, vi, afterEach } from "vitest";
import { SleipnirTransportRouter } from "../../src/transport-router.js";
import { SleipnirWebSocketClient } from "../../src/websocket.js";
import type { IWebSocket, WsFactory } from "../../src/websocket.js";
import { SleipnirSseClient } from "../../src/sse.js";
import {
  SleipnirSignalrClient,
  type IHubConnection,
  type IStreamResult,
  type IStreamSubscriber,
} from "../../src/signalr.js";
import { SleipnirError, CancelledError } from "../../src/errors.js";
import { normalizeResponse } from "../../src/request.js";
import { ExecutionMode, SleipnirConnectionState } from "../../src/types.js";
import type {
  SleipnirConnectionStatus,
  SleipnirErrorBody,
  SleipnirErrorCategory,
  SleipnirResponse,
} from "../../src/types.js";

afterEach(() => {
  vi.restoreAllMocks();
});

const tick = (ms = 0) => new Promise<void>((r) => setTimeout(r, ms));

// ─── fakes ──────────────────────────────────────────────────────────────────────

class MockWs implements IWebSocket {
  readyState = 0;
  onopen: (() => void) | null = null;
  onmessage: ((ev: { data: string | ArrayBuffer }) => void) | null = null;
  onclose: ((ev: { code?: number; reason?: string }) => void) | null = null;
  onerror: ((ev: unknown) => void) | null = null;
  readonly sent: string[] = [];
  closed = false;
  constructor(readonly url: string) {}
  send(data: string): void {
    this.sent.push(data);
  }
  close(): void {
    this.closed = true;
    this.readyState = 3;
  }
  fireOpen(): void {
    this.readyState = 1;
    this.onopen?.();
  }
  fireMessage(obj: unknown): void {
    this.onmessage?.({ data: JSON.stringify(obj) });
  }
  fireClose(code = 1006): void {
    this.readyState = 3;
    this.onclose?.({ code });
  }
  frames(): any[] {
    return this.sent.map((s) => JSON.parse(s));
  }
}

interface WsRef {
  sockets: MockWs[];
  /** Auto-open each new socket (on the next macrotask). */
  autoOpen: boolean;
}
function wsFactory(ref: WsRef): WsFactory {
  return (url) => {
    const ws = new MockWs(url);
    ref.sockets.push(ws);
    if (ref.autoOpen) setTimeout(() => ws.fireOpen(), 0);
    return ws;
  };
}
const lastWs = (ref: WsRef) => ref.sockets[ref.sockets.length - 1];

/** A fetch whose SSE bodies the test drives; aborting the request errors the body/fetch. */
class FakeSse {
  readonly requests: { url: string; headers: Record<string, string>; push: (s: string) => void; end: () => void }[] = [];
  /** Per-request status (default 200); index = request number. */
  statuses: number[] = [];
  /** When true, fetch never resolves until aborted. */
  hang = false;
  readonly fetch = (input: string | URL | Request, init?: RequestInit): Promise<Response> => {
    const signal = init?.signal ?? undefined;
    const idx = this.requests.length;
    const enc = new TextEncoder();
    let ctrl!: ReadableStreamDefaultController<Uint8Array>;
    const body = new ReadableStream<Uint8Array>({
      start(c) {
        ctrl = c;
      },
    });
    let ended = false;
    this.requests.push({
      url: String(input),
      headers: (init?.headers ?? {}) as Record<string, string>,
      push: (s) => ctrl.enqueue(enc.encode(s)),
      end: () => {
        if (!ended) {
          ended = true;
          ctrl.close();
        }
      },
    });
    return new Promise<Response>((resolve, reject) => {
      const onAbort = () => {
        try {
          if (!ended) ctrl.error(new DOMException("aborted", "AbortError"));
        } catch {
          /* already closed */
        }
        ended = true;
        reject(new DOMException("aborted", "AbortError"));
      };
      if (signal?.aborted) return onAbort();
      signal?.addEventListener("abort", onAbort, { once: true });
      if (this.hang) return;
      const status = this.statuses[idx] ?? 200;
      resolve(new Response(status === 200 ? body : "nope", { status }));
    });
  };
}
const ackBlock = (id: string) => `id: 0\nevent: ack\ndata: ${JSON.stringify({ subscriptionId: id })}\n\n`;
const sseEventBlock = (id: string, eventId: number, data: unknown) =>
  `id: ${eventId}\nevent: event\ndata: ${JSON.stringify({ type: "event", subscriptionId: id, eventId, data })}\n\n`;
const sseCompleteBlock = (id: string) =>
  `event: complete\ndata: ${JSON.stringify({ type: "complete", subscriptionId: id })}\n\n`;

class FakeStream implements IStreamResult<string> {
  observer?: { next: (v: string) => void; complete?: () => void; error?: (e: unknown) => void };
  disposed = false;
  subscribe(observer: { next: (v: string) => void; complete?: () => void; error?: (e: unknown) => void }): IStreamSubscriber {
    this.observer = observer;
    return { dispose: () => { this.disposed = true; this.observer = undefined; } };
  }
  push(obj: unknown): void {
    this.observer?.next(JSON.stringify(obj));
  }
}
class FakeHub implements IHubConnection {
  readonly streams: FakeStream[] = [];
  invokeResults: unknown[] = [];
  invokes = 0;
  stopped = false;
  reconnecting?: (e?: unknown) => void;
  reconnected?: (c?: string) => void;
  closed?: (e?: unknown) => void;
  constructor(readonly token?: string) {}
  async start(): Promise<void> {}
  async stop(): Promise<void> {
    this.stopped = true;
  }
  invoke<T>(): Promise<T> {
    const r = this.invokeResults[Math.min(this.invokes, this.invokeResults.length - 1)];
    this.invokes++;
    return Promise.resolve(r as T);
  }
  stream<T>(): IStreamResult<T> {
    const s = new FakeStream();
    this.streams.push(s);
    return s as unknown as IStreamResult<T>;
  }
  onreconnecting(h: (e?: unknown) => void): void {
    this.reconnecting = h;
  }
  onreconnected(h: (c?: string) => void): void {
    this.reconnected = h;
  }
  onclose(h: (e?: unknown) => void): void {
    this.closed = h;
  }
}

function jsonResponse(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { "content-type": "application/json" } });
}
const unauthorized = (id = "r") => ({
  code: 401,
  id,
  data: null,
  error: { code: 401, message: "Unauthorized.", category: "Unauthenticated" },
});
const ok = (data: unknown, id = "r") => ({ code: 200, id, data });

function record(router: SleipnirTransportRouter): SleipnirConnectionStatus[] {
  const seen: SleipnirConnectionStatus[] = [];
  router.connection.subscribe((s) => seen.push(s));
  return seen;
}

// ─── 1. error category ─────────────────────────────────────────────────────────

describe("U4 — SleipnirErrorBody.category", () => {
  it("is typed as the server's string-literal union", () => {
    const body: SleipnirErrorBody = { code: 404, message: "x", category: "NotFound" };
    const all: SleipnirErrorCategory[] = [
      "None", "InvalidArgument", "Unauthenticated", "PermissionDenied", "NotFound", "Conflict",
      "FailedPrecondition", "ResourceExhausted", "Internal", "Unavailable", "Cancelled",
    ];
    // @ts-expect-error — not a server category
    const bogus: SleipnirErrorCategory = "Bogus";
    expect(all).toContain(body.category);
    expect(bogus).toBe("Bogus");
  });

  it("keeps the PascalCase wire name and carries it into SleipnirError", () => {
    const r = normalizeResponse({ code: 403, error: { code: 403, message: "Forbidden.", category: "PermissionDenied" } } as SleipnirResponse);
    expect(r.error?.category).toBe("PermissionDenied");
    const err = SleipnirError.fromResponse(r);
    expect(err.category).toBe("PermissionDenied");
    expect(err.code).toBe(403);
  });

  it("canonicalizes numeric and camelCase spellings; leaves unknown values untouched", () => {
    const num = normalizeResponse({ code: 401, error: { code: 401, message: "x", category: 2 as any } } as SleipnirResponse);
    expect(num.error?.category).toBe("Unauthenticated");
    const camel = normalizeResponse({ code: 409, error: { code: 409, message: "x", category: "conflict" as any } } as SleipnirResponse);
    expect(camel.error?.category).toBe("Conflict");
    const future = normalizeResponse({ code: 500, error: { code: 500, message: "x", category: "SomethingNew" as any } } as SleipnirResponse);
    expect(future.error?.category).toBe("SomethingNew");
  });

  it("is parsed from a REST response", async () => {
    const fetch = vi.fn(async () => jsonResponse(unauthorized()));
    const router = new SleipnirTransportRouter({ baseUrl: "https://h", capability: "rest", defaultTransport: "rest", rest: { fetch } });
    const res = await router.call({ controller: "C", method: "M" });
    expect(res.error?.category).toBe("Unauthenticated");
  });
});

// ─── 2. connection state ───────────────────────────────────────────────────────

describe("U4 — router.connection (aggregated state)", () => {
  it("rest profile is 'open' immediately; the store calls a new listener synchronously", () => {
    const router = new SleipnirTransportRouter({ baseUrl: "https://h", capability: "rest", defaultTransport: "rest", rest: { fetch: vi.fn() }, sse: { fetch: vi.fn() } });
    const seen = record(router);
    expect(router.connection.state).toBe("open");
    expect(seen).toEqual(["open"]);
    router.dispose();
    expect(seen).toEqual(["open", "closed"]);
  });

  it("ws profile: closed → connecting → open → reconnecting → open → closed", async () => {
    const ref: WsRef = { sockets: [], autoOpen: false };
    const router = new SleipnirTransportRouter({
      baseUrl: "https://h", capability: "ws", defaultTransport: "ws",
      ws: { WebSocketCtor: wsFactory(ref), reconnectDelays: [5] },
    });
    const seen = record(router);
    expect(seen).toEqual(["closed"]);
    const connected = router.ws!.connect();
    expect(router.connection.state).toBe("connecting");
    lastWs(ref).fireOpen();
    await connected;
    expect(router.connection.state).toBe("open");
    lastWs(ref).fireClose();
    expect(router.connection.state).toBe("reconnecting");
    await vi.waitFor(() => expect(ref.sockets.length).toBe(2), { interval: 1, timeout: 1000 });
    lastWs(ref).fireOpen();
    await vi.waitFor(() => expect(router.connection.state).toBe("open"), { interval: 1, timeout: 1000 });
    router.dispose();
    expect(seen).toEqual(["closed", "connecting", "open", "reconnecting", "open", "closed"]);
  });

  it("ws profile: reconnect giving up ends in 'closed'", async () => {
    const ref: WsRef = { sockets: [], autoOpen: false };
    const router = new SleipnirTransportRouter({
      baseUrl: "https://h", capability: "ws", defaultTransport: "ws",
      ws: { WebSocketCtor: wsFactory(ref), reconnectDelays: [1], connectTimeout: 20 },
    });
    const connected = router.ws!.connect();
    lastWs(ref).fireOpen();
    await connected;
    lastWs(ref).fireClose();
    expect(router.connection.state).toBe("reconnecting");
    // the single retry socket never opens → connect timeout → backoff exhausted
    await vi.waitFor(() => expect(router.connection.state).toBe("closed"), { interval: 2, timeout: 2000 });
  });

  it("auto: 'connecting' during the probe, then 'open' on the REST fallback", async () => {
    const ref: WsRef = { sockets: [], autoOpen: false };
    const router = new SleipnirTransportRouter({
      baseUrl: "https://h", capability: "all", probeTimeout: 20,
      ws: { WebSocketCtor: wsFactory(ref), reconnect: false },
      rest: { fetch: vi.fn() }, sse: { fetch: vi.fn() },
    });
    const seen = record(router);
    const n = router.negotiate();
    expect(router.connection.state).toBe("connecting");
    await n;
    expect(router.activeTransport).toBe("rest");
    expect(seen).toEqual(["closed", "connecting", "open"]);
  });

  it("rest profile: an SSE stream drop shows 'reconnecting' until the stream is acked again", async () => {
    const sse = new FakeSse();
    const router = new SleipnirTransportRouter({
      baseUrl: "https://h", capability: "rest", defaultTransport: "rest",
      rest: { fetch: vi.fn() }, sse: { fetch: sse.fetch, reconnectDelays: [10] },
    });
    const seen = record(router);
    const p = router.subscribe({ controller: "C", method: "E" }, { onNext: () => {} });
    await vi.waitFor(() => expect(sse.requests.length).toBe(1), { interval: 1, timeout: 1000 });
    sse.requests[0].push(ackBlock("s1"));
    const sub = await p;
    expect(router.connection.state).toBe("open");
    sse.requests[0].end(); // drop without a terminal frame
    await vi.waitFor(() => expect(router.connection.state).toBe("reconnecting"), { interval: 1, timeout: 1000 });
    await vi.waitFor(() => expect(sse.requests.length).toBe(2), { interval: 1, timeout: 1000 });
    sse.requests[1].push(ackBlock("s2"));
    await vi.waitFor(() => expect(router.connection.state).toBe("open"), { interval: 1, timeout: 1000 });
    await sub.unsubscribe();
    expect(seen).toEqual(["open", "reconnecting", "open"]);
  });

  it("signalr profile follows the hub lifecycle", async () => {
    const hub = new FakeHub();
    const router = new SleipnirTransportRouter({
      baseUrl: "https://h", capability: "signalr", defaultTransport: "signalr",
      signalr: { hubFactory: () => hub },
    });
    const seen = record(router);
    await router.signalr!.connect();
    hub.reconnecting!();
    hub.reconnected!();
    hub.closed!(new Error("gone"));
    expect(seen).toEqual(["closed", "connecting", "open", "reconnecting", "open", "closed"]);
  });

  it("the unsubscribe function stops notifications; a user onStateChanged still fires", async () => {
    const ref: WsRef = { sockets: [], autoOpen: true };
    const user: SleipnirConnectionState[] = [];
    const router = new SleipnirTransportRouter({
      baseUrl: "https://h", capability: "ws", defaultTransport: "ws",
      ws: { WebSocketCtor: wsFactory(ref), onStateChanged: (s) => user.push(s) },
    });
    const seen: SleipnirConnectionStatus[] = [];
    const off = router.connection.subscribe((s) => seen.push(s));
    off();
    await router.ws!.connect();
    expect(seen).toEqual(["closed"]);
    expect(user).toEqual([SleipnirConnectionState.Connecting, SleipnirConnectionState.Connected]);
    router.dispose();
  });
});

// ─── 3. onUnauthenticated (401 → refresh → exactly one retry) ────────────────────

describe("U4 — onUnauthenticated", () => {
  it("REST: refreshes and retries once; the retry carries the new bearer", async () => {
    const auths: (string | undefined)[] = [];
    const fetch = vi.fn(async (_u: any, init?: RequestInit) => {
      const auth = (init?.headers as Record<string, string>)["Authorization"];
      auths.push(auth);
      return jsonResponse(auth === "Bearer new" ? ok(42) : unauthorized());
    });
    let router!: SleipnirTransportRouter;
    const hook = vi.fn(async () => router.setBearer("new"));
    router = new SleipnirTransportRouter({
      baseUrl: "https://h", capability: "rest", defaultTransport: "rest", bearer: "old",
      rest: { fetch }, onUnauthenticated: hook,
    });
    const res = await router.call({ controller: "C", method: "M" });
    expect(res.code).toBe(200);
    expect(res.data).toBe(42);
    expect(hook).toHaveBeenCalledTimes(1);
    expect(hook).toHaveBeenCalledWith({ transport: "rest", operation: "call" });
    expect(auths).toEqual(["Bearer old", "Bearer new"]);
  });

  it("retries exactly once — a second 401 is returned, no loop", async () => {
    const fetch = vi.fn(async () => jsonResponse(unauthorized()));
    const hook = vi.fn();
    const router = new SleipnirTransportRouter({
      baseUrl: "https://h", capability: "rest", defaultTransport: "rest", rest: { fetch }, onUnauthenticated: hook,
    });
    const res = await router.call({ controller: "C", method: "M" });
    expect(res.code).toBe(401);
    expect(hook).toHaveBeenCalledTimes(1);
    expect(fetch).toHaveBeenCalledTimes(2);
  });

  it("without a hook, a 401 is returned untouched (no retry)", async () => {
    const fetch = vi.fn(async () => jsonResponse(unauthorized()));
    const router = new SleipnirTransportRouter({ baseUrl: "https://h", capability: "rest", defaultTransport: "rest", rest: { fetch } });
    expect((await router.call({ controller: "C", method: "M" })).code).toBe(401);
    expect(fetch).toHaveBeenCalledTimes(1);
  });

  it("403 never triggers the hook", async () => {
    const fetch = vi.fn(async () => jsonResponse({ code: 403, error: { code: 403, message: "Forbidden.", category: "PermissionDenied" } }));
    const hook = vi.fn();
    const router = new SleipnirTransportRouter({ baseUrl: "https://h", capability: "rest", defaultTransport: "rest", rest: { fetch }, onUnauthenticated: hook });
    expect((await router.call({ controller: "C", method: "M" })).code).toBe(403);
    expect(hook).not.toHaveBeenCalled();
  });

  it("concurrent 401s share one refresh", async () => {
    let refreshed = false;
    const fetch = vi.fn(async () => jsonResponse(refreshed ? ok(1) : unauthorized()));
    const hook = vi.fn(async () => {
      await tick(5);
      refreshed = true;
    });
    const router = new SleipnirTransportRouter({ baseUrl: "https://h", capability: "rest", defaultTransport: "rest", rest: { fetch }, onUnauthenticated: hook });
    const [a, b] = await Promise.all([
      router.call({ controller: "C", method: "A" }),
      router.call({ controller: "C", method: "B" }),
    ]);
    expect([a.code, b.code]).toEqual([200, 200]);
    expect(hook).toHaveBeenCalledTimes(1);
  });

  it("a throwing hook rejects the call", async () => {
    const fetch = vi.fn(async () => jsonResponse(unauthorized()));
    const router = new SleipnirTransportRouter({
      baseUrl: "https://h", capability: "rest", defaultTransport: "rest", rest: { fetch },
      onUnauthenticated: () => { throw new Error("login required"); },
    });
    await expect(router.call({ controller: "C", method: "M" })).rejects.toThrow("login required");
  });

  it("batch: retried only when every response is 401", async () => {
    const all401 = vi.fn(async () => jsonResponse([unauthorized("a"), unauthorized("b")]));
    const hook = vi.fn();
    const r1 = new SleipnirTransportRouter({ baseUrl: "https://h", capability: "rest", defaultTransport: "rest", rest: { fetch: all401 }, onUnauthenticated: hook });
    await r1.callBatch([{ controller: "C", method: "A", id: "a" }, { controller: "C", method: "B", id: "b" }], ExecutionMode.Parallel);
    expect(all401).toHaveBeenCalledTimes(2);
    expect(hook).toHaveBeenCalledWith({ transport: "rest", operation: "batch" });

    const partial = vi.fn(async () => jsonResponse([ok(1, "a"), unauthorized("b")]));
    const hook2 = vi.fn();
    const r2 = new SleipnirTransportRouter({ baseUrl: "https://h", capability: "rest", defaultTransport: "rest", rest: { fetch: partial }, onUnauthenticated: hook2 });
    const res = await r2.callBatch([{ controller: "C", method: "A", id: "a" }, { controller: "C", method: "B", id: "b" }], ExecutionMode.Parallel);
    expect(res.map((x) => x.code)).toEqual([200, 401]);
    expect(partial).toHaveBeenCalledTimes(1);
    expect(hook2).not.toHaveBeenCalled();
  });

  it("WS: the hook is followed by a reconnect presenting the new token; the call is retried on it", async () => {
    const ref: WsRef = { sockets: [], autoOpen: true };
    let router!: SleipnirTransportRouter;
    const hook = vi.fn(async () => router.setBearer("fresh"));
    router = new SleipnirTransportRouter({
      baseUrl: "https://h", capability: "ws", defaultTransport: "ws", bearer: "stale",
      ws: { WebSocketCtor: wsFactory(ref) }, onUnauthenticated: hook,
    });
    const p = router.call({ controller: "C", method: "M", id: "c1" });
    await vi.waitFor(() => expect(ref.sockets[0]?.sent.length).toBe(1), { interval: 1, timeout: 1000 });
    ref.sockets[0].fireMessage(unauthorized("c1"));
    await vi.waitFor(() => expect(ref.sockets[1]?.sent.length).toBe(1), { interval: 1, timeout: 1000 });
    expect(ref.sockets[0].closed).toBe(true);
    // Browser-style WS (globalThis.WebSocket exists in Node ≥ 22): the token rides in the query.
    expect(ref.sockets[1].url).toContain("access_token=fresh");
    ref.sockets[1].fireMessage(ok("yes", "c1"));
    const res = await p;
    expect(res.data).toBe("yes");
    expect(hook).toHaveBeenCalledWith({ transport: "ws", operation: "call" });
    router.dispose();
  });

  it("SSE subscribe: a 401 rejection triggers the hook and one retry", async () => {
    const sse = new FakeSse();
    sse.statuses = [401, 200];
    const hook = vi.fn();
    const router = new SleipnirTransportRouter({
      baseUrl: "https://h", capability: "rest", defaultTransport: "rest",
      rest: { fetch: vi.fn() }, sse: { fetch: sse.fetch }, onUnauthenticated: hook,
    });
    const p = router.subscribe({ controller: "C", method: "E" }, { onNext: () => {} });
    await vi.waitFor(() => expect(sse.requests.length).toBe(2), { interval: 1, timeout: 1000 });
    sse.requests[1].push(ackBlock("s1"));
    const sub = await p;
    expect(sub.subscriptionId).toBe("s1");
    expect(hook).toHaveBeenCalledWith({ transport: "rest", operation: "subscribe" });
    await sub.unsubscribe();
  });

  it("SignalR: the hook is followed by a new hub connection built with the new token", async () => {
    const hubs: FakeHub[] = [];
    let router!: SleipnirTransportRouter;
    router = new SleipnirTransportRouter({
      baseUrl: "https://h", capability: "signalr", defaultTransport: "signalr", bearer: "stale",
      signalr: {
        hubFactory: (_url, o) => {
          const hub = new FakeHub(o.accessTokenProvider?.() as string | undefined);
          hub.invokeResults = [hubs.length === 0 ? unauthorized() : ok(7)];
          hubs.push(hub);
          return hub;
        },
      },
      onUnauthenticated: () => router.setBearer("fresh"),
    });
    const res = await router.call({ controller: "C", method: "M" });
    expect(res.data).toBe(7);
    expect(hubs.map((h) => h.token)).toEqual(["stale", "fresh"]);
    expect(hubs[0].stopped).toBe(true);
  });
});

// ─── 4. signal / timeout / ended on subscriptions ───────────────────────────────

describe("U4 — subscription signal, timeout and ended", () => {
  async function wsSubscribe(signal?: AbortSignal) {
    const ref: WsRef = { sockets: [], autoOpen: true };
    const client = new SleipnirWebSocketClient("http://h", { WebSocketCtor: wsFactory(ref), reconnectDelays: [5] });
    const p = client.subscribe({ controller: "C", method: "E", id: "s" }, { onNext: () => {} }, { signal });
    await vi.waitFor(() => expect(ref.sockets[0]?.sent.length).toBe(1), { interval: 1, timeout: 1000 });
    ref.sockets[0].fireMessage({ code: 200, id: "s", data: { subscriptionId: "sid-1" } });
    const sub = await p;
    return { ref, client, sub };
  }

  it("WS: aborting the signal after the ack unsubscribes and resolves ended", async () => {
    const ctrl = new AbortController();
    const { ref, client, sub } = await wsSubscribe(ctrl.signal);
    ctrl.abort();
    await sub.ended;
    expect(ref.sockets[0].frames().some((f) => f.kind === "unsubscribe" && f.subscriptionId === "sid-1")).toBe(true);
    client.dispose();
  });

  it("WS: the signal follows a reconnect re-subscribe (new server id)", async () => {
    const ctrl = new AbortController();
    const { ref, client, sub } = await wsSubscribe(ctrl.signal);
    ref.sockets[0].fireClose();
    await vi.waitFor(() => expect(ref.sockets[1]?.sent.length).toBe(1), { interval: 1, timeout: 1000 });
    const resub = ref.sockets[1].frames()[0];
    ref.sockets[1].fireMessage({ code: 200, id: resub.id, data: { subscriptionId: "sid-2" } });
    await vi.waitFor(() => expect(sub.subscriptionId).toBe("sid-2"), { interval: 1, timeout: 1000 });
    ctrl.abort();
    await sub.ended;
    expect(ref.sockets[1].frames().some((f) => f.kind === "unsubscribe" && f.subscriptionId === "sid-2")).toBe(true);
    client.dispose();
  });

  it("WS: a server-side complete resolves ended and detaches the signal listener", async () => {
    const ctrl = new AbortController();
    const remove = vi.spyOn(ctrl.signal, "removeEventListener");
    const { ref, client, sub } = await wsSubscribe(ctrl.signal);
    ref.sockets[0].fireMessage({ type: "complete", subscriptionId: "sid-1" });
    await sub.ended;
    expect(remove).toHaveBeenCalledWith("abort", expect.any(Function));
    client.dispose();
  });

  it("WS: unsubscribe() resolves ended; a closing client ends every subscription", async () => {
    const a = await wsSubscribe();
    await a.sub.unsubscribe();
    await a.sub.ended;
    a.client.dispose();
    const b = await wsSubscribe();
    b.client.dispose();
    await b.sub.ended;
  });

  it("WS: a drop with reconnect disabled ends active subscriptions (onError + ended)", async () => {
    const ref: WsRef = { sockets: [], autoOpen: true };
    const client = new SleipnirWebSocketClient("http://h", { WebSocketCtor: wsFactory(ref), reconnect: false });
    const errors: Error[] = [];
    const p = client.subscribe({ controller: "C", method: "E", id: "s" }, { onNext: () => {}, onError: (e) => errors.push(e) });
    await vi.waitFor(() => expect(ref.sockets[0]?.sent.length).toBe(1), { interval: 1, timeout: 1000 });
    ref.sockets[0].fireMessage({ code: 200, id: "s", data: { subscriptionId: "sid-x" } });
    const sub = await p;
    ref.sockets[0].fireClose();
    await sub.ended;
    expect(errors).toHaveLength(1);
  });

  it("SSE: aborting after the ack ends the subscription (no reconnect) and resolves ended", async () => {
    const sse = new FakeSse();
    const client = new SleipnirSseClient("https://h", { fetch: sse.fetch, reconnectDelays: [0] });
    const ctrl = new AbortController();
    const remove = vi.spyOn(ctrl.signal, "removeEventListener");
    const p = client.subscribe("C", "E", { onNext: () => {} }, undefined, { signal: ctrl.signal });
    await vi.waitFor(() => expect(sse.requests.length).toBe(1), { interval: 1, timeout: 1000 });
    sse.requests[0].push(ackBlock("s1"));
    const sub = await p;
    expect(client.state).toBe(SleipnirConnectionState.Connected);
    ctrl.abort();
    await sub.ended;
    await tick(10);
    expect(sse.requests.length).toBe(1);
    expect(client.state).toBe(SleipnirConnectionState.Disconnected);
    expect(remove).toHaveBeenCalled();
  });

  it("SSE: aborting before the ack rejects with CancelledError and does not retry", async () => {
    const sse = new FakeSse();
    sse.hang = true;
    const client = new SleipnirSseClient("https://h", { fetch: sse.fetch, reconnectDelays: [0] });
    const ctrl = new AbortController();
    const p = client.subscribe("C", "E", { onNext: () => {} }, undefined, { signal: ctrl.signal });
    await vi.waitFor(() => expect(sse.requests.length).toBe(1), { interval: 1, timeout: 1000 });
    ctrl.abort();
    await expect(p).rejects.toBeInstanceOf(CancelledError);
    await tick(10);
    expect(sse.requests.length).toBe(1);
  });

  it("SSE: a complete frame resolves ended and detaches the signal listener", async () => {
    const sse = new FakeSse();
    const client = new SleipnirSseClient("https://h", { fetch: sse.fetch });
    const ctrl = new AbortController();
    const remove = vi.spyOn(ctrl.signal, "removeEventListener");
    const seen: unknown[] = [];
    const p = client.subscribe("C", "E", { onNext: (v) => seen.push(v) }, undefined, { signal: ctrl.signal });
    await vi.waitFor(() => expect(sse.requests.length).toBe(1), { interval: 1, timeout: 1000 });
    sse.requests[0].push(ackBlock("s1") + sseEventBlock("s1", 1, "x") + sseCompleteBlock("s1"));
    const sub = await p;
    await sub.ended;
    expect(seen).toEqual(["x"]);
    expect(remove).toHaveBeenCalled();
  });

  it("router forwards timeout to SSE: no ack in time → CancelledError(timedOut)", async () => {
    const sse = new FakeSse();
    sse.hang = true;
    const router = new SleipnirTransportRouter({
      baseUrl: "https://h", capability: "rest", defaultTransport: "rest", rest: { fetch: vi.fn() }, sse: { fetch: sse.fetch },
    });
    const err = await router.subscribe({ controller: "C", method: "E" }, { onNext: () => {} }, { timeout: 20 }).catch((e) => e);
    expect(err).toBeInstanceOf(CancelledError);
    expect((err as CancelledError).timedOut).toBe(true);
  });

  async function signalrSubscribe(opts: { signal?: AbortSignal; timeout?: number } = {}) {
    const hub = new FakeHub();
    const client = new SleipnirSignalrClient("https://h", { hubFactory: () => hub, reconnect: false });
    const p = client.subscribe({ controller: "C", method: "E", id: "s" }, { onNext: () => {} }, opts);
    await tick();
    return { hub, client, p, stream: hub.streams[hub.streams.length - 1] };
  }

  it("SignalR: aborting after the ack disposes the stream and resolves ended", async () => {
    const ctrl = new AbortController();
    const remove = vi.spyOn(ctrl.signal, "removeEventListener");
    const { stream, p } = await signalrSubscribe({ signal: ctrl.signal });
    stream.push({ type: "ack", subscriptionId: "sr-1" });
    const sub = await p;
    ctrl.abort();
    await sub.ended;
    expect(stream.disposed).toBe(true);
    expect(remove).toHaveBeenCalled();
  });

  it("SignalR: aborting before the ack rejects with CancelledError", async () => {
    const ctrl = new AbortController();
    const { stream, p } = await signalrSubscribe({ signal: ctrl.signal });
    ctrl.abort();
    await expect(p).rejects.toBeInstanceOf(CancelledError);
    expect(stream.disposed).toBe(true);
  });

  it("SignalR: no ack within timeout → CancelledError(timedOut)", async () => {
    const { p } = await signalrSubscribe({ timeout: 15 });
    const err = await p.catch((e) => e);
    expect(err).toBeInstanceOf(CancelledError);
    expect((err as CancelledError).timedOut).toBe(true);
  });

  it("SignalR: a complete frame resolves ended", async () => {
    const { stream, p } = await signalrSubscribe();
    stream.push({ type: "ack", subscriptionId: "sr-2" });
    const sub = await p;
    stream.push({ type: "complete", subscriptionId: "sr-2" });
    await sub.ended;
  });
});
