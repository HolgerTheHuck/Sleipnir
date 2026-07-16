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