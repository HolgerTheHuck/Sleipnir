import { describe, it, expect } from "vitest";
import { mkdirSync, writeFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { selfcheck } from "../../src/cli/selfcheck.js";
import { buildEmitterInput } from "../../src/core/model.js";
import { NamingResolver } from "../../src/core/naming.js";
import { emitTsClient } from "../../src/emitters/ts.js";
import { readFixture } from "./fixture.js";

/** Create a fresh unique temp dir for a committed-tree fixture. */
function freshTempDir(): string {
  const d = join(tmpdir(), `sleipnir-selfcheck-${Math.random().toString(36).slice(2)}`);
  mkdirSync(d, { recursive: true });
  return d;
}

/** Write a path → content map as a real file tree under `dir`. */
function writeTree(dir: string, tree: Record<string, string>): void {
  for (const [rel, content] of Object.entries(tree)) {
    const dest = join(dir, rel);
    mkdirSync(dirname(dest), { recursive: true });
    writeFileSync(dest, content, "utf8");
  }
}

describe("selfcheck (the --selfcheck drift gate)", () => {
  // A hand-crafted emitted tree with nested paths (mirrors the emitter's api/* layout).
  const emitted: Record<string, string> = {
    "api/client.ts": "// client\n",
    "api/controllers.ts": "// controllers\n",
    "api/types.ts": "// types\n",
    "README.md": "# client\n",
  };

  it("is clean when the committed tree matches the emitted tree byte-for-byte", () => {
    const dir = freshTempDir();
    try {
      writeTree(dir, emitted);
      const result = selfcheck(emitted, dir);
      expect(result.clean).toBe(true);
      expect(result.drift).toEqual([]);
      expect(result.unchanged).toBe(4);
      expect(result.total).toBe(4);
    } finally { rmSync(dir, { recursive: true, force: true }); }
  });

  it("flags a committed file whose content differs as `changed`", () => {
    const dir = freshTempDir();
    try {
      writeTree(dir, emitted);
      // Mutate one committed file after writing.
      writeFileSync(join(dir, "api/controllers.ts"), "// controllers (DRIFTED)\n", "utf8");
      const result = selfcheck(emitted, dir);
      expect(result.clean).toBe(false);
      expect(result.drift).toEqual([{ path: "api/controllers.ts", status: "changed" }]);
      expect(result.unchanged).toBe(3);
    } finally { rmSync(dir, { recursive: true, force: true }); }
  });

  it("flags an emitted file that is missing from the committed tree as `missing`", () => {
    const dir = freshTempDir();
    try {
      const { "api/types.ts": _omit, ...committed } = emitted;
      writeTree(dir, committed);
      const result = selfcheck(emitted, dir);
      expect(result.clean).toBe(false);
      expect(result.drift).toEqual([{ path: "api/types.ts", status: "missing" }]);
    } finally { rmSync(dir, { recursive: true, force: true }); }
  });

  it("does not flag extra files on disk (the safe direction: generated ⊆ committed)", () => {
    const dir = freshTempDir();
    try {
      writeTree(dir, emitted);
      // A hand-written file co-located in the project client dir is not generated;
      // the gate must not flag it (would otherwise false-positive on real projects).
      writeFileSync(join(dir, "api/hand-written.ts"), "// a human wrote this\n", "utf8");
      const result = selfcheck(emitted, dir);
      expect(result.clean).toBe(true);
      expect(result.total).toBe(4);
    } finally { rmSync(dir, { recursive: true, force: true }); }
  });

  it("refuses to read a path that would escape the output dir", () => {
    const dir = freshTempDir();
    try {
      const escaped: Record<string, string> = { "../outside.ts": "x" };
      expect(() => selfcheck(escaped, dir)).toThrow(/refusing to read outside output dir/);
    } finally { rmSync(dir, { recursive: true, force: true }); }
  });

  it("round-trips the real TS emitter: a freshly generated tree is clean against itself", () => {
    const input = buildEmitterInput(readFixture(), new NamingResolver());
    const tree = emitTsClient(input, { baseUrl: undefined, capability: "rest" });
    expect(Object.keys(tree).length).toBeGreaterThan(0);
    const dir = freshTempDir();
    try {
      writeTree(dir, tree);
      const result = selfcheck(tree, dir);
      expect(result.clean).toBe(true);
      expect(result.unchanged).toBe(Object.keys(tree).length);
    } finally { rmSync(dir, { recursive: true, force: true }); }
  });
});