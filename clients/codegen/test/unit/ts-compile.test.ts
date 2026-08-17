// Regression gate: the generated TS client must compile, AND the typed batch
// must type-check the Story-01 diamond (producer exposes camelCase paths,
// consumer resolves the typed alias). Spawns `tsc --noEmit` against a temp
// project under the package root so `sleipnir-client` resolves via node_modules.
//
// Runs once per transport (rest | ws | both); the ws harness exercises the
// `.ws` escape hatch, the both harness exercises `callWs`/`batchWs` + both
// escape hatches — so the transport-specific surface in client.ts is covered.
import { describe, it, expect } from "vitest";
import { mkdirSync, writeFileSync, rmSync, existsSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { spawnSync } from "node:child_process";
import { createRequire } from "node:module";
import { buildEmitterInput } from "../../src/core/model.js";
import { NamingResolver } from "../../src/core/naming.js";
import { emitTsClient } from "../../src/emitters/ts.js";
import { readFixture } from "./fixture.js";

const here = dirname(fileURLToPath(import.meta.url));
const pkgRoot = join(here, "..", "..");
const compileDir = join(pkgRoot, ".tsc-compile");

const require = createRequire(import.meta.url);
const tscPath = require.resolve("typescript/bin/tsc");

type Transport = "rest" | "ws" | "both";

/** Shared Story-01 diamond body — identical across transports; only the client
 * construction + transport-specific surface (in `harnessFor`) differ. */
const diamondBody = `  const batch = new Batch();

  // Producer exposes camelCase paths (compile-checked against JsonPathOf<Order>).
  const order = batch.add(client.order.getById(42))
    .exposes("$.customerId", "@customerId")
    .exposes("$.id", "@orderId")
    .exposes("$.shippingAddressId", "@addressId");

  // Consumers resolve the typed alias → number.
  batch.add(client.customer.getById(order.alias("@customerId")));

  // A list producer exposes the array-valued alias → number[].
  const lines = batch.add(client.orderLine.getByOrder(order.alias("@orderId")))
    .exposes("$[*].articleId", "@articleIds");

  // Diamond: two consumers of the same array alias.
  batch.add(client.article.getByIds(lines.alias("@articleIds")));
  batch.add(client.stock.getByArticles(lines.alias("@articleIds")));
  batch.add(client.address.getById(order.alias("@addressId")));

  await client.batch(batch);
`;

function harnessFor(t: Transport): string {
  const ctor =
    t === "ws" ? `  const client = new SleipnirClient("ws://localhost:5001/sleipnirws");`
    : t === "both" ? `  const client = new SleipnirClient("http://localhost:5001", { rest: {}, ws: {} });`
    : `  const client = new SleipnirClient("http://localhost:5001");`;

  const surface =
    t === "ws"
      ? `  // ws-only client exposes the WebSocket escape hatch.
  void client.ws;
`
      : t === "both"
      ? `  // both: REST is the default call/batch; Ws variants + both escape hatches exist.
  await client.callWs(client.order.getById(1));
  await client.batchWs(batch);
  void client.rest;
  void client.ws;
`
      : ``;

  return `// Story-01 diamond: compile-time-typed dependency chain (transport: ${t}).
import { SleipnirClient } from "./api/client.js";
import { Batch } from "./api/typed-call.js";

export async function diamond(): Promise<void> {
${ctor}
${diamondBody}${surface}
  // --- Compile-time guarantees (must error without the suppression) ---
  // @ts-expect-error — PascalCase path is not in JsonPathOf<Order> (wire is camelCase).
  order.exposes("$.CustomerId", "@badPascal");
  // @ts-expect-error — an undeclared alias does not exist on the alias map.
  order.alias("@nope");
}
`;
}

const tsconfig = JSON.stringify({
  compilerOptions: {
    target: "ES2022",
    module: "NodeNext",
    moduleResolution: "NodeNext",
    lib: ["ES2022", "DOM", "DOM.Iterable"],
    strict: true,
    noEmit: true,
    skipLibCheck: true,
    esModuleInterop: true,
    types: ["node"],
  },
  include: ["api/**/*.ts", "harness.ts"],
});

function runTsc(projectDir: string): { status: number; stdout: string; stderr: string } {
  const r = spawnSync(process.execPath, [tscPath, "--noEmit", "-p", join(projectDir, "tsconfig.json")], {
    encoding: "utf8",
    cwd: pkgRoot,
  });
  return { status: r.status ?? 1, stdout: r.stdout ?? "", stderr: r.stderr ?? "" };
}

/** Emit the tree for `t`, write it + the transport harness + tsconfig into a
 * fresh per-transport dir, and run tsc --noEmit. */
function compileTransport(t: Transport): { status: number; stdout: string; stderr: string } {
  const dir = join(compileDir, t);
  rmSync(dir, { recursive: true, force: true });
  mkdirSync(join(dir, "api"), { recursive: true });

  const tree = emitTsClient(buildEmitterInput(readFixture(), new NamingResolver()), { transport: t });
  for (const [path, content] of Object.entries(tree)) {
    const abs = join(dir, path);
    mkdirSync(dirname(abs), { recursive: true });
    writeFileSync(abs, content, "utf8");
  }
  writeFileSync(join(dir, "harness.ts"), harnessFor(t), "utf8");
  writeFileSync(join(dir, "tsconfig.json"), tsconfig, "utf8");

  return runTsc(dir);
}

describe.each<Transport>(["rest", "ws", "both"])("generated TS compiles + typed diamond type-checks (transport: %s)", (t) => {
  it("tsc exits 0 against the diamond harness", () => {
    const result = compileTransport(t);
    if (result.status !== 0) {
      console.error(`tsc stdout (${t}):\n` + result.stdout);
      console.error(`tsc stderr (${t}):\n` + result.stderr);
    }
    expect(result.status).toBe(0);
  }, { timeout: 60_000 });
});

// keep existsSync referenced for the guard in case the dir lingers.
void existsSync;