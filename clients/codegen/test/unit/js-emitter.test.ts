import { describe, it, expect } from "vitest";
import { readFileSync, readdirSync, statSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { buildEmitterInput } from "../../src/core/model.js";
import { NamingResolver } from "../../src/core/naming.js";
import { emitJsClient, type SleipnirBundleCapability } from "../../src/emitters/js.js";
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

describe("emitJsClient (golden against story01 snapshots, all capabilities)", () => {
  const input = buildEmitterInput(readFixture(), new NamingResolver());

  it("emits the expected file set (default = all)", () => {
    const tree = emitJsClient(input);
    expect(Object.keys(tree).sort()).toEqual(
      ["api/client.js", "api/controllers.js", "api/index.js", "api/types.js"],
    );
  });

  it("default (no opts) = capability all, matches story01.js byte-for-byte", () => {
    const tree = emitJsClient(input);
    const snapshot = readTree(join(snapshotsDir, "story01.js"));
    for (const [path, content] of Object.entries(tree)) {
      expect(snapshot[path], `snapshot missing for ${path}`).toBeDefined();
      expect(content).toBe(snapshot[path]);
    }
    expect(Object.keys(snapshot).sort()).toEqual(Object.keys(tree).sort());
    expect(tree["api/client.js"]).toContain('capability: "all"');
  });

  for (const cap of CAPABILITIES) {
    // eslint-disable-next-line @typescript-eslint/no-loop-func
    it(`--transport ${cap} matches the committed story01${suffixFor(cap)}.js snapshot byte-for-byte`, () => {
      const tree = emitJsClient(input, { capability: cap });
      const snapshot = readTree(join(snapshotsDir, `story01${suffixFor(cap)}.js`));
      for (const [path, content] of Object.entries(tree)) {
        expect(snapshot[path], `snapshot missing for ${path}`).toBeDefined();
        expect(content).toBe(snapshot[path]);
      }
      expect(Object.keys(snapshot).sort()).toEqual(Object.keys(tree).sort());
    });
  }

  it("emits JSDoc @typedef blocks with camelCase properties", () => {
    const tree = emitJsClient(input);
    expect(tree["api/types.js"]).toContain("@typedef {Object} Order");
    expect(tree["api/types.js"]).toContain("@property {number} customerId");
  });
});

describe("emitJsClient — unified transport-router surface", () => {
  const input = buildEmitterInput(readFixture(), new NamingResolver());

  it("delegates to SleipnirTransportRouter and routes call/batch through it", () => {
    const c = emitJsClient(input)["api/client.js"];
    expect(c).toContain("import { SleipnirCall, SleipnirTransportRouter } from \"sleipnir-client\";");
    expect(c).toContain("this._router = new SleipnirTransportRouter({ baseUrl, capability:");
    expect(c).toContain("this._router.call(call.toRequest())");
    // callBatch must be called with (requests, mode), not the multi object.
    expect(c).toContain("this._router.callBatch(m.requests, m.mode)");
    expect(c).not.toContain("callBatch(b.toMulti())");
    // Runtime transport selection + escape hatches.
    expect(c).toContain("negotiate()");
    expect(c).toContain("useTransport(t)");
    expect(c).toContain("get activeTransport()");
    expect(c).toContain("get rest()");
    expect(c).toContain("get ws()");
    expect(c).toContain("get sse()");
    expect(c).toContain("setBearer(bearer)");
    expect(c).toContain("dispose()");
  });

  it("removed the per-transport Ws variants (callWs/batchWs)", () => {
    const all = emitJsClient(input, { capability: "all" })["api/client.js"];
    expect(all).not.toContain("callWs");
    expect(all).not.toContain("batchWs");
  });

  it("all four capabilities emit an identical public member set in client.js", () => {
    const surfaces = CAPABILITIES.map((cap) => {
      const c = emitJsClient(input, { capability: cap })["api/client.js"];
      return c
        .replace(/^\/\/.*$/gm, "")
        .replace(/capability: "(rest|ws|all|signalr)"/, 'capability: "<cap>"');
    });
    const first = surfaces[0];
    for (let i = 1; i < surfaces.length; i++) {
      expect(surfaces[i], `surface differs between ${CAPABILITIES[0]} and ${CAPABILITIES[i]}`).toBe(first);
    }
  });
});

describe("emitJsClient — deprecated --transport aliases canonicalize", () => {
  const input = buildEmitterInput(readFixture(), new NamingResolver());

  it("transport 'both' → capability 'all' (matches the default snapshot)", () => {
    const tree = emitJsClient(input, { transport: "both" });
    expect(tree["api/client.js"]).toContain('capability: "all"');
    const snapshot = readTree(join(snapshotsDir, "story01.js"));
    for (const [path, content] of Object.entries(tree)) {
      expect(snapshot[path]).toBeDefined();
      expect(content).toBe(snapshot[path]);
    }
  });

  it("transport 'sse' → capability 'rest' (matches the rest snapshot)", () => {
    const tree = emitJsClient(input, { transport: "sse" });
    expect(tree["api/client.js"]).toContain('capability: "rest"');
    const snapshot = readTree(join(snapshotsDir, "story01.rest.js"));
    for (const [path, content] of Object.entries(tree)) {
      expect(snapshot[path]).toBeDefined();
      expect(content).toBe(snapshot[path]);
    }
  });
});

// Story-02 (nested-array fixture). The JS emitter has no path-record types
// (JS is untyped), so this is a byte-for-byte golden + a check that the nested
// array property renders as `SearchHit[]` in the JSDoc typedef.
describe("emitJsClient story02 (nested-array fixture, golden against story02 snapshot)", () => {
  const input = buildEmitterInput(readFixture("story02"), new NamingResolver());
  const tree = emitJsClient(input); // default = all

  it("emits the expected file set", () => {
    expect(Object.keys(tree).sort()).toEqual(
      ["api/client.js", "api/controllers.js", "api/index.js", "api/types.js"],
    );
  });

  it("matches the committed story02.js snapshot byte-for-byte (default = all)", () => {
    const snapshot = readTree(join(snapshotsDir, "story02.js"));
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