# Sleipnir Observability — User Reference

A consolidated lookup reference for **observability** in Sleipnir: the two
opt-in HTTP surfaces (the JSON `/observability` snapshot built into
`SleipnirRest`, and the Prometheus-text `/metrics` scrape endpoint provided by
the optional `Sleipnir.Telemetry` package), the `sleipnir.*` metric instruments,
the `SleipnirConnectionRegistry` that backs the JSON snapshot, the
double-bookkeeping (parallel Interlocked accumulators + write-only OTel
instruments), the gauge-`Current` fix, the DevUI observability panel, the
`EnableObservability` opt-in, and the Heimdall durable-contract note.

**Scope boundary:** this doc covers **metrics + the observability endpoints**.
**Tracing** (the `SleipnirCore.Tracing.SleipnirTracing` spans/activities) is a
separate concern, covered in `TRACING_TELEMETRY_REFERENCE.md`. The two share
the `SleipnirCore.Tracing` namespace and the `sleipnir-tracing` xUnit collection
but are independent concerns: tracing does not require metrics and vice versa.

This is a **reference**, not a tutorial. When a metric does not scrape, or the
JSON snapshot is empty, look here first — the two-surface model, the instrument
table with durable symbol citations, the snapshot field table, the double-
bookkeeping rationale, the option/auth table, a diagnostics catalog, and a map
of where the deeper docs live. For the stability classification read
`STABILITY.md` §"2. Experimental surface (may change within 1.x)"; for the
production wiring read `guide/chapters/10-production.md` + `PROTOCOL.md`
§"Observability Endpoints (experimental, opt-in)". This doc consolidates those
and links back for depth.

All citations anchor on the durable symbol or a short verbatim quote at the
cited site (no line numbers — those drift on every edit). Code-facing text is
English per `CLAUDE.md`.

## Table of contents

1. [The observability model — two surfaces](#1-the-observability-model--two-surfaces)
2. [The `/observability` JSON endpoint](#2-the-observability-json-endpoint)
3. [The `/metrics` Prometheus endpoint](#3-the-metrics-prometheus-endpoint)
4. [`SleipnirMetrics` instruments](#4-sleipnirmetrics-instruments)
5. [`SleipnirConnectionRegistry` — the snapshot backing](#5-sleipnirconnectionregistry--the-snapshot-backing)
6. [The double-bookkeeping](#6-the-double-bookkeeping)
7. [The gauge-`Current` fix & flake](#7-the-gauge-current-fix--flake)
8. [The DevUI observability panel](#8-the-devui-observability-panel)
9. [`EnableObservability` & auth gates](#9-enableobservability--auth-gates)
10. [The Heimdall durable-contract note](#10-the-heimdall-durable-contract-note)
11. [Diagnostics & troubleshooting catalog](#11-diagnostics--troubleshooting-catalog)
12. [How it is verified (the tests)](#12-how-it-is-verified-the-tests)
13. [Relationship to other docs](#13-relationship-to-other-docs)

---

## 1. The observability model — two surfaces

Sleipnir exposes observability through **two separate opt-in surfaces**, not
one. Getting this wrong is the most common confusion:

| Surface | Format | Lives in | Gated by | Reads from |
|---------|--------|----------|----------|------------|
| **`/observability`** | JSON snapshot | `SleipnirRest` (in-framework, no OTel SDK) | `EnableObservability` + `RequireAuthentication` | `SleipnirConnectionRegistry` (parallel Interlocked accumulators) |
| **`/metrics`** | Prometheus text | `SleipnirTelemetry` (optional package, OTel SDK) | its own `requireAuth` + `RequireAuthentication` | the OTel `Meter "Sleipnir"` instruments (fed by `SleipnirMetrics`) |

**There is no built-in `/metrics` endpoint in `SleipnirRest`.** An exhaustive
grep of `SleipnirRest/**/*.cs` for `MapGet`/`Prometheus`/`/metrics` returns only
`/discovery`, `/observability`, and the SSE `/events/{...}` routes — no
`MapSleipnirMetricsEndpoint` or similar exists anywhere in the repo. The
`/metrics` scrape endpoint is **exclusively** provided by the optional
`Sleipnir.Telemetry` package via `UseSleipnirPrometheusScrapingEndpoint`
(`SleipnirTelemetry/SleipnirPrometheusExtensions.cs` →
`UseSleipnirPrometheusScrapingEndpoint`), which delegates to the OTel SDK's
`app.UseOpenTelemetryPrometheusScrapingEndpoint(path)`.
`SleipnirServer` does not reference `SleipnirTelemetry` — the OTel SDK
dependencies stay out of the all-in-one bundle (`README_DETAILS.md` §"Metrics &
observability endpoints (experimental, opt-in)").

The double-bookkeeping (§6) exists precisely so `/observability` can be read
**without** an OTel `MetricReader` subscribed — the registry's parallel
`Interlocked` accumulators back the JSON snapshot. `/metrics` still needs the
OTel SDK + a subscribed Prometheus exporter.

The two surfaces are **separate opt-ins** (`SleipnirHub/Extensions/SleipnirOptions.cs`
→ `EnableObservability` + `RequireAuthentication`): `EnableObservability` gates
only `/observability`; `/metrics` is gated solely by its own `requireAuth`
parameter + `RequireAuthentication`. Push (OTLP via `AddSleipnirTelemetry`) and
pull (Prometheus scrape) do not conflict (`CHANGELOG.md` §"Added — Observability:
/metrics scrape + /observability snapshot + DevUI panel (experimental)",
`PROTOCOL.md` §"GET /api/sleipnir/metrics — Prometheus text scrape").

---

## 2. The `/observability` JSON endpoint

`GET {prefix}/observability` (default `/api/sleipnir/observability`).

- **Route:** `SleipnirRest/SleipnirEndpointExtensions.cs` → `MapSleipnirEndpoints`
  — the `group.MapGet("/observability", ...)` call inside the `MapSleipnirEndpoints`
  group (default prefix `/api/sleipnir`).
- **`EnableObservability` gate:** `MapSleipnirEndpoints` — `if (enableObservability) { ... }`.
  When `false` (default) the route is never registered → 404
  (`SleipnirTests/Integration/ObservabilityEndpointTests.cs` → `Observability_Disabled_NotMapped_Returns404`).
- **Auth gate:** `MapSleipnirEndpoints` — 401 when `RequireAuthentication && !IsAuthenticated`
  (same transport-level gate as `/discovery`).
- **Response shape** (`MapSleipnirEndpoints` — the `Results.Json` anonymous payload):

| Field | Source |
|-------|--------|
| `transports` | `{ rest = true, webSocket = useWebSocket, signalR = signalREnabled, sse = useSse }` (threaded from endpoint options, not the registry — see the comment above the payload) |
| `activeConnections` | `snap.ActiveConnections` |
| `activeSubscriptions` | `snap.ActiveSubscriptions` |
| `eventDroppedTotal` | `snap.EventDroppedTotal` |
| `callCount` | `snap.CallCount` |
| `errorCount` | `snap.ErrorCount` |
| `batchCount` | `snap.BatchCount` |
| `uptimeMs` | `(long)(DateTimeOffset.UtcNow - registry.StartedAtUtc).TotalMilliseconds` (added by the endpoint, not on the snapshot type) |

The `ObservabilitySnapshot` C# shape (`SleipnirConnectionRegistry.cs` →
`ObservabilitySnapshot`) carries `ActiveConnections`/`ActiveSubscriptions`/
`EventDroppedTotal`/`CallCount`/`ErrorCount`/`BatchCount`. `transports` and
`uptimeMs` are **not** on the snapshot type — they are added by the endpoint's
anonymous payload. The TS mirror is `SleipnirDeveloperUi/src/lib/api/client.ts`
→ `interface ObservabilitySnapshot`.

---

## 3. The `/metrics` Prometheus endpoint

`GET {apiPath}/metrics` (default `/api/sleipnir/metrics`) — **only via
`Sleipnir.Telemetry`**.

- **Use:** `SleipnirTelemetry/SleipnirPrometheusExtensions.cs` →
  `UseSleipnirPrometheusScrapingEndpoint(path, requireAuth)`. It delegates to
  `app.UseOpenTelemetryPrometheusScrapingEndpoint(path)` (the OTel SDK's
  terminal middleware). Default path `/api/sleipnir/metrics`.
- **Add:** `AddSleipnirPrometheusMetrics()` subscribes the `Meter "Sleipnir"` and
  attaches the Prometheus exporter (`AddMeter(SleipnirMetrics.MeterName)` +
  `AddPrometheusExporter()`).
- **Dependency:** `OpenTelemetry.Exporter.Prometheus.AspNetCore` 1.16.0-beta.1
  (`SleipnirTelemetry/SleipnirTelemetry.csproj` → `PackageReference`
  `"OpenTelemetry.Exporter.Prometheus.AspNetCore"`).
- **Auth gate (independent of `/observability`):** `UseSleipnirPrometheusScrapingEndpoint`
  — a `Use` middleware runs before the terminal scrape middleware; if
  `requireAuth` (default `true`) and `ISleipnirCore.RequireAuthentication` is on
  and the caller is unauthenticated, it short-circuits to 401. It resolves
  `ISleipnirCore` per-request from request-scoped DI, so no `SleipnirHub`
  dependency (see the class doc comment).
- **Not gated by `EnableObservability`** — `/metrics` is gated solely by its own
  `requireAuth` + `RequireAuthentication` (§9).

`AddSleipnirTelemetry` also subscribes the metrics **push** path (OTLP):
`SleipnirTelemetry/SleipnirTelemetryServiceExtensions.cs` → `AddSleipnirTelemetry`
(the `WithMetrics` block: `.AddMeter(SleipnirMetrics.MeterName) ...`). The pull
Prometheus path is additive via `AddSleipnirPrometheusMetrics()` +
`UseSleipnirPrometheusScrapingEndpoint()` (see the comment on the `WithMetrics`
block). Push and pull do not conflict.

---

## 4. `SleipnirMetrics` instruments

`SleipnirCore/Tracing/SleipnirMetrics.cs` → `SleipnirMetrics` (`public static class`).
Meter: `MeterName = "Sleipnir"`, `Meter = new(MeterName, "1.0.0")` (internal). The
`Meter` and the tracing `ActivitySource` share the name `"Sleipnir"` — OTel
allows this (`docs/design/phase-1-interceptor-pipeline.md` §Entscheidung 4 —
Meter-Name "Sleipnir").

| Instrument | Name | Type | Unit |
|------------|------|------|------|
| `CallDuration` | `sleipnir.call.duration` | Histogram<double> | `ms` |
| `CallCount` | `sleipnir.call.count` | Counter<long> | `{call}` |
| `ErrorCount` | `sleipnir.error.count` | Counter<long> | `{call}` |
| `EventDroppedCounter` | `sleipnir.event.dropped` | Counter<long> | `{event}` |
| `BatchFanOut` | `sleipnir.batch.fan_out` | Histogram<int> | `{request}` |
| `BatchCount` | `sleipnir.batch.count` | Counter<long> | `{batch}` |
| `_connectionsGauge` | `sleipnir.ws.connections` | ObservableGauge<int> | `{connection}` |
| `_subscriptionsGauge` | `sleipnir.subscriptions.active` | ObservableGauge<int> | `{subscription}` |

**Record methods:**

| Method | Records |
|--------|---------|
| `RecordCall(request, response, durationMs, category)` | `CallDuration.Record`, `CallCount.Add`, `ErrorCount.Add` on failure. Tags: `rpc.system`, `rpc.service`, `rpc.method`, `sleipnir.error_category`, `sleipnir.success`. |
| `RecordBatch(requests, mode)` | `BatchFanOut.Record`, `BatchCount.Add`. Tags: `rpc.system`, `sleipnir.batch.mode`. |
| `EventDropped(subscriptionId)` | Tags: `rpc.system`, `sleipnir.subscription_id`. |
| `SetConnectionRegistry(registry)` | Installs the two ObservableGauges (idempotent via `??=`). |

**Call sites:** `RecordCall` from the telemetry interceptor
`SleipnirHub/Interceptors/SleipnirTelemetryInterceptor.cs` → `RecordTelemetry`
(calls `SleipnirMetrics.RecordCall`) and the invoker error paths
`SleipnirCore/Services/SleipnirInvoker.cs` → `ExecuteAuthorized` (catch), the
local `Status()`, and `TraceCallError` — three `RecordCall` sites. `RecordBatch`
from `SleipnirInvoker.cs` → the batch dispatcher. `EventDropped` from
`SleipnirCore/Events/EventObserver.cs` → `OnDropped()`,
`SleipnirRest/Sse/SleipnirSseConnection.cs` → `OnDropped()`,
`SleipnirHub/Hub/SleipnirHub.cs` → `PumpDurableAsync` (local `OnDropped`), and
`SleipnirCore/Services/SleipnirSubscriptionStore.cs` → `OnDropped(string)`.

---

## 5. `SleipnirConnectionRegistry` — the snapshot backing

`SleipnirCore/Tracing/SleipnirConnectionRegistry.cs` → `SleipnirConnectionRegistry`
(`public sealed class`). The in-process state backing the `/observability` JSON
snapshot.

| Concern | Members |
|---------|---------|
| **Live counts** (Interlocked) | `_connections`, `_subscriptions` fields; readers `Connections`/`Subscriptions` (via `Interlocked.CompareExchange(ref _x, 0, 0)`); mutators `IncConnection`/`DecConnection`/`IncSubscription`/`DecSubscription` |
| **Cumulative counters** (Interlocked long) | `_eventDroppedTotal`, `_callCount`, `_errorCount`, `_batchCount` fields; read via `Interlocked.Read` and surfaced through `GetSnapshot()`; mutators `RecordEventDrop`, `RecordCall(bool success)`, `RecordBatch` |
| **`GetSnapshot()`** | Returns `ObservabilitySnapshot` with `ActiveConnections`/`ActiveSubscriptions`/`EventDroppedTotal`/`CallCount`/`ErrorCount`/`BatchCount` |
| **`StartedAtUtc`** | `DateTimeOffset.UtcNow` at construction; used for `/observability` uptime |
| **Process-global singleton** | `_instance` field, `Instance` (throws if not registered), `Current` (nullable, internal), `SetInstance` |

`Current` is what `SleipnirMetrics` gauge callbacks and the record methods read
(§7). **DI wiring:** `AddSleipnir` does `new SleipnirConnectionRegistry()` →
`SetInstance(...)` → `SleipnirMetrics.SetConnectionRegistry(...)` →
`services.AddSingleton(...)` (`SleipnirHub/Extensions/SleipnirServiceCollectionExtension.cs`
→ `AddSleipnir`, the registry eager-wiring block). So the registry + gauges are
**always active** once `AddSleipnir` runs, regardless of `EnableObservability` —
the option only gates the HTTP route.

---

## 6. The double-bookkeeping

The OTel Counter/Histogram instruments on `SleipnirMetrics` are **write-only** —
the .NET Metrics API offers no way to read an accumulated value back out. To
keep the JSON `/observability` endpoint free of the OTel SDK and readable
without a subscribed reader, the registry holds **parallel `Interlocked`
accumulators** that `SleipnirMetrics` bumps alongside the OTel instruments
("localized double-bookkeeping", `SleipnirConnectionRegistry.cs` → the class doc
comment).

| Method | Registry bump | OTel instrument bump |
|--------|---------------|----------------------|
| `RecordCall` | `SleipnirConnectionRegistry.Current?.RecordCall(success)` — **always** | `CallDuration`/`CallCount`/`ErrorCount` — **only if enabled** (early-return when no instrument enabled) |
| `RecordBatch` | `Current?.RecordBatch()` — **always** | `BatchFanOut`/`BatchCount` — only if enabled (early-return when no instrument enabled) |
| `EventDropped` | `Current?.RecordEventDrop()` — **always** | `EventDroppedCounter.Add` — only when enabled (guard `if (!EventDroppedCounter.Enabled) return;` then `Add`; see the comment on `EventDropped`) |

**Consequence:** the registry cumulative is the in-process state backing the
`/observability` JSON snapshot (no OTel reader needed); the OTel counter is
scrape-driven via `/metrics` and is write-only / vanishes between scrapes if no
reader is subscribed. They **diverge only when no reader is subscribed** (OTel
instruments skip their `Add`/`Record`).

> **Drop-path asymmetry (the `EventDropped` case):** `EventDropped` bumps the
> registry **unconditionally** but the OTel counter only when enabled. The
> durable-subscription, SSE, and WS drop paths all route through a single
> `SleipnirMetrics.EventDropped(subscriptionId)` call — do **not** also call
> `_connectionRegistry.RecordEventDrop()` at the call site (the registry bump
> happens inside `EventDropped`). Explicit comments:
> `SleipnirSubscriptionStore.cs` → `OnDropped(string)`,
> `SleipnirSseConnection.cs` → `OnDropped()`,
> `SleipnirHub.cs` → `PumpDurableAsync` (local `OnDropped`).

---

## 7. The gauge-`Current` fix & flake

The `sleipnir.ws.connections` / `sleipnir.subscriptions.active` gauges read
`SleipnirConnectionRegistry.Current?.Connections` / `?.Subscriptions` **at
scrape time** — the process-global *current* registry, **not** the `registry`
argument passed to `SetConnectionRegistry` (`SleipnirMetrics.cs` →
`SetConnectionRegistry`, the two ObservableGauge callbacks).

**Rationale** (`SleipnirMetrics.cs` → `SetConnectionRegistry` doc comment): so a
test process running multiple Sleipnir hosts does not freeze the gauges to the
first registry. The `registry` parameter is only a trigger for one-time gauge
creation (`??=`) and is installed as `Current` by `AddSleipnir` anyway.

**The flake** (memory `sleipnir-telemetry-test-flake`):
`Gauges_Read_Current_Registry_Values`
(`SleipnirTests/Unit/Telemetry/SleipnirConnectionRegistryTests.cs` →
`Gauges_Read_Current_Registry_Values`) installs a registry as `Current`, creates
the gauges, reads them via a `MeterListener`, and asserts `connections == 2`,
`subscriptions == 3` (`.Should().Be(2)` / `.Should().Be(3)`). Under parallel
integration hosts the process-global `Current` registry races; it passes in
isolation. The mitigation is the process-global `Current` read +
`sleipnir-tracing` collection serialization (`[Collection("sleipnir-tracing")]`).
The literal label is **not** in the repo (it is a memory label). **Not a
regression — don't chase it.** Cross-referenced in
`TRACING_TELEMETRY_REFERENCE.md` §"10. Boundary: tracing vs metrics vs
observability endpoints" and `TRANSPORT_REFERENCE.md` §"11. How it is verified
(the gates)".

---

## 8. The DevUI observability panel

**Component:** `SleipnirDeveloperUi/src/lib/components/editor/ObservabilityPage.svelte`.

- Polls `/observability` every `POLL_MS = 2000` ms (`POLL_MS`) with a `HISTORY = 60`
  ring buffer (`HISTORY`). `onMount` starts the interval, `onDestroy` clears it.
  Error banner on non-2xx/401.
- `refresh()` calls `fetchObservability()`, pushes
  `activeConnections`/`activeSubscriptions` to sparkline history, and stores a
  per-poll **delta** of `eventDroppedTotal` (not the cumulative ramp).
- Renders: transport pills REST/WS/SignalR/SSE on/off, metric cards for Active
  WS connections / Active subscriptions / Events dropped with sparklines,
  cumulative table Calls/Errors/Batches/Uptime, and a pointer to `/metrics` for
  the full instrument set.
- Error banner points users to `EnableObservability = true` and to the
  `Sleipnir.Telemetry` Prometheus path.

**TS interface + fetch:** `SleipnirDeveloperUi/src/lib/api/client.ts` →
`interface ObservabilitySnapshot` + `fetchObservability()` (builds
`${baseUrl}/${apiPath}/observability`, attaches bearer if set, throws on non-ok).

**Tab registration:** `tabs.svelte.ts` → `createObservabilityTab` (title
`'Observability'`, `type: 'observability'`); `TabType` includes `'observability'`.

---

## 9. `EnableObservability` & auth gates

`SleipnirHub/Extensions/SleipnirOptions.cs` → `EnableObservability` —
`public bool EnableObservability { get; set; } = false;` (opt-in, like
`EnableJsonRpcCompat`). See the `EnableObservability` doc comment.

| Option | Default | Gates |
|--------|---------|-------|
| `EnableObservability` | `false` | only the `/observability` route registration (`SleipnirEndpointExtensions.cs` → `MapSleipnirEndpoints`, the `if (enableObservability)` gate) |
| `RequireAuthentication` | `false` | the `/observability` 401 gate (`SleipnirEndpointExtensions.cs` → `MapSleipnirEndpoints`) and the `/metrics` 401 gate (`SleipnirPrometheusExtensions.cs` → `UseSleipnirPrometheusScrapingEndpoint`) |

**What `EnableObservability` does NOT gate:** `/metrics` (separate opt-in,
§3); the gauges or registry (always active once `AddSleipnir` runs,
`SleipnirServiceCollectionExtension.cs` → `AddSleipnir`, the registry
`SetInstance`/`SetConnectionRegistry` block).

**Auth interaction:** when `RequireAuthentication=false` the `/observability`
gate is open without a token — `ObservabilityEndpointTests.cs` →
`Observability_NoRequireAuth_OpenWithoutToken_Returns200`. When
`RequireAuthentication=true` an unauthenticated caller gets 401 on both
endpoints (`ObservabilityEndpointTests.cs` →
`Observability_RequireAuth_Unauthenticated_Returns401` and
`Metrics_RequireAuth_Unauthenticated_Returns401`).

---

## 10. The Heimdall durable-contract note

The **Prometheus-text `/metrics` interface is the durable contract** — any
scraper (Prometheus, Grafana Agent, VictoriaMetrics, or **Heimdall**, Holger's
upcoming embedded OTel stack) reads it. The OTel exporter behind it is the
**interim producer**; Heimdall can later replace it without changing consumers.

- `SleipnirTelemetry/SleipnirPrometheusExtensions.cs` → the `SleipnirPrometheusExtensions`
  class doc comment (the canonical statement).
- `SleipnirTelemetry/SleipnirTelemetry.csproj` → the csproj comment.
- `STABILITY.md` §"2. Experimental surface (may change within 1.x)" — "The
  **Prometheus-text `/metrics` interface is the durable contract** …; the JSON
  snapshot shape and the DevUI panel are conveniences that may settle in a
  minor version. Experimental in v1."
- `CHANGELOG.md` §"Added — Observability: /metrics scrape + /observability
  snapshot + DevUI panel (experimental)", `README_DETAILS.md` §"Metrics &
  observability endpoints (experimental, opt-in)" — the same framing.
- DevUI panel hint: `ObservabilityPage.svelte` — "Heimdall … kann diesen
  Producer später ersetzen — das Prometheus-Text-Interface bleibt der Vertrag."

Heimdall context: a sibling project — an embeddable .NET Grafana-replacement
OTel stack (memory `heimdall-scope`). Sleipnir integration is under
consideration. The durable contract is what makes that integration safe: swap
the producer, keep the interface.

---

## 11. Diagnostics & troubleshooting catalog

### Common mistakes

- **404 on `/observability`.** `EnableObservability` defaults `false` — the
  route is not registered. Set `options.EnableObservability = true`
  (`SleipnirOptions.cs` → `EnableObservability`). Test:
  `ObservabilityEndpointTests.cs` → `Observability_Disabled_NotMapped_Returns404`.
- **404 on `/metrics` without `Sleipnir.Telemetry`.** There is **no built-in
  `/metrics` in `SleipnirRest`**. Add the `Sleipnir.Telemetry` package and call
  `AddSleipnirPrometheusMetrics()` + `UseSleipnirPrometheusScrapingEndpoint()`
  (§3). `SleipnirServer` does not reference `Sleipnir.Telemetry`.
- **401 on either endpoint.** `RequireAuthentication` is on and the caller is
  unauthenticated. `/observability` uses the same transport-level gate as
  `/discovery`; `/metrics` has its own `requireAuth` (default `true`) +
  `RequireAuthentication`.
- **`/observability` counters are zero but `/metrics` shows data (or vice
  versa).** The two surfaces read different sources: `/observability` reads
  the registry accumulators (always bumped); `/metrics` reads the OTel
  instruments (bumped only when a reader is subscribed). They diverge when no
  OTel reader is subscribed (§6).
- **Gauges frozen to an old value.** The gauges read `SleipnirConnectionRegistry.Current`
  at scrape time (§7). If you see stale values in a multi-host test, that is the
  known flake, not a production bug.
- **Double-counting event drops.** Do **not** call
  `_connectionRegistry.RecordEventDrop()` at a drop site — `SleipnirMetrics.EventDropped`
  already bumps the registry unconditionally (`SleipnirSubscriptionStore.cs` →
  `OnDropped(string)` comment).

### Stability note

The `sleipnir.*` instruments and the observability endpoints are
**experimental in v1** (`STABILITY.md` §"2. Experimental surface (may change
within 1.x)"). The Prometheus-text interface is the durable contract; the JSON
snapshot shape and the DevUI panel are conveniences that may settle in a minor
version.

---

## 12. How it is verified (the tests)

| Test file | Covers |
|-----------|--------|
| `SleipnirTests/Unit/Telemetry/SleipnirConnectionRegistryTests.cs` | Concurrent Inc/Dec connections/subscriptions return to baseline; `RecordCall(true)` bumps CallCount not ErrorCount, `RecordCall(false)` bumps both; `RecordBatch`/`RecordEventDrop` accumulate; `GetSnapshot` reflects state; `StartedAtUtc` recent; `Gauges_Read_Current_Registry_Values` (gauge-Current fix via `MeterListener`). Collection `[Collection("sleipnir-tracing")]`. |
| `SleipnirTests/Integration/ObservabilityEndpointTests.cs` | In-process Kestrel host per test (`BuildHostAsync`): `/observability` 401 unauth when RequireAuth (`Observability_RequireAuth_Unauthenticated_Returns401`); 200 + snapshot DTO when authenticated (`Observability_RequireAuth_Authenticated_Returns200_AndSnapshotDto`); `transports.webSocket` reflects the `UseWebSocket` toggle (`Observability_Transports_ReflectUseWebSocketToggle`); 404 when `EnableObservability=false` (`Observability_Disabled_NotMapped_Returns404`); 200 open without token when `RequireAuthentication=false` (`Observability_NoRequireAuth_OpenWithoutToken_Returns200`); `/metrics` 401 unauth (`Metrics_RequireAuth_Unauthenticated_Returns401`); `/metrics` 200 + Prometheus text with `sleipnir_`, `sleipnir_ws_connections`, `sleipnir_subscriptions_active` (`Metrics_RequireAuth_Authenticated_ReturnsPrometheusText`). |

---

## 13. Relationship to other docs

| Doc | Covers (observability-relevant) |
|-----|----------------------------------|
| `README_DETAILS.md` | DevUI observability panel one-line (§"Developer UI"); bullet listing `/metrics` + `/observability` (§"Features"); full "Metrics & observability endpoints (experimental, opt-in)" subsection — instruments, double-bookkeeping, both endpoints, durable-contract note, `SleipnirServer` does not reference `Sleipnir.Telemetry` (§"Metrics & observability endpoints (experimental, opt-in)"). |
| `STABILITY.md` | `sleipnir.*` instruments classification; observability endpoints + the durable-contract statement (§"2. Experimental surface (may change within 1.x)"). |
| `CHANGELOG.md` | "Added — Observability: `/metrics` scrape + `/observability` snapshot + DevUI panel (experimental)" task #10 (§"Added — Observability: /metrics scrape + /observability snapshot + DevUI panel (experimental)"); `UseWebSocket` toggle reflected in snapshot (§"Added — Transport toggles + startup introspection log"); batch-path metrics (§"Added — OpenTelemetry Metrics (Phase 1)"); durable-contract statement + `AddSleipnirTelemetry` subscribes metrics column (same §"Added — Observability…" section). |
| `PROTOCOL.md` | Wiring snippet `AddSleipnirPrometheusMetrics()` + `UseSleipnirPrometheusScrapingEndpoint("/api/sleipnir/metrics", requireAuth: true)`; push/pull do not conflict (§"GET /api/sleipnir/metrics — Prometheus text scrape"). Referenced as the contract-home by `STABILITY.md` §"2. Experimental surface (may change within 1.x)". |
| `guide/chapters/10-production.md` | "Prometheus `/metrics` — the production scrape path" + durable-contract framing (§"Prometheus /metrics — the production scrape path"); `RequireAuthentication` gates `/discovery`, `/observability`, `/metrics`, SSE event endpoints, WS upgrade (§"5. Hardening knobs (north-bound)"). |
| `README.md` | One-line metrics bullet (§"Features at a glance"). No `/observability` or `/metrics` endpoint mention. |
| `TRACING_TELEMETRY_REFERENCE.md` | **The companion doc** — tracing spans; points here for metrics/endpoints (§"`SleipnirTelemetryOptions` — `SleipnirTelemetry/SleipnirTelemetryOptions.cs`" and §"10. Boundary: tracing vs metrics vs observability endpoints"); documents the gauge flake (§"10. Boundary: tracing vs metrics vs observability endpoints") and lists the registry tests (§"12. How it is verified (the tests)"). |
| `TRANSPORT_REFERENCE.md` | Known-flake note (§"11. How it is verified (the gates)"); the `/observability` endpoint row (§2). |
| `SleipnirTelemetry/README.md` | Package README — the `AddSleipnirPrometheusMetrics` / `UseSleipnirPrometheusScrapingEndpoint` surface and the durable-contract note. |
| `CLAUDE.md` / `BEST_PRACTICES.md` | **Not found** — neither contains observability content (grep for observability/metrics/Prometheus/Heimdall/EnableObservability returned no matches). |

> **Note:** the `sleipnir-observability-endpoints` and
> `sleipnir-telemetry-test-flake` labels are memory labels, not in-repo strings.
> The durable-contract framing and the gauge flake are real and cited above;
> the labels themselves live only in the user's auto-memory.