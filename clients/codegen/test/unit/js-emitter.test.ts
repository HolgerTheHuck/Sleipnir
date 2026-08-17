import { describe, it, expect } from "vitest";
import { readFileSync, readdirSync, statSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { buildEmitterInput } from "../../src/core/model.js";
import { NamingResolver } from "../../src/core/naming.js";
import { emitJsClient } from "../../src/emitters/js.js";
import { readFixture } from "./fixture.js";

const here = dirname(fileURLToPath(import.meta.url));
const snapshotDir = join(here, "..", "snapshots", "story01.js");

function readTree(dir: string, base = dir): Record<string, string> {
  const out: Record<string, string> = {};
  for (const entry of readdirSync(dir)) {
    const abs = join(dir, entry);
    if (statSync(abs).isDirectory()) Object.assign(out, readTree(abs, base));
    else out[abs.slice(base.length + 1).replace(/\\/g, "/")] = readFileSync(abs, "utf8");
  }
  return out;
}

describe("emitJsClient (golden against story01 snapshot)", () => {
  const input = buildEmitterInput(readFixture(), new NamingResolver());
  const tree = emitJsClient(input);

  it("emits the expected file set", () => {
    expect(Object.keys(tree).sort()).toEqual(
      ["api/client.js", "api/controllers.js", "api/index.js", "api/types.js"],
    );
  });

  it("matches the committed snapshot byte-for-byte", () => {
    const snapshot = readTree(snapshotDir);
    for (const [path, content] of Object.entries(tree)) {
      expect(snapshot[path], `snapshot missing for ${path}`).toBeDefined();
      expect(content).toBe(snapshot[path]);
    }
    expect(Object.keys(snapshot).sort()).toEqual(Object.keys(tree).sort());
  });

  it("emits JSDoc @typedef blocks with camelCase properties", () => {
    expect(tree["api/types.js"]).toContain("@typedef {Object} Order");
    expect(tree["api/types.js"]).toContain("@property {number} customerId");
  });

  it("wires the REST runtime client and resolves the batch bugfix (requests+mode)", () => {
    const c = tree["api/client.js"];
    expect(c).toContain("SleipnirRestClient");
    expect(c).toContain("this._rest");
    // callBatch must be called with (requests, mode), not the multi object.
    expect(c).toContain("this._rest.callBatch(m.requests, m.mode)");
    expect(c).not.toContain("callBatch(b.toMulti())");
  });
});

describe("emitJsClient --transport ws (golden against story01.ws.js snapshot)", () => {
  const input = buildEmitterInput(readFixture(), new NamingResolver());
  const tree = emitJsClient(input, { transport: "ws" });

  it("emits the same file set as rest (only client.js differs)", () => {
    expect(Object.keys(tree).sort()).toEqual(
      ["api/client.js", "api/controllers.js", "api/index.js", "api/types.js"],
    );
  });

  it("matches the committed ws snapshot byte-for-byte", () => {
    const snapshot = readTree(join(here, "..", "snapshots", "story01.ws.js"));
    for (const [path, content] of Object.entries(tree)) {
      expect(snapshot[path], `snapshot missing for ${path}`).toBeDefined();
      expect(content).toBe(snapshot[path]);
    }
    expect(Object.keys(snapshot).sort()).toEqual(Object.keys(tree).sort());
  });

  it("wires the WebSocket runtime client", () => {
    const c = tree["api/client.js"];
    expect(c).toContain("SleipnirWebSocketClient");
    expect(c).toContain("this._ws");
    expect(c).toContain("this._ws.callBatch(m.requests, m.mode)");
    expect(c).not.toContain("SleipnirRestClient");
    expect(c).not.toContain("callWs");
  });
});

describe("emitJsClient --transport both (golden against story01.both.js snapshot)", () => {
  const input = buildEmitterInput(readFixture(), new NamingResolver());
  const tree = emitJsClient(input, { transport: "both" });

  it("emits the same file set as rest (only client.js differs)", () => {
    expect(Object.keys(tree).sort()).toEqual(
      ["api/client.js", "api/controllers.js", "api/index.js", "api/types.js"],
    );
  });

  it("matches the committed both snapshot byte-for-byte", () => {
    const snapshot = readTree(join(here, "..", "snapshots", "story01.both.js"));
    for (const [path, content] of Object.entries(tree)) {
      expect(snapshot[path], `snapshot missing for ${path}`).toBeDefined();
      expect(content).toBe(snapshot[path]);
    }
    expect(Object.keys(snapshot).sort()).toEqual(Object.keys(tree).sort());
  });

  it("wires both runtime clients and exposes the Ws variants", () => {
    const c = tree["api/client.js"];
    expect(c).toContain("SleipnirRestClient");
    expect(c).toContain("SleipnirWebSocketClient");
    expect(c).toContain("this._rest");
    expect(c).toContain("this._ws");
    expect(c).toContain("async callWs(call)");
    expect(c).toContain("async batchWs(b)");
    expect(c).toContain("this._rest.callBatch(m.requests, m.mode)");
    expect(c).toContain("this._ws.callBatch(m.requests, m.mode)");
  });
});

// Story-02 (nested-array fixture). The JS emitter has no path-record types
// (JS is untyped), so this is a byte-for-byte golden + a check that the nested
// array property renders as `SearchHit[]` in the JSDoc typedef.
describe("emitJsClient story02 (nested-array fixture, golden against story02.js snapshot)", () => {
  const input = buildEmitterInput(readFixture("story02"), new NamingResolver());
  const tree = emitJsClient(input, { transport: "rest" });

  it("emits the expected file set", () => {
    expect(Object.keys(tree).sort()).toEqual(
      ["api/client.js", "api/controllers.js", "api/index.js", "api/types.js"],
    );
  });

  it("matches the committed story02.js snapshot byte-for-byte", () => {
    const snapshot = readTree(join(here, "..", "snapshots", "story02.js"));
    for (const [path, content] of Object.entries(tree)) {
      expect(snapshot[path], `snapshot missing for ${path}`).toBeDefined();
      expect(content).toBe(snapshot[path]);
    }
    expect(Object.keys(snapshot).sort()).toEqual(Object.keys(tree).sort());
  });

  it("renders the nested array + nested object properties in the typedefs", () => {
    const types = tree["api/types.js"];
    expect(types).toContain("@typedef {Object} SearchResult");
    expect(types).toContain("@property {SearchHit[]} hits");
    expect(types).toContain("@property {Author} author");
  });
});