// Regenerate the Story-03 (server-push events) emitter golden snapshots across the
// canonical bundle-capabilities (rest|ws|all|signalr) for TS + JS. Run after an
// intentional codegen/fixture change affecting the event subscribe surface (requires
// `npm run build` first, since it imports the compiled dist — mirroring regen-snapshots.mjs):
//   node scripts/regen-story03.mjs
// The snapshots are byte-for-byte; story03-emitter.test.ts pins them.
//
// Naming: `story03.ts`/`.js` = default capability (`all`); `story03.<cap>.ts`/`.js`
// = explicit capability. Obsolete `both`/`sse` dirs are removed (both→all default, sse→rest).
import { writeFileSync, mkdirSync, rmSync, readFileSync, existsSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { buildEmitterInput } from "../dist/core/model.js";
import { NamingResolver } from "../dist/core/naming.js";
import { emitTsClient } from "../dist/emitters/ts.js";
import { emitJsClient } from "../dist/emitters/js.js";
import { assertDiscoveryShape } from "../dist/core/discovery.js";

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, "..");
const snapRoot = join(root, "test", "snapshots");

const discovery = assertDiscoveryShape(JSON.parse(readFileSync(join(root, "test", "fixtures", "story03-discovery.json"), "utf8")));
const input = buildEmitterInput(discovery, new NamingResolver());

const CAPABILITIES = ["rest", "ws", "all", "signalr"];

function writeTree(dir, tree) {
  rmSync(dir, { recursive: true, force: true });
  mkdirSync(dir, { recursive: true });
  for (const [rel, content] of Object.entries(tree)) {
    const abs = join(dir, rel);
    mkdirSync(dirname(abs), { recursive: true });
    writeFileSync(abs, content, "utf8");
  }
}

function suffixFor(cap) {
  return cap === "all" ? "" : `.${cap}`;
}

for (const cap of CAPABILITIES) {
  const sfx = suffixFor(cap);
  writeTree(join(snapRoot, `story03${sfx}.ts`), emitTsClient(input, { capability: cap }));
  writeTree(join(snapRoot, `story03${sfx}.js`), emitJsClient(input, { capability: cap }));
  console.log(`wrote story03${sfx}.ts + story03${sfx}.js (${cap})`);
}

// Remove obsolete pre-unification alias dirs (both→all default, sse→rest).
for (const alias of ["both", "sse"]) {
  for (const ext of ["ts", "js"]) {
    const dir = join(snapRoot, `story03.${alias}.${ext}`);
    if (existsSync(dir)) {
      rmSync(dir, { recursive: true, force: true });
      console.log(`removed obsolete story03.${alias}.${ext}`);
    }
  }
}