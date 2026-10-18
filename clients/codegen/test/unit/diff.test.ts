import { describe, it, expect } from "vitest";
import { mkdtempSync, readFileSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { DIFF_EXIT_CHANGES, diffExitCode, parseDiffArgs, runDiff } from "../../src/cli/diff.js";
import { compareContracts } from "../../src/core/compat.js";

const here = dirname(fileURLToPath(import.meta.url));
const spec = JSON.parse(readFileSync(join(here, "..", "fixtures", "compat", "cases.json"), "utf8")) as {
  cases: { name: string; base: unknown; head: unknown }[];
};
const byName = (prefix: string) => spec.cases.find((c) => c.name.startsWith(prefix))!;

/** Write a case's base/head contracts to a temp dir, return their paths. */
function files(prefix: string): { base: string; head: string } {
  const c = byName(prefix);
  const dir = mkdtempSync(join(tmpdir(), "sleipnir-diff-"));
  writeFileSync(join(dir, "base.json"), JSON.stringify(c.base));
  writeFileSync(join(dir, "head.json"), JSON.stringify(c.head));
  return { base: join(dir, "base.json"), head: join(dir, "head.json") };
}

async function run(argv: string[]): Promise<{ code: number; out: string; err: string }> {
  let out = "";
  let err = "";
  const code = await runDiff(argv, (s) => { out += s; }, (s) => { err += s; });
  return { code, out, err };
}

describe("sleipnir-gen diff", () => {
  it("parses options and rejects bad input", () => {
    expect(parseDiffArgs(["--base", "a", "--head", "b"])).toMatchObject({ failOn: "breaking", rollback: false, json: false });
    expect(parseDiffArgs(["--base", "a", "--head", "b", "--fail-on", "soft", "--rollback", "--json"]))
      .toMatchObject({ failOn: "soft", rollback: true, json: true });
    expect(() => parseDiffArgs(["--head", "b"])).toThrow("--base is required");
    expect(() => parseDiffArgs(["--base", "-", "--head", "-"])).toThrow("only one of --base / --head");
    expect(() => parseDiffArgs(["--base", "a", "--head", "b", "--fail-on", "all"])).toThrow("--fail-on must be");
  });

  it("maps the report to exit codes by --fail-on", () => {
    const soft = byName("opaque:"); // one soft change, nothing breaking
    const report = compareContracts(soft.base, soft.head);
    expect(diffExitCode(report, "breaking")).toBe(0);
    expect(diffExitCode(report, "soft")).toBe(DIFF_EXIT_CHANGES);
    expect(diffExitCode(report, "never")).toBe(0);
  });

  it("exits 5 on breaking changes and prints the text report", async () => {
    const { base, head } = files("controllers and methods");
    const r = await run(["--base", base, "--head", head]);
    expect(r.code).toBe(5);
    expect(r.out).toContain("Contract compatibility: 2 breaking, 0 soft, 2 safe");
    expect(r.out).toContain("BREAKING Legacy  [controller-removed]");
    expect(r.err).toContain("CONTRACT_EVOLUTION.md");
  });

  it("exits 0 for a compatible change and can print JSON", async () => {
    const { base, head } = files("a new byte[] parameter");
    const r = await run(["--base", base, "--head", head, "--json"]);
    expect(r.code).toBe(0);
    const report = JSON.parse(r.out);
    expect(report.summary).toEqual({ breaking: 0, soft: 0, safe: 1 });
    expect(report.changes[0].id).toBe("optional-parameter-added");
  });

  it("exits 3 when a contract cannot be read", async () => {
    const r = await run(["--base", join(tmpdir(), "does-not-exist.json"), "--head", join(tmpdir(), "nope.json")]);
    expect(r.code).toBe(3);
  });
});
