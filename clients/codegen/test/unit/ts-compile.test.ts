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

// Story-02 compile gate: the nested-array path descent must make a chain that
// extracts from a NESTED array (`$.hits[*].articleId`) type-check end-to-end —
// the exact case that was untypable in 1.2.0 and forced a raw-SleipnirCall
// fallback. Also asserts the `keyof TPaths` constraint still rejects unknown
// paths and a cardinality mismatch (number fed to number[]).
describe("generated TS story02 compiles + nested-array chain type-checks", () => {
  const story02Harness = `// Story-02: typed dependency chain extracting from a nested array.
import { SleipnirClient } from "./api/client.js";
import { Batch } from "./api/typed-call.js";

export async function semanticChain(): Promise<void> {
  const client = new SleipnirClient("http://localhost:5001");
  const batch = new Batch();

  // Producer: extract articleIds from a NESTED array ($.hits[*].articleId).
  // This is the chain that was untypable in 1.2.0 — now it type-checks.
  const search = batch.add(client.search.semanticSearch("q"))
    .exposes("$.hits[*].articleId", "@ids");

  // Consumer: getByIds(articleIds: number[]) accepts the typed number[] alias.
  batch.add(client.article.getByIds(search.alias("@ids")));

  // A nested-object-under-array extraction also type-checks (string[]).
  const named = batch.add(client.search.semanticSearch("q2"))
    .exposes("$.hits[*].author.name", "@authorNames");
  void named;

  await client.batch(batch);

  // --- Compile-time guarantees (must error without the suppression) ---
  // @ts-expect-error — $.hits[*].nope is not a key of SearchResultPaths.
  search.exposes("$.hits[*].nope", "@badPath");
  const total = batch.add(client.search.semanticSearch("q3")).exposes("$.total", "@total");
  // @ts-expect-error — $.total is number, but getByIds expects number[].
  batch.add(client.article.getByIds(total.alias("@total")));
}
`;

  it("tsc exits 0 against the story02 nested-array harness", () => {
    const dir = join(compileDir, "story02-rest");
    rmSync(dir, { recursive: true, force: true });
    mkdirSync(join(dir, "api"), { recursive: true });
    const tree = emitTsClient(buildEmitterInput(readFixture("story02"), new NamingResolver()), { transport: "rest" });
    for (const [path, content] of Object.entries(tree)) {
      const abs = join(dir, path);
      mkdirSync(dirname(abs), { recursive: true });
      writeFileSync(abs, content, "utf8");
    }
    writeFileSync(join(dir, "harness.ts"), story02Harness, "utf8");
    writeFileSync(join(dir, "tsconfig.json"), tsconfig, "utf8");
    const result = runTsc(dir);
    if (result.status !== 0) {
      console.error("tsc stdout (story02):\n" + result.stdout);
      console.error("tsc stderr (story02):\n" + result.stderr);
    }
    expect(result.status).toBe(0);
  }, { timeout: 60_000 });
});

// Story-03 compile gate: the generated typed subscribe surface must type-check —
// `messageReceived(chatId, handlers)` returns Promise<SleipnirSubscription> with
// onNext typed to the event payload (Message for the object event, number for the
// scalar event), the mixed controller's call method stays a TypedCall, and the
// `@ts-expect-error` guards inside subscribeBody prove the handler payload is
// enforced. Runs for all transports: ws/both delegate to the WS runtime; rest has
// the throwing _subscribe but the SAME typed signature, so it must still compile.
const subscribeBody = `  // Object-payload event: onNext typed to Message; resolves to a subscription.
  const sub: SleipnirSubscription = await client.chat.messageReceived(1, {
    onNext: (m: Message) => { void m; },
    onComplete: () => {},
    onError: (err: Error) => { void err; },
  });
  await sub.unsubscribe();

  // Scalar-payload event: onNext typed to number.
  const ticks: SleipnirSubscription = await client.ticker.ticks({
    onNext: (n: number) => { void n; },
  });
  await ticks.unsubscribe();

  // A call method on the same (mixed) controller still type-checks as a TypedCall.
  void client.chat.getHistory(1);

  // --- Compile-time guarantees (must error without the suppression) ---
  // @ts-expect-error — onNext payload is Message, not number.
  client.chat.messageReceived(1, { onNext: (n: number) => { void n; } });
  // @ts-expect-error — onNext is a required handler.
  client.ticker.ticks({});
`;

function subscribeHarnessFor(t: Transport): string {
  const ctor =
    t === "ws" ? `  const client = new SleipnirClient("ws://localhost:5001/sleipnirws");`
    : t === "both" ? `  const client = new SleipnirClient("http://localhost:5001", { rest: {}, ws: {} });`
    : `  const client = new SleipnirClient("http://localhost:5001");`;
  return `// Story-03: typed subscribe surface (transport: ${t}).
import { SleipnirClient } from "./api/client.js";
import type { SleipnirSubscription } from "sleipnir-client";
import type { Message } from "./api/types.js";

export async function subscribe(): Promise<void> {
${ctor}
${subscribeBody}}
`;
}

describe.each<Transport>(["rest", "ws", "both"])("generated TS story03 compiles + typed subscribe type-checks (transport: %s)", (t) => {
  it("tsc exits 0 against the story03 subscribe harness", () => {
    const dir = join(compileDir, `story03-${t}`);
    rmSync(dir, { recursive: true, force: true });
    mkdirSync(join(dir, "api"), { recursive: true });
    const tree = emitTsClient(buildEmitterInput(readFixture("story03"), new NamingResolver()), { transport: t });
    for (const [path, content] of Object.entries(tree)) {
      const abs = join(dir, path);
      mkdirSync(dirname(abs), { recursive: true });
      writeFileSync(abs, content, "utf8");
    }
    writeFileSync(join(dir, "harness.ts"), subscribeHarnessFor(t), "utf8");
    writeFileSync(join(dir, "tsconfig.json"), tsconfig, "utf8");
    const result = runTsc(dir);
    if (result.status !== 0) {
      console.error(`tsc stdout (story03 ${t}):\n` + result.stdout);
      console.error(`tsc stderr (story03 ${t}):\n` + result.stderr);
    }
    expect(result.status).toBe(0);
  }, { timeout: 60_000 });
});

// keep existsSync referenced for the guard in case the dir lingers.
void existsSync;