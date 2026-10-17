import { describe, it, expect } from "vitest";
import type { DiscoveryInfo } from "sleipnir-client";
import { findOpaqueSites, formatOpaqueWarning } from "../../src/core/opaque.js";
import { selfcheckDriftHint } from "../../src/cli/selfcheck.js";
import { readFixture } from "./fixture.js";

// A domain type from outside the controller assemblies (IngestionSummary), the
// non-generic envelope, and one nested inside a contract DTO's list property.
const discovery: DiscoveryInfo = {
  discoveryVersion: "1",
  controllers: [
    {
      name: "Run",
      methods: [
        { methodName: "Execute", returnType: { kind: "opaque", nativeName: "IngestionSummary" }, parameters: [] },
        {
          methodName: "Progress",
          returnType: { kind: "event", element: { kind: "opaque", nativeName: "ProgressInfo" } },
          parameters: [{ parameterName: "source", parameterType: { kind: "opaque", nativeName: "SourceRef" } }],
        },
        { methodName: "Raw", returnType: { kind: "opaque", nativeName: "SleipnirResponse" }, parameters: [] },
        { methodName: "Count", returnType: { kind: "scalar", name: "int" }, parameters: [] },
      ],
    },
  ],
  types: {
    "Api.InvoiceDto": {
      kind: "object",
      typeName: "Api.InvoiceDto",
      properties: [
        { propertyName: "lines", propertyType: { kind: "array", element: { kind: "opaque", nativeName: "LineItem" } } },
        { propertyName: "total", propertyType: { kind: "scalar", name: "decimal" } },
      ],
    },
  },
};

describe("findOpaqueSites", () => {
  it("reports returns, event payloads, parameters and nested properties", () => {
    expect(findOpaqueSites(discovery)).toEqual([
      { site: "Run.Execute → return", nativeName: "IngestionSummary" },
      { site: "Run.Progress → event payload", nativeName: "ProgressInfo" },
      { site: "Run.Progress(source)", nativeName: "SourceRef" },
      { site: "Run.Raw → return", nativeName: "SleipnirResponse" },
      { site: "Api.InvoiceDto.lines", nativeName: "LineItem" },
    ]);
  });

  it("finds nothing in a fully modelled contract (the committed fixtures)", () => {
    for (const name of ["story01", "story02", "story03", "enums"] as const) {
      expect(findOpaqueSites(readFixture(name)), name).toEqual([]);
    }
  });
});

describe("formatOpaqueWarning", () => {
  it("is empty without sites, so the CLI writes nothing", () => {
    expect(formatOpaqueWarning([])).toBe("");
  });

  it("names each site and the fix", () => {
    const text = formatOpaqueWarning(findOpaqueSites(discovery));
    expect(text).toContain("5 contract usages are opaque");
    expect(text).toContain("  Run.Execute → return: IngestionSummary\n");
    expect(text).toContain("Map it to a DTO in the API project");
    expect(text).toContain("return SleipnirResponse<T>");
  });

  it("caps the list", () => {
    const many = Array.from({ length: 25 }, (_, i) => ({ site: `C.M${i} → return`, nativeName: "X" }));
    const text = formatOpaqueWarning(many);
    expect(text).toContain("C.M19 → return");
    expect(text).not.toContain("C.M20 → return");
    expect(text).toContain("… (5 more)");
  });
});

describe("selfcheckDriftHint", () => {
  it("points a live-URL source at the committed contract + server gate", () => {
    const hint = selfcheckDriftHint("https://localhost:5001/api/sleipnir/discovery");
    expect(hint).toContain("--discovery path/to/contract.sleipnir.json");
    expect(hint).toContain("Sleipnir.Server.Codegen");
  });

  it("explains who keeps a contract file current", () => {
    const hint = selfcheckDriftHint("contract.sleipnir.json");
    expect(hint).toContain("Sleipnir.Server.Codegen keeps it current");
    expect(hint).not.toContain("live URL");
  });
});
