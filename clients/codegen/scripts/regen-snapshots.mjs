// One-off: regenerate the four Story-01 emitter golden snapshots from the
// committed fixture. Run after an intentional codegen/fixture change:
//   node scripts/regen-snapshots.mjs
// The snapshots are byte-for-byte; the emitter tests then pin them.
import { writeFileSync, mkdirSync, rmSync, readFileSync } from "node:fs";
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
const fixturePath = join(root, "test", "fixtures", "story01-discovery.json");
const snapRoot = join(root, "test", "snapshots");

const discovery = assertDiscoveryShape(JSON.parse(readFileSync(fixturePath, "utf8")));
const input = buildEmitterInput(discovery, new NamingResolver());

function writeTree(dir, tree) {
  rmSync(dir, { recursive: true, force: true });
  mkdirSync(dir, { recursive: true });
  for (const [rel, content] of Object.entries(tree)) {
    const abs = join(dir, rel);
    mkdirSync(dirname(abs), { recursive: true });
    writeFileSync(abs, content, "utf8");
  }
}

import { dirname as _dn } from "node:path";
writeTree(join(snapRoot, "story01.ts"), emitTsClient(input));
writeTree(join(snapRoot, "story01.js"), emitJsClient(input));
writeTree(join(snapRoot, "story01.cs"), emitCsClient(input));
writeTree(join(snapRoot, "story01.py"), emitPyClient(input));
console.log("Snapshots regenerated for story01 (ts, js, cs, py).");