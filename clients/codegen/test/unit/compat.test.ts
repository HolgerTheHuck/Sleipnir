// The TS engine against the shared executable spec — the same cases the C# engine runs
// (SleipnirTests/Unit/Codegen/ContractCompatibilityTests.cs), so both classify every change
// identically: id, severity, site, position, direction and message.
import { describe, it, expect } from "vitest";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { compareContracts, formatCompatReport } from "../../src/core/compat.js";
import { DiscoveryShapeError } from "../../src/core/discovery.js";

const here = dirname(fileURLToPath(import.meta.url));
const spec = JSON.parse(readFileSync(join(here, "..", "fixtures", "compat", "cases.json"), "utf8")) as {
  cases: { name: string; rollback: boolean; base: unknown; head: unknown; expected: unknown[] }[];
};

describe("compareContracts (shared spec)", () => {
  for (const c of spec.cases) {
    it(c.name, () => {
      expect(compareContracts(c.base, c.head, c.rollback).changes).toEqual(c.expected);
    });
  }
});

describe("compareContracts report", () => {
  const params = spec.cases.find((c) => c.name.startsWith("parameters: required added"))!;

  it("counts by severity", () => {
    expect(compareContracts(params.base, params.head).summary).toEqual({ breaking: 2, soft: 2, safe: 1 });
  });

  it("formats like the C# ToText", () => {
    const text = formatCompatReport(compareContracts(params.base, params.head));
    expect(text.startsWith("Contract compatibility: 2 breaking, 2 soft, 1 safe\n")).toBe(true);
    expect(text).toContain("  BREAKING Order.Find(note)  [required-parameter-not-sent]  The server requires parameter 'note'");
  });

  it("rejects a malformed contract", () => {
    expect(() => compareContracts({ discoveryVersion: "9", controllers: [], types: {} }, {})).toThrow(DiscoveryShapeError);
  });
});
