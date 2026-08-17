// One-shot: regenerate the Story-01 + Story-02 TS/JS snapshots for all
// transports. Run from clients/codegen with `node test/gen-snapshots.mjs`
// after `npm run build`. Writes LF-terminated files (the .gitattributes forces
// eol=lf for test/snapshots/** so byte-for-byte goldens stay stable on Windows).
//
// Story-01 is flat (scalar props only); the recursive path descent reproduces
// its one-level entries verbatim except for ordering: the `$[0]` subtree is
// emitted in full before the `$[*]` subtree (was: per-property `$[0].p`/`$[*].p`
// interleaved). Same keys + types. Story-02 exercises nested arrays/objects.
// CS/Python snapshots are NOT regenerated here (their emitters are unchanged).
import { writeFileSync, mkdirSync, readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { buildEmitterInput } from "../dist/core/model.js";
import { NamingResolver } from "../dist/core/naming.js";
import { emitTsClient } from "../dist/emitters/ts.js";
import { emitJsClient } from "../dist/emitters/js.js";

const here = dirname(fileURLToPath(import.meta.url));
const fixturesDir = join(here, "fixtures");
const snapRoot = join(here, "snapshots");

const stories = [
  { name: "story01", tsDir: "story01.ts", jsDir: "story01.js", wsTs: "story01.ws.ts", wsJs: "story01.ws.js", bothTs: "story01.both.ts", bothJs: "story01.both.js" },
  { name: "story02", tsDir: "story02.ts", jsDir: "story02.js", wsTs: "story02.ws.ts", wsJs: "story02.ws.js", bothTs: "story02.both.ts", bothJs: "story02.both.js" },
];

function writeTree(tree, dir) {
  for (const [rel, content] of Object.entries(tree)) {
    const abs = join(snapRoot, dir, rel);
    mkdirSync(dirname(abs), { recursive: true });
    writeFileSync(abs, content, "utf8");
  }
}

for (const s of stories) {
  const discovery = JSON.parse(readFileSync(join(fixturesDir, `${s.name}-discovery.json`), "utf8"));
  writeTree(emitTsClient(buildEmitterInput(discovery, new NamingResolver()), { transport: "rest" }), s.tsDir);
  writeTree(emitJsClient(buildEmitterInput(discovery, new NamingResolver()), { transport: "rest" }), s.jsDir);
  writeTree(emitTsClient(buildEmitterInput(discovery, new NamingResolver()), { transport: "ws" }), s.wsTs);
  writeTree(emitJsClient(buildEmitterInput(discovery, new NamingResolver()), { transport: "ws" }), s.wsJs);
  writeTree(emitTsClient(buildEmitterInput(discovery, new NamingResolver()), { transport: "both" }), s.bothTs);
  writeTree(emitJsClient(buildEmitterInput(discovery, new NamingResolver()), { transport: "both" }), s.bothJs);
  console.log(`wrote ${s.name} (rest/ws/both × ts/js)`);
}
console.log("done");