// Contract identity on the wire: the router sends the generated client's contract hash as header
// (REST, SSE) or query parameter (WebSocket, SignalR — browsers cannot set upgrade headers).
import { describe, it, expect, vi } from "vitest";
import { SleipnirTransportRouter } from "../../src/transport-router.js";
import { CONTRACT_HEADER, CONTRACT_QUERY, withContractHeader, withContractQuery } from "../../src/contract.js";
import type { IWebSocket, WsFactory } from "../../src/websocket.js";
import type { IHubConnection, SignalrHubFactory } from "../../src/signalr.js";

const HASH = "sha256:" + "ab".repeat(32);

describe("contract helpers", () => {
  it("appends the query parameter with ? or &", () => {
    expect(withContractQuery("wss://h/sleipnirws", HASH)).toBe(`wss://h/sleipnirws?${CONTRACT_QUERY}=${encodeURIComponent(HASH)}`);
    expect(withContractQuery("wss://h/sleipnirws?access_token=t", HASH)).toBe(`wss://h/sleipnirws?access_token=t&${CONTRACT_QUERY}=${encodeURIComponent(HASH)}`);
    expect(withContractQuery("wss://h/x", undefined)).toBe("wss://h/x");
  });

  it("puts the header under the caller's own headers", () => {
    expect(withContractHeader({ "X-A": "1" }, HASH)).toEqual({ [CONTRACT_HEADER]: HASH, "X-A": "1" });
    expect(withContractHeader({ [CONTRACT_HEADER]: "mine" }, HASH)).toEqual({ [CONTRACT_HEADER]: "mine" });
    expect(withContractHeader(undefined, undefined)).toBeUndefined();
  });
});

describe("router sends the contract hash", () => {
  it("REST: Sleipnir-Contract header on every call", async () => {
    const fetch = vi.fn(async () => new Response(JSON.stringify({ code: 200, data: 1, id: "C.M" }), { status: 200 }));
    const r = new SleipnirTransportRouter({ baseUrl: "https://host", capability: "rest", contractHash: HASH, rest: { fetch: fetch as any } });

    await r.call({ controller: "C", method: "M", id: "C.M" });

    const init = (fetch.mock.calls[0] as unknown[])[1] as { headers: Record<string, string> };
    expect(init.headers[CONTRACT_HEADER]).toBe(HASH);
  });

  it("REST: no header without a hash (hand-written client)", async () => {
    const fetch = vi.fn(async () => new Response(JSON.stringify({ code: 200, data: 1, id: "C.M" }), { status: 200 }));
    const r = new SleipnirTransportRouter({ baseUrl: "https://host", capability: "rest", rest: { fetch: fetch as any } });

    await r.call({ controller: "C", method: "M", id: "C.M" });

    const init = (fetch.mock.calls[0] as unknown[])[1] as { headers: Record<string, string> };
    expect(init.headers[CONTRACT_HEADER]).toBeUndefined();
  });

  it("WebSocket: sleipnir-contract query on connect", () => {
    const urls: string[] = [];
    const factory: WsFactory = (url) => {
      urls.push(url);
      return { readyState: 0, onopen: null, onmessage: null, onclose: null, onerror: null, send() {}, close() {} } as IWebSocket;
    };
    const r = new SleipnirTransportRouter({ baseUrl: "https://host", capability: "ws", contractHash: HASH, ws: { WebSocketCtor: factory } });

    void r.ws!.connect().catch(() => undefined);

    expect(urls[0]).toBe(`wss://host/sleipnirws?${CONTRACT_QUERY}=${encodeURIComponent(HASH)}`);
  });

  it("SignalR: sleipnir-contract query on the hub URL", async () => {
    const urls: string[] = [];
    const hub = { start: async () => {}, stop: async () => {}, invoke: async () => null, stream: () => ({ subscribe: () => ({ dispose() {} }) }), onreconnecting() {}, onreconnected() {}, onclose() {} } as unknown as IHubConnection;
    const factory: SignalrHubFactory = (url) => {
      urls.push(url);
      return hub;
    };
    const r = new SleipnirTransportRouter({ baseUrl: "https://host", capability: "signalr", contractHash: HASH, signalr: { hubFactory: factory, reconnect: false } });

    await r.signalr!.connect();

    expect(urls[0]).toBe(`https://host/sleipnirhub?${CONTRACT_QUERY}=${encodeURIComponent(HASH)}`);
  });
});
