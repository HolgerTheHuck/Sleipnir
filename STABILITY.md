# Stability & Compatibility — Trame 1.0.0

> What you can depend on, what is still moving, and how Trame evolves without breaking you.
>
> This file is the authoritative stability promise for Trame 1.0.0. It complements
> [`CHANGELOG.md`](CHANGELOG.md) (what changed) and [`ROADMAP.md`](ROADMAP.md) (what is
> planned). Where the three disagree, **this file wins** for the 1.0.0 surface.
>
> A predecessor of Trame has been in internal production for years without contract breakage.
> 1.0.0 is the public, versioned expression of that proven surface — not an early prototype.

---

## TL;DR

- **Stable (1.0.0):** the wire protocol, the attribute surface (`[TrameController]` /
  `[TrameMethod]` / `[TrameAuthorise]` / `[TrameAnonymous]`), the builder (`TrameCall`),
  `TrameOptions`, `TrameResponse` / `TrameError`, `discoveryVersion`, and the client runtime
  (`ITrameClient`, `TrameRestJsonClient`, `TrameWebSocketClient`, `TrameSignalrClient`).
  A consumer built against these can upgrade within 1.x without code changes.
- **Experimental:** codegen outputs, `Arg<T>` / `Call` / `Batch` generated shapes, the
  interceptor pipeline extension points beyond the built-in logging interceptor, and every
  feature marked `[Experimental]` in this file or in `ROADMAP.md`. These may change between
  minor versions; pin the exact version if you build on them.
- **Out of scope for stability:** the Developer UI (`/Trame`), the sample app (`Trame/`),
  the spikes (`spikes/`), and anything under `TrameTests/` / `TrameBench/`. Treat as
  reference, not contract.

---

## 1. Stable surface (v1.0.0 → v1.x, SemVer-backed)

The following are **guaranteed stable** within the 1.x line. A breaking change to any of these
requires a 2.0.0 (SemVer major).

### 1.1 Wire protocol
- The Trame request/response envelope as specified in [`PROTOCOL.md`](PROTOCOL.md):
  `TrameRequest` (`controller`, `method`, `params[]`, `id`, `dependencyMapping?`),
  `TrameResponse` (`code`, `data`, `error?`, `content?`, `id`, `exposedDependencies?`,
  `isSuccess` derived client-side).
- The JSON-RPC 2.0 compatibility adapter (`POST /api/trame/jsonrpc`, `TrameOptions.EnableJsonRpcCompat`)
  — its mapping table is stable within 1.x.
- The single-REST-endpoint shape (`POST /api/trame/json`, body-envelope-at-200) and the batch
  endpoint (`POST /api/trame/json/multi`) — routes are stable.
- WebSocket path `tramews` and SignalR hub path `tramehub` — stable.
- `discoveryVersion` is **additive-only**: a consumer that accepts version `"1"` will continue
  to accept future `"1"`-prefixed versions; new fields may be added, existing fields keep their
  meaning. A breaking discovery-schema change bumps the version to `"2"`.

### 1.2 Attribute surface
- `[TrameController("name")]` and `[TrameController("name", AutoDiscover = false)]` — names,
  constructor signatures, and discovery semantics are stable.
- `[TrameMethod("name")]` — stable.
- `[TrameAuthorise]` and `[TrameAuthorise(Role = "...")]` — stable. **The `Policy` argument is
  experimental** (planned, see `ROADMAP.md` Phase 1) until the interceptor pipeline lands; until
  then, role-based authorization is the stable form.
- `[TrameAnonymous]` — stable (method-level opt-out from `RequireAuthentication`).
- `[TrameDataContract]` and `[TrameDataContract(Exclude = true)]` — stable.
- `[TrameDocumentation("...")]` / `[TrameExample("...")]` — stable (additive; new optional
  constructor arguments may be added in minor versions, existing ones keep meaning).

### 1.3 Builder and options
- `TrameCall.Init(controller, method)` fluent builder: `.With(...)`, `.With(name, value)`,
  `.Named(...)`, `.Exposes(jsonPath, alias)`, `.WithAlias(alias)`, `.ToRequest()` — stable.
- `TrameMultiRequest` with `ExecutionMode.Parallel` / `ExecutionMode.Serial` — stable.
- `TrameOptions` — all properties present in 1.0.0 are stable; new properties may be added in
  minor versions (with backward-compatible defaults), existing property names and defaults do
  not change within 1.x. Cardinality caps (`MaxParameterArrayLength`, `MaxResultElementCount`,
  `MaximumBatchSize`, `MaxDependencyPathLength`, `AllowRecursiveDescent`) keep their defaults
  within 1.x.
- `TrameOptions.AliasBindingMode` (`Weak` / `Strict` / `Paranoid`) — the three modes and their
  semantics (`DEPENDENCY_BINDING.md` §7) are stable. `Weak` remains the default.

### 1.4 Response and error model
- `TrameResponse` / `TrameError` field shape and meaning — stable.
- The Trame logical codes returned in `TrameResponse.code` (`200`, `204`, `400`, `401`, `404`,
  `413`, `499`, `500`) — stable in their current mapping. A future **error taxonomy**
  (`ROADMAP.md` Phase 1, item A) will add *semantic categories* on top, not replace the existing
  numeric codes.
- `TrameResults.*` factory (`Ok`, `NotFound`, `BadRequest`, `Error(...)`, etc.) — stable.
- `TrameException` on the client (thrown on non-2xx body `code` from `Call<T>`) — stable.
- `OperationCanceledException` propagation semantics (cancellation surfaces as OCE, not wrapped)
  — stable.

### 1.5 Client runtime
- `ITrameClient` interface and the three implementations (`TrameRestJsonClient`,
  `TrameWebSocketClient`, `TrameSignalrClient`) — method signatures and behaviors are stable.
- Auto-reconnect behavior of `TrameWebSocketClient` (reject in-flight calls on drop, reconnect
  in background, new calls wait for the in-flight connect) — stable in its current shape.
- The TypeScript/JavaScript client `trame-client` (`clients/ts/`) public API surface
  (`TrameRestClient`, `TrameWebSocketClient`, `TrameCall`, `rest.call(...)`,
  `ws.call(...)`, `withBinary`, `callBinary`, `CancelledError` / `TrameError`) — stable within
  1.x, SemVer on the npm package.

### 1.6 Discovery
- `GET /api/trame/discovery` returns the contract described in
  [`docs/discovery-schema.md`](docs/discovery-schema.md). The structured `TypeRef` model
  (`kind` ∈ `scalar | array | set | map | ref | stream | opaque | void`), the `discoveryVersion`
  field, and the additive-only rule (§1.1) are stable.

---

## 2. Experimental surface (may change within 1.x)

These exist in 1.0.0 but are **not yet stability-guaranteed**. They may be renamed, restructured,
or removed in a minor version. Pin the exact Trame version if you build on them. They are
expected to *graduate* into the stable surface as the corresponding `ROADMAP.md` phases land.

- **Codegen outputs.** Everything generated by `trame-gen` (`--lang ts | js | cs | py`) and by
  the Roslyn `Trame.SourceGenerator`: the `TrameGeneratedClient`, per-controller `*Client`
  classes, `Arg<T>`, `Call`, `BatchEntry`, `Batch`, `Alias`, `Exposes` generated helpers, the
  `JsonPathOf<T>` literal-union typing. Generated code is a *projection* of the stable wire;
  when the generator's input shape changes between minor versions, regenerated output may
  change. **Pin the generator version** alongside the server version.
- **`contract.trame.json`** as a committed contract artifact (the v1.1 build-time-contract
  model, `ROADMAP.md` "v1.1 — Versioning & build-time contract"). The drift-check is a
  build-time guarantee, not yet a stable public surface — its CLI flags and target names may
  settle in 1.1.
- **Interceptor pipeline extension points** beyond the built-in `TrameLoggingInterceptor`.
  `ITrameInterceptor` exists, but the Phase 1 work (`ROADMAP.md`) will reshape the pipeline to
  host Auth/OTel/error-classification as first-class interceptors. Custom interceptors written
  against the 1.0.0 `ITrameInterceptor` may need adjustment in a 1.x minor.
- **`EnableJsonRpcCompat` adapter limitations** are documented (no `@alias` chaining, no
  execution-mode selection, no binary out-of-band, no streaming). The adapter is stable in
  what it *does*; what it *does not do* may change as it graduates.
- **Developer UI** (`/Trame`). Its layout, tabs, history, codegen panel, and dependency builder
  are conveniences, not contract. The DevUI reflects the stable discovery; the DevUI itself is
  free to evolve.
- **Telemetry `trame.*` span/tag names** as currently emitted. `rpc.system`, `rpc.service`,
  `rpc.method` follow OTel RPC semantic conventions (stable); the Trame-specific tag names
  (`trame.request_id`, `trame.batch.*`, `trame.binary.length`) are stable in 1.0.0 but the
  **metrics** added in Phase 1 (`trame.call.duration`, `trame.call.count`,
  `trame.batch.fan_out`, `trame.error.rate`) are new and will settle as the pipeline lands.
- **The `samples/`, `spikes/`, `stories/`, and `Trame/` (sample app) projects.** Reference and
  demo material; no stability promise.

---

## 3. Compatibility rules (how 1.x evolves)

1. **No silent breaking changes.** Any change that would require a consumer to update code or
   change behavior at a stable seam triggers a SemVer major (2.0.0).
2. **Additive changes are minor (1.x.0).** New `TrameOptions` properties (with safe defaults),
   new `[Trame*]` attributes, new `TrameResults.*` factory methods, new discovery `kind` values,
   new `trame.*` metric instruments — all additive, all backward-compatible, all minor.
3. **`discoveryVersion` is additive-only within `"1"`.** New fields may appear; existing fields
   keep meaning. A breaking discovery-schema change bumps to `"2"` and ships in a 2.0.0.
4. **Wire-compatibility is the contract.** A 1.x server must serve a 1.0.0 client without the
   client needing changes, and vice versa, for everything in §1. The envelope-at-200, the
   logical `code` in the body, the `params[].parameterName` binding, the `@alias` JsonPath
   semantics against camelCase output — these are the load-bearing invariants.
5. **Error code numbers in §1.4 are fixed.** Future semantic categories (Phase 1, item A) layer
   on top (an `error.category` field or similar), they do not renumber existing codes.
6. **Defaults do not tighten in 1.x.** A cap, a rate limit, a binding mode default that is
   permissive in 1.0.0 stays permissive within 1.x. Tightening a default is a behavior change
   and goes to 2.0.0. (Adopters who want tighter posture set the option explicitly.)
7. **Experimental surface changes are noted in `CHANGELOG.md`** under the version that changes
   them, with a migration note where applicable. They do not require a SemVer major.
8. **The `ROADMAP.md` "Benutzbarkeit-Roadmap" phases each declare**, when they land, whether
   they promote an experimental surface to stable. Each phase's graduation is a `CHANGELOG.md`
   entry.

---

## 4. Versioning model (routing key, not gate)

Trame v1 has **no built-in API versioning mechanism** (see `README_DETAILS.md` *Known
Limitations*). Versioning is a convention: `[TrameController("Customer.v1")]` and
`Customer.v2` coexist as two dictionary entries; the client selects via the `controller`
field. This is stable and recommended.

Build-time version enforcement (source generator burning the version constant into the stub,
drift-check failing the build) is the v1.1 endgame (`ROADMAP.md` "v1.1 — Versioning &
build-time contract") and is **experimental** until it lands. The routing-key convention itself
is stable.

---

## 5. What is explicitly out of scope for stability

- **Native AOT.** Trame uses runtime reflection and `Expression.Compile`. Native AOT is not
  supported in 1.x (see `ROADMAP.md` for the AOT-via-codegen strategic direction, which would
  graduate into a stability promise only after landing).
- **Performance numbers.** Benchmark results in `TrameBench/` are measurements, not
  guarantees. Trame's internal design (pre-compiled delegates, single-pass response parsing,
  serial auth pre-pass) is stable; the resulting numbers are not a contract.
- **The exact JSON property order** in serialized responses. Trame uses
  `System.Text.Json` with camelCase output; property order is not guaranteed stable. Consumers
  must not rely on field order.
- **The DevUI's HTML/JS/CSS.** Stable in what it reflects (discovery), free to evolve in form.

---

## 6. How to consume this file

- **Building a Trame client (any language):** rely on §1 (stable). Pin the Trame server
  version; upgrade within 1.x without code changes.
- **Using codegen output:** pin both the Trame server version **and** the generator version.
  Regenerate when you upgrade either. See `CLIENT_GENERATION.md`.
- **Writing a custom interceptor:** target the 1.0.0 `ITrameInterceptor`, but expect a
  possible adjustment in a 1.x minor when Phase 1 lands. Track `ROADMAP.md`.
- **Operating Trame in production:** §1.4 (error model) and §3.6 (defaults don't tighten) are
  your operational invariants. The security posture is described in `SECURITY.md`; the
  `RequireAuthentication` default-deny behavior is stable.
- **Evaluating Trame for adoption:** §1 is the surface your code will bind to; §2 is what is
  still moving. The asymmetry is intentional — the core is frozen, the edges are explicit.

---

## 7. Relationship to the other documents

| Document | Role |
|---|---|
| **`STABILITY.md`** (this file) | What is stable vs. experimental, and the compatibility rules |
| [`CHANGELOG.md`](CHANGELOG.md) | What changed in each version, with migration notes |
| [`ROADMAP.md`](ROADMAP.md) | What is planned, including which experimental surfaces graduate to stable |
| [`PROTOCOL.md`](PROTOCOL.md) | The authoritative wire format (referenced by §1.1) |
| [`SECURITY.md`](SECURITY.md) | The security posture and the `RequireAuthentication` default-deny guarantee |
| [`DEPENDENCY_BINDING.md`](DEPENDENCY_BINDING.md) | The `@alias` binding semantics (referenced by §1.3) |
| [`docs/discovery-schema.md`](docs/discovery-schema.md) | The discovery `TypeRef` schema (referenced by §1.6) |
| `README_DETAILS.md` *Known Limitations* | Deliberate v1 scope decisions (non-bugs); consistent with §2 and §5 |