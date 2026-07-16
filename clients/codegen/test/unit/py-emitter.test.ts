import { describe, it, expect } from "vitest";
import { readFileSync, readdirSync, statSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { buildEmitterInput } from "../../src/core/model.js";
import { NamingResolver } from "../../src/core/naming.js";
import { emitPyClient } from "../../src/emitters/py.js";
import { readFixture } from "./fixture.js";

const here = dirname(fileURLToPath(import.meta.url));
const snapshotDir = join(here, "..", "snapshots", "story01.py");

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

describe("emitPyClient (golden against story01 snapshot)", () => {
  const input = buildEmitterInput(readFixture(), new NamingResolver());
  const tree = emitPyClient(input);

  it("emits the expected file set", () => {
    expect(Object.keys(tree).sort()).toEqual(["__init__.py", "client.py", "types.py"]);
  });

  it("matches the committed snapshot byte-for-byte", () => {
    const snapshot = readTree(snapshotDir);
    for (const [path, content] of Object.entries(tree)) {
      expect(snapshot[path], `snapshot missing for ${path}`).toBeDefined();
      expect(content).toBe(snapshot[path]);
    }
    expect(Object.keys(snapshot).sort()).toEqual(Object.keys(tree).sort());
  });

  it("emits camelCase @dataclass fields with from_dict", () => {
    expect(tree["types.py"]).toContain("class Order:");
    expect(tree["types.py"]).toContain("customerId: Optional[int] = None");
    expect(tree["types.py"]).toContain("placedAt: Optional[str] = None");
    expect(tree["types.py"]).toContain("def from_dict(cls, d: dict) -> \"Order\":");
  });

  it("emits snake_case method names with verbatim wire method + params", () => {
    const py = tree["client.py"];
    expect(py).toContain("def get_by_id(self, id: int) -> TrameCall:");
    expect(py).toContain('return TrameCall("Order", "GetById", {"id": id})');
    expect(py).toContain("def get_by_articles(self, articleIds: list[int]) -> TrameCall:");
  });

  it("alias() returns the @placeholder and exposes strips @ for the mapping key", () => {
    const py = tree["client.py"];
    expect(py).toContain("def alias(self, name: str) -> str:");
    expect(py).toContain('"""Return the \'@alias\' wire placeholder (for a consumer parameter)."""');
    expect(py).toContain('key = alias[1:] if alias.startswith("@") else alias');
  });

  it("sends the batch mode as integer 1 (Serial), not a string", () => {
    expect(tree["client.py"]).toContain('return {"requests": [c.to_request() for c in self._calls], "mode": 1}');
  });
});