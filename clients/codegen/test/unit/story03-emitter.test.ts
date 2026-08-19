// Story-03: server-push events ([SleipnirEvent] / IObservable<T>). Verifies the
// generated TS + JS subscribe surface (golden + structural), and that the
// REST-only C# + Python emitters emit a clear NotImplemented marker instead of a
// false REST call (events are WS-exclusive in v1).
import { describe, it, expect } from "vitest";
import { readFileSync, readdirSync, statSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { buildEmitterInput } from "../../src/core/model.js";
import { NamingResolver } from "../../src/core/naming.js";
import { emitTsClient } from "../../src/emitters/ts.js";
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

// --- TS: golden + typed subscribe surface ---

describe("emitTsClient story03 (events, golden)", () => {
  for (const t of ["rest", "sse", "ws", "both"] as const) {
    const suffix = t === "rest" ? "" : `.${t}`;
    // eslint-disable-next-line @typescript-eslint/no-loop-func
    it(`--transport ${t} matches the committed story03${suffix}.ts snapshot byte-for-byte`, () => {
      const tree = emitTsClient(buildEmitterInput(readFixture("story03"), new NamingResolver()), { transport: t });
      const snapshot = readTree(join(snapshotsDir, `story03${suffix}.ts`));
      for (const [path, content] of Object.entries(tree)) {
        expect(snapshot[path], `snapshot missing for ${path}`).toBeDefined();
        expect(content).toBe(snapshot[path]);
      }
      expect(Object.keys(snapshot).sort()).toEqual(Object.keys(tree).sort());
    });
  }

  it("emits a typed subscribe for event methods (object payload) and keeps calls as TypedCall", () => {
    const tree = emitTsClient(buildEmitterInput(readFixture("story03"), new NamingResolver()), { transport: "ws" });
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

  it("ws client delegates _subscribe to the WebSocket runtime; rest client throws a clear WS-only error", () => {
    const wsTree = emitTsClient(buildEmitterInput(readFixture("story03"), new NamingResolver()), { transport: "ws" });
    const ws = wsTree["api/client.ts"];
    expect(ws).toContain("this._ws.subscribe<T>(req, handlers)");
    // Event controllers constructed with (build, this._subscribe); call controllers with (build).
    expect(ws).toContain("this.chat = new ChatClient(build, this._subscribe);");
    expect(ws).toContain("this.user = new UserClient(build);");

    const restTree = emitTsClient(buildEmitterInput(readFixture("story03"), new NamingResolver()), { transport: "rest" });
    const rest = restTree["api/client.ts"];
    expect(rest).toContain("Sleipnir events require WebSocket transport. Regenerate with --transport ws|both to subscribe.");
    // Rest client still constructs event controllers (with the throwing _subscribe).
    expect(rest).toContain("this.chat = new ChatClient(build, this._subscribe);");
  });

  it("sse client delegates _subscribe to the SSE runtime (REST calls + SSE events)", () => {
    const tree = emitTsClient(buildEmitterInput(readFixture("story03"), new NamingResolver()), { transport: "sse" });
    const sse = tree["api/client.ts"];
    // Wires REST (calls) + SSE (events) — not WebSocket.
    expect(sse).toContain("import { SleipnirCall, SleipnirRestClient, SleipnirSseClient } from \"sleipnir-client\";");
    expect(sse).toContain("private readonly _sse: SleipnirSseClient;");
    // The adapter destructures the SleipnirRequest into (controller, method, params).
    expect(sse).toContain("return this._sse.subscribe<T>(req.controller, req.method, handlers, params);");
    // Calls go over REST; SSE is the event escape hatch.
    expect(sse).toContain("get rest(): SleipnirRestClient");
    expect(sse).toContain("get sse(): SleipnirSseClient");
    expect(sse).not.toContain("SleipnirWebSocketClient");
    // Event controllers constructed with the SSE-backed _subscribe.
    expect(sse).toContain("this.chat = new ChatClient(build, this._subscribe);");
  });
});

// --- JS: golden + subscribe surface ---

describe("emitJsClient story03 (events, golden)", () => {
  for (const t of ["rest", "sse", "ws", "both"] as const) {
    const suffix = t === "rest" ? "" : `.${t}`;
    // eslint-disable-next-line @typescript-eslint/no-loop-func
    it(`--transport ${t} matches the committed story03${suffix}.js snapshot byte-for-byte`, () => {
      const tree = emitJsClient(buildEmitterInput(readFixture("story03"), new NamingResolver()), { transport: t });
      const snapshot = readTree(join(snapshotsDir, `story03${suffix}.js`));
      for (const [path, content] of Object.entries(tree)) {
        expect(snapshot[path], `snapshot missing for ${path}`).toBeDefined();
        expect(content).toBe(snapshot[path]);
      }
      expect(Object.keys(snapshot).sort()).toEqual(Object.keys(tree).sort());
    });
  }

  it("emits an async subscribe method with JSDoc handler typing", () => {
    const tree = emitJsClient(buildEmitterInput(readFixture("story03"), new NamingResolver()), { transport: "ws" });
    const ctrl = tree["api/controllers.js"];
    expect(ctrl).toContain("@param {SubscribeHandlers<Message>} handlers");
    expect(ctrl).toContain("@returns {Promise<SleipnirSubscription>}");
    expect(ctrl).toContain("async messageReceived(chatId, handlers)");
    expect(ctrl).toContain('this._subscribe(this._build("Chat", "MessageReceived").with({ chatId: chatId }).toRequest(), handlers)');
  });
});

// --- C# (REST-only): event methods emit a NotImplemented marker, not a false call ---

describe("emitCsClient story03 (events → WS-only marker)", () => {
  const cs = emitCsClient(buildEmitterInput(readFixture("story03"), new NamingResolver()))["SleipnirGenerated.cs"];

  it("event methods throw NotImplementedException with the WS subscribe hint", () => {
    expect(cs).toContain("public Call MessageReceived(Arg<int> chatId) => throw new System.NotImplementedException(");
    expect(cs).toContain("SleipnirWebSocketClient.SubscribeAsync<Message>(\\\"Chat\\\", \\\"MessageReceived\\\", args)");
    // Scalar-payload event names its payload type too.
    expect(cs).toContain("IObservable<int>");
  });

  it("call methods on the same controller stay regular Call builders", () => {
    expect(cs).toContain('public Call GetHistory(Arg<int> chatId) => new Call(SleipnirCall.Init("Chat", "GetHistory").Param("chatId", chatId.ToWireValue()));');
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