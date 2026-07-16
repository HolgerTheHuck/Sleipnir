# Client Generation — Stub Generators from Discovery

> **Hands-on guide:** [`CODEGEN_ONBOARDING.md`](CODEGEN_ONBOARDING.md) — wiring the server-side
> export + drift-check (`Trame.Server.Codegen`) and the client-side Roslyn source generator
> (`Trame.Generator`), the regen flow, CI, config, and troubleshooting. This document is the
> *why* and the design history.

> Operationalizes the roadmap items in [`ROADMAP.md`](ROADMAP.md) → *v1.1 Versioning &
> build-time contract* and *Later → Discovery → TypeScript codegen*. Status: Increments 1–2
> done (TS/JS/C#/Python Node emitters); the **.NET-native C# codegen trio (Roslyn source
> generator + server-side export/drift-check + parity gate) is delivered**, Node-free and
> NuGet-shipped, with the drift-detection failure path + regen + generator diagnostics now
> covered by automated tests. As of: 2026-07-15.

## Why

Trame's code-first promise — *the C# classes are the contract, no IDL, no `.proto`* — is a
**server-side** advantage. The .NET client gets a typed fluent builder (`TrameCall` /
`ITrameClient`), but every **non-.NET client** (a browser TypeScript frontend, a Node service,
a Python tool) gets the raw wire shape to hand-roll: `params` arrays of `{ parameterName, data }`,
`dependencyMapping`, the camelCase-vs-PascalCase casing contract, the `@alias` chaining rules.
There are no generated, typed stubs the way gRPC produces them from `.proto`.

That is the strongest adoption objection against Trame for polyglot shops, and it is
addressable **without giving up the code-first principle**: the runtime discovery payload
(`GET /api/trame/discovery`) already carries the full contract as structured JSON — controllers,
methods, parameters, contract type schemas. A generator turns that payload into typed client
stubs in any language. The contract source stays the C# classes; generation is a *downstream
convenience*, not a second contract.

## The decision: one TypeScript core, all languages

The generator is written **once in TypeScript** and emits stubs for **TypeScript, JavaScript,
C#, and Python**. Rationale:

- The reusable logic already exists in TypeScript — the type-name tables and static binding
  analysis in `TrameDeveloperUi/src/lib/utils/{params,dependencyCheck}.ts`, the wire + discovery
  types and the fluent builder in `clients/ts/` (`trame-client`). A TS core reuses these directly;
  a C#-based generator would have to re-port them.
- The non-.NET clients are the gap; TS/JS are the highest-value emitters first. C# already has a
  real client (`Trame.Client`) — a generated typed C# surface is an ergonomics win, not a gap-closer.
- One core, pluggable emitters per target language, means a single type-mapping table and a
  single naming/casing policy serve all outputs. The DevUI's existing inline codegen becomes a
  thin wrapper over the core (de-duplicating ~4 divergent copies of the scalar type table).

The generator lives in a new package [`clients/codegen/`](clients/codegen/) (`trame-codegen`),
depends on `trame-client` (`file:../ts`) for the wire types, the fluent builder, and
`TrameRestClient.discover()`. It ships a CLI: `npx trame-gen`.

## Build-chain fit — and why C# needs a native path

The single TS core fits the JS build chain natively, but **not** every target's chain. Node is
not a build-time dependency .NET shops can be assumed to have: their chain is SDK / MSBuild /
dotnet / NuGet, often on pinned build agents in containers behind corporate mirrors. Requiring
Node to generate the C# client is a cross-toolchain adoption blocker, and the discovery JSON
being elevated to the standard makes the consequence plain — if the JSON is the contract, C#
needs a *producer* of it that runs in the .NET edge, not the Node edge. The generator's role per
target therefore differs:

| Target | Build chain | Node-CLI fit | Prio | Native path |
|---|---|---|---|---|
| TS / JS | npm / vite / npx | native (`trame-codegen` devDependency) | A | the TS core |
| C# | MSBuild / dotnet / NuGet | **mismatch** — Node in the .NET build | A | Roslyn source generator (below) |
| Python | pip / venv / poetry | mismatch — Node in the Python build | B | none planned; Node emitter accepted |

Consequences:

- **C# is A-prio, but the A-prio deliverable is the Roslyn source generator, not the Node
  `--lang cs` emitter.** The Node `--lang cs` emitter (shipped) keeps a supporting role — the
  DevUI C# tab, the CI drift-check, quick one-off generation — and is *not* the .NET build
  solution. The DevUI is a TS/Svelte app and cannot host a Roslyn generator, so the TS emitter
  stays legitimate there.
- **Python is B-prio.** The Node `--lang py` emitter (shipped) is the deliverable; a native
  Python generator is not justified at B. Cross-ecosystem friction is accepted.
- **The Roslyn generator ports the C# emission logic to C# — it does not subprocess the TS
  emitter.** Subprocessing would pull Node back into the .NET build, defeating the point. The
  versioned discovery schema + `assertDiscoveryShape` makes a faithful C# port attackable — this
  is the "second implementer" the schema was versioned for.
- **Two C# emitters means a parity gate.** The TS `--lang cs` emitter (DevUI/CI) and the Roslyn
  generator (build) must produce equivalent C# for the same `contract.trame.json`. A conformance
  test runs both on the shared golden and asserts C# equivalence — the same pattern as
  `DiscoveryContractTests`, one level down (one input, two producers, equal C# output). This is
  the cost of the two paths; the gate keeps them honest.

## What the generator produces (Increment 1: TS + JS)

A typed client with **dependency chaining as a first-class, compile-checked surface** — not just
fluent call snippets. Concretely, from Story 01's six controllers:

```ts
const client = new TrameClient("http://localhost:5001");

// Single call — typed params + typed return.
const order = await client.order.getById(42);          // Promise<TrameResponse<Order | null>>

// The diamond, with compile-time guarantees.
const batch = client.batch();                          // mode locked to Serial (@alias needs order)
const o = batch.add(
  client.order.getById(42)
    .exposes("$.customerId", "customerId")             // JsonPathOf<Order | null> literal union —
    .exposes("$.id",           "orderId")              // $.CustomerId (PascalCase) is a compile error
    .exposes("$.shippingAddressId", "addressId"),
);
const c      = batch.add(client.customer.getById(batch.alias("@customerId"))); // number
const lines  = batch.add(
  client.orderLine.getByOrder(batch.alias("@orderId")).exposes("$[*].articleId", "articleIds"),
);
const arts   = batch.add(client.article.getByIds(batch.alias("@articleIds")));   // number[]
const stock  = batch.add(client.stock.getByArticles(batch.alias("@articleIds")));
const addr   = batch.add(client.address.getById(batch.alias("@addressId")));
const responses = await batch.send();                  // one roundtrip, six responses
```

`batch.alias("@x")` returns the **type the producer exposed** for that alias, so a consumer's
parameter typechecks against it. `JsonPathOf<T>` is a generated literal union of valid `$`-paths
for `T`, so a wrong-cased or nonexistent path is a compile error, not a runtime `400`. The raw
fluent builder (`TrameCall.batch([...])` from `trame-client`) remains available as an escape hatch.

The JS output mirrors the TS API shape with JSDoc `@typedef` blocks (IntelliSense without a
compile step); `--lang js` loses only the hard compile errors.

## Discovery as a stable spec (a prerequisite for independent implementers)

The discovery payload is the generator's input and — for anyone who wants to implement a Trame
client without this generator — the de-facto spec. It is now a **versioned, documented** contract:

- A `discoveryVersion` field atop `DiscoveryInfo` (additive-only compatibility rule; the generator's
  `assertDiscoveryShape` accepts known versions and rejects unknown ones loudly).
- A spec document — [`docs/discovery-schema.md`](docs/discovery-schema.md) — is the authoritative
  description of the `DiscoveryInfo`/`TypeRef`/`TypeMeta` shape, the scalar table, collection-kind
  semantics, enum members, nullability, default values, and the versioning rule.
- Types are carried as structured, language-neutral `TypeRef` objects (`kind` ∈
  `scalar | array | set | map | ref | stream | opaque | void`), not .NET type-name strings — so a
  non-C# producer can emit the same shape (see `discovery-driven-non-csharp-servers`).
- The generator's `assertDiscoveryShape` runtime guard validates the full `TypeRef` shape on
  ingress (kind ∈ enum, scalar `name` ∈ table, map has key+value, array/set/stream has element, ref
  resolves into `types`, enum `TypeMeta` has non-empty members).

This does not solve the bus-factor-one concern structurally, but it makes it *attackable*: with a
versioned spec + a working generator + cross-language samples, a second implementer can build a
client (or server) without reading Trame's source. The no-drift guarantee is enforced by a
conformance gate (`TrameTests/Integration/DiscoveryContractTests.cs`) that rebuilds the discovery
from the Story-01 controllers and asserts it equals the committed codegen golden
(`clients/codegen/test/fixtures/story01-discovery.json`), so a producer change that drifts the
contract fails CI.

## Type system on the wire (formerly "known gaps")

The discovery payload now carries enum members, nullability, default values, and an explicit
collection-kind flag — the gaps the schema used to carry are closed. The generator's policies:

- **Nullability** → occurrence-level NRT nullability is on the `TypeRef` (`nullable: true`); the TS
  emitter renders `T | null`, C# leaves it to `nullable()`, Python renders `Optional[...]`.
- **Enums** register as a `TypeMeta` with `kind:"enum"` + `members:[{name,value}]`; a usage site is
  `{kind:"ref", ref:"<enumKey>"}`. Trame serializes enums as their underlying **integer**, so a ref
  to an enum emits as `number`/`long`/`int` (lossless for every C# enum backing); the generator does
  **not** emit a native enum declaration — the `TypeMeta` members are documentation only.
- **Collections** → `array`/`set`/`stream` all materialize as JSON arrays → emit `T[]`
  (`Array<T>` in JSDoc) / `List<T>` / `list[T]` (the invoker consumes `IAsyncEnumerable<T>` to a
  `List<T>` at runtime, but the contract declares streaming so other servers/clients can model it).
- **Maps** → `{kind:"map", key, value}` → `Record<string,V>` / `Dictionary<K,V>` / `dict[K,V]`
  (JSON keys are strings).
- **Default values** → `ParameterMeta.defaultValue` carries a C# compile-time constant; the
  generator renders it as the parameter default in the generated signature.

## Phased plan

- **Increment 1 (done)** — `clients/codegen` core (extracted + consolidated) + **TS** and
  **JS** emitters + `trame-gen` CLI + DevUI `CodegenPage` refactor (thin wrapper over the core) +
  golden-file + compile + live e2e tests against Story 01. Verified end-to-end: the Story-01
  diamond compiles and executes in one roundtrip via the generated typed client.
- **Increment 2 (done)** — **C#** and **Python** Node emitters (`csTypeOf` / `pyTypeOf` tables in
  the core). C#: nullable reference types, `[JsonPropertyName]` camelCase, a typed
  `TrameCall.Init(...).Exposes(...).WithAlias(...)` surface. Python: `dataclasses` + async over
  `httpx`, `py.typed` marker. CLI `--lang cs|py`; DevUI C#/Python tabs. Role now: **C# emitter =
  DevUI/CI convenience** (the .NET build uses the Roslyn generator, below); **Python emitter =
  the B-prio deliverable** (no native Python generator planned).
- **Delivered (.NET-native C# trio — was the v1.1 endgame, pulled forward)** — **.NET-native C#:
  the Roslyn source-generator trio**, Node-free, NuGet-shipped. This is the A-prio C# deliverable.
  1. **Export (server side) — done.** `Trame.Server.Codegen` (net8.0 Exe, NuGet `Trame.Server.Codegen`)
     loads the built server assembly in its own process, reflects the `[TrameController]` types
     (scoped to the server output dir, not AppDomain-wide), builds a `TrameInvoker`, calls
     `GetDiscoveryInfo()`, sorts controllers by name for determinism, and serializes with the same
     `DiscoverySerialization.Options` `/api/trame/discovery` uses — writing `contract.trame.json`.
     No running HTTP server, no Node. `build/Trame.Server.Codegen.targets` runs it `AfterTargets="Build"`.
  2. **Generate (client side) — done.** `Trame.SourceGenerator` (netstandard2.0, NuGet `Trame.Generator`)
     is a Roslyn `IIncrementalGenerator` that reads `contract.trame.json` via `AdditionalFiles` and
     emits C# stubs **into the compilation**. NuGet `PackageReference`, interface break → build fails.
     The C# emission logic lives in `Trame.Codegen.Core` (a pure C# port of the TS emitter) and is
     **linked into the generator** (not subprocessed — the Roslyn analyzer load context cannot
     resolve a ProjectReference dep); the standalone `Trame.Codegen.Core` project remains as the
     parity-gate + compile-gate target. `build/Trame.Generator.props` marks the contract as an
     AdditionalFile. Diagnostics `TRAME001` (shape violation) / `TRAME002` (emit failure); the
     exception→diagnostic mapping is a testable seam (`CodegenSeam.TryEmit`).
  3. **Drift check (mandatory component) — done, and the failure path is now automated.** The
     `Trame.Server.Codegen` target regenerates the discovery at build time and **fails the build**
     (exit 1) if the committed JSON differs; `TRAME_REGEN_GOLDEN=1` regenerates instead. Without
     it this is exactly the `wsdl.exe` trap. Story 01 is the first consumer
     (`stories/01-n-plus-one-screen/contract.trame.json` committed). `TrameTests/Integration/
     ServerCodegenDriftTests.cs` automates the full negative cycle against the real Story 01
     assembly: happy path (exit 0, committed contract matches runtime), drift detection
     (tampered contract → exit 1, file untouched), and regen (`--regen` → exit 0, file repaired).
  Plus a **parity gate — done**: `Trame.Codegen.Core.EmitClient` vs the committed
  `clients/codegen/test/snapshots/story01.cs/TrameGenerated.cs` byte-for-byte, plus a spawn-`dotnet
  build` compile gate against TrameClient (`TrameTests/Unit/Core/CsCodegenParityTests.cs`). And a
  **generator diagnostic gate — done**: `TrameTests/Unit/Core/SourceGeneratorDiagnosticsTests.cs`
  exercises the `CodegenSeam.TryEmit` mapping (valid → source, no diagnostic; shape violation →
  TRAME001; malformed JSON → TRAME002; source suppressed on both failure paths). This gives .NET
  the compile-time contract boundary that is gRPC's real edge, without a second protocol.
- **Discovery schema versioning** — ✅ done: `discoveryVersion` + `docs/discovery-schema.md` +
  the structured `TypeRef` model (closing the former "known gaps") + `assertDiscoveryShape`
  version + shape gate + the `DiscoveryContractTests` no-drift conformance gate. The versioned
  schema is the prerequisite that makes the Roslyn port a faithful second implementer.
- **CI publishing** — publish `trame-client` and `trame-codegen` to npm; ship the Roslyn
  generator as a NuGet package, from `.github/workflows/` (today both Trame npm packages are local
  `file:` dependencies).
- **Deferred cleanups** — unify `BatchCodegen`'s snippet renderer into `trame-codegen/snippets`;
  `--out-mode single` emitter option for one-file output. (The legacy scalar-table copies in
  `EditorPane` / `ParamEditor` / `DependencyBuilderPage` / `tabs.svelte.ts` were consolidated into
  `TrameDeveloperUi/src/lib/utils/params.ts`, sourced from `trame-codegen`'s scalar tables.)

## Relationship to the code-first default

Generation is **opt-in**. The default Trame model — runtime discovery, no code generation, the
untyped `TrameCall` builder — is unchanged and remains the recommendation for flexibility and
zero-tooling. The generator is the counterpart for teams that want a compile-time contract
boundary and typed cross-language clients. It does not add a second protocol: the generated stubs
build on `trame-client`'s `TrameCall` / `TrameRestClient` (TS) and `TrameCall` / `ITrameClient`
(.NET) — they are a typed wrapper over the existing wire, not a parallel one.