// Generates the typed TS client (src/api/) from the committed Story-01 discovery
// fixture, using the same sleipnir-codegen core the CLI and DevUI use. This makes the
// codegen literally part of the UI's build pipeline — `npm run dev` / `npm run build`
// regenerate the client first, so the UI always consumes its own generated code.
//
// The fixture lives outside sleipnir-codegen's `files` set, so it is resolved by
// filesystem path (not via the package).
import { mkdirSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { emitTsClient, buildEmitterInput, NamingResolver, loadDiscovery } from "sleipnir-codegen/node";

const here = dirname(fileURLToPath(import.meta.url));
const fixturePath = resolve(here, "../../../../clients/codegen/test/fixtures/story01-discovery.json");
const outDir = resolve(here, "../src");

const discovery = await loadDiscovery(fixturePath);
const input = buildEmitterInput(discovery, new NamingResolver());
const tree = emitTsClient(input);

let count = 0;
for (const [path, content] of Object.entries(tree)) {
  const abs = join(outDir, path);
  mkdirSync(dirname(abs), { recursive: true });
  writeFileSync(abs, content, "utf8");
  count++;
}
console.log(`gen: wrote ${count} file${count === 1 ? "" : "s"} to ${outDir}`);