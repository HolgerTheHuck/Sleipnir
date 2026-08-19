# Stability & Compatibility — Sleipnir 1.0.0

> What you can depend on, what is still moving, and how Sleipnir evolves without breaking you.
>
> This file is the authoritative stability promise for Sleipnir 1.0.0. It complements
> [`CHANGELOG.md`](CHANGELOG.md) (what changed) and [`ROADMAP.md`](ROADMAP.md) (what is
> planned). Where the three disagree, **this file wins** for the 1.0.0 surface.
>
> A predecessor of Sleipnir has been in internal production for years without contract breakage.
> 1.0.0 is the public, versioned expression of that proven surface — not an early prototype.

---

## TL;DR

- **Stable (1.0.0):** the wire protocol, the attribute surface (`[SleipnirController]` /
  `[SleipnirMethod]` / `[SleipnirAuthorise]` / `[SleipnirAnonymous]`), the builder (`SleipnirCall`),
  `SleipnirOptions`, `SleipnirResponse` / `SleipnirError`, `discoveryVersion`, and the client runtime
  (`ISleipnirClient`, `SleipnirRestJsonClient`, `SleipnirWebSocketClient`, `SleipnirSignalrClient`).
  A consumer built against these can upgrade within 1.x without code changes.
- **Experimental:** codegen outputs, `Arg<T>` / `Call` / `Batch` generated shapes, the
  interceptor pipeline extension points beyond the built-in logging interceptor, and every
  feature marked `[Experimental]` in this file or in `ROADMAP.md`. These may change between
  minor versions; pin the exact version if you build on them.
- **Out of scope for stability:** the Developer UI (`/Sleipnir`), the sample app (`Sleipnir/`),
  the spikes (`spikes/`), and anything under `SleipnirTests/` / `SleipnirBench/`. Treat as
  reference, not contract.

---

## 1. Stable surface (v1.0.0 → v1.x, SemVer-backed)

The following are **guaranteed stable** within the 1.x line. A breaking change to any of these
requires a 2.0.0 (SemVer major).

### 1.1 Wire protocol
- The Sleipnir request/response envelope as specified in [`PROTOCOL.md`](PROTOCOL.md):
  `SleipnirRequest` (`controller`, `method`, `params[]`, `id`, `dependencyMapping?`),
  `SleipnirResponse` (`code`, `data`, `error?`, `content?`, `id`, `exposedDependencies?`,
  `isSuccess` derived client-side).
- The JSON-RPC 2.0 compatibility adapter (`POST /api/sleipnir/jsonrpc`, `SleipnirOptions.EnableJsonRpcCompat`)
  — its mapping table is stable within 1.x.
- The single-REST-endpoint shape (`POST /api/sleipnir/json`, body-envelope-at-200) and the batch
  endpoint (`POST /api/sleipnir/json/multi`) — routes are stable.
- WebSocket path `sleipnirws` and SignalR hub path `sleipnirhub` — stable.
- `discoveryVersion` is **additive-only**: a consumer that accepts version `"1"` will continue
  to accept future `"1"`-prefixed versions; new fields may be added, existing fields keep their
  meaning. A breaking discovery-schema change bumps the version to `"2"`.

### 1.2 Attribute surface
- `[SleipnirController("name")]` and `[SleipnirController("name", AutoDiscover = false)]` — names,
  constructor signatures, and discovery semantics are stable.
- `[SleipnirMethod("name")]` — stable.
- `[SleipnirAuthorise]` and `[SleipnirAuthorise(Role = "...")]` — stable. **`[SleipnirAuthorise(Policy = "...")]`
  is stable as of Phase 1** (policy-based authorization via ASP.NET Core `IAuthorizationService`,
  `resource: null` in v1.1). `403 Forbidden` (`PermissionDenied`) is now distinguished from
  `401 Unauthorized` (`Unauthenticated`) — see `ERROR_CATALOG.md` and
  `docs/design/phase-1-interceptor-pipeline.md`.
- `[SleipnirAnonymous]` — stable (method-level opt-out from `RequireAuthentication`).
- `[SleipnirDataContract]` and `[SleipnirDataContract(Exclude = true)]` — stable.
- `[SleipnirDocumentation("...")]` / `[SleipnirExample("...")]` — stable (additive; new optional
  constructor arguments may be added in minor versions, existing ones keep meaning).

### 1.3 Builder and options
- `SleipnirCall.Init(controller, method)` fluent builder: `.With(...)`, `.With(name, value)`,
  `.Named(...)`, `.Exposes(jsonPath, alias)`, `.WithAlias(alias)`, `.ToRequest()` — stable.
- `SleipnirMultiRequest` with `ExecutionMode.Parallel` / `ExecutionMode.Serial` — stable.
- `SleipnirOptions` — all properties present in 1.0.0 are stable; new properties may be added in
  minor versions (with backward-compatible defaults), existing property names and defaults do
  not change within 1.x. Cardinality caps (`MaxParameterArrayLength`, `MaxResultElementCount`,
  `MaximumBatchSize`, `MaxDependencyPathLength`, `AllowRecursiveDescent`) keep their defaults
  within 1.x.
- `SleipnirOptions.AliasBindingMode` (`Weak` / `Strict` / `Paranoid`) — the three modes and their
  semantics (`DEPENDENCY_BINDING.md` §7) are stable. `Weak` remains the default.
- **`SleipnirOptions.UseRest` / `SleipnirOptions.UseWebSocket` (additive, default `true`)** — gate
  the unified `UseSleipnirTransports`/`MapSleipnir` pipeline. `UseRest=false` → headless mode (no
  `/json`, `/json/multi`, `/discovery`, JSON-RPC compat, or Developer UI backend — the DevUI panel
  calls those REST-group endpoints at runtime, so it is gated on `UseRest` too). `UseWebSocket=false`
  → no WebSocket transport. Together with the existing `UseSignalR` they form a symmetric
  transport-toggle trio, all default-on (non-breaking). **Caveat:** only the unified pipeline honors
  them; hosts that call the low-level `MapSleipnirEndpoints`/`UseSleipnirWebSocket` directly bypass
  the toggles. `UseSleipnirTransports` emits one `Information` startup line naming the active
  transports (`Sleipnir transports: REST=…, WebSocket=…, SignalR=…`).
- **`SleipnirOptions.Interceptors` / `SleipnirOptions.BatchInterceptors` (Phase 1, additive)** —
  collections for user interceptors; `RegisterBuiltInInterceptors` (default `true`) toggles the
  built-in Auth/Telemetry/Logging interceptors. Additive properties with safe defaults.
- **`ISleipnirInterceptor` + `SleipnirInvocationContext` (Phase 1)** — stable in the single-call
  path. The signature `InvokeAsync(SleipnirInvocationContext, SleipnirInvocationDelegate)` is stable;
  `SleipnirInvocationContext` carries `Request`, `HttpContext`, `InvokeInfo`, `Response`,
  `Activity`, `CancellationToken`. New context properties may be added additively. The batch
  path's use of the pipeline is experimental (see §2).
- **Built-in interceptors** (`SleipnirAuthorizationInterceptor`, `SleipnirTelemetryInterceptor`,
  `SleipnirLoggingInterceptor`) — stable in behavior and registration order (Auth → Telemetry →
  Logging, outer to inner). `SleipnirAuthorizationInterceptor` evaluates `[SleipnirAuthorise]` +
  `Policy` via `IAuthorizationService` (optional); `SleipnirTelemetryInterceptor` emits
  `sleipnir.*` metrics and OTel-convention logs. See `docs/design/phase-1-interceptor-pipeline.md`.

### 1.4 Response and error model
- `SleipnirResponse` / `SleipnirError` field shape and meaning — stable.
- The Sleipnir logical codes returned in `SleipnirResponse.code` (`200`, `204`, `400`, `401`, `403`,
  `404`, `409`, `413`, `499`, `500`) — stable in their current mapping. See `ERROR_CATALOG.md`
  for the authoritative catalog. A future **error taxonomy** (`ROADMAP.md` Phase 1, item A)
  adds *semantic categories* on top, not replaces the existing numeric codes.
- **`SleipnirError.Category` (Phase 1, additive)** — a new `SleipnirErrorCategory` enum field
  (`InvalidArgument`/`Unauthenticated`/`PermissionDenied`/`NotFound`/`Conflict`/
  `FailedPrecondition`/`ResourceExhausted`/`Internal`/`Unavailable`/`Cancelled`) carried on the
  wire as `error.category` (string, default `None`). Additive per STABILITY.md §3.2 — existing
  1.0.0 clients ignore it. See `ERROR_CATALOG.md`.
- `SleipnirErrorCodes` constants (Phase 1) — stable named constants for the numeric codes; replace
  the magic numbers that were scattered across `SleipnirResults` and the Invoker. The numeric
  values are unchanged from 1.0.0.
- `SleipnirResults.*` factory (`Ok`, `NotFound`, `BadRequest`, `Forbidden` (new, Phase 1),
  `Error(...)`, etc.) — stable. `Error(code, message, category, details)` takes an optional
  category; the convenience methods set it automatically.
- `SleipnirException` on the client (thrown on non-2xx body `code` from `Call<T>`) — stable.
- `OperationCanceledException` propagation semantics (cancellation surfaces as OCE, not wrapped)
  — stable.
- `ForbiddenAccessException` (Phase 1) — thrown by the auth path when authenticated but
  role/policy denied; translated to `403 Forbidden` (`PermissionDenied`). Distinct from
  `UnauthorizedAccessException` (→ `401 Unauthorized` / `Unauthenticated`).

### 1.5 Client runtime
- `ISleipnirClient` interface and the three implementations (`SleipnirRestJsonClient`,
  `SleipnirWebSocketClient`, `SleipnirSignalrClient`) — method signatures and behaviors are stable.
- Auto-reconnect behavior of `SleipnirWebSocketClient` (reject in-flight calls on drop, reconnect
  in background, new calls wait for the in-flight connect) — stable in its current shape.
- The TypeScript/JavaScript client `sleipnir-client` (`clients/ts/`) public API surface
  (`SleipnirRestClient`, `SleipnirWebSocketClient`, `SleipnirCall`, `rest.call(...)`,
  `ws.call(...)`, `withBinary`, `callBinary`, `CancelledError` / `SleipnirError`) — stable within
  1.x, SemVer on the npm package.

### 1.6 Discovery
- `GET /api/sleipnir/discovery` returns the contract described in
  [`docs/discovery-schema.md`](docs/discovery-schema.md). The structured `TypeRef` model
  (`kind` ∈ `scalar | array | set | map | ref | stream | opaque | void`), the `discoveryVersion`
  field, and the additive-only rule (§1.1) are stable.

---

## 2. Experimental surface (may change within 1.x)

These exist in 1.0.0/Phase 1 but are **not yet stability-guaranteed**. They may be renamed,
restructured, or removed in a minor version. Pin the exact Sleipnir version if you build on them.
They are expected to *graduate* into the stable surface as the corresponding `ROADMAP.md`
phases land.

- **Codegen outputs.** Everything generated by `sleipnir-gen` (`--lang ts | js | cs | py`) and by
  the Roslyn `Sleipnir.SourceGenerator`: the `SleipnirGeneratedClient`, per-controller `*Client`
  classes, `Arg<T>`, `Call`, `BatchEntry`, `Batch`, `Alias`, `Exposes` generated helpers, the
  `JsonPathOf<T>` literal-union typing. Generated code is a *projection* of the stable wire;
  when the generator's input shape changes between minor versions, regenerated output may
  change. **Pin the generator version** alongside the server version.
- **`contract.sleipnir.json`** as a committed contract artifact (the v1.1 build-time-contract
  model, `ROADMAP.md` "v1.1 — Versioning & build-time contract"). The drift-check is a
  build-time guarantee, not yet a stable public surface — its CLI flags and target names may
  settle in 1.1.
- **Interceptor pipeline in the batch path.** Phase 1 lands the pipeline (`ISleipnirInterceptor`
  + `SleipnirInvocationContext`) as stable in the single-call path, and registers built-in
  interceptors (Auth/Telemetry/Logging). The *batch path* (`ExecuteInParallel`/
  `ExecuteSequentially`/`ExecuteInDependencyBatches`) runs Auth via the serial pre-pass and
  Tracing/Metrics via direct calls in `ExecuteAuthorized`/`TraceCallError` — **not** through
  the per-element interceptor pipeline. User interceptors registered via
  `SleipnirOptions.Interceptors` currently run *only* in the single-call path. Routing the batch
  path through the per-element pipeline (so user interceptors run for batch elements too) is a
  post-Phase-1 refactor that must preserve the serial-auth-pre-pass constraint. See
  `docs/design/phase-1-interceptor-pipeline.md` step 7.
- **`ISleipnirBatchInterceptor`** (Phase 1) — the batch-level interceptor surface exists and is
  registered via `SleipnirOptions.BatchInterceptors`, but no built-in batch interceptor ships yet
  (batch metrics are emitted directly in `InvokeDi(IEnumerable)` via `SleipnirMetrics.RecordBatch`).
  The interface is stable in shape; the built-in batch interceptors that will use it are
  post-Phase-1.
- **`EnableJsonRpcCompat` adapter limitations** are documented (no `@alias` chaining, no
  execution-mode selection, no binary out-of-band, no streaming). The adapter is stable in
  what it *does*; what it *does not do* may change as it graduates.
- **Developer UI** (`/Sleipnir`). Its layout, tabs, history, codegen panel, and dependency builder
  are conveniences, not contract. The DevUI reflects the stable discovery; the DevUI itself is
  free to evolve.
- **Telemetry `sleipnir.*` metric instruments** as currently emitted (`sleipnir.call.duration`,
  `sleipnir.call.count`, `sleipnir.error.count`, `sleipnir.batch.fan_out`, `sleipnir.batch.count`,
  `sleipnir.event.dropped` (Phase 3), and the live gauges `sleipnir.ws.connections` /
  `sleipnir.subscriptions.active`). The `Meter "Sleipnir"` name and the OTel RPC span tag names
  (`rpc.system`/`rpc.service`/`rpc.method`) are stable; the *metric instrument names and tag
  keys* are stable in Phase 1/3 but may gain additional instruments/tags in minor versions
  (additive). See `ERROR_CATALOG.md` §6.
- **Observability endpoints** — `GET /api/sleipnir/metrics` (Prometheus-text scrape, opt-in via
  `Sleipnir.Telemetry`'s `AddSleipnirPrometheusMetrics` + `UseSleipnirPrometheusScrapingEndpoint`)
  and `GET /api/sleipnir/observability` (JSON snapshot, opt-in via
  `SleipnirOptions.EnableObservability`), plus the backing `SleipnirConnectionRegistry` and the
  DevUI Observability panel. Both are RequireAuth-gated like `/discovery`. The **Prometheus-text
  `/metrics` interface is the durable contract** (any scraper / embedded stack reads it); the JSON
  snapshot shape and the DevUI panel are conveniences that may settle in a minor version.
  Experimental in v1 — see `PROTOCOL.md` → Observability Endpoints.
- **Events / Server-Push (Phase 3)** — `[SleipnirEvent]` attribute (the **required marker** for
  event methods as of 1.2.0 — a `[SleipnirMethod]` method returning `IObservable<T>` is rejected at
  registration with a migration message; plain calls to event methods return `400`), `IObservable<T>`
  subscribe surface, `ISleipnirCore.SubscribeAsync`, the WS subscribe/unsubscribe/event-frame wire
  (`kind:"subscribe"`/`kind:"unsubscribe"`/`{type:"event",...}`), the `SleipnirSubscriptionManager`,
  and `sleipnir.event.dropped` metric. **Experimental in v1**: the wire format, subscription
  lifecycle (pro-Connection, client-side re-subscribe, at-most-once-while-disconnected),
  and backpressure (configurable per-subscription buffer: `EventBackpressureStrategy`
  `DropOldest`/`DropWrite`/`Block`/`Unbounded` + `EventBufferCapacity`, overridable per event via
  `[SleipnirEvent]`) are implemented but may settle in a minor version. WS-only in v1; SignalR and
  REST-Long-Polling are out of scope. `Last-Event-Id`-resume and server-side buffer are v1.x+. See
  `docs/design/phase-3-events.md`.
- **The `samples/`, `spikes/`, `stories/`, and `Sleipnir/` (sample app) projects.** Reference and
  demo material; no stability promise.

---

## 3. Compatibility rules (how 1.x evolves)

1. **No silent breaking changes.** Any change that would require a consumer to update code or
   change behavior at a stable seam triggers a SemVer major (2.0.0).
2. **Additive changes are minor (1.x.0).** New `SleipnirOptions` properties (with safe defaults),
   new `[Sleipnir*]` attributes, new `SleipnirResults.*` factory methods, new discovery `kind` values,
   new `sleipnir.*` metric instruments — all additive, all backward-compatible, all minor.
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

Sleipnir v1 has **no built-in API versioning mechanism** (see `README_DETAILS.md` *Known
Limitations*). Versioning is a convention: `[SleipnirController("Customer.v1")]` and
`Customer.v2` coexist as two dictionary entries; the client selects via the `controller`
field. This is stable and recommended.

Build-time version enforcement (source generator burning the version constant into the stub,
drift-check failing the build) is the v1.1 endgame (`ROADMAP.md` "v1.1 — Versioning &
build-time contract") and is **experimental** until it lands. The routing-key convention itself
is stable.

---

## 5. What is explicitly out of scope for stability

- **Native AOT.** Sleipnir uses runtime reflection and `Expression.Compile`. Native AOT is not
  supported in 1.x (see `ROADMAP.md` for the AOT-via-codegen strategic direction, which would
  graduate into a stability promise only after landing).
- **Performance numbers.** Benchmark results in `SleipnirBench/` are measurements, not
  guarantees. Sleipnir's internal design (pre-compiled delegates, single-pass response parsing,
  serial auth pre-pass) is stable; the resulting numbers are not a contract.
- **The exact JSON property order** in serialized responses. Sleipnir uses
  `System.Text.Json` with camelCase output; property order is not guaranteed stable. Consumers
  must not rely on field order.
- **The DevUI's HTML/JS/CSS.** Stable in what it reflects (discovery), free to evolve in form.

---

## 6. How to consume this file

- **Building a Sleipnir client (any language):** rely on §1 (stable). Pin the Sleipnir server
  version; upgrade within 1.x without code changes.
- **Using codegen output:** pin both the Sleipnir server version **and** the generator version.
  Regenerate when you upgrade either. See `CLIENT_GENERATION.md`.
- **Writing a custom interceptor:** target the 1.0.0 `ISleipnirInterceptor`, but expect a
  possible adjustment in a 1.x minor when Phase 1 lands. Track `ROADMAP.md`.
- **Operating Sleipnir in production:** §1.4 (error model) and §3.6 (defaults don't tighten) are
  your operational invariants. The security posture is described in `SECURITY.md`; the
  `RequireAuthentication` default-deny behavior is stable.
- **Evaluating Sleipnir for adoption:** §1 is the surface your code will bind to; §2 is what is
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