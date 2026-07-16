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
});