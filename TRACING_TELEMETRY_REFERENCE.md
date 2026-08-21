# Sleipnir Tracing & Telemetry — User Reference

A consolidated lookup reference for **distributed tracing** in Sleipnir: the
always-on OpenTelemetry `ActivitySource` instrumentation in the engine, the
span model (`SleipnirCall`/`SleipnirBatch`), the tags and status each span
carries, the three instrumentation sites in `SleipnirInvoker`, why the
instrumentation is inline (not an `ISleipnirInterceptor`), the cost-neutrality
contract, the optional `Sleipnir.Telemetry` package that subscribes the source
and wires exporters, the `Sleipnir`↔`Trame` rename history, and the
process-global-`ActivityListener` test-isolation story.

**Scope boundary:** this doc covers **tracing** (`SleipnirCore.Tracing.SleipnirTracing`
— spans/activities). **Metrics** (`SleipnirCore.Tracing.SleipnirMetrics` — the
`sleipnir.*` counters/histograms/gauges) and the **observability HTTP endpoints**
(`/api/sleipnir/observability`, `/api/sleipnir/metrics`) are a separate concern,
covered in `OBSERVABILITY_REFERENCE.md`. Metrics are mentioned here only where
they overlap the tracing test-isolation story (§9).

This is a **reference**, not a tutorial. When a span does not appear, or the
wrong source name is subscribed, look here first — the member table, the
instrumentation-site table with `path:line` citations, the `Sleipnir.Telemetry`
options table, a diagnostics catalog (incl. stale-doc flags), and a map of
where the deeper docs live. For the architecture summary read `CLAUDE.md`
§"Distributed Tracing"; for the design rationale read
`docs/design/phase-1-interceptor-pipeline.md`. This doc consolidates those,
corrects two stale points in `CLAUDE.md`, and links back for depth.

All citations are `repo-relative/path.cs:line` against the repo root. Code-facing
text is English per `CLAUDE.md`.

## Table of contents

1. [The tracing model](#1-the-tracing-model)
2. [`SleipnirTracing` — the class](#2-sleipnirtracing--the-class)
3. [The three instrumentation sites](#3-the-three-instrumentation-sites)
4. [Why inline, not an `ISleipnirInterceptor`](#4-why-inline-not-an-isleipnirinterceptor)
5. [Cost-neutrality](#5-cost-neutrality)
6. [The span model & tags](#6-the-span-model--tags)
7. [`Sleipnir.Telemetry` — the opt-in package](#7-sleipnirtelemetry--the-opt-in-package)
8. [The `Sleipnir` ↔ `Trame` rename](#8-the-sleipnir--trame-rename)
9. [Test isolation — the process-global `ActivityListener`](#9-test-isolation--the-process-global-activitylistener)
10. [Boundary: tracing vs metrics vs observability endpoints](#10-boundary-tracing-vs-metrics-vs-observability-endpoints)
11. [Diagnostics & troubleshooting catalog](#11-diagnostics--troubleshooting-catalog)
12. [How it is verified (the tests)](#12-how-it-is-verified-the-tests)
13. [Relationship to other docs](#13-relationship-to-other-docs)

---

## 1. The tracing model

Sleipnir instruments the engine with an always-on `ActivitySource` named
`"Sleipnir"` (`SleipnirCore/Tracing/SleipnirTracing.cs:23`). The instrumentation
lives **directly in `SleipnirInvoker`**, not as an `ISleipnirInterceptor`, because
the batch path bypasses the interceptor pipeline (§4). It is **cost-neutral**:
`StartActivity` returns `null` without a listener, every helper null-checks the
activity, and it is not DI-registered (§5).

The source name is `public` so the optional `Sleipnir.Telemetry` package (a
different assembly) can reach `SleipnirTracing.ActivitySourceName`; all other
members are `internal` and the instrumentation is not part of the public contract
(`SleipnirTracing.cs:13-19`). Consumers subscribe to the source name `"Sleipnir"`
from their own OpenTelemetry setup, or via the convenience
`AddSleipnirTelemetry` (§7).

`SleipnirServer` does **not** reference `Sleipnir.Telemetry`
(`SleipnirServer/SleipnirServer.csproj:19-24` references only `SleipnirHub`,
`SleipnirRest`, `SleipnirWebSocket`, `SleipnirDeveloperUi`). Tracing is opt-in:
consumers add the `Sleipnir.Telemetry` package and call `AddSleipnirTelemetry`
(`README_DETAILS.md:837, :849`).

---

## 2. `SleipnirTracing` — the class

`SleipnirCore/Tracing/SleipnirTracing.cs:20` — `public static class SleipnirTracing`.

| Member | Visibility | Line | Purpose |
|--------|-----------|------|---------|
| `ActivitySourceName` | `public const string` = `"Sleipnir"` | `:23` | The source name consumers subscribe to. |
| `Source` | `internal static readonly ActivitySource` | `:26` | `new(ActivitySourceName, "1.0.0")`. The source itself is internal; version is hardcoded. |
| `StartCall(SleipnirRequest)` | `internal static Activity?` | `:31` | Starts a `"SleipnirCall"` activity, `ActivityKind.Internal` (`:33`). Returns `null` without a listener (`:34-35`). |
| `StartBatch(IReadOnlyList<SleipnirRequest>, ExecutionMode)` | `internal static Activity?` | `:52` | Starts a `"SleipnirBatch"` activity (`:54`). Returns `null` without a listener (`:55-56`). |
| `SetCallStatus(Activity?, SleipnirResponse?)` | `public static void` | `:68` | No-op if null (`:70-71`); `Ok` when `response?.IsSuccess` (`:73-74`), else `Error` with `response?.Error?.Message ?? "RPC failed"` (`:75-76`). |
| `RecordException(Activity?, Exception)` | `public static void` | `:87` | No-op if null (`:89-90`); sets `exception.type` (`:92`), `exception.message` (`:93`), `exception.stacktrace` only if non-empty (`:94-95`). |

**Why `RecordException` exists:** `Activity.RecordException` (the SDK extension
method) is not resolvable in a net8.0 class library, so `SleipnirTracing.RecordException`
sets the OTel-compliant `exception.type`/`exception.message`/`exception.stacktrace`
tags directly (`SleipnirTracing.cs:79-84`, `CLAUDE.md` §"Distributed Tracing").

---

## 3. The three instrumentation sites

All in `SleipnirCore/Services/SleipnirInvoker.cs` (`using SleipnirCore.Tracing;`
at `:15`). Every `SleipnirTracing.*` call site in the invoker, by line: `303`,
`346`, `362`, `370`, `371`, `1449`, `1509`, `1523`, `1543`, `1544`.

### Site 1 — single-call `InvokeDi(SleipnirRequest)`

Signature `:329`. The call span wraps the whole interceptor pipeline:

- `using var activity = SleipnirTracing.StartCall(request);` — `:346`
  (comment `:344-345`: a future tracing interceptor would become a child span,
  no double-count).
- The activity is threaded into the pipeline via
  `SleipnirInvocationContext.Activity = activity` — `:354`.
- On success: `SleipnirTracing.SetCallStatus(activity, response);` — `:362`.
- On exception: `SleipnirTracing.RecordException(activity, ex);` (`:370`) then
  `SetCallStatus(activity, response);` (`:371`).

### Site 2 — batch dispatcher `InvokeDi(IEnumerable<SleipnirRequest>)`

Signature `:288`. The batch parent span:

- `using var batchActivity = SleipnirTracing.StartBatch(requestList, mode);` —
  `:303` (comment `:302`: batch parent with `rpc.system` +
  `sleipnir.batch.mode/count`, null without listener).
- Auto-detect dependency path overwrites the mode tag:
  `batchActivity?.SetTag("sleipnir.batch.mode", "DependencyBatches");` — `:312`.

### Site 3 — per-request execution in the batch path

The per-request span in the batch path is opened in **two** methods, so every
request gets exactly one span:

- **`ExecuteAuthorized`** (success / business-error path) — signature `:1443`.
  `using var activity = SleipnirTracing.StartCall(request);` — `:1449`; sets
  `Activity.Current = activity` (`:1450-1451`) so user-code spans nest under the
  call span; restored in `finally` (`:1517`). On exception:
  `SleipnirTracing.RecordException(activity, ex);` — `:1509`. Status via the
  local `Status()` function → `SetCallStatus` — `:1523`.
- **`TraceCallError`** (pre-execution error path: auth/lookup/availability) —
  signature `:1541`. Opens a short `SleipnirCall` span, sets status, disposes
  (`:1543-1544`). Comment `:1534-1540`: error paths get their span here, execution
  in `ExecuteAuthorized`.

> **Naming discrepancy with `CLAUDE.md`:** `CLAUDE.md:156-158` names the third
> site "ExecuteSingleInvocation". No method of that exact name exists in
> `SleipnirInvoker.cs` — the legacy name survives only in comments (`:1435`,
> `:1439`). The actual sites are `ExecuteAuthorized` (`:1449`) and
> `TraceCallError` (`:1543`). The single-call execution helper
> `ExecuteSingleInvocationSimple` (`:512`) does **not** open its own span — the
> single-call span is opened by Site 1. (Doc-bug — flagged §11.)

---

## 4. Why inline, not an `ISleipnirInterceptor`

The batch path **bypasses the interceptor pipeline**. The WARN comment at
`SleipnirInvoker.cs:282-287` states `ISleipnirBatchInterceptor` /
`SleipnirOptions.BatchInterceptors` are currently **not called** — the batch
path goes directly into `ExecuteInParallel`/`ExecuteSequentially`/
`ExecuteInDependencyBatches` without a batch-interceptor pipeline; user
interceptors from `SleipnirOptions.Interceptors` run only in the single-call
path. A batch-interceptor pipeline is planned (comment `:286`). So the tracing
instrumentation is inline in the invoker to cover both paths
(`CLAUDE.md` §"Distributed Tracing").

**`SleipnirTelemetryInterceptor` is a different thing.**
`SleipnirHub/Interceptors/SleipnirTelemetryInterceptor.cs:44` is a single-call-path
`ISleipnirInterceptor` that **reuses** `context.Activity` (`:58`) — it does not
create its own span ("kein Double-Count", `:13-15, :66`). It calls
`SleipnirTracing.SetCallStatus` (`:67`) and `SleipnirTracing.RecordException`
(`:79`) on the existing activity, plus `SleipnirMetrics.RecordCall` (`:99`). It
is metrics/logging convenience around the invoker-owned span, **not** the
span-creation site. The invoker's inline `SleipnirTracing.*` calls remain
because the batch path bypasses the interceptor pipeline.

---

## 5. Cost-neutrality

- **No listener → no activity.** `ActivitySource.StartActivity` returns `null`
  when no `ActivityListener` is subscribed; every helper no-ops via a null check
  (`SleipnirTracing.cs:9-11`; null-checks at `:34-35`, `:55-56`, `:70-71`,
  `:89-90`). Verified by `SleipnirTracingTests.NoListener_StartActivityReturnsNull_IsCostNeutral`
  (`SleipnirTests/Unit/Core/SleipnirTracingTests.cs:142`).
- **Not DI-registered.** `AddSleipnir` registers the `SleipnirConnectionRegistry`
  and calls `SleipnirMetrics.SetConnectionRegistry`
  (`SleipnirHub/Extensions/SleipnirServiceCollectionExtension.cs:59-61`) but does
  **not** register `SleipnirTracing` or any `ActivitySource` (grep of the file:
  no `SleipnirTracing` reference). `SleipnirTracing.Source` is a static field
  initialized inline (`SleipnirTracing.cs:26`).
- **No registration synchronization needed.** There is now only one
  registration factory — `AddSleipnir` (`SleipnirServiceCollectionExtension.cs:44`)
  — and it does not touch tracing. The metrics gauge registration
  (`SetConnectionRegistry`) is idempotent via `??=`
  (`SleipnirCore/Tracing/SleipnirMetrics.cs:59, :65`), so repeated `AddSleipnir`
  calls do not create duplicate gauges.

> **`CLAUDE.md:158` is partly stale.** It references "two parallel registration
> factories (`AddSleipnir` + `AddSleipnirCore`)" needing no synchronization.
> `AddSleipnirCore` was **deleted in 1.1.2** (R1 — "delete drifted
> `AddSleipnirCore`, route fluent overload through canonical `AddSleipnir`",
> `docs/audits/2026-08-08-consolidation-roadmap.md:23, :46, :70`,
> `CHANGELOG.md:460-468`). The fluent overload now routes through the canonical
> `AddSleipnir` (`SleipnirControllerBuilder.cs:124`). With only `AddSleipnir`
> remaining and tracing being static/not-DI-registered, there is nothing to
> synchronize. (Doc-bug — flagged §11.)

---

## 6. The span model & tags

| Span | Name | `ActivityKind` | Tags | Status |
|------|------|----------------|------|--------|
| **Call** | `SleipnirCall` | `Internal` | `rpc.system`=`"sleipnir"`, `rpc.service`=`Controller`, `rpc.method`=`Method`, `sleipnir.request_id`=`Id` (only if non-empty), `sleipnir.binary.length`=`BinaryData.Length` (only if non-empty) | `Ok`/`Error` via `SetCallStatus`; `exception.*` on throw | 
| **Batch** | `SleipnirBatch` | `Internal` | `rpc.system`=`"sleipnir"`, `sleipnir.batch.mode`=`mode.ToString()` (overwritten to `"DependencyBatches"` on auto-detect), `sleipnir.batch.count`=`Count` | (parent span; children are per-request `SleipnirCall`) |

Call tags: `SleipnirTracing.cs:37-43`. Batch tags: `:58-60`. The batch parent
spawns one `SleipnirCall` child per request (Site 3); verified by
`SleipnirTracingTests.Batch_EmitsParentAndChildActivities`
(`SleipnirTracingTests.cs:158`).

The `rpc.system`/`rpc.service`/`rpc.method` tags follow the OpenTelemetry
**RPC semantic conventions**. A domain error (a returned `SleipnirResponse` with
a non-2xx code) sets `Error` status **without** `exception.*` tags — only a
thrown exception records the exception tags
(`SleipnirTracingTests.cs:83` `SingleCall_DomainError_SetsErrorStatus_WithoutExceptionTags`).

---

## 7. `Sleipnir.Telemetry` — the opt-in package

**Project:** `SleipnirTelemetry/SleipnirTelemetry.csproj` — `PackageId` =
`Sleipnir.Telemetry` (`:7`), `net8.0` (`:4`), `ProjectReference` to `SleipnirCore`
only (`:22`). The csproj comment (`:18-20`) states this grants access to the
public `ActivitySourceName`; the instrumentation lives in the engine, always-on
without an SDK dependency; this package only brings the OTel SDK and subscribes
the source name. OTel packages locked at 1.16.0 (`:27-32`), plus
`OpenTelemetry.Exporter.Prometheus.AspNetCore` 1.16.0-beta.1 (`:33-37`).

**Extension:** `SleipnirTelemetry/SleipnirTelemetryServiceExtensions.cs:22` —
`public static class SleipnirTelemetryServiceExtensions`.

```csharp
public static IServiceCollection AddSleipnirTelemetry(
    this IServiceCollection services, Action<SleipnirTelemetryOptions>? configure = null)
```

`SleipnirTelemetryServiceExtensions.cs:27`. The single tracing integration point:
`builder.AddSource(SleipnirTracing.ActivitySourceName)` — `:34-39`
(comment `:36`). Optional instrumentation gates:
`AddAspNetCoreInstrumentation()` (`:42`) if `options.IncludeAspNetCore`,
`AddHttpClientInstrumentation()` (`:44`) if `options.IncludeHttpClient`.
Exporters: `AddConsoleExporter()` (`:48`) when `Exporter == Console`; else
`AddOtlpExporter(o => o.Endpoint = new Uri(options.OtlpEndpoint))` (`:52-56`).

It also wires the **metrics column**: `services.AddOpenTelemetry().WithMetrics(
builder => { builder.AddMeter(SleipnirMetrics.MeterName)... })` (`:66-69`),
with the same Console/Otlp exporter scheme (`:72-83`). Metrics are covered in
`OBSERVABILITY_REFERENCE.md`.

### `SleipnirTelemetryOptions` — `SleipnirTelemetry/SleipnirTelemetryOptions.cs`

| Member | Type | Default | Line |
|--------|------|---------|------|
| `SleipnirExporter` enum | `Console \| Otlp` | — | `:4-11` |
| `ServiceName` | `string` | `"Sleipnir"` | `:22` |
| `Exporter` | `SleipnirExporter` | `Otlp` | `:25` |
| `OtlpEndpoint` | `string?` | — | `:31` |
| `IncludeAspNetCore` | `bool` | `true` | `:34` |
| `IncludeHttpClient` | `bool` | `true` | `:37` |

**Prometheus pull model (metrics, out of scope):**
`SleipnirTelemetry/SleipnirPrometheusExtensions.cs` —
`AddSleipnirPrometheusMetrics()` (`:49`) and
`UseSleipnirPrometheusScrapingEndpoint(path, requireAuth)` (`:67`). See
`OBSERVABILITY_REFERENCE.md`.

---

## 8. The `Sleipnir` ↔ `Trame` rename

Before the rename, the source/meter name was `"Trame"` and the operation names
were `TrameCall`/`TrameBatch`. The rename isolated Sleipnir spans from
Heimdall/Walhalla consumer sources (`CHANGELOG.md:434-437`):

- `ActivitySource`/`Meter` name `"Trame"` → `"Sleipnir"`.
- Operation names `TrameCall`/`TrameBatch` → `SleipnirCall`/`SleipnirBatch`.
- Migration (`CHANGELOG.md:454-455`): "If you reference the telemetry source by
  name, add `AddSource("Sleipnir")` (instead of `"Trame"`) to your OpenTelemetry
  tracing pipeline."

So a consumer still subscribing to `"Trame"` sees **no** Sleipnir spans —
subscribe to `"Sleipnir"`.

---

## 9. Test isolation — the process-global `ActivityListener`

The `ActivityListener` is **process-global** and captures Activities emitted by
other invoker-based tests running in parallel. The telemetry tests start the
OTel-SDK subscription which subscribes to `"Sleipnir"` and would falsify the
`NoListener` test and the `probe != null` assertions
(`SleipnirTracingTests.cs:26-38`). Therefore tracing and telemetry tests share
the **`sleipnir-tracing` xUnit collection** — serialized only among themselves;
the rest of the assembly parallelizes normally.

**Collection definition:** `SleipnirTests/Unit/Core/SleipnirTracingTests.cs:377`
— `[CollectionDefinition("sleipnir-tracing")] public class SleipnirTracingCollectionDefinition { }`
(comment `:373-376`). Sole definition in the repo.

**Tests in the collection:**

| Test file | Collection attr line |
|-----------|----------------------|
| `SleipnirTracingTests.cs` | `:39` |
| `SleipnirTelemetryExtensionsTests.cs` | `:22` |
| `SleipnirConnectionRegistryTests.cs` | `:18` (gauge/meter tests join the same collection) |
| `TransportToggleTests.cs` (Integration) | `:34` |
| `SubscriptionStoreTests.cs` | `:23` |

**Test-harness activity for isolation** — the `ActivityCapture` private sealed
class (`SleipnirTracingTests.cs:286-349`):

- `HarnessSourceName = "Sleipnir.Tests.Harness"` (`:288`).
- The listener filters `source.Name == SleipnirTracing.ActivitySourceName || source.Name == HarnessSourceName` (`:302-303`); samples `AllDataAndRecorded` (`:304-307`); records only Sleipnir-source activities (`:308-312`).
- The harness activity is started from a **separate** source
  `_harnessSource.StartActivity("test-harness", ActivityKind.Internal)!` (`:318`)
  and becomes `Activity.Current`, so all Sleipnir activities of the test become
  its children.
- `Mine()` (`:322`) returns only `Activities.Where(a => IsDescendantOf(a, _harness))`
  — foreign tests have a different/no parent and are filtered out, preserving
  full parallelism for the rest of the assembly.

---

## 10. Boundary: tracing vs metrics vs observability endpoints

| Concern | Class | Surface | Doc |
|---------|-------|---------|-----|
| **Tracing** (spans) | `SleipnirCore.Tracing.SleipnirTracing` | `ActivitySource "Sleipnir"`, `SleipnirCall`/`SleipnirBatch` | **this doc** |
| **Metrics** (counters/histograms/gauges) | `SleipnirCore.Tracing.SleipnirMetrics` | `Meter "Sleipnir"`: `sleipnir.call.duration/count`, `sleipnir.error.count`, `sleipnir.event.dropped`, `sleipnir.batch.fan_out/count`, `sleipnir.ws.connections`, `sleipnir.subscriptions.active` | `OBSERVABILITY_REFERENCE.md` |
| **Connection registry** (gauge backing) | `SleipnirCore.Tracing.SleipnirConnectionRegistry` | `Connections`/`Subscriptions`/cumulative counters/`GetSnapshot()` | `OBSERVABILITY_REFERENCE.md` |
| **Observability HTTP endpoints** | `SleipnirRest` | `GET /api/sleipnir/observability` (JSON), `GET /api/sleipnir/metrics` (Prometheus) | `OBSERVABILITY_REFERENCE.md` |
| **Prometheus pull model** | `SleipnirTelemetry/SleipnirPrometheusExtensions` | `AddSleipnirPrometheusMetrics()`, `UseSleipnirPrometheusScrapingEndpoint()` | `OBSERVABILITY_REFERENCE.md` |

The `Meter` and `ActivitySource` share the name `"Sleipnir"` — OTel allows this
(`SleipnirMetrics.cs:28`, `docs/design/phase-1-interceptor-pipeline.md:155-158`).
The two are independent: tracing does not require metrics and vice versa; both
are cost-neutral without a listener/SDK.

> **Metrics/test-flake note (memory `sleipnir-telemetry-test-flake`):**
> `Gauges_Read_Current_Registry_Values` (`SleipnirConnectionRegistryTests.cs:138`)
> reads the observable gauges via a `MeterListener` and asserts
> `connections == 2`, `subscriptions == 3`. Under parallel integration hosts the
> process-global `Current` registry races; it passes in isolation. The
> mitigation is the process-global `Current` read
> (`SleipnirMetrics.cs:61, :67`) + collection serialization. The literal label
> `"sleipnir-telemetry-test-flake"` is **not** in the repo (it is a memory
> label). Not a regression — don't chase it.

---

## 11. Diagnostics & troubleshooting catalog

### Common mistakes

- **Subscribing the wrong source name.** After the rename, the source is
  `"Sleipnir"`, not `"Trame"`. Add `AddSource("Sleipnir")`
  (`CHANGELOG.md:454-455`). A consumer still on `"Trame"` sees no spans.
- **Expecting spans from the batch path via an interceptor.** The batch path
  bypasses the interceptor pipeline (`SleipnirInvoker.cs:282-287`); batch spans
  come from the inline instrumentation, not a user interceptor. A user
  `ISleipnirInterceptor` runs only on the single-call path.
- **Confusing `SleipnirTelemetryInterceptor` with the instrumentation site.**
  The interceptor **reuses** the invoker-opened `context.Activity`
  (`SleipnirTelemetryInterceptor.cs:58`); it does not create spans. It exists for
  metrics/logging convenience on the single-call path.
- **Expecting `exception.*` tags on a business error.** A returned
  `SleipnirResponse` with a non-2xx code sets `Error` status but records **no**
  `exception.*` tags; only a thrown exception does
  (`SleipnirTracingTests.cs:83`).

### Doc-bugs to fix when convenient

- **`CLAUDE.md:156-158` names the third instrumentation site
  "ExecuteSingleInvocation".** No method of that exact name exists in
  `SleipnirInvoker.cs`; the name survives only in comments (`:1435`, `:1439`).
  The actual per-request span sites are `ExecuteAuthorized` (`:1449`) and
  `TraceCallError` (`:1543`). The single-call helper
  `ExecuteSingleInvocationSimple` (`:512`) opens no span.
- **`CLAUDE.md:158` references "two parallel registration factories
  (`AddSleipnir` + `AddSleipnirCore`)".** `AddSleipnirCore` was deleted in 1.1.2
  (R1); only `AddSleipnir` remains. The "no synchronization" claim is now
  trivially true (tracing is static, not DI-registered).

---

## 12. How it is verified (the tests)

| Test file | Covers |
|-----------|--------|
| `SleipnirTests/Unit/Core/SleipnirTracingTests.cs` | Always-on instrumentation via the public surface (source name `"Sleipnir"` + in-box `ActivityListener`, no OTel SDK, no `InternalsVisibleTo`): `SingleCall_EmitsActivityWithRpcTags_AndOkStatus` (`:65`), `SingleCall_DomainError_SetsErrorStatus_WithoutExceptionTags` (`:83`), `SingleCall_EmptyId_OmitsRequestIdTag` (`:99`), `SingleCall_BinaryData_RecordsLengthTag` (`:118`), `NoListener_StartActivityReturnsNull_IsCostNeutral` (`:142`), `Batch_EmitsParentAndChildActivities` (`:158`), `Batch_WithDependencyMapping_SetsDependencyBatchesMode` (`:185`), `Batch_ThrowingStream_RecordsExceptionAndErrorStatus` (`:204`). Throwing fixture `ThrowingSleipnirController` (`:361-371`). Collection definition at `:377`. |
| `SleipnirTests/Unit/Telemetry/SleipnirTelemetryExtensionsTests.cs` | `AddSleipnirTelemetry`: Console exporter subscribes the Sleipnir source (`:26`); gates off (AspNetCore/HttpClient false) still subscribes (`:50`); OTLP with endpoint builds the provider without throwing (`:78`). Helpers `StartHostedServicesAsync` (`:96`), `StopAndDisposeAsync` (`:102`). |
| `SleipnirTests/Unit/Telemetry/SleipnirConnectionRegistryTests.cs` | `SleipnirConnectionRegistry` lock-free `Interlocked` counters (concurrent inc/dec, RecordCall success/failure, RecordBatch/EventDrop accumulation, GetSnapshot, StartedAtUtc) + `SleipnirMetrics` gauges `sleipnir.ws.connections`/`sleipnir.subscriptions.active` via `MeterListener` (`Gauges_Read_Current_Registry_Values` `:138`). |

---

## 13. Relationship to other docs

| Doc | Covers (tracing-relevant) |
|-----|----------------------------|
| `CLAUDE.md` | "Distributed Tracing" section (`:156-158`) — the architecture summary. **Two points are stale**: the "ExecuteSingleInvocation" third-site name (no such method) and the "AddSleipnir + AddSleipnirCore" two-factory claim (`AddSleipnirCore` deleted in 1.1.2). The rest (inline instrumentation, cost-neutrality, `RecordException` rationale, `SleipnirServer` not referencing `Sleipnir.Telemetry`, collection isolation) is accurate. |
| `README.md` | Feature bullet "OpenTelemetry distributed tracing" (`:273`); "OpenTelemetry metrics — `Meter "Sleipnir"`" (`:270`); package matrix row `SleipnirTelemetry` (`:837`); optional package reference (`:849`). |
| `README_DETAILS.md` | Interceptor-pipeline point (`:236`); observability bullet (`:238`, metrics/endpoints). |
| `STABILITY.md` | Built-in interceptors incl. `SleipnirTelemetryInterceptor` (`:86, :92-95`); telemetry `sleipnir.*` instruments experimental, Prometheus as durable contract (`:178-190`). |
| `CHANGELOG.md` | `Trame`→`Sleipnir` telemetry rename (`:434-437`), migration `AddSource("Sleipnir")` (`:454-455`), OpenTelemetry Metrics Phase 1 (`:566-569`), `AddSleipnirTelemetry` subscribes metrics column (`:181`), R1 `AddSleipnirCore` deletion (`:460-468`). |
| `docs/design/phase-1-interceptor-pipeline.md` | Design: `SleipnirTracing` at eight sites (`:21-23`), Decision 4 Meter name `"Sleipnir"` shared with ActivitySource (`:155-158`), no change to the tracing-span model (`:167-168`), `SleipnirTelemetryInterceptor` consolidation intent (`:95-96`). |
| `docs/audits/2026-08-08-consolidation-roadmap.md` | R1: delete `AddSleipnirCore`, route fluent overload through canonical `AddSleipnir` (`:23, :46, :70`). |
| `SleipnirTelemetry/README.md` | Package README (the `PackageReadmeFile`, `SleipnirTelemetry.csproj:10, :15`). |
| `OBSERVABILITY_REFERENCE.md` | **The companion doc** — metrics (`SleipnirMetrics`), the connection registry, the `/observability` + `/metrics` HTTP endpoints, and the Prometheus pull model. |

> **No dedicated `docs/**/tracing*.md` design file exists** beyond
> `docs/design/phase-1-interceptor-pipeline.md` (which covers tracing as one of
> its concerns). This reference consolidates the tracing side.