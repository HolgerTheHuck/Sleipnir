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
table with `path:line` citations, the snapshot field table, the double-
bookkeeping rationale, the option/auth table, a diagnostics catalog, and a map
of where the deeper docs live. For the stability classification read
`STABILITY.md:178-192`; for the production wiring read
`guide/chapters/10-production.md` + `PROTOCOL.md` → "Observability Endpoints".
This doc consolidates those and links back for depth.

All citations are `repo-relative/path.cs:line` against the repo root. Code-facing
text is English per `CLAUDE.md`.

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
(`SleipnirTelemetry/SleipnirPrometheusExtensions.cs:67-95`), which delegates to
the OTel SDK's `app.UseOpenTelemetryPrometheusScrapingEndpoint(path)` (`:94`).
`SleipnirServer` does not reference `SleipnirTelemetry` — the OTel SDK
dependencies stay out of the all-in-one bundle (`README_DETAILS.md:870`).

The double-bookkeeping (§6) exists precisely so `/observability` can be read
**without** an OTel `MetricReader` subscribed — the registry's parallel
`Interlocked` accumulators back the JSON snapshot. `/metrics` still needs the
OTel SDK + a subscribed Prometheus exporter.

The two surfaces are **separate opt-ins** (`SleipnirHub/Extensions/SleipnirOptions.cs:224-227`):
`EnableObservability` gates only `/observability`; `/metrics` is gated solely
by its own `requireAuth` parameter + `RequireAuthentication`. Push (OTLP via
`AddSleipnirTelemetry`) and pull (Prometheus scrape) do not conflict
(`CHANGELOG.md:181-183`, `PROTOCOL.md:1023`).

---

## 2. The `/observability` JSON endpoint

`GET {prefix}/observability` (default `/api/sleipnir/observability`).

- **Route:** `SleipnirRest/SleipnirEndpointExtensions.cs:107` —
  `group.MapGet("/observability", ...)`, inside the `MapSleipnirEndpoints` group
  (`:38`, default prefix `:32-35`).
- **`EnableObservability` gate:** `:105` — `if (enableObservability) { ... }`.
  When `false` (default) the route is never registered → 404
  (`SleipnirTests/Integration/ObservabilityEndpointTests.cs:168-182`).
- **Auth gate:** `:112-113` — 401 when `RequireAuthentication && !IsAuthenticated`
  (same transport-level gate as `/discovery`, `:92-93`).
- **Response shape** (`:120-131`, anonymous object via `Results.Json`):

| Field | Source | Line |
|-------|--------|------|
| `transports` | `{ rest = true, webSocket = useWebSocket, signalR = signalREnabled, sse = useSse }` (threaded from endpoint options, not the registry; comment `:116-119`) | `:122` |
| `activeConnections` | `snap.ActiveConnections` | `:123` |
| `activeSubscriptions` | `snap.ActiveSubscriptions` | `:124` |
| `eventDroppedTotal` | `snap.EventDroppedTotal` | `:125` |
| `callCount` | `snap.CallCount` | `:126` |
| `errorCount` | `snap.ErrorCount` | `:127` |
| `batchCount` | `snap.BatchCount` | `:128` |
| `uptimeMs` | `(long)(DateTimeOffset.UtcNow - registry.StartedAtUtc).TotalMilliseconds` (added by the endpoint, not on the snapshot type) | `:129` |

The `ObservabilitySnapshot` C# shape (`SleipnirConnectionRegistry.cs:138-156`)
carries `ActiveConnections`/`ActiveSubscriptions`/`EventDroppedTotal`/
`CallCount`/`ErrorCount`/`BatchCount`. `transports` and `uptimeMs` are **not** on
the snapshot type — they are added by the endpoint's anonymous payload. The TS
mirror is `SleipnirDeveloperUi/src/lib/api/client.ts:69-78`.

---

## 3. The `/metrics` Prometheus endpoint

`GET {apiPath}/metrics` (default `/api/sleipnir/metrics`) — **only via
`Sleipnir.Telemetry`**.

- **Use:** `SleipnirTelemetry/SleipnirPrometheusExtensions.cs:67-95` —
  `UseSleipnirPrometheusScrapingEndpoint(path, requireAuth)`. At `:94` it
  delegates to `app.UseOpenTelemetryPrometheusScrapingEndpoint(path)` (the OTel
  SDK's terminal middleware). Default path `/api/sleipnir/metrics` (`:69`).
- **Add:** `AddSleipnirPrometheusMetrics()` (`:49-58`) subscribes the
  `Meter "Sleipnir"` and attaches the Prometheus exporter (`:51-55`:
  `AddMeter(SleipnirMetrics.MeterName)` + `AddPrometheusExporter()`).
- **Dependency:** `OpenTelemetry.Exporter.Prometheus.AspNetCore` 1.16.0-beta.1
  (`SleipnirTelemetry/SleipnirTelemetry.csproj:37`).
- **Auth gate (independent of `/observability`):** `:72-91` — a `Use` middleware
  runs before the terminal scrape middleware; if `requireAuth` (default `true`)
  and `ISleipnirCore.RequireAuthentication` is on and the caller is
  unauthenticated, it short-circuits to 401 (`:86`). It resolves `ISleipnirCore`
  per-request from request-scoped DI (`:82`), so no `SleipnirHub` dependency
  (`:27-32`).
- **Not gated by `EnableObservability`** — `/metrics` is gated solely by its own
  `requireAuth` + `RequireAuthentication` (§9).

`AddSleipnirTelemetry` also subscribes the metrics **push** path (OTLP):
`SleipnirTelemetry/SleipnirTelemetryServiceExtensions.cs:60-69` —
`WithMetrics(builder => ... .AddMeter(SleipnirMetrics.MeterName) ...)`. The pull
Prometheus path is additive via `AddSleipnirPrometheusMetrics()` +
`UseSleipnirPrometheusScrapingEndpoint()` (comment `:63`). Push and pull do not
conflict.

---

## 4. `SleipnirMetrics` instruments

`SleipnirCore/Tracing/SleipnirMetrics.cs:25` — `public static class SleipnirMetrics`.
Meter: `MeterName = "Sleipnir"` (`:28`), `Meter = new(MeterName, "1.0.0")`
(`:31`, internal). The `Meter` and the tracing `ActivitySource` share the name
`"Sleipnir"` — OTel allows this
(`docs/design/phase-1-interceptor-pipeline.md:155-158`).

| Instrument | Name | Type | Unit | Line |
|------------|------|------|------|------|
| `CallDuration` | `sleipnir.call.duration` | Histogram<double> | `ms` | `:78-81` |
| `CallCount` | `sleipnir.call.count` | Counter<long> | `{call}` | `:87-90` |
| `ErrorCount` | `sleipnir.error.count` | Counter<long> | `{call}` | `:96-99` |
| `EventDroppedCounter` | `sleipnir.event.dropped` | Counter<long> | `{event}` | `:127-130` |
| `BatchFanOut` | `sleipnir.batch.fan_out` | Histogram<int> | `{request}` | `:107-110` |
| `BatchCount` | `sleipnir.batch.count` | Counter<long> | `{batch}` | `:115-118` |
| `_connectionsGauge` | `sleipnir.ws.connections` | ObservableGauge<int> | `{connection}` | `:59-63` |
| `_subscriptionsGauge` | `sleipnir.subscriptions.active` | ObservableGauge<int> | `{subscription}` | `:65-69` |

**Record methods:**

| Method | Line | Records |
|--------|------|---------|
| `RecordCall(request, response, durationMs, category)` | `:149-176` | `CallDuration.Record` (`:172`), `CallCount.Add` (`:173`), `ErrorCount.Add` on failure (`:175`). Tags: `rpc.system`, `rpc.service`, `rpc.method`, `sleipnir.error_category`, `sleipnir.success` (`:163-170`). |
| `RecordBatch(requests, mode)` | `:179-196` | `BatchFanOut.Record` (`:194`), `BatchCount.Add` (`:195`). Tags: `rpc.system`, `sleipnir.batch.mode` (`:188-192`). |
| `EventDropped(subscriptionId)` | `:133-140` | Tags: `rpc.system`, `sleipnir.subscription_id` (`:139`). |
| `SetConnectionRegistry(registry)` | `:53-70` | Installs the two ObservableGauges (idempotent via `??=`). |

**Call sites:** `RecordCall` from the telemetry interceptor
`SleipnirHub/Interceptors/SleipnirTelemetryInterceptor.cs:99` and invoker error
paths `SleipnirCore/Services/SleipnirInvoker.cs:1512, :1528, :1546`. `RecordBatch`
from `SleipnirInvoker.cs:306`. `EventDropped` from
`SleipnirCore/Events/EventObserver.cs:43`,
`SleipnirRest/Sse/SleipnirSseConnection.cs:237`,
`SleipnirHub/Hub/SleipnirHub.cs:246`, and
`SleipnirCore/Services/SleipnirSubscriptionStore.cs:138`.

---

## 5. `SleipnirConnectionRegistry` — the snapshot backing

`SleipnirCore/Tracing/SleipnirConnectionRegistry.cs:32` —
`public sealed class SleipnirConnectionRegistry`. The in-process state backing
the `/observability` JSON snapshot.

| Concern | Members | Line |
|---------|---------|------|
| **Live counts** (Interlocked) | `_connections`, `_subscriptions`; readers `Interlocked.CompareExchange(ref _x, 0, 0)` (`:74, :77`); mutators `IncConnection`/`DecConnection`/`IncSubscription`/`DecSubscription` (`:96-105`) | `:34-35` |
| **Cumulative counters** (Interlocked long) | `_eventDroppedTotal`, `_callCount`, `_errorCount`, `_batchCount`; readers `Interlocked.Read` (`:82, :85, :88, :91`); mutators `RecordEventDrop` (`:108`), `RecordCall(bool success)` (`:111-115`), `RecordBatch` (`:118`) | `:36-39` |
| **`GetSnapshot()`** | Returns `ObservabilitySnapshot` (`:138-156`) with `ActiveConnections`/`ActiveSubscriptions`/`EventDroppedTotal`/`CallCount`/`ErrorCount`/`BatchCount` | `:121-129` |
| **`StartedAtUtc`** | `DateTimeOffset.UtcNow` at construction; used for `/observability` uptime | `:46` |
| **Process-global singleton** | `_instance` (`:48`), `Instance` (throws if not registered, `:55-57`), `Current` (nullable, internal, `:65`), `SetInstance` (`:68-69`) | `:48-69` |

`Current` is what `SleipnirMetrics` gauge callbacks and the record methods read
(§7). **DI wiring:** `AddSleipnir` does
`new SleipnirConnectionRegistry()` → `SetInstance(...)` →
`SleipnirMetrics.SetConnectionRegistry(...)` →
`services.AddSingleton(...)` (`SleipnirServiceCollectionExtension.cs:59-62`,
eager-wiring comment `:52-58`). So the registry + gauges are **always active**
once `AddSleipnir` runs, regardless of `EnableObservability` — the option only
gates the HTTP route.

---

## 6. The double-bookkeeping

The OTel Counter/Histogram instruments on `SleipnirMetrics` are **write-only** —
the .NET Metrics API offers no way to read an accumulated value back out. To
keep the JSON `/observability` endpoint free of the OTel SDK and readable
without a subscribed reader, the registry holds **parallel `Interlocked`
accumulators** that `SleipnirMetrics` bumps alongside the OTel instruments
("localized double-bookkeeping", `SleipnirConnectionRegistry.cs:15-24`).

| Method | Registry bump | OTel instrument bump |
|--------|---------------|----------------------|
| `RecordCall` (`SleipnirMetrics.cs:158-176`) | `SleipnirConnectionRegistry.Current?.RecordCall(success)` — **always** (`:158`) | `CallDuration`/`CallCount`/`ErrorCount` — **only if enabled** (early-return `:160-161` if none enabled) |
| `RecordBatch` (`:183-196`) | `Current?.RecordBatch()` — **always** (`:183`) | `BatchFanOut`/`BatchCount` — only if enabled (`:185-186`) |
| `EventDropped` (`:133-140`) | `Current?.RecordEventDrop()` — **always** (`:137`) | `EventDroppedCounter.Add` — only `if (!EventDroppedCounter.Enabled) return;` then `Add` (`:138-139`, comment `:135-136`) |

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
> `SleipnirSubscriptionStore.cs:136-137`, `SleipnirSseConnection.cs:235`,
> `SleipnirHub.cs:242-246`.

---

## 7. The gauge-`Current` fix & flake

The `sleipnir.ws.connections` / `sleipnir.subscriptions.active` gauges read
`SleipnirConnectionRegistry.Current?.Connections` / `?.Subscriptions` **at
scrape time** — the process-global *current* registry, **not** the `registry`
argument passed to `SetConnectionRegistry` (`SleipnirMetrics.cs:61, :67`).

**Rationale** (`SleipnirMetrics.cs:40-52`): so a test process running multiple
Sleipnir hosts does not freeze the gauges to the first registry. The `registry`
parameter is only a trigger for one-time gauge creation (`??=`) and is installed
as `Current` by `AddSleipnir` anyway.

**The flake** (memory `sleipnir-telemetry-test-flake`):
`Gauges_Read_Current_Registry_Values`
(`SleipnirTests/Unit/Telemetry/SleipnirConnectionRegistryTests.cs:138`)
installs a registry as `Current`, creates the gauges, reads them via a
`MeterListener` (`:155-170`), and asserts `connections == 2`,
`subscriptions == 3` (`:172-173`). Under parallel integration hosts the
process-global `Current` registry races; it passes in isolation. The mitigation
is the process-global `Current` read + `sleipnir-tracing` collection
serialization (`:18`). The literal label is **not** in the repo (it is a memory
label). **Not a regression — don't chase it.** Cross-referenced in
`TRACING_TELEMETRY_REFERENCE.md:345-353` and `TRANSPORT_REFERENCE.md:744-746`.

---

## 8. The DevUI observability panel

**Component:** `SleipnirDeveloperUi/src/lib/components/editor/ObservabilityPage.svelte` (1-304).

- Polls `/observability` every `POLL_MS = 2000` ms (`:11`) with a `HISTORY = 60`
  ring buffer (`:12`). `onMount` starts the interval (`:48-51`), `onDestroy`
  clears it (`:53-55`). Error banner on non-2xx/401 (`:96-108`).
- `refresh()` (`:28-46`) calls `fetchObservability()` (`:31`), pushes
  `activeConnections`/`activeSubscriptions` to sparkline history (`:35-36`),
  and stores a per-poll **delta** of `eventDroppedTotal` (not the cumulative ramp,
  `:39-40`).
- Renders: transport pills REST/WS/SignalR/SSE on/off (`:111-125`), metric cards
  for Active WS connections / Active subscriptions / Events dropped with
  sparklines (`:127-149`), cumulative table Calls/Errors/Batches/Uptime
  (`:151-161`), and a pointer to `/metrics` for the full instrument set
  (`:163-168`).
- Error banner points users to `EnableObservability = true` and to the
  `Sleipnir.Telemetry` Prometheus path (`:100-106`).

**TS interface + fetch:** `SleipnirDeveloperUi/src/lib/api/client.ts:69-90`
(`interface ObservabilitySnapshot`; builds `${baseUrl}/${apiPath}/observability`,
attaches bearer if set, throws on non-ok).

**Tab registration:** `tabs.svelte.ts:214-240` (`createObservabilityTab`,
title `'Observability'`, `type: 'observability'`); `TabType` includes
`'observability'` (`:6`).

---

## 9. `EnableObservability` & auth gates

`SleipnirHub/Extensions/SleipnirOptions.cs:229` —
`public bool EnableObservability { get; set; } = false;` (opt-in, like
`EnableJsonRpcCompat` at `:214`). Doc comment `:216-228`.

| Option | Default | Gates | Line |
|--------|---------|-------|------|
| `EnableObservability` | `false` | only the `/observability` route registration (`SleipnirEndpointExtensions.cs:105`) | `:229` |
| `RequireAuthentication` | `false` | the `/observability` 401 gate (`SleipnirEndpointExtensions.cs:112-113`) and the `/metrics` 401 gate (`SleipnirPrometheusExtensions.cs:72-91`) | `:142` |

**What `EnableObservability` does NOT gate:** `/metrics` (separate opt-in,
§3); the gauges or registry (always active once `AddSleipnir` runs,
`SleipnirServiceCollectionExtension.cs:60-61`).

**Auth interaction:** when `RequireAuthentication=false` the `/observability`
gate is open without a token —
`ObservabilityEndpointTests.cs:184-199` (`Observability_NoRequireAuth_OpenWithoutToken_Returns200`).
When `RequireAuthentication=true` an unauthenticated caller gets 401 on both
endpoints (`:80-93`, `:203-216`).

---

## 10. The Heimdall durable-contract note

The **Prometheus-text `/metrics` interface is the durable contract** — any
scraper (Prometheus, Grafana Agent, VictoriaMetrics, or **Heimdall**, Holger's
upcoming embedded OTel stack) reads it. The OTel exporter behind it is the
**interim producer**; Heimdall can later replace it without changing consumers.

- `SleipnirTelemetry/SleipnirPrometheusExtensions.cs:34-38` — the code comment
  (the canonical statement).
- `SleipnirTelemetry/SleipnirTelemetry.csproj:33-36` — csproj comment.
- `STABILITY.md:185-192` — "The **Prometheus-text `/metrics` interface is the
  durable contract** …; the JSON snapshot shape and the DevUI panel are
  conveniences that may settle in a minor version. Experimental in v1."
- `CHANGELOG.md:169-171`, `README_DETAILS.md:878` — the same framing.
- DevUI panel hint: `ObservabilityPage.svelte:164-168` — "Heimdall … kann diesen
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
  (`SleipnirOptions.cs:229`). Test: `ObservabilityEndpointTests.cs:168-182`.
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
  already bumps the registry unconditionally (`SleipnirSubscriptionStore.cs:136-137`).

### Stability note

The `sleipnir.*` instruments and the observability endpoints are
**experimental in v1** (`STABILITY.md:178-192`). The Prometheus-text interface
is the durable contract; the JSON snapshot shape and the DevUI panel are
conveniences that may settle in a minor version.

---

## 12. How it is verified (the tests)

| Test file | Covers |
|-----------|--------|
| `SleipnirTests/Unit/Telemetry/SleipnirConnectionRegistryTests.cs` | Concurrent Inc/Dec connections/subscriptions return to baseline; `RecordCall(true)` bumps CallCount not ErrorCount, `RecordCall(false)` bumps both; `RecordBatch`/`RecordEventDrop` accumulate; `GetSnapshot` reflects state; `StartedAtUtc` recent; `Gauges_Read_Current_Registry_Values` (gauge-Current fix via `MeterListener`, `:138`). Collection `[Collection("sleipnir-tracing")]` (`:18`). |
| `SleipnirTests/Integration/ObservabilityEndpointTests.cs` | In-process Kestrel host per test (`:37-71`): `/observability` 401 unauth when RequireAuth (`:80-93`); 200 + snapshot DTO when authenticated (`:95-125`); `transports.webSocket` reflects the `UseWebSocket` toggle (`:127-166`); 404 when `EnableObservability=false` (`:168-182`); 200 open without token when `RequireAuthentication=false` (`:184-199`); `/metrics` 401 unauth (`:203-216`); `/metrics` 200 + Prometheus text with `sleipnir_`, `sleipnir_ws_connections`, `sleipnir_subscriptions_active` (`:218-238`). |

---

## 13. Relationship to other docs

| Doc | Covers (observability-relevant) |
|-----|----------------------------------|
| `README_DETAILS.md` | DevUI observability panel one-line (`:218`); bullet listing `/metrics` + `/observability` (`:238`); full "Metrics & observability endpoints (experimental, opt-in)" subsection — instruments, double-bookkeeping, both endpoints, durable-contract note, `SleipnirServer` does not reference `Sleipnir.Telemetry` (`:870-881`). |
| `STABILITY.md` | `sleipnir.*` instruments classification (`:178-184`); observability endpoints + the durable-contract statement (`:185-192`). |
| `CHANGELOG.md` | "Added — Observability: `/metrics` scrape + `/observability` snapshot + DevUI panel (experimental)" task #10 (`:163-186`); `UseWebSocket` toggle reflected in snapshot (`:199-203`); batch-path metrics (`:569`); durable-contract statement (`:169-171`); `AddSleipnirTelemetry` subscribes metrics column (`:181-183`). |
| `PROTOCOL.md` | Wiring snippet `AddSleipnirPrometheusMetrics()` + `UseSleipnirPrometheusScrapingEndpoint("/api/sleipnir/metrics", requireAuth: true)` (`:997-999`); push/pull do not conflict (`:1023`). Referenced as the contract-home by `STABILITY.md:192`. |
| `guide/chapters/10-production.md` | "Prometheus `/metrics` — the production scrape path" (`:138-148`, durable-contract framing `:140-141`); `RequireAuthentication` gates `/discovery`, `/observability`, `/metrics`, SSE event endpoints, WS upgrade (`:301`). |
| `README.md` | One-line metrics bullet (`:270`). No `/observability` or `/metrics` endpoint mention. |
| `TRACING_TELEMETRY_REFERENCE.md` | **The companion doc** — tracing spans; points here for metrics/endpoints (`:268-269, :338`); documents the gauge flake (`:345-353`) and lists the registry tests (`:398`). |
| `TRANSPORT_REFERENCE.md` | Known-flake note (`:744-746`); the `/observability` endpoint row (§2). |
| `SleipnirTelemetry/README.md` | Package README — the `AddSleipnirPrometheusMetrics` / `UseSleipnirPrometheusScrapingEndpoint` surface and the durable-contract note. |
| `CLAUDE.md` / `BEST_PRACTICES.md` | **Not found** — neither contains observability content (grep for observability/metrics/Prometheus/Heimdall/EnableObservability returned no matches). |

> **Note:** the `sleipnir-observability-endpoints` and
> `sleipnir-telemetry-test-flake` labels are memory labels, not in-repo strings.
> The durable-contract framing and the gauge flake are real and cited above;
> the labels themselves live only in the user's auto-memory.