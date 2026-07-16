// Regression gate: the generated TS client must compile, AND the typed batch
// must type-check the Story-01 diamond (producer exposes camelCase paths,
// consumer resolves the typed alias). Spawns `tsc --noEmit` against a temp
// project under the package root so `trame-client` resolves via node_modules.
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

const harness = `// Story-01 diamond: compile-time-typed dependency chain.
import { TrameClient } from "./api/client.js";
import { Batch } from "./api/typed-call.js";

export async function diamond(): Promise<void> {
  const client = new TrameClient("http://localhost:5001");
  const batch = new Batch();

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

  // --- Compile-time guarantees (must error without the suppression) ---
  // @ts-expect-error — PascalCase path is not in JsonPathOf<Order> (wire is camelCase).
  order.exposes("$.CustomerId", "@badPascal");
  // @ts-expect-error — an undeclared alias does not exist on the alias map.
  order.alias("@nope");
}
`;

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

function runTsc(): { status: number; stdout: string; stderr: string } {
  const r = spawnSync(process.execPath, [tscPath, "--noEmit", "-p", join(compileDir, "tsconfig.json")], {
    encoding: "utf8",
    cwd: pkgRoot,
  });
  return { status: r.status ?? 1, stdout: r.stdout ?? "", stderr: r.stderr ?? "" };
}

describe("generated TS compiles + typed diamond type-checks (tsc --noEmit)", () => {
  it("tsc exits 0 against the diamond harness", () => {
    rmSync(compileDir, { recursive: true, force: true });
    mkdirSync(join(compileDir, "api"), { recursive: true });

    const tree = emitTsClient(buildEmitterInput(readFixture(), new NamingResolver()));
    for (const [path, content] of Object.entries(tree)) {
      const abs = join(compileDir, path);
      mkdirSync(dirname(abs), { recursive: true });
      writeFileSync(abs, content, "utf8");
    }
    writeFileSync(join(compileDir, "harness.ts"), harness, "utf8");
    writeFileSync(join(compileDir, "tsconfig.json"), tsconfig, "utf8");

    const result = runTsc();
    if (result.status !== 0) {
      console.error("tsc stdout:\n" + result.stdout);
      console.error("tsc stderr:\n" + result.stderr);
    }
    expect(result.status).toBe(0);

    rmSync(compileDir, { recursive: true, force: true });
  }, { timeout: 60_000 });
});

// keep existsSync referenced for the guard in case the dir lingers.
void existsSync;