# sleipnir-codegen

Typed client stub generator for [Sleipnir](../../README.md) — the code-first,
multi-transport RPC framework for .NET 8+. It turns a Sleipnir **discovery
payload** (`GET /api/sleipnir/discovery`) into typed client stubs in
**TypeScript**, **JavaScript**, **C#**, and **Python**, so a consumer calls
`client.order.getById(42)` instead of hand-writing `SleipnirCall.init(...)`.

The discovery payload *is* the contract — there is no `.proto` / IDL. The C#
classes decorated with `[SleipnirController]` / `[SleipnirMethod]` on the server
are reflected at runtime into the discovery JSON, and this generator turns
that JSON back into a typed surface on the client.

## Install

```bash
# CLI (global tool, optional — `npx` works too)
npm i -g sleipnir-codegen
# or ad-hoc:
npx sleipnir-gen --lang ts --discovery https://localhost:5001/api/sleipnir/discovery --out src/api

# Programmatic (build tool, DevUI, custom emitter)
npm i -D sleipnir-codegen
```

The generator depends on [`sleipnir-client`](https://www.npmjs.com/package/sleipnir-client)
for the TS/JS runtime — the generated `SleipnirClient` wires up a
`SleipnirTransportRouter` that bundles the backends selected by `--transport`.
The `cs` emitter targets the `SleipnirClient` .NET runtime (same router); `py`
is self-contained (httpx, REST only).

## CLI

```bash
sleipnir-gen --lang <ts|js|cs|py> --discovery <url|file|-> [--out <dir> | --stdout]
             [--base-url <url>] [--bearer <token>] [--timeout <s>]
             [--transport rest|ws|all|signalr] [--selfcheck]
```

- `--lang` (required): `ts` | `js` | `cs` | `py`.
- `--discovery` / `-d` (required): a live `http(s)://…` URL, a file path, or
  `-` for stdin.
- `--out` / `-o`: output directory (one file per generated module, plus a
  `types` module for TS/CS/PY). Mutually exclusive with `--stdout`.
- `--stdout`: write the generated source to stdout (single concatenated
  stream) instead of files.
- `--base-url`: base URL hint rendered into the generated client preamble
  (the generated client still takes a base URL at construction; this is a
  design-time default baked into the header).
- `--bearer`: bearer token used **only** to fetch `--discovery` over HTTP
  (never written into the generated code).
- `--timeout`: HTTP timeout (seconds) for fetching `--discovery` from a URL.
- `--transport rest|ws|all|signalr` (ts|js|cs; default `all`): a **bundle-capability**
  selector — which backends the generated `SleipnirClient` *bundles*. The public
  client surface (method signatures, options type) is **byte-identical across all
  values**; transport is chosen **at runtime** via `SleipnirTransportRouter`
  (`auto` default probes WebSocket → falls back to REST+SSE on failure;
  `useTransport()` switches explicitly). `rest` = REST calls + SSE events
  (HTTP-only, proxy-safe); `ws` = WebSocket calls + events; `all` = REST + WS + SSE
  (enables `auto` fallback); `signalr` = `all` + SignalR (opt-in add-on; events via
  hub-streaming `IAsyncEnumerable<T>`). `py` ships REST only (default `rest`).
  Deprecated aliases kept one minor version: `sse`→`rest`, `both`→`all`.
- `--selfcheck`: regenerate the client tree from `--discovery` in memory and
  compare it against the committed tree at `--out`; exit `4` on drift (a missing
  or changed generated file), `0` if clean. **No files are written.** This is the
  client-side contract-drift gate — the CLI counterpart of the server-side MSBuild
  drift check (`ROADMAP.md` §3): without it, a server change without regen leaves
  the client build green and the drift surfaces only at runtime as a `400`. The
  comparison is one-directional (generated ⊆ committed): a file present in `--out`
  that the emitter no longer produces is not flagged (`--out` is a project client
  dir that may hold hand-written files); a removed controller still shows up as a
  `changed` entry (its generated file shrinks). Requires `--out`; mutually
  exclusive with `--stdout`.
- `--help` / `-h`, `--version` / `-v`.

Exit codes: `0` ok · `1` usage/runtime error · `2` discovery shape mismatch ·
`3` I/O error · `4` `--selfcheck` drift (committed tree out of date).

### Examples

```bash
# Live server → typed TS client into src/api/ (default --transport all: auto WS→REST+SSE)
npx sleipnir-gen --lang ts --discovery https://localhost:5001/api/sleipnir/discovery --out src/api

# WebSocket-only bundle (no fallback)
npx sleipnir-gen --lang ts --transport ws --discovery https://localhost:5001/api/sleipnir/discovery --out src/api

# HTTP-only bundle (REST calls + SSE events — for clients behind proxies/firewalls
# that block WebSocket upgrades)
npx sleipnir-gen --lang ts --transport rest --discovery https://localhost:5001/api/sleipnir/discovery --out src/api

# Opt-in SignalR add-on (all backends + hub-streaming events)
npx sleipnir-gen --lang ts --transport signalr --discovery https://localhost:5001/api/sleipnir/discovery --out src/api

# Saved contract file → self-contained Python client to stdout
npx sleipnir-gen --lang py --discovery contract.sleipnir.json --stdout > client.py

# Pipe a contract through stdin → single C# file
cat contract.sleipnir.json | npx sleipnir-gen --lang cs --discovery - --stdout > SleipnirGenerated.cs

# Authenticated discovery fetch (token never reaches the generated code)
npx sleipnir-gen --lang ts --discovery https://api.example.com/api/sleipnir/discovery \
  --bearer "$TOKEN" --out src/api

# Drift gate: fail CI if the committed src/api tree is out of date vs the live
# server contract (no files written; exit 4 on drift, 0 if clean).
npx sleipnir-gen --lang ts --discovery https://api.example.com/api/sleipnir/discovery \
  --out src/api --selfcheck
```

## What each emitter produces

| `--lang` | Output | Runtime dependency | Transports (`--transport`) |
|---|---|---|---|
| `ts` | `api/client.ts`, `api/controllers.ts`, `api/index.ts`, `api/typed-call.ts`, `api/types.ts` | `sleipnir-client` | `rest` / `ws` / `all` / `signalr` |
| `js` | `api/client.js`, `api/controllers.js`, `api/index.js`, `api/types.js` (JSDoc-typed) | `sleipnir-client` | `rest` / `ws` / `all` / `signalr` |
| `cs` | `SleipnirGenerated.cs` (single file) | `SleipnirClient` (.NET, referenced) | `rest` / `ws` / `all` / `signalr` |
| `py` | `client.py`, `types.py`, `__init__.py` | `httpx` (self-contained) | REST only |

Every emitter produces the **same public client surface** regardless of
`--transport`; the value only selects which backends the runtime
`SleipnirTransportRouter` bundles. Transport is selected at runtime
(`auto` default), not codegen time — `--transport` is a capability, not a shape.

The TS/JS emitters generate a **typed call surface**: a `TypedCall` /
`TypedRequest` builder with path records (`XPaths` / `XArrayPaths`) constraining
`exposes(jsonPath, alias)` to real JSONPaths, and a typed `alias(name)` that
returns the `@alias` wire placeholder. The `exposes`/`alias` pair is
`@`-normalized symmetrically — `exposes` strips a leading `@` for the wire
`dependencyMapping` key, `alias` ensures one for the consumer placeholder —
so both `alias("ids")` and `alias("@ids")` yield `"@ids"`. (Pre-1.2.2 `alias`
returned the bare name and the server's `ReplaceDependencyByAlias` never
matched; fixed in 1.2.2.)

The C# and Python emitters mirror the same typed-batch runtime (`Alias` /
`Arg<T>` / `Batch`) for their respective languages; the C# port is kept
byte-for-byte in parity with the TS `--lang cs` snapshot by the
`CsCodegenParityTests` gate in the .NET test suite.

## Programmatic API

The core is browser-safe (`sleipnir-codegen`); Node-only discovery loading
(`node:fs`, stdin) lives in `sleipnir-codegen/node`. The DevUI imports the
core directly.

```ts
// Browser-safe core (emitters + model)
import {
  buildEmitterInput, NamingResolver,
  emitTsClient, emitJsClient, emitCsClient, emitPyClient,
} from "sleipnir-codegen";

// Node entry adds discovery loading (fs + stdin) — not browser-safe.
import { loadDiscovery } from "sleipnir-codegen/node";

// loadDiscovery asserts the shape internally and returns a typed DiscoveryInfo;
// it throws DiscoveryShapeError on a non-conformant payload (the ingress gate).
const discovery = await loadDiscovery(
  "https://localhost:5001/api/sleipnir/discovery",
  { bearer: token },
);

const input = buildEmitterInput(discovery, new NamingResolver());
const tree = emitTsClient(input, { transport: "rest", baseUrl: "https://localhost:5001" });
// tree: Record<relativePath, fileContent> — write each entry to disk.
for (const [rel, content] of Object.entries(tree)) writeFileSync(join(outDir, rel), content);
```

Each `emit*Client(input, opts?)` returns a `Record<string, string>` of
relative path → file content. The emitter input is built once from the
`DiscoveryInfo` via `buildEmitterInput(discovery, namingResolver)`; the same
input can be fed to all four emitters.

If you already hold a parsed payload (e.g. from `sleipnir-client`'s
`client.discover()`), validate it explicitly with
`assertDiscoveryShape(obj)` (throws `DiscoveryShapeError`) before feeding it
to `buildEmitterInput` — the ingress gate refuses a malformed contract at
generation time rather than emitting a broken client.

## Development

```bash
cd clients/codegen
npm install
npm run build        # tsc -> dist/
npm run typecheck    # src + test, --noEmit
npm test             # vitest unit tests (golden snapshots + compile gates)
npm run test:e2e     # optional end-to-end (needs a running sample server)
```

Golden snapshots live under `test/snapshots/` (one folder per
`storyNN.<lang>[.transport]` fixture). Regenerate after an intentional emitter
change with `scripts/regen-snapshots.mjs` (all four emitters) or
`test/gen-snapshots.mjs` (ts/js only), then review the diff.

## License

MIT.