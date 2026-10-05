// U3 — TS emitter correctness: enum identity (wire stays numeric) and event
// subscribe options (`signal` ends the subscription).
//
// Enums: `export const X = { … } as const;` + `export type X = (typeof X)[keyof typeof X];`
// — no TS `enum`. Use sites (DTO properties, parameters, return types, event
// payloads, path records) reference the type instead of `number`. The JS / C# /
// Python emitters are deliberately unchanged and keep the numeric scalar.
import { describe, it, expect } from "vitest";
import { mkdirSync, writeFileSync, rmSync } from "node:fs";
import { fileURLToPath, pathToFileURL } from "node:url";
import { dirname, join } from "node:path";
import { spawnSync } from "node:child_process";
import { createRequire } from "node:module";
import ts from "typescript";
import type { DiscoveryInfo } from "sleipnir-client";
import { buildEmitterInput } from "../../src/core/model.js";
import { NamingResolver } from "../../src/core/naming.js";
import { emitTsClient } from "../../src/emitters/ts.js";
import { emitJsClient } from "../../src/emitters/js.js";
import { emitCsClient } from "../../src/emitters/cs.js";
import { emitPyClient } from "../../src/emitters/py.js";
import { readFixture } from "./fixture.js";

const here = dirname(fileURLToPath(import.meta.url));
const pkgRoot = join(here, "..", "..");
const require = createRequire(import.meta.url);
const tscPath = require.resolve("typescript/bin/tsc");

function emitEnums(): Record<string, string> {
  return emitTsClient(buildEmitterInput(readFixture("enums"), new NamingResolver()));
}

describe("emitTsClient — enum identity", () => {
  it("declares each enum as an `as const` object plus a same-named literal-union type", () => {
    const types = emitEnums()["api/types.ts"];
    expect(types).toContain(
      "export const OrderState = {\n  Open: 0,\n  Shipped: 1,\n  Cancelled: -1,\n} as const;\n" +
        "export type OrderState = (typeof OrderState)[keyof typeof OrderState];",
    );
    expect(types).toContain(
      "export const Priority = {\n  Low: 0,\n  High: 10,\n} as const;\n" +
        "export type Priority = (typeof Priority)[keyof typeof Priority];",
    );
    // No TS `enum` keyword.
    expect(types).not.toMatch(/\benum\s+\w+\s*\{/);
  });

  it("types DTO properties with the enum type (presence and arrays honored)", () => {
    const types = emitEnums()["api/types.ts"];
    // Presence rule: non-nullable → required; nullable → `?: T | null`.
    expect(types).toContain("  state: OrderState;");
    expect(types).toContain("  previousState?: OrderState | null;");
    expect(types).toContain("  history: OrderState[];");
    expect(types).toContain("  priority: Priority;");
  });

  it("states the presence rule (derived from nullability, not a separate wire fact)", () => {
    const types = emitEnums()["api/types.ts"];
    expect(types).toContain("presence-aware");
    expect(types).toContain("required (always present on\n// the wire)");
    expect(types).toContain("presence-optional");
    expect(types).toContain("WhenWritingNull");
    expect(types).not.toContain("discovery carries no nullability");
    expect(types).not.toContain("discovery carries no per-property requiredness");
  });

  it("types parameters, return types, event payloads and path records with the enum type", () => {
    const tree = emitEnums();
    const ctrl = tree["api/controllers.ts"];
    expect(ctrl).toContain("getState(id: number): TypedCall<OrderState, OrderStatePaths>");
    expect(ctrl).toContain("getStates(): TypedCall<OrderState[], OrderStateArrayPaths>");
    expect(ctrl).toContain("findByState(state: OrderState): TypedCall<Order[], OrderArrayPaths>");
    expect(ctrl).toContain("setState(id: number, state: OrderState, priority: Priority | null): TypedCall<void, _VoidPaths>");
    expect(ctrl).toContain(
      "stateChanged(orderId: number, handlers: SubscribeHandlers<OrderState>, options?: SleipnirSubscribeOptions): Promise<SleipnirSubscription>",
    );
    expect(ctrl).toContain('import type { Order, OrderState, Priority } from "./types.js";');

    const typed = tree["api/typed-call.ts"];
    expect(typed).toContain('import type { Order, OrderState, Priority } from "./types.js";');
    expect(typed).toContain('export interface OrderStatePaths {\n  "$": OrderState;\n}');
    expect(typed).toContain(
      'export interface OrderStateArrayPaths {\n  "$": OrderState[];\n  "$[0]": OrderState;\n  "$[*]": OrderState[];\n}',
    );
    expect(typed).toContain('  "$.state": OrderState;');
    expect(typed).toContain('  "$.history[*]": OrderState[];');
    expect(typed).toContain('  "$[*].state": OrderState[];');
  });

  it("leaves the JS / C# / Python emitters on the numeric wire scalar (decision 6.5)", () => {
    const input = buildEmitterInput(readFixture("enums"), new NamingResolver());
    const js = Object.values(emitJsClient(input)).join("\n");
    const cs = Object.values(emitCsClient(input)).join("\n");
    const py = Object.values(emitPyClient(input)).join("\n");
    for (const out of [js, cs, py]) {
      expect(out).not.toContain("OrderState");
      expect(out).not.toContain("Priority =");
    }
    expect(js).toContain("@property {number} state");
  });

  it("falls back to `number` for an enum whose member values are not all numeric", () => {
    const discovery = {
      discoveryVersion: "1",
      controllers: [
        {
          name: "Flag",
          methods: [
            { methodName: "Get", returnType: { kind: "ref", ref: "X.Odd" }, parameters: [] },
          ],
        },
      ],
      types: {
        "X.Odd": { kind: "enum", typeName: "X.Odd", properties: [], members: [{ name: "A", value: "a" }] },
      },
    } as unknown as DiscoveryInfo;
    const input = buildEmitterInput(discovery, new NamingResolver());
    expect(input.enums).toEqual([]);
    const tree = emitTsClient(input);
    expect(tree["api/types.ts"]).not.toContain("Odd");
    expect(tree["api/controllers.ts"]).toContain("get(): TypedCall<number, _NumberPaths>");
  });

  it("quotes member names that are not plain identifiers and disambiguates enum/object name collisions", () => {
    const discovery = {
      discoveryVersion: "1",
      controllers: [
        {
          name: "Mix",
          methods: [
            { methodName: "A", returnType: { kind: "ref", ref: "A.Status" }, parameters: [] },
            { methodName: "B", returnType: { kind: "ref", ref: "B.Status" }, parameters: [] },
          ],
        },
      ],
      types: {
        "A.Status": { kind: "object", typeName: "A.Status", properties: [{ propertyName: "Code", propertyType: { kind: "ref", ref: "B.Status" } }] },
        "B.Status": { kind: "enum", typeName: "B.Status", properties: [], members: [{ name: "Ok", value: 0 }, { name: "Not-Ok", value: 1 }] },
      },
    } as unknown as DiscoveryInfo;
    const tree = emitTsClient(buildEmitterInput(discovery, new NamingResolver()));
    const types = tree["api/types.ts"];
    expect(types).toContain('export const BStatus = {\n  Ok: 0,\n  "Not-Ok": 1,\n} as const;');
    expect(types).toContain("export interface AStatus {\n  code: BStatus;\n}");
    expect(tree["api/controllers.ts"]).toContain("a(): TypedCall<AStatus, AStatusPaths>");
    expect(tree["api/controllers.ts"]).toContain("b(): TypedCall<BStatus, BStatusPaths>");
  });
});

describe("emitTsClient — scalar path records", () => {
  it("maps a scalar without its own path record (`any`) to _UnknownPaths, not an undeclared _AnyPaths", () => {
    const discovery = {
      discoveryVersion: "1",
      controllers: [
        {
          name: "Misc",
          methods: [
            { methodName: "Raw", returnType: { kind: "scalar", name: "any" }, parameters: [] },
            { methodName: "Raws", returnType: { kind: "array", element: { kind: "scalar", name: "any" } }, parameters: [] },
          ],
        },
      ],
      types: {},
    } as unknown as DiscoveryInfo;
    const tree = emitTsClient(buildEmitterInput(discovery, new NamingResolver()));
    const ctrl = tree["api/controllers.ts"];
    expect(ctrl).toContain("raw(): TypedCall<any, _UnknownPaths>");
    expect(ctrl).toContain("raws(): TypedCall<any[], _UnknownArrayPaths>");
    expect(ctrl).not.toContain("_AnyPaths");
    expect(tree["api/typed-call.ts"]).toContain("export interface _UnknownArrayPaths");
  });
});

describe("emitTsClient — event subscribe options", () => {
  it("client.ts passes options through to the router and bridges `signal` to unsubscribe()", () => {
    const client = emitEnums()["api/client.ts"];
    expect(client).toContain("const sub = await this._router.subscribe<T>(req, handlers, options);");
    expect(client).toContain('signal.addEventListener("abort", end, { once: true });');
    expect(client).toContain("SleipnirSubscribeOptions");
  });

  it("aborting `signal` ends an active subscription (runtime, against a fake router)", async () => {
    // Transpile the generated tree to JS under the package root (so `sleipnir-client`
    // resolves from node_modules) and drive the generated client with a fake router.
    const dir = join(pkgRoot, ".subscribe-runtime");
    rmSync(dir, { recursive: true, force: true });
    mkdirSync(join(dir, "api"), { recursive: true });
    try {
      for (const [path, content] of Object.entries(emitEnums())) {
        const js = ts.transpileModule(content, {
          compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 },
        }).outputText;
        writeFileSync(join(dir, path.replace(/\.ts$/, ".js")), js, "utf8");
      }
      const mod = await import(pathToFileURL(join(dir, "api", "client.js")).href);

      const calls: { req: unknown; options: unknown }[] = [];
      let unsubscribed = 0;
      const fakeRouter = {
        subscribe: async (req: unknown, _handlers: unknown, options: unknown) => {
          calls.push({ req, options });
          return { subscriptionId: "s1", lastEventId: 0, unsubscribe: async () => { unsubscribed++; } };
        },
      };
      const client = new mod.SleipnirClient("http://localhost:1");
      (client as { _router: { dispose(): void } })._router.dispose();
      (client as { _router: unknown })._router = fakeRouter;

      const ac = new AbortController();
      const resumePolicy = () => null;
      const sub = await client.order.stateChanged(7, { onNext: () => {} }, { signal: ac.signal, resumePolicy, timeout: 500 });
      expect(sub.subscriptionId).toBe("s1");
      // Options are passed through verbatim; the request carries the named param.
      expect(calls[0].options).toEqual({ signal: ac.signal, resumePolicy, timeout: 500 });
      expect(calls[0].req).toMatchObject({ controller: "Order", method: "StateChanged" });
      expect(unsubscribed).toBe(0);

      ac.abort();
      expect(unsubscribed).toBe(1);
      ac.abort(); // a second abort is a no-op (`once` listener)
      expect(unsubscribed).toBe(1);

      // Without options: nothing to wire, still subscribes.
      await client.order.stateChanged(8, { onNext: () => {} });
      expect(calls[1].options).toBeUndefined();
    } finally {
      rmSync(dir, { recursive: true, force: true });
    }
  });
});

describe("generated TS (enums fixture) compiles under strict + isolatedModules + verbatimModuleSyntax", () => {
  it("tsc exits 0 against the enum + subscribe-options harness", () => {
    const dir = join(pkgRoot, ".tsc-compile", "enums");
    rmSync(dir, { recursive: true, force: true });
    mkdirSync(join(dir, "api"), { recursive: true });
    for (const [path, content] of Object.entries(emitEnums())) {
      writeFileSync(join(dir, path), content, "utf8");
    }
    writeFileSync(
      join(dir, "harness.ts"),
      `import { SleipnirClient, Batch, OrderState, Priority } from "./api/index.js";
import type { Order } from "./api/index.js";

export async function run(): Promise<void> {
  const client = new SleipnirClient("http://localhost:5001");

  // Enum values by name; the type is the union of the numeric member values.
  const open: OrderState = OrderState.Open;
  const states: OrderState[] = [OrderState.Shipped, OrderState.Cancelled, 0, 1, -1];
  void states;
  await client.call(client.order.setState(1, open, Priority.High));
  await client.call(client.order.setState(1, OrderState.Shipped, null));
  // @ts-expect-error — 5 is not an OrderState member value.
  client.order.setState(1, 5, null);
  // @ts-expect-error — Priority.High (10) is not an OrderState.
  client.order.setState(1, Priority.High, null);

  const res = await client.call(client.order.getById(1));
  const order: Order | null = res.data;
  const s: OrderState | undefined = order?.state;
  const prev: OrderState | null | undefined = order?.previousState;
  void s; void prev;

  // Enum-typed aliases bind to enum-typed parameters in a batch.
  const batch = new Batch();
  const byId = batch.add(client.order.getById(1)).exposes("$.state", "@state");
  batch.add(client.order.findByState(byId.alias("@state")));
  const st = batch.add(client.order.getState(1)).exposes("$", "@s");
  batch.add(client.order.findByState(st.alias("@s")));
  await client.batch(batch);

  // Event options: signal / resumePolicy / timeout.
  const ac = new AbortController();
  const sub = await client.order.stateChanged(1, { onNext: (v: OrderState) => { void v; } }, {
    signal: ac.signal,
    resumePolicy: () => null,
    timeout: 1000,
  });
  void sub;
  ac.abort();
  await client.order.stateChanged(1, { onNext: () => {} });
  client.dispose();
}
`,
      "utf8",
    );
    writeFileSync(
      join(dir, "tsconfig.json"),
      JSON.stringify({
        compilerOptions: {
          target: "ES2022",
          module: "NodeNext",
          moduleResolution: "NodeNext",
          lib: ["ES2022", "DOM", "DOM.Iterable"],
          strict: true,
          isolatedModules: true,
          verbatimModuleSyntax: true,
          noEmit: true,
          skipLibCheck: true,
          types: ["node"],
        },
        include: ["api/**/*.ts", "harness.ts"],
      }),
      "utf8",
    );
    const r = spawnSync(process.execPath, [tscPath, "--noEmit", "-p", join(dir, "tsconfig.json")], {
      encoding: "utf8",
      cwd: pkgRoot,
    });
    if (r.status !== 0) console.error("tsc (enums):\n" + r.stdout + r.stderr);
    expect(r.status).toBe(0);
  }, { timeout: 60_000 });
});
