import { describe, it, expect, vi, afterEach } from "vitest";
import {
  SleipnirTransportRouter,
  SleipnirTransportNotBundledError,
} from "../../src/transport-router.js";
import type { SleipnirRouterOptions } from "../../src/transport-router.js";
import type { IWebSocket, WsFactory } from "../../src/websocket.js";
import { ExecutionMode } from "../../src/types.js";

const OPEN = 1;

// --- Mock-WebSocket (spiegelt websocket.test.ts) ---

class MockWs implements IWebSocket {
  readyState = 0; // CONNECTING
  onopen: (() => void) | null = null;
  onmessage: ((ev: { data: string | ArrayBuffer }) => void) | null = null;
  onclose: ((ev: { code?: number; reason?: string }) => void) | null = null;
  onerror: ((ev: unknown) => void) | null = null;
  readonly sent: string[] = [];
  fireOpen(): void {
    this.readyState = OPEN;
    this.onopen?.();
  }
  fireError(): void {
    this.readyState = 3;
    this.onerror?.({});
  }
  fireMessage(data: string): void {
    this.onmessage?.({ data });
  }
  send(data: string): void {
    this.sent.push(data);
  }
  close(): void {
    this.readyState = 3;
  }
}

interface WsRef {
  ws: MockWs | undefined;
  created: number;
}

function wsFactory(ref: WsRef): WsFactory {
  return (_url, _opts) => {
    ref.created++;
    ref.ws = new MockWs();
    return ref.ws;
  };
}

// --- REST/SSE fake fetch ---

function restResponse(data: unknown) {
  const body = JSON.stringify({ code: 200, data, id: "r1" });
  return new Response(body, { status: 200, headers: { "content-type": "application/json" } });
}

function sseBody(blocks: string[]): ReadableStream<Uint8Array> {
  const enc = new TextEncoder();
  return new ReadableStream<Uint8Array>({
    start(controller) {
      for (const b of blocks) controller.enqueue(enc.encode(b));
      controller.close();
    },
  });
}
function ack(id: string): string {
  return `id: 0\nevent: ack\ndata: ${JSON.stringify({ subscriptionId: id })}\n\n`;
}
function sseEvent(id: string, eventId: number, data: unknown): string {
  return `id: ${eventId}\nevent: event\ndata: ${JSON.stringify({ type: "event", subscriptionId: id, eventId, data })}\n\n`;
}
function sseComplete(id: string): string {
  return `event: complete\ndata: ${JSON.stringify({ type: "complete", subscriptionId: id })}\n\n`;
}

afterEach(() => {
  vi.restoreAllMocks();
});

describe("SleipnirTransportRouter — bundling & escape hatches", () => {
  it("bundles REST+SSE for capability 'rest' (no WS)", () => {
    const r = new SleipnirTransportRouter({
      baseUrl: "https://host",
      capability: "rest",
      rest: { fetch: vi.fn() as any },
      sse: { fetch: vi.fn() as any },
    });
    expect(r.rest).toBeDefined();
    expect(r.sse).toBeDefined();
    expect(r.ws).toBeUndefined();
  });

  it("bundles WS only for capability 'ws' (no REST/SSE)", () => {
    const ref: WsRef = { ws: undefined, created: 0 };
    const r = new SleipnirTransportRouter({
      baseUrl: "https://host",
      capability: "ws",
      ws: { WebSocketCtor: wsFactory(ref) },
    });
    expect(r.ws).toBeDefined();
    expect(r.rest).toBeUndefined();
    expect(r.sse).toBeUndefined();
  });

  it("bundles REST+WS+SSE for capability 'all'", () => {
    const ref: WsRef = { ws: undefined, created: 0 };
    const r = new SleipnirTransportRouter({
      baseUrl: "https://host",
      capability: "all",
      defaultTransport: "rest",
      rest: { fetch: vi.fn() as any },
      ws: { WebSocketCtor: wsFactory(ref) },
      sse: { fetch: vi.fn() as any },
    });
    expect(r.rest).toBeDefined();
    expect(r.ws).toBeDefined();
    expect(r.sse).toBeDefined();
  });
});

describe("SleipnirTransportRouter — useTransport", () => {
  it("resolves a non-auto profile immediately and exposes activeTransport", () => {
    const r = new SleipnirTransportRouter({
      baseUrl: "https://host",
      capability: "all",
      defaultTransport: "rest",
      rest: { fetch: vi.fn() as any },
      sse: { fetch: vi.fn() as any },
    });
    expect(r.activeTransport).toBe("rest");
  });

  it("throws SleipnirTransportNotBundledError when the profile backend is missing", async () => {
    const ref: WsRef = { ws: undefined, created: 0 };
    const r = new SleipnirTransportRouter({
      baseUrl: "https://host",
      capability: "ws", // no rest/sse bundled
      ws: { WebSocketCtor: wsFactory(ref) },
    });
    await expect(r.useTransport("rest")).rejects.toThrow(SleipnirTransportNotBundledError);
  });

  it("selects the 'signalr' profile when the signalr backend is bundled (Phase 3)", async () => {
    const ref: WsRef = { ws: undefined, created: 0 };
    const r = new SleipnirTransportRouter({
      baseUrl: "https://host",
      capability: "signalr",
      defaultTransport: "rest",
      rest: { fetch: vi.fn() as any },
      ws: { WebSocketCtor: wsFactory(ref) },
      sse: { fetch: vi.fn() as any },
    });
    await r.useTransport("signalr");
    expect(r.activeTransport).toBe("signalr");
    expect(r.signalr).toBeDefined();
  });

  it("throws SleipnirTransportNotBundledError for 'signalr' when the signalr backend is not bundled", async () => {
    const ref: WsRef = { ws: undefined, created: 0 };
    const r = new SleipnirTransportRouter({
      baseUrl: "https://host",
      capability: "all", // rest+ws+sse, NO signalr
      defaultTransport: "rest",
      rest: { fetch: vi.fn() as any },
      ws: { WebSocketCtor: wsFactory(ref) },
      sse: { fetch: vi.fn() as any },
    });
    await expect(r.useTransport("signalr")).rejects.toThrow(SleipnirTransportNotBundledError);
  });
});

describe("SleipnirTransportRouter — auto negotiation", () => {
  it("probes WS and uses the 'ws' profile when the handshake succeeds", async () => {
    const ref: WsRef = { ws: undefined, created: 0 };
    const r = new SleipnirTransportRouter({
      baseUrl: "http://127.0.0.1:0",
      capability: "all",
      defaultTransport: "auto",
      probeTimeout: 2000,
      rest: { fetch: vi.fn() as any },
      ws: { WebSocketCtor: wsFactory(ref), connectTimeout: 5000 },
      sse: { fetch: vi.fn() as any },
    });
    const p = r.negotiate();
    // The factory ran synchronously inside connect(); fire the open event to settle the probe.
    await vi.waitFor(() => expect(ref.ws).toBeDefined());
    ref.ws!.fireOpen();
    await p;
    expect(r.activeTransport).toBe("ws");
    r.dispose();
  });

  it("falls back to the 'rest' profile when the WS handshake fails", async () => {
    const ref: WsRef = { ws: undefined, created: 0 };
    const r = new SleipnirTransportRouter({
      baseUrl: "http://127.0.0.1:0",
      capability: "all",
      defaultTransport: "auto",
      probeTimeout: 2000,
      rest: { fetch: vi.fn() as any },
      ws: { WebSocketCtor: wsFactory(ref), connectTimeout: 5000 },
      sse: { fetch: vi.fn() as any },
    });
    const p = r.negotiate();
    await vi.waitFor(() => expect(ref.ws).toBeDefined());
    ref.ws!.fireError(); // connect() rejects → probe fails → fallback
    await p;
    expect(r.activeTransport).toBe("rest");
    r.dispose();
  });

  it("resolves to 'rest' immediately when no WS is bundled (rest capability, auto)", async () => {
    const r = new SleipnirTransportRouter({
      baseUrl: "https://host",
      capability: "rest",
      defaultTransport: "auto",
      rest: { fetch: vi.fn() as any },
      sse: { fetch: vi.fn() as any },
    });
    await r.negotiate();
    expect(r.activeTransport).toBe("rest");
  });
});

describe("SleipnirTransportRouter — call routing", () => {
  it("routes a call to REST under the 'rest' profile", async () => {
    const fetchFn = vi.fn(async () => restResponse({ ok: true })) as any;
    const r = new SleipnirTransportRouter({
      baseUrl: "https://host",
      capability: "rest",
      defaultTransport: "rest",
      rest: { fetch: fetchFn },
      sse: { fetch: vi.fn() as any },
    });
    const res = await r.call({ controller: "C", method: "M", id: "r1" });
    expect(res.code).toBe(200);
    expect(fetchFn).toHaveBeenCalledTimes(1);
    expect(fetchFn.mock.calls[0][0]).toContain("/api/sleipnir/json");
  });

  it("routes a batch to REST under the 'rest' profile", async () => {
    const fetchFn = vi.fn(async () =>
      new Response(JSON.stringify([{ code: 200, data: 1, id: "a.b" }]), {
        status: 200,
        headers: { "content-type": "application/json" },
      }),
    ) as any;
    const r = new SleipnirTransportRouter({
      baseUrl: "https://host",
      capability: "rest",
      defaultTransport: "rest",
      rest: { fetch: fetchFn },
      sse: { fetch: vi.fn() as any },
    });
    const res = await r.callBatch(
      [{ controller: "A", method: "B", id: "a.b" }],
      ExecutionMode.Parallel,
    );
    expect(res).toHaveLength(1);
    expect(fetchFn).toHaveBeenCalledTimes(1);
    expect(fetchFn.mock.calls[0][0]).toContain("/api/sleipnir/json/multi");
  });
});

describe("SleipnirTransportRouter — subscribe routing", () => {
  it("routes a subscribe to SSE under the 'rest' profile (unpacks the request)", async () => {
    const fetchFn = vi.fn(async () => ({
      ok: true,
      status: 200,
      body: sseBody([ack("S1"), sseEvent("S1", 1, "hello"), sseComplete("S1")]),
    })) as any;
    const r = new SleipnirTransportRouter({
      baseUrl: "https://host",
      capability: "rest",
      defaultTransport: "rest",
      rest: { fetch: vi.fn() as any },
      sse: { fetch: fetchFn, apiPath: "api/sleipnir" },
    });
    const got: string[] = [];
    let done = false;
    const sub = await r.subscribe<string>(
      {
        controller: "Ticker",
        method: "Ticks",
        params: [{ parameterName: "speed", data: 5 }],
      },
      { onNext: (v) => got.push(v), onComplete: () => (done = true) },
    );
    expect(typeof sub.subscriptionId).toBe("string");
    await vi.waitFor(() => expect(done).toBe(true), { timeout: 1000 });
    expect(got).toEqual(["hello"]);
    // SSE URL carries the JSON-encoded query param (speed=5).
    expect(fetchFn.mock.calls[0][0]).toBe("https://host/api/sleipnir/events/Ticker/Ticks?speed=5");
  });

  it("routes a subscribe to WS under the 'ws' profile (passes the request through)", async () => {
    const ref: WsRef = { ws: undefined, created: 0 };
    const r = new SleipnirTransportRouter({
      baseUrl: "http://127.0.0.1:0",
      capability: "all",
      defaultTransport: "ws",
      probeTimeout: 2000,
      rest: { fetch: vi.fn() as any },
      ws: { WebSocketCtor: wsFactory(ref), connectTimeout: 5000 },
      sse: { fetch: vi.fn() as any },
    });
    // The 'ws' profile is set immediately (no auto probe), but subscribe() triggers connect().
    const subP = r.subscribe<number>(
      { controller: "Ticker", method: "Ticks", id: "Ticker.Ticks" },
      { onNext: () => {} },
    );
    await vi.waitFor(() => expect(ref.ws).toBeDefined());
    ref.ws!.fireOpen();
    // Wait for the subscribe frame to be sent (pending request is registered alongside it)
    // before delivering the server response — otherwise the response arrives too early.
    await vi.waitFor(() => expect(ref.ws!.sent.length).toBeGreaterThan(0));
    // Server sends the subscribe response with the subscriptionId.
    ref.ws!.fireMessage(JSON.stringify({ code: 200, data: { subscriptionId: "WS1" }, id: "Ticker.Ticks" }));
    const sub = await subP;
    expect(sub.subscriptionId).toBe("WS1");
    // The WS subscribe frame carries kind:"subscribe" + the request fields.
    const sent = ref.ws!.sent.map((s) => JSON.parse(s));
    expect(sent.some((f) => f.kind === "subscribe" && f.controller === "Ticker")).toBe(true);
    r.dispose();
  });
});

describe("SleipnirTransportRouter — bearer fan-out & dispose", () => {
  it("fans a bearer swap out to all bundled backends without throwing", () => {
    const ref: WsRef = { ws: undefined, created: 0 };
    const r = new SleipnirTransportRouter({
      baseUrl: "https://host",
      capability: "all",
      defaultTransport: "rest",
      rest: { fetch: vi.fn() as any },
      ws: { WebSocketCtor: wsFactory(ref) },
      sse: { fetch: vi.fn() as any },
    });
    expect(() => r.setBearer("new-token")).not.toThrow();
  });

  it("dispose is idempotent and marks the router disposed", async () => {
    const ref: WsRef = { ws: undefined, created: 0 };
    const r = new SleipnirTransportRouter({
      baseUrl: "https://host",
      capability: "all",
      defaultTransport: "rest",
      rest: { fetch: vi.fn() as any },
      ws: { WebSocketCtor: wsFactory(ref) },
      sse: { fetch: vi.fn() as any },
    });
    r.dispose();
    await expect(r.negotiate()).rejects.toThrow(/disposed/);
    r.dispose(); // second call must not throw
  });
});

describe("SleipnirTransportRouter — cross-transport resume (WS → SSE)", () => {
  it("resumes a WS-created subscription over SSE after useTransport('rest')", async () => {
    const ref: WsRef = { ws: undefined, created: 0 };
    const sseFetch = vi.fn(async () => ({
      ok: true,
      status: 200,
      body: sseBody([
        `id: 0\nevent: ack\ndata: ${JSON.stringify({ subscriptionId: "WS1", replayedFrom: 1 })}\n\n`,
        sseEvent("WS1", 2, "live"),
        sseComplete("WS1"),
      ]),
    })) as any;
    const r = new SleipnirTransportRouter({
      baseUrl: "https://host",
      capability: "all",
      defaultTransport: "ws",
      probeTimeout: 2000,
      rest: { fetch: vi.fn() as any },
      ws: { WebSocketCtor: wsFactory(ref), connectTimeout: 5000 },
      sse: { fetch: sseFetch, apiPath: "api/sleipnir" },
    });

    // 1. Subscribe over WebSocket.
    const subP = r.subscribe<string>(
      { controller: "Ticker", method: "Ticks", id: "Ticker.Ticks" },
      { onNext: () => {} },
    );
    await vi.waitFor(() => expect(ref.ws).toBeDefined());
    ref.ws!.fireOpen();
    await vi.waitFor(() => expect(ref.ws!.sent.length).toBeGreaterThan(0));
    ref.ws!.fireMessage(JSON.stringify({ code: 200, data: { subscriptionId: "WS1" }, id: "Ticker.Ticks" }));
    const sub = await subP;
    expect(sub.subscriptionId).toBe("WS1");
    expect(sub.lastEventId).toBe(0);

    // 2. Deliver one WS event → the live cursor on the handle advances to 1.
    ref.ws!.fireMessage(JSON.stringify({ type: "event", subscriptionId: "WS1", eventId: 1, data: "x" }));
    await vi.waitFor(() => expect(sub.lastEventId).toBe(1));

    // 3. Simulate the auto-fallback: switch to the rest profile (SSE events) and resume the
    //    SAME subscription over SSE, replaying from the cursor the WS handle reached.
    await r.useTransport("rest");
    const resumed: string[] = [];
    let resumedDone = false;
    const resumedSub = await r.resume<string>(sub.subscriptionId, sub.lastEventId, {
      onNext: (v) => resumed.push(v),
      onComplete: () => { resumedDone = true; },
    });
    await vi.waitFor(() => expect(resumedDone).toBe(true), { timeout: 1000 });

    expect(resumed).toEqual(["live"]);
    expect(resumedSub.subscriptionId).toBe("WS1");
    expect(resumedSub.lastEventId).toBe(2);
    // The SSE resume URL is self-contained — subscriptionId + lastEventId from the WS handle.
    expect(sseFetch.mock.calls[0][0]).toBe("https://host/api/sleipnir/events/WS1?lastEventId=1");
    expect((sseFetch.mock.calls[0][1].headers as Record<string, string>)["Last-Event-Id"]).toBe("1");
    r.dispose();
  });

  it("throws a clear error when resume targets the WS backend (no cross-transport-into-WS yet)", async () => {
    const ref: WsRef = { ws: undefined, created: 0 };
    const r = new SleipnirTransportRouter({
      baseUrl: "https://host",
      capability: "all",
      defaultTransport: "ws",
      rest: { fetch: vi.fn() as any },
      ws: { WebSocketCtor: wsFactory(ref), connectTimeout: 5000 },
      sse: { fetch: vi.fn() as any },
    });
    await expect(r.resume("S1", 1, { onNext: () => {} })).rejects.toThrow(
      /cross-transport resume into WebSocket is not supported/i,
    );
    r.dispose();
  });
});