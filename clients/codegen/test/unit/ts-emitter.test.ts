import { describe, it, expect } from "vitest";
import { readFileSync, readdirSync, statSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { buildEmitterInput } from "../../src/core/model.js";
import { NamingResolver } from "../../src/core/naming.js";
import { emitTsClient, type SleipnirBundleCapability } from "../../src/emitters/ts.js";
import { readFixture } from "./fixture.js";

const here = dirname(fileURLToPath(import.meta.url));
const snapshotsDir = join(here, "..", "snapshots");

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

/** `all` is the default → bare `<story>.ts`; others get a `.<cap>` suffix. */
function suffixFor(cap: SleipnirBundleCapability): string {
  return cap === "all" ? "" : `.${cap}`;
}

const CAPABILITIES: SleipnirBundleCapability[] = ["rest", "ws", "all", "signalr"];

describe("emitTsClient (golden against story01 snapshots, all capabilities)", () => {
  const input = buildEmitterInput(readFixture(), new NamingResolver());

  it("emits the expected file set (default = all)", () => {
    const tree = emitTsClient(input);
    expect(Object.keys(tree).sort()).toEqual(
      ["api/client.ts", "api/controllers.ts", "api/index.ts", "api/typed-call.ts", "api/types.ts"],
    );
  });

  it("default (no opts) = capability all, matches story01.ts byte-for-byte", () => {
    const tree = emitTsClient(input);
    const snapshot = readTree(join(snapshotsDir, "story01.ts"));
    for (const [path, content] of Object.entries(tree)) {
      expect(snapshot[path], `snapshot missing for ${path}`).toBeDefined();
      expect(content).toBe(snapshot[path]);
    }
    expect(Object.keys(snapshot).sort()).toEqual(Object.keys(tree).sort());
    // The default capability is `all`.
    expect(tree["api/client.ts"]).toContain('capability: "all"');
  });

  for (const cap of CAPABILITIES) {
    // eslint-disable-next-line @typescript-eslint/no-loop-func
    it(`--transport ${cap} matches the committed story01${suffixFor(cap)}.ts snapshot byte-for-byte`, () => {
      const tree = emitTsClient(input, { capability: cap });
      const snapshot = readTree(join(snapshotsDir, `story01${suffixFor(cap)}.ts`));
      for (const [path, content] of Object.entries(tree)) {
        expect(snapshot[path], `snapshot missing for ${path}`).toBeDefined();
        expect(content).toBe(snapshot[path]);
      }
      expect(Object.keys(snapshot).sort()).toEqual(Object.keys(tree).sort());
    });
  }

  it("camelCases properties (wire fix) and method names", () => {
    const tree = emitTsClient(input);
    expect(tree["api/types.ts"]).toContain("id?: number;");
    expect(tree["api/types.ts"]).toContain("customerId?: number;");
    expect(tree["api/types.ts"]).toContain("shippingAddressId?: number;");
    expect(tree["api/controllers.ts"]).toContain("getById(id: number): TypedCall<Order, OrderPaths>");
    expect(tree["api/controllers.ts"]).not.toContain("GetById(id"); // no PascalCase method name
  });

  it("carries the path-record type per method (typed-batch design)", () => {
    const tree = emitTsClient(input);
    expect(tree["api/controllers.ts"]).toContain("getByOrder(orderId: number): TypedCall<OrderLine[], OrderLineArrayPaths>");
    expect(tree["api/controllers.ts"]).toContain("getByArticles(articleIds: number[]): TypedCall<StockInfo[], StockInfoArrayPaths>");
  });
});

describe("emitTsClient — unified transport-router surface", () => {
  const input = buildEmitterInput(readFixture(), new NamingResolver());

  it("delegates to SleipnirTransportRouter (no direct backend wiring in client.ts)", () => {
    const c = emitTsClient(input)["api/client.ts"];
    expect(c).toContain("import { SleipnirCall, SleipnirTransportRouter } from \"sleipnir-client\";");
    expect(c).toContain("private readonly _router: SleipnirTransportRouter;");
    expect(c).toContain("this._router = new SleipnirTransportRouter({ baseUrl, capability:");
    // No direct backend construction in the generated client — the router owns that.
    expect(c).not.toMatch(/new SleipnirRestClient\(/);
    expect(c).not.toMatch(/new SleipnirWebSocketClient\(/);
    expect(c).not.toMatch(/new SleipnirSseClient\(/);
  });

  it("exposes call/batch routing + runtime transport selection + escape hatches", () => {
    const c = emitTsClient(input)["api/client.ts"];
    // Runtime transport selection.
    expect(c).toContain("negotiate(): Promise<void>");
    expect(c).toContain("useTransport(t: SleipnirTransport): Promise<void>");
    expect(c).toContain("get activeTransport():");
    // Calls + batch route through the router.
    expect(c).toContain("this._router.call(call.toRequest())");
    expect(c).toContain("this._router.callBatch(multi.requests, multi.mode)");
    // Escape hatches return `| undefined` (capability-agnostic surface).
    expect(c).toContain("get rest(): SleipnirRestClient | undefined");
    expect(c).toContain("get ws(): SleipnirWebSocketClient | undefined");
    expect(c).toContain("get sse(): SleipnirSseClient | undefined");
    expect(c).toContain("setBearer(bearer");
    expect(c).toContain("dispose(): void");
  });

  it("removed the per-transport Ws variants (callWs/batchWs) — use useTransport + call instead", () => {
    const all = emitTsClient(input, { capability: "all" })["api/client.ts"];
    expect(all).not.toContain("callWs");
    expect(all).not.toContain("batchWs");
  });

  it("the ws-only capability still bundles WS but the surface is identical (escape hatch may be undefined)", () => {
    const ws = emitTsClient(input, { capability: "ws" })["api/client.ts"];
    expect(ws).toContain('capability: "ws"');
    // Identical member surface — same getters/methods as `all`.
    expect(ws).toContain("get ws(): SleipnirWebSocketClient | undefined");
    expect(ws).toContain("get rest(): SleipnirRestClient | undefined");
    expect(ws).toContain("useTransport(t: SleipnirTransport)");
  });

  it("all four capabilities emit an identical public member set in client.ts", () => {
    // Extract the exported/class member signatures (the surface) from each capability's
    // client.ts and assert they are byte-identical — only the `capability` literal + header differ.
    const surfaces = CAPABILITIES.map((cap) => {
      const c = emitTsClient(input, { capability: cap })["api/client.ts"];
      // Strip the header comment + the capability literal so only the structural surface remains.
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

describe("emitTsClient — deprecated --transport aliases canonicalize", () => {
  const input = buildEmitterInput(readFixture(), new NamingResolver());

  it("transport 'both' → capability 'all' (matches the default snapshot)", () => {
    const tree = emitTsClient(input, { transport: "both" });
    expect(tree["api/client.ts"]).toContain('capability: "all"');
    const snapshot = readTree(join(snapshotsDir, "story01.ts"));
    for (const [path, content] of Object.entries(tree)) {
      expect(snapshot[path]).toBeDefined();
      expect(content).toBe(snapshot[path]);
    }
  });

  it("transport 'sse' → capability 'rest' (matches the rest snapshot)", () => {
    const tree = emitTsClient(input, { transport: "sse" });
    expect(tree["api/client.ts"]).toContain('capability: "rest"');
    const snapshot = readTree(join(snapshotsDir, "story01.rest.ts"));
    for (const [path, content] of Object.entries(tree)) {
      expect(snapshot[path]).toBeDefined();
      expect(content).toBe(snapshot[path]);
    }
  });

  it("capability wins over a deprecated transport alias when both are given", () => {
    const tree = emitTsClient(input, { capability: "ws", transport: "both" });
    expect(tree["api/client.ts"]).toContain('capability: "ws"');
  });
});

// Story-02 exercises NESTED path descent: a SearchResult with `hits: SearchHit[]`,
// each hit carrying a scalar `articleId` and a nested object `author: Author`.
// The generated path records must enumerate `$.hits[*].articleId`,
// `$.hits[0].author.name`, etc. — not stop at `$.hits` — so a typed dependency
// chain extracting from a nested array compiles.
describe("emitTsClient story02 (nested-array path descent, golden against story02 snapshot)", () => {
  const input = buildEmitterInput(readFixture("story02"), new NamingResolver());
  const tree = emitTsClient(input); // default = all

  it("emits the expected file set", () => {
    expect(Object.keys(tree).sort()).toEqual(
      ["api/client.ts", "api/controllers.ts", "api/index.ts", "api/typed-call.ts", "api/types.ts"],
    );
  });

  it("matches the committed story02 snapshot byte-for-byte (default = all)", () => {
    const snapshot = readTree(join(snapshotsDir, "story02.ts"));
    for (const [path, content] of Object.entries(tree)) {
      expect(snapshot[path], `snapshot missing for ${path}`).toBeDefined();
      expect(content).toBe(snapshot[path]);
    }
    expect(Object.keys(snapshot).sort()).toEqual(Object.keys(tree).sort());
  });

  it("SearchResultPaths enumerates nested array-element paths (the user's gap)", () => {
    const tc = tree["api/typed-call.ts"];
    // The chain the user had to fall back to raw SleipnirCall for:
    expect(tc).toContain('"$.hits[*].articleId": number[];');
    expect(tc).toContain('"$.hits[0].articleId": number;');
    // Nested object UNDER an array element:
    expect(tc).toContain('"$.hits[*].author": Author[];');
    expect(tc).toContain('"$.hits[*].author.name": string[];');
    expect(tc).toContain('"$.hits[0].author.name": string;');
    // Top-level scalar still present:
    expect(tc).toContain('"$.total": number;');
    // The whole-array leaf is still there (not regressed):
    expect(tc).toContain('"$.hits": SearchHit[];');
  });

  it("SearchResultArrayPaths descends two array levels (stacked [*] collected)", () => {
    const tc = tree["api/typed-call.ts"];
    // $[*].hits is the outer array of SearchResults, each carrying hits.
    expect(tc).toContain('"$[*].hits": SearchHit[][];');
    // Stacked [*][*] collects into one array (JsonPath multi-match semantics).
    expect(tc).toContain('"$[*].hits[*].articleId": number[];');
  });

  it("wires the typed chain surface (Search + Article controllers)", () => {
    const ctrl = tree["api/controllers.ts"];
    expect(ctrl).toContain("semanticSearch(query: string): TypedCall<SearchResult, SearchResultPaths>");
    expect(ctrl).toContain("getByIds(articleIds: number[]): TypedCall<Article[], ArticleArrayPaths>");
  });
});