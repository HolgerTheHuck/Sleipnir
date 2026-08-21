# Sleipnir Codegen — User Reference

> The single lookup reference for Sleipnir's client-code generation. It covers **every**
> path, parameter, configuration knob, generated artifact, usage pattern, and failure mode
> in one place. If something does not work, the answer is here or it points to the file that
> has it.
>
> Source documents this consolidates: `CODEGEN_ONBOARDING.md` (the .NET build loop),
> `CLIENT_GENERATION.md` (the why/design), `docs/discovery-schema.md` (the contract shape),
> `clients/codegen/README.md` (the Node CLI), and the per-package READMEs.

Sleipnir is code-first: the C# classes decorated with `[SleipnirController]` / `[SleipnirMethod]`
*are* the contract. At runtime the server reflects them into a **discovery payload**
(`GET /api/sleipnir/discovery`), and codegen turns that payload back into **typed client
stubs** in TypeScript, JavaScript, C#, or Python — so a consumer writes
`client.order.getById(42)` instead of hand-writing `SleipnirCall.init(...)`.

There are **two codegen paths**, both producing byte-identical C# for the same contract:

| Path | When to use | Node needed? |
|---|---|---|
| **.NET-native** — `Sleipnir.Server.Codegen` (export + drift-check) + `Sleipnir.Generator` (Roslyn source generator) | The .NET build. Compile-time typed C# client, drift fails the build. | **No** |
| **Node CLI** — `sleipnir-gen` (`sleipnir-codegen` npm package) | TS/JS/C#/Python stubs, DevUI, CI convenience, one-off generation. | Yes (for ts/js/cs/py) |

Both consume the **same** `contract.sleipnir.json` and the C# they emit is held equal by the
parity gate (`SleipnirTests/Unit/Core/CsCodegenParityTests.cs`).

---

## Table of contents

1. [The contract loop](#1-the-contract-loop)
2. [The contract file — `contract.sleipnir.json`](#2-the-contract-file--contractsleipnirjson)
3. [Path A — .NET-native (no Node)](#3-path-a--net-native-no-node)
   - 3.1 [Server: export + drift-check](#31-server-export--drift-check-sleipnirservercodegen)
   - 3.2 [Client: the Roslyn source generator](#32-client-the-roslyn-source-generator-sleipnirgenerator)
   - 3.3 [The end-to-end change flow](#33-the-end-to-end-change-flow)
   - 3.4 [In-repo (ProjectReference) wiring](#34-in-repo-projectreference-wiring)
4. [Path B — the Node CLI (`sleipnir-gen`)](#4-path-b--the-node-cli-sleipnir-gen)
   - 4.1 [CLI parameters](#41-cli-parameters)
   - 4.2 [What each `--lang` produces](#42-what-each---lang--produces)
   - 4.3 [Programmatic API](#43-programmatic-api)
   - 4.4 [`--selfcheck` drift gate](#44---selfcheck--drift-gate)
5. [What each emitter generates — concrete shapes](#5-what-each-emitter-generates--concrete-shapes)
   - 5.1 [TypeScript / JavaScript](#51-typescript--javascript)
   - 5.2 [C#](#52-c)
   - 5.3 [Python](#53-python)
6. [Usage patterns](#6-usage-patterns)
   - 6.1 [Single typed call](#61-single-typed-call)
   - 6.2 [Dependency-chained batch (the diamond)](#62-dependency-chained-batch-the-diamond)
   - 6.3 [Server-push events](#63-server-push-events)
   - 6.4 [Switching transport at runtime](#64-switching-transport-at-runtime)
7. [Configuration reference (all knobs in one place)](#7-configuration-reference-all-knobs-in-one-place)
8. [Diagnostics & exit codes](#8-diagnostics--exit-codes)
9. [Troubleshooting catalog](#9-troubleshooting-catalog)
10. [How it is verified (the gates)](#10-how-it-is-verified-the-gates)
11. [Relationship to the code-first default](#11-relationship-to-the-code-first-default)

---

## 1. The contract loop

```
   SERVER project                         CLIENT project
   ─────────────                          ──────────────
   [SleipnirController] C# classes    ──┐
   are the contract                 │  contract.sleipnir.json
                                   │  (the committed contract)
   Sleipnir.Server.Codegen             │
   regenerates discovery  ──────────┤
   from the built assembly          │        Sleipnir.Generator reads it
   at every build                   │        via AdditionalFiles and
                                   │        emits SleipnirGenerated.cs
   drift? ──► build FAILS           │        into the compilation
   (committed ≠ runtime)            │
   SLEIPNIR_REGEN_GOLDEN=1 ──► rewrite ┘
```

1. **Server**: the C# controllers *are* the contract. `Sleipnir.Server.Codegen` regenerates
   `contract.sleipnir.json` from the built assembly on every build and **fails the build if the
   committed file has drifted** from what the runtime actually exposes.
2. **Commit**: `contract.sleipnir.json` is checked in — the versioned, reviewable artifact a PR
   diff shows.
3. **Client**: `Sleipnir.Generator` reads that same file and emits a typed `SleipnirGeneratedClient`
   **into the compilation**. No runtime codegen, no Node, no `sleipnir-gen` step.

The discovery JSON is the single source of truth on both sides. Its schema is versioned
(`discoveryVersion`, additive-only); a payload a consumer does not understand is rejected loudly,
never silently degraded.

---

## 2. The contract file — `contract.sleipnir.json`

`contract.sleipnir.json` is the JSON `SleipnirDiscoveryService.GetDiscoveryInfo()` returns,
serialized with `DiscoverySerialization.Options` (camelCase, nulls omitted). It is the same
payload `GET /api/sleipnir/discovery` serves over HTTP. Top-level shape:

```jsonc
{
  "discoveryVersion": "1",
  "controllers": [ /* { name, methods: [{ methodName, returnType, parameters: [...] }] } */ ],
  "types": { /* "<TypeName>": { kind: "object"|"enum", properties: [...], members: [...] } */ }
}
```

### `TypeRef` — the neutral type model

Every type-bearing slot (`returnType`, `parameterType`, `propertyType`) holds a `TypeRef`,
not a string — a discriminated object identified by `kind`:

```jsonc
TypeRef =
  | { "kind": "scalar",  "name": <scalarName>, "nullable": bool? }
  | { "kind": "array",   "element": TypeRef,   "nullable": bool? }
  | { "kind": "set",     "element": TypeRef,   "nullable": bool? }
  | { "kind": "map",     "key": TypeRef, "value": TypeRef, "nullable": bool? }
  | { "kind": "stream",  "element": TypeRef }              // IAsyncEnumerable<T> — never nullable
  | { "kind": "ref",     "ref": <typeKey>,    "nullable": bool? }
  | { "kind": "opaque",  "nativeName": "SleipnirResponse", "nullable": bool? }
  | { "kind": "void" }
```

| `kind` | Semantics | .NET sources |
|---|---|---|
| `scalar` | A primitive from the fixed scalar table (`string`, `int`, `long`, `bool`, `double`, `decimal`, `datetime`, `guid`, `bytes`, `any`, …) | BCL primitives |
| `array` | Ordered, duplicates allowed | `T[]`, `List<T>`, `IEnumerable<T>`, … |
| `set` | Distinct, unordered | `HashSet<T>`, `ISet<T>`, `SortedSet<T>` |
| `map` | Keyed entries | `Dictionary<K,V>`, `IDictionary<K,V>`, … |
| `stream` | Async sequence — contract declaration only; runtime materializes to a JSON array | `IAsyncEnumerable<T>` |
| `ref` | Points into the `types` registry (object **or** enum) | any expandable contract type |
| `opaque` | Unmodelled framework/BCL type; `nativeName` is a diagnostic hint only — consumers must not branch on it | `SleipnirResponse`, `ExpandoObject`, … |
| `void` | No return | `void` |

**Key rules:**

- A `TypeRef` is a *usage site*, not a type definition. A type's structure lives once in the
  `types` registry (`TypeMeta`); every usage points to it with `kind:"ref"`. There is no inline
  type definition on a `TypeRef`.
- **Nullability** is occurrence-level, on the `TypeRef` (`nullable: true`); absent ⟹ not
  nullable. `stream` and `void` are never nullable. `Task<T>`/`ValueTask<T>` returns are reported
  non-nullable (the NRT of `T` inside `Task<T>` is not exposed by `NullabilityInfoContext`).
- **Enums** register as `TypeMeta` with `kind:"enum"` + `members:[{name,value}]`; a usage site is
  `{kind:"ref", ref:"<enumKey>"}`. Sleipnir serializes enums as their underlying **integer**, so a
  ref to an enum emits as `number`/`long`/`int` — the generator does **not** emit a native enum
  declaration; `TypeMeta.members` is documentation only.
- **Default values**: `ParameterMeta.defaultValue` carries a C# compile-time constant when the
  method declares one (`void M(int id = 0)`); absent for non-constant defaults (`= new X()`) or no
  default. The generator renders it as the parameter default in the generated signature.
- **Wire casing**: property *names* on the wire are **camelCase** (`JsonNamingPolicy.CamelCase`).
  `propertyName` inside `PropertyMeta` carries the **PascalCase** C# name; the generator applies
  the camelCase wire fix when emitting. JsonPath is case-sensitive against the camelCase wire
  document (`$.Id` matches nothing → `Unresolved`; use `$.id`).
- **`CancellationToken`** parameters are dropped (the framework injects them); they never appear.
- **Not in the schema**: method overloads (dispatch is by `Controller_Method` name only), the
  old `*TypeDefinition` inline overrides (gone — a `ref` resolves into the single registry),
  parameter positional info (binding is by name), and authorization metadata (discovery is an
  attack-surface oracle and is itself auth-gated).

Full schema spec: [`docs/discovery-schema.md`](docs/discovery-schema.md).

---

## 3. Path A — .NET-native (no Node)

The A-prio C# deliverable. Two packages:

| Package (NuGet `PackageId`) | TFM | Role | Imported build file |
|---|---|---|---|
| `Sleipnir.Server.Codegen` | net8.0 | Export tool + drift-check MSBuild target (server side) | `build/Sleipnir.Server.Codegen.targets` |
| `Sleipnir.Generator` | netstandard2.0 | Roslyn `IIncrementalGenerator` (client side) | `build/Sleipnir.Generator.props` |

Both are **self-contained**: `Sleipnir.Server.Codegen` ships the tool + deps in `tasks/net8.0/`;
`Sleipnir.Generator` links its emission core into the generator assembly (the Roslyn analyzer
load context cannot resolve a `ProjectReference` dep). Neither needs Node, `npm`, or `sleipnir-gen`
on the build machine.

### 3.1 Server: export + drift-check (`Sleipnir.Server.Codegen`)

#### Wire it

Reference the package and let the convention work — a file named `contract.sleipnir.json` in
the project directory is picked up automatically:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <ItemGroup>
    <PackageReference Include="Sleipnir.Server.Codegen" Version="1.1.0" />
  </ItemGroup>
</Project>
```

`build/Sleipnir.Server.Codegen.targets` is auto-imported on `PackageReference`; it runs a target
`AfterTargets="Build"` that regenerates the discovery in-process and drift-checks it.

#### Generate the contract the first time

On the first build there is no committed contract yet, so the export tool **writes one** and
exits clean (exit 0). Commit it:

```bash
dotnet build                              # creates contract.sleipnir.json
git add contract.sleipnir.json
git commit -m "chore: add Sleipnir contract"
```

From now on every `dotnet build` regenerates the discovery from the built assembly and compares
it to the committed `contract.sleipnir.json`. They match → build succeeds. They differ →
**build fails**.

#### The intended-change flow (regen)

When you intentionally change a controller, the committed contract is stale and the build fails
with a clear drift message plus a normalized regenerated-vs-committed diff. Regenerate:

```bash
# Linux/macOS
SLEIPNIR_REGEN_GOLDEN=1 dotnet build
# Windows PowerShell
$env:SLEIPNIR_REGEN_GOLDEN=1; dotnet build
# Windows cmd
set SLEIPNIR_REGEN_GOLDEN=1 && dotnet build

git add contract.sleipnir.json
git commit -m "feat: rename Order.GetById -> Order.Get"
```

The regen flow is the **only** way the committed contract changes. Drift without regen is a hard
build failure — that is the whole point (the `wsdl.exe` trap: a stale contract that lies).

#### What the target does, exactly

`SleipnirExportDriftCheck` runs `AfterTargets="Build"`, only when a contract file is configured
(`$(SleipnirContractFile)` is set, or `contract.sleipnir.json` exists in the project dir). It
shells out to the export tool in its **own process**:

```
dotnet <tool.dll> --assembly <TargetPath> --contract <SleipnirContractFile> [--regen]
```

Running in a separate process isolates the server-assembly load from MSBuild (no version
collisions, no locked files). The target only translates the tool's exit code into an MSBuild
`<Error>`; the tool's stdout (pass / drift diff / regen confirmation) is echoed into the build log.

| Tool exit code | Meaning | Build result |
|---|---|---|
| `0` | ok, or regenerated (with `--regen`) | success |
| `1` | **drift detected** | build fails (drift `<Error>`) |
| `2` | tool error (not a drift) | build fails (tool-error `<Error>`) |

The tool itself: loads the built assembly (`Assembly.LoadFrom`, scoped to the server output dir —
not an AppDomain-wide scan), reflects the `[SleipnirController]` types, builds a `SleipnirInvoker`
with a stub `IServiceScopeFactory` + `NullLogger`, calls `GetDiscoveryInfo()`, serializes with
`DiscoverySerialization.Options`, sorts controllers by name for determinism, and drift-checks
against the committed file (normalize-sort + `JsonNode.DeepEquals`). `--regen` (or
`SLEIPNIR_REGEN_GOLDEN=1`) overwrites the committed contract.

### 3.2 Client: the Roslyn source generator (`Sleipnir.Generator`)

#### Wire it

Reference the generator (as an analyzer) and the Sleipnir client runtime, and drop the server's
`contract.sleipnir.json` into the client project:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <!-- The runtime the generated stubs call (SleipnirRestJsonClient, ISleipnirClient, SleipnirCall, ...). -->
    <PackageReference Include="Sleipnir.Client" Version="1.1.0" />
    <!-- The source generator: loaded as an analyzer, emits SleipnirGenerated.cs at compile time. -->
    <PackageReference Include="Sleipnir.Generator" Version="1.1.0"
                      OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
  </ItemGroup>
  <ItemGroup>
    <!-- The committed contract (copy it from the server project, or share it via a linked file). -->
    <AdditionalFiles Include="contract.sleipnir.json" />
  </ItemGroup>
</Project>
```

`build/Sleipnir.Generator.props` (auto-imported) already marks `contract.sleipnir.json` as an
`AdditionalFile` and surfaces `SleipnirContractFile` to the generator, so the explicit
`<AdditionalFiles Include="contract.sleipnir.json" />` is only needed when you place the contract
somewhere other than the project directory.

#### What you get

At compile time the generator emits `SleipnirGenerated.cs` in the `Sleipnir.Generated` namespace,
containing (see [§5.2](#52-c) for the concrete shape):

- A POCO for every contract type (properties nullable, `[JsonPropertyName]` in camelCase matching
  the wire).
- An `Arg<T>` wrapper for method parameters, `Call` / `BatchEntry` / `Batch` shapes, and
  `Alias` / `Exposes` helpers for dependency chaining.
- One client class per controller and a root `SleipnirGeneratedClient` that owns them.

#### Use it

```csharp
using Sleipnir.Generated;

var client = new SleipnirGeneratedClient("http://localhost:5001");

// Single typed call — Call<T> deserializes into the generated POCO.
Order order = await client.Call<Order>(client.Order.GetById(42));

// Typed diamond batch (Serial — required for @alias resolution).
var batch = new Batch();
var o = batch.Add(client.Order.GetById(42))
    .Exposes("$.customerId", "@customerId")
    .Exposes("$.id",         "@orderId");
batch.Add(client.Customer.GetById(o.Alias("@customerId")));
var lines = batch.Add(client.OrderLine.GetByOrder(o.Alias("@orderId")))
    .Exposes("$[*].articleId", "@articleIds");
batch.Add(client.Article.GetByIds(lines.Alias("@articleIds")));

SleipnirMultiCallResponse resp = await client.Batch(batch);
var fetchedOrder = resp.Get<Order>("Order.GetById");
var articles     = resp.Get<List<Article>>("Article.GetByIds");
```

Every identifier above — `SleipnirGeneratedClient`, `Order`, `client.Order.GetById`, `Batch`,
`Exposes`, `Alias`, `resp.Get<T>` — is **generated** from `contract.sleipnir.json`. None of it is
hand-written. If the contract changes and you regenerate on the server, the client rebuild picks up
the new shape; if a method disappears, the client code that calls it stops compiling.

A complete, compiling example lives in
[`Sleipnir.Samples.GeneratedClient/Program.cs`](Sleipnir.Samples.GeneratedClient/Program.cs) (the
Story-01 typed diamond). Its `contract.sleipnir.json` is a copy of the Story-01 server's contract.

#### Diagnostics

The generator emits two diagnostics, both build-breaking (see [§8](#8-diagnostics--exit-codes)):

| Id | Meaning |
|---|---|
| `SLEIPNIR001` | The contract failed **shape validation** (unknown `discoveryVersion`, an invalid `TypeRef` kind, a `ref` that does not resolve into the `types` registry, …). The generated client is **not** emitted. |
| `SLEIPNIR002` | The contract passed shape validation but the C# emitter threw — a bug in `Sleipnir.Codegen.Core`. The generated client is **not** emitted. |

`SLEIPNIR001` is the one you can cause from a normal workflow (e.g. feeding the generator a
contract from a newer, unsupported `discoveryVersion`). It is additive-only: upgrade the
generator package to one that knows the new version.

### 3.3 The end-to-end change flow

```bash
# 1. Change a controller on the server.
#    e.g. rename [SleipnirMethod("GetById")] -> [SleipnirMethod("Get")] on OrderController.

# 2. Build the server — drift is detected, build FAILS.
dotnet build
# error: Sleipnir contract drift detected: the committed contract.sleipnir.json
#        does not match the server's runtime discovery. ...

# 3. The change is intentional — regenerate the contract.
SLEIPNIR_REGEN_GOLDEN=1 dotnet build
git add path/to/contract.sleipnir.json
git commit -m "feat: rename Order.GetById -> Order.Get"

# 4. Update the client's copy of contract.sleipnir.json (or point both at a shared file),
#    then rebuild the client. Calls to client.Order.GetById now fail to compile —
#    fix them to client.Order.Get — exactly the compile-time boundary you want.
dotnet build path/to/client
```

If step 2 did **not** fail, the contract would lie to every client. That is the failure mode this
build action exists to prevent.

### 3.4 In-repo (ProjectReference) wiring

The NuGet wiring above assumes packaged packages. When developing inside the Sleipnir repo
(everything is a `ProjectReference`), the same targets/props are imported manually with the
tool/generator paths pointed at their built output. See how Story 01 does it
([`stories/01-n-plus-one-screen/Story01.csproj`](stories/01-n-plus-one-screen/Story01.csproj)):

```xml
<!-- Server: build-order-only ref to the tool (not a runtime dep), then import the targets
     with the tool dll pointed at its built output. -->
<ItemGroup>
  <ProjectReference Include="..\..\Sleipnir.Server.Codegen\Sleipnir.Server.Codegen.csproj"
                    ReferenceOutputAssembly="false" />
</ItemGroup>
<PropertyGroup>
  <SleipnirContractFile>contract.sleipnir.json</SleipnirContractFile>
  <SleipnirExportToolDll>..\..\Sleipnir.Server.Codegen\bin\$(Configuration)\net8.0\Sleipnir.Server.Codegen.dll</SleipnirExportToolDll>
</PropertyGroup>
<Import Project="..\..\Sleipnir.Server.Codegen\build\Sleipnir.Server.Codegen.targets" />
```

For the client generator in-repo, reference the generator project as an analyzer
([`Sleipnir.Samples.GeneratedClient/Sleipnir.Samples.GeneratedClient.csproj`](Sleipnir.Samples.GeneratedClient/Sleipnir.Samples.GeneratedClient.csproj)):

```xml
<ItemGroup>
  <ProjectReference Include="..\..\SleipnirClient\SleipnirClient.csproj" />
  <ProjectReference Include="..\..\Sleipnir.SourceGenerator\Sleipnir.SourceGenerator.csproj"
                    OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
</ItemGroup>
<ItemGroup>
  <AdditionalFiles Include="contract.sleipnir.json" />
</ItemGroup>
```

The published-NuGet and in-repo paths run **the same code** (the same tool dll, the same generator
assembly). The only difference is how the build file is wired in.

---

## 4. Path B — the Node CLI (`sleipnir-gen`)

The TS-core generator that emits TS, JS, C#, and Python. For the **.NET build** you do not need
it — Path A is Node-free. The CLI is the A-prio path for TS/JS clients, and a convenience for C#
(DevUI, CI, one-off) and Python (the B-prio deliverable; no native Python generator is planned).

### Install

```bash
# CLI (global tool, optional — `npx` works too)
npm i -g sleipnir-codegen
# or ad-hoc:
npx sleipnir-gen --lang ts --discovery https://localhost:5001/api/sleipnir/discovery --out src/api

# Programmatic (build tool, DevUI, custom emitter)
npm i -D sleipnir-codegen
```

The generator depends on [`sleipnir-client`](https://www.npmjs.com/package/sleipnir-client) for
the TS/JS runtime — the generated `SleipnirClient` wires a `SleipnirTransportRouter` that bundles
the backends selected by `--transport`. The `cs` emitter targets the `SleipnirClient` .NET runtime
(same router); `py` is self-contained (httpx, REST only).

### 4.1 CLI parameters

```bash
sleipnir-gen --lang <ts|js|cs|py> --discovery <url|file|-> [--out <dir> | --stdout]
             [--base-url <url>] [--bearer <token>] [--timeout <s>]
             [--transport rest|ws|all|signalr] [--selfcheck]
```

| Flag | Required | Meaning |
|---|---|---|
| `--lang` | yes | `ts` \| `js` \| `cs` \| `py`. |
| `--discovery` / `-d` | yes | A live `http(s)://…` URL, a file path, or `-` for stdin. |
| `--out` / `-o` | one of `--out`/`--stdout` | Output directory (one file per generated module, plus a `types` module for TS/CS/PY). Mutually exclusive with `--stdout`. |
| `--stdout` | one of `--out`/`--stdout` | Write the generated source to stdout (single concatenated stream) instead of files. |
| `--base-url` | no | Base URL hint rendered into the generated client preamble (the generated client still takes a base URL at construction; this is a design-time default baked into the header). |
| `--bearer` | no | Bearer token used **only** to fetch `--discovery` over HTTP (never written into the generated code). |
| `--timeout` | no | HTTP timeout (seconds) for fetching `--discovery` from a URL. |
| `--transport` | no (default `all`) | `rest` \| `ws` \| `all` \| `signalr` (ts/js/cs; `py` ships REST only). A **bundle-capability** selector — which backends the generated `SleipnirClient` *bundles*. The public client surface (method signatures, options type) is **byte-identical across all values**; transport is chosen **at runtime** via `SleipnirTransportRouter` (`auto` default probes WebSocket → falls back to REST+SSE; `useTransport()` switches explicitly). See [§6.4](#64-switching-transport-at-runtime). Deprecated aliases kept one minor version: `sse`→`rest`, `both`→`all`. |
| `--selfcheck` | no | Regenerate the client tree from `--discovery` in memory and compare against the committed tree at `--out`; exit `4` on drift, `0` if clean. **No files written.** See [§4.4](#44---selfcheck--drift-gate). Requires `--out`; mutually exclusive with `--stdout`. |
| `--help` / `-h`, `--version` / `-v` | — | Help / version. |

**Exit codes:** `0` ok · `1` usage/runtime error · `2` discovery shape mismatch · `3` I/O error ·
`4` `--selfcheck` drift (committed tree out of date).

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

# Drift gate: fail CI if the committed src/api tree is out of date vs the live server contract.
npx sleipnir-gen --lang ts --discovery https://api.example.com/api/sleipnir/discovery \
  --out src/api --selfcheck
```

### 4.2 What each `--lang` produces

| `--lang` | Output files | Runtime dependency | Transports (`--transport`) |
|---|---|---|---|
| `ts` | `api/client.ts`, `api/controllers.ts`, `api/index.ts`, `api/typed-call.ts`, `api/types.ts` | `sleipnir-client` | `rest` / `ws` / `all` / `signalr` |
| `js` | `api/client.js`, `api/controllers.js`, `api/index.js`, `api/types.js` (JSDoc-typed) | `sleipnir-client` | `rest` / `ws` / `all` / `signalr` |
| `cs` | `SleipnirGenerated.cs` (single file) | `SleipnirClient` (.NET, referenced) | `rest` / `ws` / `all` / `signalr` |
| `py` | `client.py`, `types.py`, `__init__.py` | `httpx` (self-contained) | REST only |

Every emitter produces the **same public client surface** regardless of `--transport`; the value
only selects which backends the runtime `SleipnirTransportRouter` bundles. Transport is selected at
runtime (`auto` default), not codegen time — `--transport` is a capability, not a shape.

The TS/JS emitters generate a **typed call surface**: a `TypedCall` / `TypedRequest` builder with
path records (`XPaths` / `XArrayPaths`) constraining `exposes(jsonPath, alias)` to real JSONPaths,
and a typed `alias(name)` that returns the `@alias` wire placeholder. The `exposes`/`alias` pair is
`@`-normalized symmetrically — `exposes` strips a leading `@` for the wire `dependencyMapping` key,
`alias` ensures one for the consumer placeholder — so both `alias("ids")` and `alias("@ids")` yield
`"@ids"`. (Pre-1.2.2 `alias` returned the bare name and the server's `ReplaceDependencyByAlias`
never matched; fixed in 1.2.2.)

The C# and Python emitters mirror the same typed-batch runtime (`Alias` / `Arg<T>` / `Batch`) for
their respective languages; the C# port is kept byte-for-byte in parity with the TS `--lang cs`
snapshot by the `CsCodegenParityTests` gate in the .NET test suite.

### 4.3 Programmatic API

The core is browser-safe (`sleipnir-codegen`); Node-only discovery loading (`node:fs`, stdin) lives
in `sleipnir-codegen/node`. The DevUI imports the core directly.

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

Each `emit*Client(input, opts?)` returns a `Record<string, string>` of relative path → file
content. The emitter input is built once from the `DiscoveryInfo` via
`buildEmitterInput(discovery, namingResolver)`; the same input can be fed to all four emitters.

If you already hold a parsed payload (e.g. from `sleipnir-client`'s `client.discover()`), validate
it explicitly with `assertDiscoveryShape(obj)` (throws `DiscoveryShapeError`) before feeding it to
`buildEmitterInput` — the ingress gate refuses a malformed contract at generation time rather than
emitting a broken client.

### 4.4 `--selfcheck` drift gate

`--selfcheck` regenerates the client tree from `--discovery` in memory and compares it against the
committed tree at `--out`; exit `4` on drift (a missing or changed generated file), `0` if clean.
**No files are written.** This is the client-side contract-drift gate — the CLI counterpart of the
server-side MSBuild drift check ([§3.1](#31-server-export--drift-check-sleipnirservercodegen)):
without it, a server change without regen leaves the client build green and the drift surfaces only
at runtime as a `400`.

The comparison is one-directional (generated ⊆ committed): a file present in `--out` that the
emitter no longer produces is **not** flagged (`--out` is a project client dir that may hold
hand-written files); a removed controller still shows up as a `changed` entry (its generated file
shrinks). Requires `--out`; mutually exclusive with `--stdout`.

---

## 5. What each emitter generates — concrete shapes

The shapes below are from the committed Story-01 snapshots
(`clients/codegen/test/snapshots/story01.{ts,cs,py}/`), the real output — not invented.

### 5.1 TypeScript / JavaScript

The TS emitter produces a typed client with **dependency chaining as a first-class,
compile-checked surface**. Five files:

- **`api/types.ts`** — a POCO interface per contract type.
- **`api/controllers.ts`** — one client class per controller; each method returns a `TypedCall<T, TPaths>`.
- **`api/typed-call.ts`** — `TypedCall`, `TypedRequest`, `Batch`, and the per-type **path records**
  (`OrderPaths`, `OrderArrayPaths`, …) that constrain `exposes(jsonPath, alias)` to real `$`-paths
  with the correct extracted type. A wrong-cased or nonexistent path (`$.Id` instead of `$.id`) is a
  **compile error**, not a runtime `400`.
- **`api/client.ts`** — the root `SleipnirClient` wrapping a `SleipnirTransportRouter`, with one
  accessor per controller, `call<T>()`, `batch()`, `negotiate()`, `useTransport()`, the raw-backend
  escape hatches (`rest`/`ws`/`sse`/`signalr`), `setBearer()`, `dispose()`, and the
  `SleipnirClientOptions` interface (strict superset across all capabilities — fields for
  unbundled backends are accepted but ignored).
- **`api/index.ts`** — barrel.

```ts
import { SleipnirClient } from "./api";

const client = new SleipnirClient("http://localhost:5001");

// Single call — typed params + typed return.
const order = await client.call(client.order.getById(42));   // TypedResponse<Order | null>

// The diamond, with compile-time guarantees.
const batch = client.batch();                                 // mode locked to Serial (@alias needs order)
const o = batch.add(
  client.order.getById(42)
    .exposes("$.customerId", "customerId")                    // JsonPathOf<Order | null> literal union —
    .exposes("$.id",           "orderId")                     // $.CustomerId (PascalCase) is a compile error
    .exposes("$.shippingAddressId", "addressId"),
);
const c      = batch.add(client.customer.getById(batch.alias("@customerId"))); // number
const lines = batch.add(
  client.orderLine.getByOrder(batch.alias("@orderId")).exposes("$[*].articleId", "articleIds"),
);
const arts   = batch.add(client.article.getByIds(batch.alias("@articleIds")));   // number[]
const stock  = batch.add(client.stock.getByArticles(batch.alias("@articleIds")));
const addr   = batch.add(client.address.getById(batch.alias("@addressId")));
const responses = await batch.send();                        // one roundtrip, six responses
```

`batch.alias("@x")` returns the **type the producer exposed** for that alias, so a consumer's
parameter typechecks against it. The raw fluent builder (`SleipnirCall.batch([...])` from
`sleipnir-client`) remains available as an escape hatch.

The JS output mirrors the TS API shape with JSDoc `@typedef` blocks (IntelliSense without a compile
step); `--lang js` loses only the hard compile errors.

### 5.2 C#

A single file `SleipnirGenerated.cs` in namespace `Sleipnir.Generated`. The full Story-01 output is
at [`clients/codegen/test/snapshots/story01.cs/SleipnirGenerated.cs`](clients/codegen/test/snapshots/story01.cs/SleipnirGenerated.cs).
It contains:

- **POCOs** — one per contract type, properties nullable with `[JsonPropertyName("camelCase")]`:

  ```csharp
  public class Order {
      [JsonPropertyName("id")]                public int? Id { get; set; }
      [JsonPropertyName("customerId")]        public int? CustomerId { get; set; }
      [JsonPropertyName("shippingAddressId")] public int? ShippingAddressId { get; set; }
      [JsonPropertyName("status")]            public string? Status { get; set; }
      [JsonPropertyName("placedAt")]          public DateTime? PlacedAt { get; set; }
  }
  ```

- **`Alias`** — a readonly struct holding the `"@alias"` wire placeholder; implicitly converts into
  any `Arg<T>`.
- **`Arg<T>`** — a typed method argument accepting either a literal `T` or an `Alias`. `GetById(Arg<int> id)`
  accepts `42` and `order.Alias("@customerId")` but rejects `"oops"`. (The alias→param-type match is
  runtime-checked today; a Roslyn source-generator compile-time check is a future increment.)
- **`Call`** — a single Sleipnir call built from a generated controller method; `Exposes(jsonPath, alias)`
  (strips a leading `@` from the alias — the wire `dependencyMapping` key is the bare name) and
  `Named(id)`.
- **`BatchEntry`** — a call enrolled in a batch; `Exposes(...)` and `Alias(name)` (ensures a leading
  `@` — symmetric with `Exposes`; both `Alias("ids")` and `Alias("@ids")` yield `"@ids"`).
- **`Batch`** — the batch builder; execution mode locked to `Serial` (the only mode that resolves
  `@alias` placeholders). Add calls in topological order: a producer's `Exposes` must run before any
  consumer's `Alias`.
- **One client class per controller** — each method is `public Call GetById(Arg<int> id) => new Call(SleipnirCall.Init("Order", "GetById").Param("id", id.ToWireValue()));`.
- **`SleipnirGeneratedClient`** — the root client, wrapping an `ISleipnirClient` (a
  `SleipnirTransportRouter` by default, bundling the codegen `--transport` capability). Named
  `SleipnirGeneratedClient` (not `SleipnirClient`) to avoid colliding with the global `SleipnirClient`
  namespace. Two constructors: `(string baseUrl)` (router with the capability, `auto` default
  transport) and `(ISleipnirClient client)` (custom — e.g. a pre-configured router, a specific
  backend, or `SleipnirInMemoryClient` for tests). Exposes one accessor per controller, `Call<T>(call)`,
  `Subscribe<T>(call, resumePolicy?, ct)`, `Batch(batch)`, and a `Client` accessor to reach the
  underlying router (`NegotiateAsync`/`UseTransportAsync` and raw backend escape hatches).

> The Roslyn source generator ([§3.2](#32-client-the-roslyn-source-generator-sleipnirgenerator))
> emits the **same** `SleipnirGenerated.cs` into the compilation; the CLI `--lang cs` path emits it
> to a file. Both are held byte-equal by the parity gate ([§10](#10-how-it-is-verified-the-gates)).

### 5.3 Python

`client.py`, `types.py`, `__init__.py` — self-contained (only `httpx`), REST only. `types.py` holds
`@dataclass` contract types; `client.py` holds an async client over `httpx`, with a `py.typed`
marker for typed consumption. The `Alias` / `Arg` / `Batch` typed-batch surface mirrors the TS/C#
shape. Python ships REST only (no Python WS/SSE runtime yet), so `--transport` is rejected for
`--lang py`.

---

## 6. Usage patterns

### 6.1 Single typed call

**C# (Roslyn generator):**
```csharp
var client = new SleipnirGeneratedClient("http://localhost:5001");
Order order = await client.Call<Order>(client.Order.GetById(42));
```

**TypeScript (CLI):**
```ts
const client = new SleipnirClient("http://localhost:5001");
const resp = await client.call(client.order.getById(42));   // TypedResponse<Order | null>
const order = resp.data;
```

### 6.2 Dependency-chained batch (the diamond)

Several dependent calls in one roundtrip. Execution mode is **Serial** (the only mode that
resolves `@alias` placeholders). Add calls in topological order: a producer's `exposes`/`Exposes`
must run before any consumer's `alias`/`Alias`.

**C#:** see [§3.2](#32-client-the-roslyn-source-generator-sleipnirgenerator) and
[`Sleipnir.Samples.GeneratedClient/Program.cs`](Sleipnir.Samples.GeneratedClient/Program.cs). Fetch
results by request id (`resp.Get<T>("Controller.Method")`) — topological order is not request
order.

**TypeScript:** see [§5.1](#51-typescript--javascript). `batch.alias("@x")` returns the producer's
exposed type so the consumer param typechecks; a wrong-cased JsonPath is a compile error.

**The `@`-normalization contract (all languages):** `exposes`/`Exposes` **strips** a leading `@`
(the wire `dependencyMapping` key is the bare name — the server strips the consumer's `@alias`
placeholder before lookup); `alias`/`Alias` **ensures** a leading `@` (the consumer sends
`data: "@alias"`). So both `alias("ids")` and `alias("@ids")` yield `"@ids"` on the wire.
Returning the bare name (the 1.2.1 bug) sent `"ids"`, which the server's `ReplaceDependencyByAlias`
never matched — the typed chain compiled but the dependent received an unresolved literal. Fixed
in 1.2.2 across all four emitters + the .NET `CsEmitter`.

> JsonPath is **case-sensitive** against the camelCase wire document: `$.customerId` works,
> `$.CustomerId` (PascalCase) matches nothing → the dependent gets a `400 Unresolved`. See
> [`DEPENDENCY_BINDING.md`](DEPENDENCY_BINDING.md) for the full binding/casing contract.

### 6.3 Server-push events

The generated root client exposes a typed `Subscribe<T>(call, resumePolicy?, ct)` (C#) /
`subscribe<T>(...)` surface for `[SleipnirEvent]` methods. It routes to the active **event** backend
(WebSocket / SSE / SignalR, per the transport profile) and returns a hot `SleipnirSubscription<T>`
(`IObservable<T>` + `IDisposable`); dispose to unsubscribe. `resumePolicy` governs cross-transport
resume on disconnect (`Last-Event-Id` for SSE).

```csharp
var sub = await client.Subscribe<PriceTick>(client.Market.Prices("BTC"), ResumePolicy.Default, ct);
sub.Subscribe(tick => Console.WriteLine(tick.Price));
// ... later
sub.Dispose();
```

Events are only available over a bundled event backend: `--transport rest` bundles SSE,
`ws` bundles WebSocket, `all` bundles both (so `auto` can pick), `signalr` adds hub-streaming.
`py` has no events (REST only).

### 6.4 Switching transport at runtime

`--transport` selects which backends the generated client **bundles**; the public surface is
byte-identical across all values. Transport is chosen at **runtime**, not codegen time:

- **`auto` (default)** — `SleipnirTransportRouter` probes WebSocket and falls back to REST+SSE on
  failure. Call `negotiate()` (TS) / `NegotiateAsync()` (C#) to resolve the profile; before that,
  `activeTransport` is `null`.
- **Explicit switch** — `useTransport(t)` (TS) / `UseTransportAsync(t)` (C#) switches the active
  transport. Throws if the backend isn't bundled.
- **Escape hatches** — `client.rest` / `client.ws` / `client.sse` / `client.signalr` (TS) /
  `client.Client` cast to `SleipnirTransportRouter` (C#) reach the raw bundled backend (or
  `null`/`undefined` when not bundled).

Capability values: `rest` = REST calls + SSE events (HTTP-only, proxy-safe); `ws` = WebSocket
calls + events; `all` = REST + WS + SSE (enables `auto`); `signalr` = `all` + SignalR (opt-in
add-on; events via hub-streaming `IAsyncEnumerable<T>`). Deprecated aliases kept one minor version:
`sse`→`rest`, `both`→`all`.

---

## 7. Configuration reference (all knobs in one place)

### Server-side (`Sleipnir.Server.Codegen`)

| Property / env | Default | Purpose |
|---|---|---|
| `SleipnirContractFile` | `contract.sleipnir.json` if it exists in the project dir | Path to the contract (project-relative or absolute). Set explicitly to use a different name/location. |
| `SleipnirContractEnabled` | `true` | Set `false` to opt the project out (e.g. a library that transitively references the package but is not a server). |
| `SleipnirExportToolDll` | `$(MSBuildThisFileDirectory)..\tasks\net8.0\Sleipnir.Server.Codegen.dll` | Path to the export tool dll. The NuGet default is right for published consumption; for in-repo `ProjectReference` dev, set it to the built tool output ([§3.4](#34-in-repo-projectreference-wiring)). |
| `SLEIPNIR_REGEN_GOLDEN` (env) | unset | Set to `1` to regenerate the committed contract instead of failing on drift. **Never set in CI** — that would silently rewrite the contract instead of failing. |

Contract-file selection priority: 1. `$(SleipnirContractFile)`, 2. `contract.sleipnir.json` in the
project directory. The drift-check target runs only when `SleipnirContractEnabled != false` **and**
a contract file is configured **and** `SleipnirExportToolDll` is non-empty.

### Client-side (`Sleipnir.Generator`)

| Property | Default | Purpose |
|---|---|---|
| `SleipnirContractFile` | `contract.sleipnir.json` if it exists in the project dir | Contract path. The generator matches the AdditionalFile whose filename equals this (case-insensitive). |
| `SleipnirContractEnabled` | `true` | Set `false` to disable the generator for a project. |

The `build/Sleipnir.Generator.props` auto-marks `contract.sleipnir.json` as an `AdditionalFile` and
surfaces `SleipnirContractFile` to the generator via a `build_property` analyzer option, so the
explicit `<AdditionalFiles>` line is only needed for a non-default file name or location.

### Node CLI (`sleipnir-gen`)

All flags are in [§4.1](#41-cli-parameters). There are no config files — everything is a CLI flag
or a programmatic `opts` object ([§4.3](#43-programmatic-api)).

---

## 8. Diagnostics & exit codes

### Generator diagnostics (Roslyn, client side)

| Id | Severity | Meaning | Source emitted? |
|---|---|---|---|
| `SLEIPNIR001` | error | Contract failed **shape validation** (unknown `discoveryVersion`, invalid `TypeRef` kind, unresolvable `ref`, …). | No |
| `SLEIPNIR002` | error | Contract passed shape validation but the C# emitter threw — a bug in `Sleipnir.Codegen.Core`. | No |

A valid contract → source emitted, no diagnostic. `SLEIPNIR001` is the one a normal workflow can
cause (e.g. a contract from a newer, unsupported `discoveryVersion`). The schema is additive-only:
upgrade the generator package to one that knows the new version.

### Export tool exit codes (server side)

| Code | Meaning | Build result |
|---|---|---|
| `0` | ok, or regenerated (with `--regen`) | success |
| `1` | **drift detected** | build fails |
| `2` | tool error (not a drift) | build fails |

### CLI exit codes (`sleipnir-gen`)

| Code | Meaning |
|---|---|
| `0` | ok |
| `1` | usage / runtime error |
| `2` | discovery shape mismatch (`assertDiscoveryShape` rejected the payload) |
| `3` | I/O error |
| `4` | `--selfcheck` drift (committed tree out of date) |

---

## 9. Troubleshooting catalog

**"Sleipnir contract drift detected" on a build where I didn't touch anything.**
The committed `contract.sleipnir.json` does not match what the built server exposes. This can happen
after a merge, a controller change, or if the contract was never regenerated after the last server
edit. Review the regenerated-vs-committed diff in the build log; if the change is intended, run
`SLEIPNIR_REGEN_GOLDEN=1 dotnet build` (PowerShell: `$env:SLEIPNIR_REGEN_GOLDEN=1; dotnet build`)
and commit the result.

**The generator does not emit anything (no `SleipnirGenerated.cs`).**
The generator only fires on an AdditionalFile whose filename is `contract.sleipnir.json` (or
matches `$(SleipnirContractFile)`). Confirm the file is present in the project directory and
included as an AdditionalFile, and that `SleipnirContractEnabled` is not `false`. The generator
runs at compile time — check the build log for `SLEIPNIR001`/`SLEIPNIR002`.

**`SLEIPNIR001` — "Unsupported discoveryVersion".**
The contract was produced by a newer server than the generator package understands. Upgrade
`Sleipnir.Generator` (and `Sleipnir.Server.Codegen` on the server side) to a matching version. The
schema is additive-only; an older generator cannot read a newer contract by design.

**"Sleipnir export tool error" (exit 2).**
The tool itself failed, not a drift. The exception is printed to the build log. Common cause in
in-repo dev: `SleipnirExportToolDll` points at a tool dll that was not built yet — build the
`Sleipnir.Server.Codegen` project first (or build the whole solution).

**Ambiguous type / `CS0433` "exists in multiple assemblies".**
You have both the generated `SleipnirGenerated.cs` and a hand-written duplicate of a contract type
in the same compilation. The generated types live in `Sleipnir.Generated`; do not re-declare them.
If you share contract types between server and client, let the client use the *generated* ones and
keep the server's as the source.

**The drift-check target does not run.**
It is gated on `$(SleipnirContractFile)` being non-empty (or `contract.sleipnir.json` existing in
the project dir) and `SleipnirContractEnabled != false`. A library project that transitively
references the package but is not a server should set `SleipnirContractEnabled=false` (or simply not
configure a contract file).

**A dependent call in a batch gets a `400 Unresolved` / receives a literal instead of the alias value.**
Two common causes: (1) the JsonPath casing is wrong — paths are case-sensitive against the
camelCase wire document, so `$.CustomerId` (PascalCase) matches nothing; use `$.id`, `$.customerId`.
(2) You are on a codegen output older than 1.2.2, where `alias("ids")` returned the bare name
`"ids"` instead of the `"@ids"` wire placeholder (the server's `ReplaceDependencyByAlias` never
matched). Regenerate with a current `sleipnir-codegen` / `Sleipnir.Generator`. See
[`DEPENDENCY_BINDING.md`](DEPENDENCY_BINDING.md).

**`--selfcheck` exits `4` in CI.**
The committed client tree at `--out` is out of date vs the live/discovery contract. Regenerate
(`sleipnir-gen … --out src/api`) and commit. Note `--selfcheck` is one-directional
(generated ⊆ committed): extra hand-written files in `--out` are not flagged, so a `4` means a
generated file is missing or changed.

**`--lang py` rejects `--transport`.**
Python ships REST only (no Python WS/SSE runtime yet). Omit `--transport` for `--lang py`.

**Events (`subscribe`) are `undefined` / not available.**
The event backend is not bundled. `--transport rest` bundles SSE (events over SSE); `ws` bundles
WebSocket; `all` bundles both; `signalr` adds hub-streaming. Pick a capability that includes an
event backend. `py` has no events.

**`useTransport("ws")` throws "backend not bundled".**
You generated with `--transport rest` (REST + SSE only) and tried to switch to WebSocket. The
capability decides which backends are instantiated; `useTransport` only switches among the
**bundled** ones. Regenerate with `all` (or `ws`/`signalr`) to bundle WebSocket.

---

## 10. How it is verified (the gates)

Four automated gates keep the build action honest — all run in `dotnet test`:

| Gate | File | Asserts |
|---|---|---|
| **Parity** | [`SleipnirTests/Unit/Core/CsCodegenParityTests.cs`](SleipnirTests/Unit/Core/CsCodegenParityTests.cs) | The C# emitter (`Sleipnir.Codegen.Core.EmitClient`) is byte-for-byte equal to the committed `clients/codegen/test/snapshots/story01.cs/SleipnirGenerated.cs`, and the emitted file compiles against `Sleipnir.Client` (spawn-`dotnet build` compile gate). |
| **Drift detection** | [`SleipnirTests/Integration/ServerCodegenDriftTests.cs`](SleipnirTests/Integration/ServerCodegenDriftTests.cs) | The export tool against the real Story 01 assembly: happy path (exit 0, committed matches runtime), drift (tampered contract → exit 1, file untouched), regen (`--regen` → exit 0, file repaired). |
| **Generator diagnostics** | [`SleipnirTests/Unit/Core/SourceGeneratorDiagnosticsTests.cs`](SleipnirTests/Unit/Core/SourceGeneratorDiagnosticsTests.cs) | The generator's diagnostic mapping: valid contract → source emitted (no diagnostic), unknown `discoveryVersion` → `SLEIPNIR001`, malformed JSON → `SLEIPNIR002`, source suppressed on both failure paths. |
| **Wire-vs-contract** | [`SleipnirTests/Integration/DiscoveryContractTests.cs`](SleipnirTests/Integration/DiscoveryContractTests.cs) | The live Story 01 server's `GET /api/sleipnir/discovery` equals the committed codegen golden — the contract is the wire, not a sketch. |

The parity gate is the price of having two C# producers (the Roslyn build path and the TS DevUI/CI
path): one input, two producers, equal C#. It is mandatory, not optional.

CI integration: nothing special is needed — a plain `dotnet build` runs the server-side
drift-check as part of the build. **Do not** set `SLEIPNIR_REGEN_GOLDEN` in CI — that would silently
rewrite the contract instead of failing. Regeneration is a developer action that produces a commit;
CI only verifies.

```bash
dotnet build Sleipnir.sln -c Release          # server drift-check runs as part of the build
dotnet test  SleipnirTests/SleipnirTests.csproj -c Release --no-build
```

---

## 11. Relationship to the code-first default

Generation is **opt-in**. The default Sleipnir model — runtime discovery, no code generation, the
untyped `SleipnirCall` builder — is unchanged and remains the recommendation for flexibility and
zero-tooling. The generator is the counterpart for teams that want a compile-time contract
boundary and typed cross-language clients. It does not add a second protocol: the generated stubs
build on `sleipnir-client`'s `SleipnirCall` / `SleipnirTransportRouter` (TS) and `SleipnirCall` /
`ISleipnirClient` (.NET) — they are a typed wrapper over the existing wire, not a parallel one.