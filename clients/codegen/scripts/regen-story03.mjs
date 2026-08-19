// One-off: regenerate the Story-03 (server-push events) emitter golden snapshots
// across rest/sse/ws/both for TS + JS. Run after an intentional codegen/fixture change
// affecting the event subscribe surface (requires `npm run build` first, since it
// imports the compiled dist — mirroring regen-snapshots.mjs):
//   node scripts/regen-story03.mjs
// The snapshots are byte-for-byte; story03-emitter.test.ts pins them.
// `sse` is REST calls + SSE events; without event methods it is identical to `rest`
// (no SSE client wired), so its snapshot diverges from `rest` only in client.ts.
import { writeFileSync, mkdirSync, rmSync, readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { buildEmitterInput } from "../dist/core/model.js";
import { NamingResolver } from "../dist/core/naming.js";
import { emitTsClient } from "../dist/emitters/ts.js";
import { emitJsClient } from "../dist/emitters/js.js";
import { assertDiscoveryShape } from "../dist/core/discovery.js";

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, "..");
const fixturePath = join(root, "test", "fixtures", "story03-discovery.json");
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

const transports = ["rest", "sse", "ws", "both"];
for (const t of transports) {
  const suffix = t === "rest" ? "" : `.${t}`;
  writeTree(join(snapRoot, `story03${suffix}.ts`), emitTsClient(input, { transport: t }));
  writeTree(join(snapRoot, `story03${suffix}.js`), emitJsClient(input, { transport: t }));
  console.log(`wrote story03${suffix}.ts + story03${suffix}.js (${t})`);
}