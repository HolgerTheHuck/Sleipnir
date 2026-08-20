// Regenerate the emitter golden snapshots for Story-01 (call-only diamond) and
// Story-02 (nested-array path descent) across the canonical bundle-capabilities
// (rest|ws|all|signalr) for TS + JS, plus the REST-only C# + Python for Story-01.
// Run after an intentional codegen/fixture change (requires `npm run build` first,
// since it imports the compiled dist):
//   node scripts/regen-snapshots.mjs
// The snapshots are byte-for-byte; the emitter tests pin them.
//
// Naming: `<story>.ts`/`.js` = default capability (`all`); `<story>.<cap>.ts`/`.js`
// = explicit capability. Obsolete `both`/`sse` dirs (pre-unification aliases) are
// removed — `both`→`all` (the default), `sse`→`rest`.
import { writeFileSync, mkdirSync, rmSync, readFileSync, existsSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { buildEmitterInput } from "../dist/core/model.js";
import { NamingResolver } from "../dist/core/naming.js";
import { emitTsClient } from "../dist/emitters/ts.js";
import { emitJsClient } from "../dist/emitters/js.js";
import { emitCsClient } from "../dist/emitters/cs.js";
import { emitPyClient } from "../dist/emitters/py.js";
import { assertDiscoveryShape } from "../dist/core/discovery.js";

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, "..");
const snapRoot = join(root, "test", "snapshots");

const CAPABILITIES = ["rest", "ws", "all", "signalr"];

function loadInput(fixture) {
  const discovery = assertDiscoveryShape(JSON.parse(readFileSync(join(root, "test", "fixtures", `${fixture}-discovery.json`), "utf8")));
  return buildEmitterInput(discovery, new NamingResolver());
}

function writeTree(dir, tree) {
  rmSync(dir, { recursive: true, force: true });
  mkdirSync(dir, { recursive: true });
  for (const [rel, content] of Object.entries(tree)) {
    const abs = join(dir, rel);
    mkdirSync(dirname(abs), { recursive: true });
    writeFileSync(abs, content, "utf8");
  }
}

// `all` is the default → written as the bare `<story>.ts`/`.js` (no capability suffix).
function suffixFor(cap) {
  return cap === "all" ? "" : `.${cap}`;
}

// Story-01 + Story-02 (TS + JS), per capability.
for (const fixture of ["story01", "story02"]) {
  const input = loadInput(fixture);
  for (const cap of CAPABILITIES) {
    const sfx = suffixFor(cap);
    writeTree(join(snapRoot, `${fixture}${sfx}.ts`), emitTsClient(input, { capability: cap }));
    writeTree(join(snapRoot, `${fixture}${sfx}.js`), emitJsClient(input, { capability: cap }));
  }
  console.log(`regenerated ${fixture} (ts + js × ${CAPABILITIES.length} capabilities)`);
}

// Story-01 C# + Python. C# now bundles the full SleipnirClient runtime
// (SleipnirTransportRouter; default capability `all`); Python stays REST-only.
const s1 = loadInput("story01");
writeTree(join(snapRoot, "story01.cs"), emitCsClient(s1));
writeTree(join(snapRoot, "story01.py"), emitPyClient(s1));
console.log("regenerated story01 (cs, py)");

// Remove obsolete pre-unification alias dirs (both→all, sse→rest).
for (const alias of ["both", "sse"]) {
  for (const fixture of ["story01", "story02", "story03"]) {
    for (const ext of ["ts", "js"]) {
      const dir = join(snapRoot, `${fixture}.${alias}.${ext}`);
      if (existsSync(dir)) {
        rmSync(dir, { recursive: true, force: true });
        console.log(`removed obsolete ${fixture}.${alias}.${ext}`);
      }
    }
  }
}
console.log("Snapshots regenerated for story01 + story02 (ts, js, cs, py).");