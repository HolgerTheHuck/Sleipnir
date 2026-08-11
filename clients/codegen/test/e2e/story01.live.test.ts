// E2E against a running Story-01 server. Opt-in via SLEIPNIR_E2E=1, else skipped.
//   PowerShell: $env:SLEIPNIR_E2E=1; npm run test:e2e
//   Bash:       SLEIPNIR_E2E=1 npm run test:e2e
// Boot the server:  dotnet run --project stories/01-n-plus-one-screen/Story01.csproj
// Target URL:      SLEIPNIR_URL (default http://127.0.0.1:5001) — the server *base* URL.
//
// Verifies the full loop end to end: live discovery → emit TS → tsc type-check +
// emit JS → dynamically import the GENERATED SleipnirClient → execute a single call
// AND the typed Story-01 diamond batch → assert the order round-trips with
// id === 42 and the alias chain resolves (customer.id === 7, 3 articles, etc.).
import { describe, it, expect } from "vitest";
import { mkdirSync, writeFileSync, rmSync } from "node:fs";
import { fileURLToPath, pathToFileURL } from "node:url";
import { dirname, join } from "node:path";
import { spawnSync } from "node:child_process";
import { createRequire } from "node:module";
import { loadDiscovery } from "../../src/core/discovery.js";
import { buildEmitterInput } from "../../src/core/model.js";
import { NamingResolver } from "../../src/core/naming.js";
import { emitTsClient } from "../../src/emitters/ts.js";

const enabled = process.env.SLEIPNIR_E2E === "1";
const url = process.env.SLEIPNIR_URL ?? "http://127.0.0.1:5001";
const here = dirname(fileURLToPath(import.meta.url));
const pkgRoot = join(here, "..", "..");
const compileDir = join(pkgRoot, ".tsc-e2e");
const require = createRequire(import.meta.url);
const tscPath = require.resolve("typescript/bin/tsc");

// A harness that builds the typed Story-01 diamond via the GENERATED client +
// Batch, and a single getById call. Emitted alongside api/*.ts, type-checked by
// tsc, then imported at runtime to execute against the live server.
const harness = `import { SleipnirClient } from "./api/client.js";
import { Batch } from "./api/typed-call.js";

export async function singleOrder(baseUrl: string) {
  const client = new SleipnirClient(baseUrl);
  return await client.call(client.order.getById(42));
}

export async function diamond(baseUrl: string) {
  const client = new SleipnirClient(baseUrl);
  const batch = new Batch();
  const order = batch.add(client.order.getById(42))
    .exposes("$.customerId", "@customerId")
    .exposes("$.id", "@orderId")
    .exposes("$.shippingAddressId", "@addressId");
  batch.add(client.customer.getById(order.alias("@customerId")));
  const lines = batch.add(client.orderLine.getByOrder(order.alias("@orderId")))
    .exposes("$[*].articleId", "@articleIds");
  batch.add(client.article.getByIds(lines.alias("@articleIds")));
  batch.add(client.stock.getByArticles(lines.alias("@articleIds")));
  batch.add(client.address.getById(order.alias("@addressId")));
  return await client.batch(batch);
}
`;

const tsconfig = JSON.stringify({
  compilerOptions: {
    target: "ES2022", module: "NodeNext", moduleResolution: "NodeNext",
    lib: ["ES2022", "DOM", "DOM.Iterable"], strict: true, noEmit: false,
    outDir: "dist", rootDir: ".",
    skipLibCheck: true, esModuleInterop: true, types: ["node"],
  },
  include: ["api/**/*.ts", "harness.ts"],
});

const run = (cmd: string, args: string[], opts: Record<string, unknown> = {}) =>
  spawnSync(cmd, args, { encoding: "utf8", cwd: pkgRoot, ...opts });

describe.skipIf(!enabled)("e2e: Story-01 live discovery → emit → compile → execute", () => {
  it("discovers, emits, type-checks, and executes the single call + typed diamond", async () => {
    // 1. Live discovery (base URL → default apiPath "api/sleipnir").
    const discovery = await loadDiscovery(url);
    expect(discovery.controllers.length).toBe(6);

    // 2. Emit the typed client + write it with a diamond harness to a temp project.
    rmSync(compileDir, { recursive: true, force: true });
    const tree = emitTsClient(buildEmitterInput(discovery, new NamingResolver()));
    for (const [path, content] of Object.entries(tree)) {
      const abs = join(compileDir, path);
      mkdirSync(dirname(abs), { recursive: true });
      writeFileSync(abs, content, "utf8");
    }
    writeFileSync(join(compileDir, "harness.ts"), harness, "utf8");
    writeFileSync(join(compileDir, "tsconfig.json"), tsconfig, "utf8");

    // 3. tsc: type-check AND emit JS (exit non-zero on any type error).
    const tsc = run(process.execPath, [tscPath, "-p", join(compileDir, "tsconfig.json")]);
    expect(tsc.status, `tsc stderr:\n${tsc.stderr}\n${tsc.stdout}`).toBe(0);

    // 4. Import the emitted harness and execute against the live server.
    const harnessUrl = pathToFileURL(join(compileDir, "dist", "harness.js")).href;
    const mod = await import(harnessUrl) as {
      singleOrder(baseUrl: string): Promise<{ data: { id: number; customerId: number; shippingAddressId: number } | null }>;
      diamond(baseUrl: string): Promise<{ id: string; data: unknown; code: number }[]>;
    };

    // 4a. Single typed call.
    const single = await mod.singleOrder(url);
    expect(single.data?.id).toBe(42);
    expect(single.data?.customerId).toBe(7);
    expect(single.data?.shippingAddressId).toBe(101);

    // 4b. Typed diamond batch — aliases resolve server-side. The server runs the
    // topological path whenever a request carries DependencyMapping (regardless of
    // the wire mode), so responses return in *topological* order, not request
    // order. Match by id to stay robust to that reordering.
    const responses = await mod.diamond(url);
    expect(responses.length).toBe(6);
    const byId = new Map(responses.map((r) => [r.id, r]));
    const order = byId.get("Order.GetById");
    const customer = byId.get("Customer.GetById");
    const lines = byId.get("OrderLine.GetByOrder");
    const article = byId.get("Article.GetByIds");
    const stock = byId.get("Stock.GetByArticles");
    const address = byId.get("Address.GetById");
    expect(order, "Order response present").toBeDefined();
    expect((order!.data as { id: number }).id).toBe(42);        // producer
    expect((customer!.data as { id: number }).id).toBe(7);       // via @customerId=7
    expect((lines!.data as unknown[]).length).toBe(3);           // via @orderId=42
    expect((article!.data as unknown[]).length).toBe(3);        // via @articleIds=[1001,1002,1003]
    expect((stock!.data as unknown[]).length).toBe(3);           // same @articleIds (diamond)
    expect((address!.data as { id: number }).id).toBe(101);     // via @addressId=101

    rmSync(compileDir, { recursive: true, force: true });
  }, { timeout: 60_000 });
});