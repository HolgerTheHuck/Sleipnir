// Regression gate: the generated Python client (types.py / client.py /
// __init__.py) must byte-compile, AND the typed batch must build the Story-01
// diamond (producer exposes camelCase paths, consumer resolves the alias via
// BatchEntry.alias). Spawns `python -m py_compile` (byte-compile only — does
// NOT execute imports, so it passes without httpx installed). Skipped when
// neither `python` nor `python3` is on PATH.
import { describe, it, expect } from "vitest";
import { mkdirSync, writeFileSync, rmSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { spawnSync } from "node:child_process";
import { buildEmitterInput } from "../../src/core/model.js";
import { NamingResolver } from "../../src/core/naming.js";
import { emitPyClient } from "../../src/emitters/py.js";
import { readFixture } from "./fixture.js";

const here = dirname(fileURLToPath(import.meta.url));
const pkgRoot = join(here, "..", "..");
const compileDir = join(pkgRoot, ".py-compile");

const shell = process.platform === "win32";

/** Probe `python` then `python3`; return the first that responds to --version. */
function findPython(): string | null {
  for (const bin of ["python", "python3"]) {
    const r = spawnSync(bin, ["--version"], { encoding: "utf8", shell });
    if (r.status === 0) return bin;
  }
  return null;
}

const python = findPython();
const testFn = python ? it : it.skip;

// A harness that builds the Story-01 diamond via the GENERATED Python client +
// Batch (byte-compiled only; never executed). Exercises:
//  - single typed call_typed(call, cls)
//  - BatchEntry.alias() feeding a consumer param (the "@x" placeholder string)
//  - array alias (lines.alias("@articleIds") → get_by_articles param)
// py_compile does not execute imports, so `from types import Order` etc. compile
// without resolving (running the client needs `pip install httpx`).
const harness = `from __future__ import annotations

from types import Order, Customer, OrderLine, Article, Address, StockInfo
from client import SleipnirClient, Batch, SleipnirCall


async def main() -> None:
    client = SleipnirClient("http://localhost:5001")

    # Single typed call: call_typed deserializes data into the dataclass.
    order = await client.call_typed(client.order.get_by_id(42), Order)

    # Typed diamond batch (Serial — required for @alias resolution).
    batch = Batch()
    o = (
        batch.add(client.order.get_by_id(42))
        .exposes("$.customerId", "@customerId")
        .exposes("$.id", "@orderId")
        .exposes("$.shippingAddressId", "@addressId")
    )
    batch.add(client.customer.get_by_id(o.alias("@customerId")))
    lines = (
        batch.add(client.order_line.get_by_order(o.alias("@orderId")))
        .exposes("$[*].articleId", "@articleIds")
    )
    batch.add(client.article.get_many(lines.alias("@articleIds")))
    batch.add(client.stock.get_by_articles(lines.alias("@articleIds")))
    batch.add(client.address.get_by_id(o.alias("@addressId")))

    responses = await client.call_batch(batch)
    # Responses return in topological order; fetch by id, not by position.
    by_id = {r.get("id"): r for r in responses if r.get("id")}
    _order_resp = by_id.get("Order.GetById")
    _customer_resp = by_id.get("Customer.GetById")
`;

describe(python ? "generated Python byte-compiles + typed diamond builds (py_compile)"
  : "generated Python byte-compiles (skipped: python not on PATH)", () => {
  testFn("python -m py_compile exits 0 against the diamond harness", () => {
    rmSync(compileDir, { recursive: true, force: true });
    mkdirSync(compileDir, { recursive: true });

    const tree = emitPyClient(buildEmitterInput(readFixture(), new NamingResolver()));
    for (const [path, content] of Object.entries(tree)) {
      writeFileSync(join(compileDir, path), content, "utf8");
    }
    writeFileSync(join(compileDir, "harness.py"), harness, "utf8");

    const r = spawnSync(python!, ["-m", "py_compile", "types.py", "client.py", "__init__.py", "harness.py"], {
      encoding: "utf8",
      cwd: compileDir,
      shell,
      timeout: 60_000,
    });

    if ((r.status ?? 1) !== 0) {
      console.error("py_compile stdout:\n" + r.stdout);
      console.error("py_compile stderr:\n" + r.stderr);
    }
    expect(r.status, `py_compile failed:\n${r.stdout}\n${r.stderr}`).toBe(0);

    rmSync(compileDir, { recursive: true, force: true });
  }, { timeout: 90_000 });
});