#!/usr/bin/env node
// sleipnir-gen — generate typed Sleipnir client stubs from discovery.
//
// Usage:
//   npx sleipnir-gen --lang ts --discovery <url|file|-> [--out <dir> | --stdout]
//                                       [--base-url <url>] [--bearer <token>]
//
// `--discovery` accepts a live URL (http(s)://…), a file path, or `-` (stdin).
// `--lang ts` emits TypeScript; `js` JSDoc-typed JS; `cs` a single C# file
// calling the SleipnirClient runtime; `py` a self-contained httpx async client.
// `--transport rest|ws|both` (ts|js only, default rest) picks which sleipnir-client
// runtime client the generated `SleipnirClient` wires up.
//
// Exit codes: 0 ok · 1 usage/runtime error · 2 discovery shape mismatch · 3 I/O.

import { writeFile, mkdir } from "node:fs/promises";
import { readFileSync } from "node:fs";
import { dirname, join, resolve, isAbsolute, normalize } from "node:path";
import { loadDiscovery, assertDiscoveryShape, DiscoveryShapeError } from "../core/discovery.js";
import { NamingResolver } from "../core/naming.js";
import { buildEmitterInput } from "../core/model.js";
import { emitTsClient } from "../emitters/ts.js";
import { emitJsClient } from "../emitters/js.js";
import { emitCsClient } from "../emitters/cs.js";
import { emitPyClient } from "../emitters/py.js";

interface ParsedArgs {
  lang: string | undefined;
  discovery: string | undefined;
  out: string | undefined;
  stdout: boolean;
  baseUrl: string | undefined;
  bearer: string | undefined;
  timeout: number | undefined;
  transport: "rest" | "ws" | "both" | undefined;
}

const SUPPORTED_LANGS = new Set(["ts", "js", "cs", "py"]);
const SUPPORTED_TRANSPORTS = new Set(["rest", "ws", "both"]);
/** Languages whose runtime only ships a REST client (no WS/SignalR to wire). */
const REST_ONLY_LANGS = new Set(["cs", "py"]);

function parseArgs(argv: string[]): ParsedArgs {
  const args: ParsedArgs = { lang: undefined, discovery: undefined, out: undefined, stdout: false, baseUrl: undefined, bearer: undefined, timeout: undefined, transport: undefined };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    const next = (): string => {
      if (i + 1 >= argv.length) failUsage(`option ${a} requires a value`);
      return argv[++i];
    };
    switch (a) {
      case "--lang": args.lang = next(); break;
      case "--discovery": case "-d": args.discovery = next(); break;
      case "--out": case "-o": args.out = next(); break;
      case "--stdout": args.stdout = true; break;
      case "--base-url": args.baseUrl = next(); break;
      case "--bearer": args.bearer = next(); break;
      case "--timeout": args.timeout = parseTimeout(next()); break;
      case "--transport": args.transport = parseTransport(next()); break;
      case "--help": case "-h": printHelp(); process.exit(0);
      case "--version": case "-v": printVersion(); process.exit(0);
      default:
        if (a.startsWith("--")) failUsage(`unknown option ${a}`);
        failUsage(`unexpected argument ${a}`);
    }
  }
  return args;
}

function parseTimeout(s: string): number {
  const n = Number(s);
  if (!Number.isFinite(n) || n <= 0) failUsage(`--timeout must be a positive number, got "${s}"`);
  return n;
}

function parseTransport(s: string): "rest" | "ws" | "both" {
  if (!SUPPORTED_TRANSPORTS.has(s)) failUsage(`--transport must be one of rest|ws|both, got "${s}"`);
  return s as "rest" | "ws" | "both";
}

async function main(): Promise<void> {
  const args = parseArgs(process.argv.slice(2));

  if (!args.lang) failUsage("--lang is required (ts | js | cs | py)");
  if (!SUPPORTED_LANGS.has(args.lang)) {
    stderr(`sleipnir-gen: error: unsupported --lang "${args.lang}" (expected ts | js | cs | py).`);
    process.exit(1);
  }
  const transport = args.transport ?? "rest";
  if (transport !== "rest" && REST_ONLY_LANGS.has(args.lang)) {
    stderr(`sleipnir-gen: error: --transport "${transport}" is only supported for ts|js (the ${args.lang} runtime ships REST only).`);
    process.exit(1);
  }
  if (!args.discovery) failUsage("--discovery is required (url | file | -)");
  if (!args.stdout && !args.out) failUsage("either --out <dir> or --stdout is required");
  if (args.stdout && args.out) failUsage("--stdout and --out are mutually exclusive");

  let discoveryRaw: import("sleipnir-client").DiscoveryInfo;
  try {
    discoveryRaw = await loadDiscovery(args.discovery, { bearer: args.bearer, timeout: args.timeout });
  } catch (err) {
    if (err instanceof DiscoveryShapeError) {
      stderr(`sleipnir-gen: error: discovery shape: ${err.message}`);
      process.exit(2);
    }
    stderr(`sleipnir-gen: error: failed to load discovery: ${(err as Error).message}`);
    process.exit(3);
  }

  // loadDiscovery already validates shape; re-assert keeps the CLI's exit-2
  // contract explicit if the validation surface moves later.
  try {
    assertDiscoveryShape(discoveryRaw);
  } catch (err) {
    stderr(`sleipnir-gen: error: discovery shape: ${(err as Error).message}`);
    process.exit(2);
  }

  const resolver = new NamingResolver();
  const input = buildEmitterInput(discoveryRaw, resolver);
  const files = emitForLang(args.lang, input, args.baseUrl, transport);

  if (args.stdout) {
    for (const [path, content] of Object.entries(files)) {
      process.stdout.write(`// ===== file: ${path} =====\n${content}`);
    }
    return;
  }

  const outDir = resolve(args.out!);
  for (const [path, content] of Object.entries(files)) {
    const dest = safeJoin(outDir, path);
    await mkdir(dirname(dest), { recursive: true });
    await writeFile(dest, content, "utf8");
  }
  const count = Object.keys(files).length;
  process.stdout.write(`sleipnir-gen: wrote ${count} file${count === 1 ? "" : "s"} to ${outDir}\n`);
}

/** Dispatch the emitter for a language, threading the base-url hint + transport. */
function emitForLang(lang: string, input: import("../core/model.js").EmitterInput, baseUrl: string | undefined, transport: "rest" | "ws" | "both"): Record<string, string> {
  switch (lang) {
    case "ts": return emitTsClient(input, { baseUrl, transport });
    case "js": return emitJsClient(input, { baseUrl, transport });
    case "cs": return emitCsClient(input, { baseUrl });
    case "py": return emitPyClient(input, { baseUrl });
    default: throw new Error(`unsupported lang ${lang}`);
  }
}

/** Join an absolute base dir with a relative file path, refusing to escape. */
function safeJoin(base: string, rel: string): string {
  const normalized = normalize(rel);
  if (isAbsolute(normalized) || normalized.startsWith("..")) {
    throw new Error(`refusing to write outside output dir: ${rel}`);
  }
  return join(base, normalized);
}

function failUsage(message: string): never {
  stderr(`sleipnir-gen: error: ${message}`);
  printHelp();
  process.exit(1);
}

function stderr(line: string): void {
  process.stderr.write(line + "\n");
}

function printHelp(): void {
  process.stdout.write(
    `sleipnir-gen — generate typed Sleipnir client stubs from discovery.\n` +
    `\n` +
    `Usage:\n` +
    `  sleipnir-gen --lang <ts|js|cs|py> --discovery <url|file|-> [--out <dir> | --stdout]\n` +
    `           [--base-url <url>] [--transport <rest|ws|both>] [--bearer <token>] [--timeout <ms>]\n` +
    `\n` +
    `Options:\n` +
    `  --lang <ts|js|cs|py>  Output language.\n` +
    `  --discovery <src>    Discovery source: http(s) URL, file path, or - for stdin.\n` +
    `  --out <dir>          Write the client tree to this directory.\n` +
    `  --stdout             Concatenate all files to stdout (with file banners).\n` +
    `  --base-url <url>     Rendered into the client header comment (hint only).\n` +
    `  --transport <rest|ws|both>  Transport the generated client wires (ts|js only; default rest).\n` +
    `  --bearer <token>    Authorization bearer for URL discovery.\n` +
    `  --timeout <ms>       Request timeout for URL discovery.\n` +
    `  -h, --help           Show this help.\n` +
    `  -v, --version        Show the version.\n`,
  );
}

function printVersion(): void {
  // Read the version from the adjacent package.json so the CLI never drifts
  // from the published package version.
  try {
    const pkgUrl = new URL("../../package.json", import.meta.url);
    const version = JSON.parse(readFileSync(pkgUrl, "utf8")).version;
    process.stdout.write(`sleipnir-gen ${version}\n`);
  } catch {
    process.stdout.write("sleipnir-gen\n");
  }
}

main().catch((err) => {
  stderr(`sleipnir-gen: error: ${(err as Error).message}`);
  process.exit(1);
});