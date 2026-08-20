// Story-03: server-push events ([SleipnirEvent] / IObservable<T>). Verifies the
// generated TS + JS subscribe surface (golden + structural) across all capabilities,
// and that the REST-only C# + Python emitters emit a clear NotImplemented marker
// instead of a false REST call (events are WS/SSE-exclusive; CS events land in Phase 4).
import { describe, it, expect } from "vitest";
import { readFileSync, readdirSync, statSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { buildEmitterInput } from "../../src/core/model.js";
import { NamingResolver } from "../../src/core/naming.js";
import { emitTsClient, type SleipnirBundleCapability } from "../../src/emitters/ts.js";
import { emitJsClient } from "../../src/emitters/js.js";
import { emitCsClient } from "../../src/emitters/cs.js";
import { emitPyClient } from "../../src/emitters/py.js";
import { readFixture } from "./fixture.js";

const here = dirname(fileURLToPath(import.meta.url));
const snapshotsDir = join(here, "..", "snapshots");

function readTree(dir: string, base = dir): Record<string, string> {
  const out: Record<string, string> = {};
  for (const entry of readdirSync(dir)) {
    const abs = join(dir, entry);
    if (statSync(abs).isDirectory()) Object.assign(out, readTree(abs, base));
    else out[abs.slice(base.length + 1).replace(/\\/g, "/")] = readFileSync(abs, "utf8");
  }
  return out;
}

function suffixFor(cap: SleipnirBundleCapability): string {
  return cap === "all" ? "" : `.${cap}`;
}

const CAPABILITIES: SleipnirBundleCapability[] = ["rest", "ws", "all", "signalr"];

// --- TS: golden + typed subscribe surface ---

describe("emitTsClient story03 (events, golden)", () => {
  for (const cap of CAPABILITIES) {
    // eslint-disable-next-line @typescript-eslint/no-loop-func
    it(`--transport ${cap} matches the committed story03${suffixFor(cap)}.ts snapshot byte-for-byte`, () => {
      const tree = emitTsClient(buildEmitterInput(readFixture("story03"), new NamingResolver()), { capability: cap });
      const snapshot = readTree(join(snapshotsDir, `story03${suffixFor(cap)}.ts`));
      for (const [path, content] of Object.entries(tree)) {
        expect(snapshot[path], `snapshot missing for ${path}`).toBeDefined();
        expect(content).toBe(snapshot[path]);
      }
      expect(Object.keys(snapshot).sort()).toEqual(Object.keys(tree).sort());
    });
  }

  it("emits a typed subscribe for event methods (object payload) and keeps calls as TypedCall", () => {
    const tree = emitTsClient(buildEmitterInput(readFixture("story03"), new NamingResolver()));
    const ctrl = tree["api/controllers.ts"];
    // Event method → typed subscribe returning Promise<SleipnirSubscription>.
    expect(ctrl).toContain(
      "messageReceived(chatId: number, handlers: SubscribeHandlers<Message>): Promise<SleipnirSubscription>",
    );
    expect(ctrl).toContain('this._subscribe<Message>(this._build("Chat", "MessageReceived").with({ chatId: chatId }).toRequest(), handlers)');
    // A sibling call method on the same (mixed) controller stays a TypedCall.
    expect(ctrl).toContain("getHistory(chatId: number): TypedCall<Message[], MessageArrayPaths>");
    // Scalar-payload event → SubscribeHandlers<number>.
    expect(ctrl).toContain("ticks(handlers: SubscribeHandlers<number>): Promise<SleipnirSubscription>");
    // Pure-call controller keeps the 1-arg ctor (no _subscribe field).
    expect(ctrl).toContain("export class UserClient {\n  /** @internal */ _build");
    expect(ctrl).not.toMatch(/UserClient[\s\S]*_subscribe/);
  });

  it("client.ts delegates _subscribe to the transport router (the router bridges WS/SSE)", () => {
    const tree = emitTsClient(buildEmitterInput(readFixture("story03"), new NamingResolver()));
    const client = tree["api/client.ts"];
    // The _subscribe adapter delegates to the router (which routes to WS or SSE per profile).
    expect(client).toContain("this._router.subscribe<T>(req, handlers)");
    // Event controllers constructed with (build, this._subscribe); call controllers with (build).
    expect(client).toContain("this.chat = new ChatClient(build, this._subscribe);");
    expect(client).toContain("this.user = new UserClient(build);");
    // No transport-specific subscribe adapter inline in the generated client anymore.
    expect(client).not.toContain("this._ws.subscribe<T>(req, handlers)");
    expect(client).not.toContain("this._sse.subscribe<T>");
    // The old "events require WebSocket" throw is gone — `rest` capability routes events to SSE.
    expect(client).not.toContain("Sleipnir events require WebSocket transport");
  });

  it("the event surface is identical across all capabilities (only the capability literal differs)", () => {
    const surfaces = CAPABILITIES.map((cap) => {
      const c = emitTsClient(buildEmitterInput(readFixture("story03"), new NamingResolver()), { capability: cap })["api/client.ts"];
      return c
        .replace(/^\/\/.*$/gm, "")
        .replace(/capability: "(rest|ws|all|signalr)"/, 'capability: "<cap>"');
    });
    const first = surfaces[0];
    for (let i = 1; i < surfaces.length; i++) {
      expect(surfaces[i], `story03 surface differs between ${CAPABILITIES[0]} and ${CAPABILITIES[i]}`).toBe(first);
    }
  });
});

// --- JS: golden + subscribe surface ---

describe("emitJsClient story03 (events, golden)", () => {
  for (const cap of CAPABILITIES) {
    // eslint-disable-next-line @typescript-eslint/no-loop-func
    it(`--transport ${cap} matches the committed story03${suffixFor(cap)}.js snapshot byte-for-byte`, () => {
      const tree = emitJsClient(buildEmitterInput(readFixture("story03"), new NamingResolver()), { capability: cap });
      const snapshot = readTree(join(snapshotsDir, `story03${suffixFor(cap)}.js`));
      for (const [path, content] of Object.entries(tree)) {
        expect(snapshot[path], `snapshot missing for ${path}`).toBeDefined();
        expect(content).toBe(snapshot[path]);
      }
      expect(Object.keys(snapshot).sort()).toEqual(Object.keys(tree).sort());
    });
  }

  it("emits an async subscribe method with JSDoc handler typing", () => {
    const tree = emitJsClient(buildEmitterInput(readFixture("story03"), new NamingResolver()));
    const ctrl = tree["api/controllers.js"];
    expect(ctrl).toContain("@param {SubscribeHandlers<Message>} handlers");
    expect(ctrl).toContain("@returns {Promise<SleipnirSubscription>}");
    expect(ctrl).toContain("async messageReceived(chatId, handlers)");
    expect(ctrl).toContain('this._subscribe(this._build("Chat", "MessageReceived").with({ chatId: chatId }).toRequest(), handlers)');
  });

  it("client.js delegates _subscribe to the transport router", () => {
    const client = emitJsClient(buildEmitterInput(readFixture("story03"), new NamingResolver()))["api/client.js"];
    expect(client).toContain("this._subscribe = (req, handlers) => this._router.subscribe(req, handlers)");
    expect(client).not.toContain("this._ws.subscribe(req, handlers)");
    expect(client).not.toContain("this._sse.subscribe(");
  });
});

// --- C# (Phase 4 unified transport): event methods build a regular Call; the
// execution entry point (Subscribe<T> on the router-backed root client) is what
// routes to the active event backend. No more NotImplemented throw. ---

describe("emitCsClient story03 (events → Call builder, subscribed via router)", () => {
  const cs = emitCsClient(buildEmitterInput(readFixture("story03"), new NamingResolver()))["SleipnirGenerated.cs"];

  it("event methods build a regular Call (identical to a call method — no throw)", () => {
    expect(cs).toContain('public Call MessageReceived(Arg<int> chatId) => new Call(SleipnirCall.Init("Chat", "MessageReceived").Param("chatId", chatId.ToWireValue()));');
    // The doc remarks name the payload type so the consumer knows what to subscribe to.
    expect(cs).toContain("IObservable&lt;Message&gt;");
    // Scalar-payload event names its payload type too.
    expect(cs).toContain("IObservable&lt;int&gt;");
    // The NotImplemented throw + WS-only hint are gone (events now route via the router).
    expect(cs).not.toContain("NotImplementedException");
    expect(cs).not.toContain("SleipnirWebSocketClient.SubscribeAsync");
  });

  it("call methods on the same controller stay regular Call builders", () => {
    expect(cs).toContain('public Call GetHistory(Arg<int> chatId) => new Call(SleipnirCall.Init("Chat", "GetHistory").Param("chatId", chatId.ToWireValue()));');
  });

  it("the root client wraps SleipnirTransportRouter and exposes Subscribe<T>", () => {
    expect(cs).toContain("new SleipnirTransportRouter(");
    expect(cs).toContain("SleipnirRouterOptions { BaseUrl = baseUrl, Capability = SleipnirBundleCapability.All }");
    expect(cs).toContain("public Task<SleipnirSubscription<T>> Subscribe<T>(Call call, ResumePolicy? resumePolicy = null, CancellationToken ct = default)");
    expect(cs).toContain("_client.SubscribeAsync<T>(call.ToRequest(), resumePolicy, ct)");
  });
});

// --- Python (REST-only): event methods raise NotImplementedError ---

describe("emitPyClient story03 (events → WS-only marker)", () => {
  const py = emitPyClient(buildEmitterInput(readFixture("story03"), new NamingResolver()))["client.py"];

  it("event methods raise NotImplementedError with the WS-only hint", () => {
    // Method name is snake_cased; params keep their camelCase wire names.
    expect(py).toContain("def message_received(self, chatId: int):");
    expect(py).toContain("raise NotImplementedError(");
    expect(py).toContain("server-push event");
    expect(py).toContain("IObservable[Message]");
  });

  it("call methods on the same controller stay regular SleipnirCall builders", () => {
    expect(py).toContain('return SleipnirCall("Chat", "GetHistory", {"chatId": chatId})');
  });
});