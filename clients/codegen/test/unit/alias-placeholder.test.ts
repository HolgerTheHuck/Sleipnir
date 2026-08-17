// Runtime regression guard for the 1.2.1 `TypedRequest.alias()` bug.
//
//   `alias(name)` returned the BARE name instead of the `@alias` wire placeholder.
//   The docstring claimed "@placeholder" but `return name` sent "ids" on the wire
//   for `alias("ids")`, which the server's ReplaceDependencyByAlias never matched —
//   the typed batch chain compiled but the dependent call received an unresolved
//   literal. The existing compile/snapshot tests used only the `alias("@ids")`
//   convention, where `return name` happened to return "@ids" (correct by
//   accident), so the bug was invisible. This test exercises BOTH call styles at
//   runtime against the EMITTED module: `alias("ids")` → "@ids" AND
//   `alias("@ids")` → "@ids".
//
// Emits the Story-01 TS tree to a temp dir under the package root (so
// `sleipnir-client` resolves via node_modules), then dynamically imports the
// generated `typed-call.ts` and calls the real `alias()`.
import { describe, it, expect } from "vitest";
import { mkdirSync, writeFileSync, rmSync } from "node:fs";
import { fileURLToPath, pathToFileURL } from "node:url";
import { dirname, join } from "node:path";
import { buildEmitterInput } from "../../src/core/model.js";
import { NamingResolver } from "../../src/core/naming.js";
import { emitTsClient } from "../../src/emitters/ts.js";
import { readFixture } from "./fixture.js";

const here = dirname(fileURLToPath(import.meta.url));
const pkgRoot = join(here, "..", "..");
const dir = join(pkgRoot, ".alias-runtime");

async function importTypedCall() {
  rmSync(dir, { recursive: true, force: true });
  mkdirSync(join(dir, "api"), { recursive: true });
  const tree = emitTsClient(buildEmitterInput(readFixture(), new NamingResolver()), { transport: "rest" });
  for (const [rel, content] of Object.entries(tree)) {
    const abs = join(dir, rel);
    mkdirSync(dirname(abs), { recursive: true });
    writeFileSync(abs, content, "utf8");
  }
  // Dynamic-import the EMITTED typed-call.ts (vitest/esbuild transforms .ts on
  // the fly; `sleipnir-client` + `./types.js` resolve from the package root).
  return await import(pathToFileURL(join(dir, "api", "typed-call.ts")).href) as {
    TypedCall: new <T = unknown, P extends Record<string, unknown> = Record<string, unknown>>(call: unknown) => { _call: unknown };
    TypedRequest: new <T = unknown, P extends Record<string, unknown> = Record<string, unknown>, A extends Record<string, unknown> = Record<string, unknown>>(call: { _call: unknown }) => {
      exposes: (path: string, alias: string) => { alias: (name: string) => string };
    };
  };
}

describe("generated TypedRequest.alias() returns the @alias wire placeholder (both call styles)", () => {
  it('alias("ids") → "@ids" (bare convention — the 1.2.1 bug returned "ids")', async () => {
    const mod = await importTypedCall();
    // Stub SleipnirCall: exposes() is called by TypedRequest.exposes() but its
    // return is ignored; alias() never touches the call (pure string normalize).
    const stub: { exposes: () => undefined } = { exposes: () => undefined };
    const req = new mod.TypedRequest(new mod.TypedCall(stub)).exposes("$.x", "ids");
    expect(req.alias("ids")).toBe("@ids");
  });

  it('alias("@ids") → "@ids" (@-prefixed convention — already correct, must not double-prefix)', async () => {
    const mod = await importTypedCall();
    const stub: { exposes: () => undefined } = { exposes: () => undefined };
    const req = new mod.TypedRequest(new mod.TypedCall(stub)).exposes("$.x", "@ids");
    expect(req.alias("@ids")).toBe("@ids");
  });
});