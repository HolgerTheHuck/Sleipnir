import { describe, it, expect } from "vitest";
import { readFileSync, readdirSync, statSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { buildEmitterInput } from "../../src/core/model.js";
import { NamingResolver } from "../../src/core/naming.js";
import { emitTsClient } from "../../src/emitters/ts.js";
import { readFixture } from "./fixture.js";

const here = dirname(fileURLToPath(import.meta.url));
const snapshotDir = join(here, "..", "snapshots", "story01.ts");

/** Recursively read all files under `dir` as a path→content map. */
function readTree(dir: string, base = dir): Record<string, string> {
  const out: Record<string, string> = {};
  for (const entry of readdirSync(dir)) {
    const abs = join(dir, entry);
    if (statSync(abs).isDirectory()) {
      Object.assign(out, readTree(abs, base));
    } else {
      const rel = abs.slice(base.length + 1).replace(/\\/g, "/");
      out[rel] = readFileSync(abs, "utf8");
    }
  }
  return out;
}

describe("emitTsClient (golden against story01 snapshot)", () => {
  const input = buildEmitterInput(readFixture(), new NamingResolver());
  const tree = emitTsClient(input);

  it("emits the expected file set", () => {
    expect(Object.keys(tree).sort()).toEqual(
      ["api/client.ts", "api/controllers.ts", "api/index.ts", "api/typed-call.ts", "api/types.ts"],
    );
  });

  it("matches the committed snapshot byte-for-byte", () => {
    const snapshot = readTree(snapshotDir);
    for (const [path, content] of Object.entries(tree)) {
      expect(snapshot[path], `snapshot missing for ${path}`).toBeDefined();
      expect(content).toBe(snapshot[path]);
    }
    // And no extra snapshot files linger.
    expect(Object.keys(snapshot).sort()).toEqual(Object.keys(tree).sort());
  });

  it("camelCases properties (wire fix) and method names", () => {
    expect(tree["api/types.ts"]).toContain("id?: number;");
    expect(tree["api/types.ts"]).toContain("customerId?: number;");
    expect(tree["api/types.ts"]).toContain("shippingAddressId?: number;");
    expect(tree["api/controllers.ts"]).toContain("getById(id: number): TypedCall<Order, OrderPaths>");
    expect(tree["api/controllers.ts"]).not.toContain("GetById(id"); // no PascalCase method name
  });

  it("carries the path-record type per method (typed-batch design)", () => {
    expect(tree["api/controllers.ts"]).toContain("getByOrder(orderId: number): TypedCall<OrderLine[], OrderLineArrayPaths>");
    expect(tree["api/controllers.ts"]).toContain("getByArticles(articleIds: number[]): TypedCall<StockInfo[], StockInfoArrayPaths>");
  });
});

describe("emitTsClient --transport ws (golden against story01.ws snapshot)", () => {
  const input = buildEmitterInput(readFixture(), new NamingResolver());
  const tree = emitTsClient(input, { transport: "ws" });

  it("emits the same file set as rest (only client.ts differs)", () => {
    expect(Object.keys(tree).sort()).toEqual(
      ["api/client.ts", "api/controllers.ts", "api/index.ts", "api/typed-call.ts", "api/types.ts"],
    );
  });

  it("matches the committed ws snapshot byte-for-byte", () => {
    const snapshot = readTree(join(here, "..", "snapshots", "story01.ws.ts"));
    for (const [path, content] of Object.entries(tree)) {
      expect(snapshot[path], `snapshot missing for ${path}`).toBeDefined();
      expect(content).toBe(snapshot[path]);
    }
    expect(Object.keys(snapshot).sort()).toEqual(Object.keys(tree).sort());
  });

  it("wires the WebSocket runtime client", () => {
    expect(tree["api/client.ts"]).toContain("SleipnirWebSocketClient");
    expect(tree["api/client.ts"]).toContain("SleipnirWebSocketClientOptions");
    expect(tree["api/client.ts"]).toContain("this._ws");
    expect(tree["api/client.ts"]).toContain("get ws(): SleipnirWebSocketClient");
    // REST surface must be absent in ws-only mode.
    expect(tree["api/client.ts"]).not.toContain("SleipnirRestClient");
    expect(tree["api/client.ts"]).not.toContain("callWs");
  });
});

describe("emitTsClient --transport both (golden against story01.both snapshot)", () => {
  const input = buildEmitterInput(readFixture(), new NamingResolver());
  const tree = emitTsClient(input, { transport: "both" });

  it("emits the same file set as rest (only client.ts differs)", () => {
    expect(Object.keys(tree).sort()).toEqual(
      ["api/client.ts", "api/controllers.ts", "api/index.ts", "api/typed-call.ts", "api/types.ts"],
    );
  });

  it("matches the committed both snapshot byte-for-byte", () => {
    const snapshot = readTree(join(here, "..", "snapshots", "story01.both.ts"));
    for (const [path, content] of Object.entries(tree)) {
      expect(snapshot[path], `snapshot missing for ${path}`).toBeDefined();
      expect(content).toBe(snapshot[path]);
    }
    expect(Object.keys(snapshot).sort()).toEqual(Object.keys(tree).sort());
  });

  it("wires both runtime clients and exposes the Ws variants", () => {
    const c = tree["api/client.ts"];
    expect(c).toContain("SleipnirRestClient");
    expect(c).toContain("SleipnirWebSocketClient");
    expect(c).toContain("SleipnirClientOptions");
    expect(c).toContain("this._rest");
    expect(c).toContain("this._ws");
    expect(c).toContain("async callWs");
    expect(c).toContain("async batchWs");
    expect(c).toContain("get rest(): SleipnirRestClient");
    expect(c).toContain("get ws(): SleipnirWebSocketClient");
  });
});