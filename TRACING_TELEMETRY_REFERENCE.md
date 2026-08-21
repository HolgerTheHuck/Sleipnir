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
instrumentation-site table with durable citations, the `Sleipnir.Telemetry`
options table, a diagnostics catalog (incl. stale-doc flags), and a map of
where the deeper docs live. For the architecture summary read `CLAUDE.md`
§"Distributed Tracing"; for the design rationale read
`docs/design/phase-1-interceptor-pipeline.md`. This doc consolidates those,
corrects two stale points in `CLAUDE.md`, and links back for depth.

All citations are repo-relative file paths anchored to durable symbols
(member/type names) or short verbatim quotes — line numbers are intentionally
omitted so the references survive code edits. Exceptions: version numbers,
HTTP status codes, and `:port` literals retain their numeric form. Code-facing
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
`"Sleipnir"` (`SleipnirCore/Tracing/SleipnirTracing.cs` → `ActivitySourceName`). The
instrumentation lives **directly in `SleipnirInvoker`**, not as an
`ISleipnirInterceptor`, because the batch path bypasses the interceptor pipeline
(§4). It is **cost-neutral**: `StartActivity` returns `null` without a listener,
every helper null-checks the activity, and it is not DI-registered (§5).

The source name is `public` so the optional `Sleipnir.Telemetry` package (a
different assembly) can reach `SleipnirTracing.ActivitySourceName`; all other
members are `internal` and the instrumentation is not part of the public contract
(`SleipnirTracing.cs` → class `<remarks>`). Consumers subscribe to the source name
`"Sleipnir"` from their own OpenTelemetry setup, or via the convenience
`AddSleipnirTelemetry` (§7).

`SleipnirServer` does **not** reference `Sleipnir.Telemetry`
(`SleipnirServer/SleipnirServer.csproj` → the `ProjectReference` `ItemGroup`
references only `SleipnirHub`, `SleipnirRest`, `SleipnirWebSocket`,
`SleipnirDeveloperUi`). Tracing is opt-in: consumers add the `Sleipnir.Telemetry`
package and call `AddSleipnirTelemetry` (`README_DETAILS.md` §"Project Structure"
→ `SleipnirTelemetry` row, and §"Installation (NuGet)" → the
`Sleipnir.Telemetry` `PackageReference`).

---

## 2. `SleipnirTracing` — the class

`SleipnirCore/Tracing/SleipnirTracing.cs` → `public static class SleipnirTracing`.

| Member | Visibility | Where | Purpose |
|--------|-----------|-------|---------|
| `ActivitySourceName` | `public const string` = `"Sleipnir"` | `SleipnirTracing.ActivitySourceName` | The source name consumers subscribe to. |
| `Source` | `internal static readonly ActivitySource` | `SleipnirTracing.Source` | `new(ActivitySourceName, "1.0.0")`. The source itself is internal; version is hardcoded. |
| `StartCall(SleipnirRequest)` | `internal static Activity?` | `SleipnirTracing.StartCall()` | Starts a `"SleipnirCall"` activity, `ActivityKind.Internal`. Returns `null` without a listener. |
| `StartBatch(IReadOnlyList<SleipnirRequest>, ExecutionMode)` | `internal static Activity?` | `SleipnirTracing.StartBatch()` | Starts a `"SleipnirBatch"` activity. Returns `null` without a listener. |
| `SetCallStatus(Activity?, SleipnirResponse?)` | `public static void` | `SleipnirTracing.SetCallStatus()` | No-op if null; `Ok` when `response?.IsSuccess`, else `Error` with `response?.Error?.Message ?? "RPC failed"`. |
| `RecordException(Activity?, Exception)` | `public static void` | `SleipnirTracing.RecordException()` | No-op if null; sets `exception.type`, `exception.message`, `exception.stacktrace` only if non-empty. |

**Why `RecordException` exists:** `Activity.RecordException` (the SDK extension
method) is not resolvable in a net8.0 class library, so `SleipnirTracing.RecordException`
sets the OTel-compliant `exception.type`/`exception.message`/`exception.stacktrace`
tags directly (`SleipnirTracing.cs` → `RecordException` doc comment, `CLAUDE.md`
§"Distributed Tracing").

---

## 3. The three instrumentation sites

All in `SleipnirCore/Services/SleipnirInvoker.cs` (`using SleipnirCore.Tracing;`
directive). Every `SleipnirTracing.*` call site in the invoker, by method:
`InvokeDi(IEnumerable<…>)` → `StartBatch`; single-call `InvokeDi(SleipnirRequest)`
→ `StartCall`, `SetCallStatus`, `RecordException`, `SetCallStatus`;
`ExecuteAuthorized` → `StartCall`, `RecordException`, `SetCallStatus`;
`TraceCallError` → `StartCall`, `SetCallStatus`.

### Site 1 — single-call `InvokeDi(SleipnirRequest)`

Signature: `InvokeDi(SleipnirRequest, HttpContext?, CancellationToken)`. The call
span wraps the whole interceptor pipeline:

- `using var activity = SleipnirTracing.StartCall(request);` — in single-call
  `InvokeDi` (comment: a future tracing interceptor would become a child span,
  no double-count).
- The activity is threaded into the pipeline via
  `SleipnirInvocationContext.Activity = activity` — in single-call `InvokeDi`.
- On success: `SleipnirTracing.SetCallStatus(activity, response);` — in
  single-call `InvokeDi`.
- On exception: `SleipnirTracing.RecordException(activity, ex);` then
  `SetCallStatus(activity, response);` — in single-call `InvokeDi`.

### Site 2 — batch dispatcher `InvokeDi(IEnumerable<SleipnirRequest>)`

Signature: `InvokeDi(IEnumerable<SleipnirRequest>, HttpContext?, ExecutionMode,
CancellationToken)`. The batch parent span:

- `using var batchActivity = SleipnirTracing.StartBatch(requestList, mode);` —
  in `InvokeDi(IEnumerable<…>)` (comment: batch parent with `rpc.system` +
  `sleipnir.batch.mode/count`, null without listener).
- Auto-detect dependency path overwrites the mode tag:
  `batchActivity?.SetTag("sleipnir.batch.mode", "DependencyBatches");` — in
  `InvokeDi(IEnumerable<…>)`.

### Site 3 — per-request execution in the batch path

The per-request span in the batch path is opened in **two** methods, so every
request gets exactly one span:

- **`ExecuteAuthorized`** (success / business-error path).
  `using var activity = SleipnirTracing.StartCall(request);`; sets
  `Activity.Current = activity` so user-code spans nest under the call span;
  restored in `finally`. On exception:
  `SleipnirTracing.RecordException(activity, ex);`. Status via the local
  `Status()` function → `SetCallStatus`.
- **`TraceCallError`** (pre-execution error path: auth/lookup/availability).
  Opens a short `SleipnirCall` span, sets status, disposes. Comment: error paths
  get their span here, execution in `ExecuteAuthorized`.

> **Naming discrepancy with `CLAUDE.md`:** `CLAUDE.md` §"Distributed Tracing"
> names the third site "ExecuteSingleInvocation". No method of that exact name
> exists in `SleipnirInvoker.cs` — the legacy name survives only in comments in
> `ExecuteAuthorized`'s doc comment. The actual sites are `ExecuteAuthorized`
> and `TraceCallError`. The single-call execution helper
> `ExecuteSingleInvocationSimple` does **not** open its own span — the
> single-call span is opened by Site 1. (Doc-bug — flagged §11.)

---

## 4. Why inline, not an `ISleipnirInterceptor`

The batch path **bypasses the interceptor pipeline**. The WARN comment in
`InvokeDi(IEnumerable<…>)` states `ISleipnirBatchInterceptor` /
`SleipnirOptions.BatchInterceptors` are currently **not called** — the batch
path goes directly into `ExecuteInParallel`/`ExecuteSequentially`/
`ExecuteInDependencyBatches` without a batch-interceptor pipeline; user
interceptors from `SleipnirOptions.Interceptors` run only in the single-call
path. A batch-interceptor pipeline is planned (same comment). So the tracing
instrumentation is inline in the invoker to cover both paths
(`CLAUDE.md` §"Distributed Tracing").

**`SleipnirTelemetryInterceptor` is a different thing.**
`SleipnirHub/Interceptors/SleipnirTelemetryInterceptor.cs` →
`SleipnirTelemetryInterceptor` is a single-call-path `ISleipnirInterceptor` that
**reuses** `context.Activity` (in `InvokeAsync`) — it does not create its own
span ("kein Double-Count", class doc + `InvokeAsync`). It calls
`SleipnirTracing.SetCallStatus` and `SleipnirTracing.RecordException` on the
existing activity (in `InvokeAsync`), plus `SleipnirMetrics.RecordCall` (in
`RecordTelemetry`). It is metrics/logging convenience around the
invoker-owned span, **not** the span-creation site. The invoker's inline
`SleipnirTracing.*` calls remain because the batch path bypasses the
interceptor pipeline.

---

## 5. Cost-neutrality

- **No listener → no activity.** `ActivitySource.StartActivity` returns `null`
  when no `ActivityListener` is subscribed; every helper no-ops via a null check
  (class `<summary>`; null-checks in `StartCall`, `StartBatch`, `SetCallStatus`,
  `RecordException`). Verified by
  `SleipnirTracingTests.NoListener_StartActivityReturnsNull_IsCostNeutral`
  (`SleipnirTests/Unit/Core/SleipnirTracingTests.cs`).
- **Not DI-registered.** `AddSleipnir` registers the `SleipnirConnectionRegistry`
  and calls `SleipnirMetrics.SetConnectionRegistry`
  (`SleipnirHub/Extensions/SleipnirServiceCollectionExtension.cs` → `AddSleipnir`)
  but does **not** register `SleipnirTracing` or any `ActivitySource` (grep of
  the file: no `SleipnirTracing` reference). `SleipnirTracing.Source` is a static
  field initialized inline (`SleipnirCore/Tracing/SleipnirTracing.cs` → `Source`).
- **No registration synchronization needed.** There is now only one registration
  factory — `AddSleipnir` (`SleipnirServiceCollectionExtension.cs` → `AddSleipnir`)
  — and it does not touch tracing. The metrics gauge registration
  (`SetConnectionRegistry`) is idempotent via `??=`
  (`SleipnirCore/Tracing/SleipnirMetrics.cs` → `SetConnectionRegistry`), so
  repeated `AddSleipnir` calls do not create duplicate gauges.

> **`CLAUDE.md` §"Distributed Tracing" is partly stale.** It references "two
> parallel registration factories (`AddSleipnir` + `AddSleipnirCore`)" needing no
> synchronization. `AddSleipnirCore` was **deleted in 1.1.2** (R1 — "delete drifted
> `AddSleipnirCore`, route fluent overload through canonical `AddSleipnir`",
> `docs/audits/2026-08-08-consolidation-roadmap.md` §"Status board" / §"R1 — Delete
> drifted `AddSleipnirCore`…", `CHANGELOG.md` → "R1 — fluent overload routed
> through canonical `AddTrame`" entry). The fluent overload now routes through
> the canonical `AddSleipnir` (`SleipnirHub/Extensions/SleipnirControllerBuilder.cs`
> → the `AddSleipnir(SleipnirOptions, Action<SleipnirControllerBuilder>)` fluent
> overload, comment "Route through the canonical AddSleipnir"). With only
> `AddSleipnir` remaining and tracing being static/not-DI-registered, there is
> nothing to synchronize. (Doc-bug — flagged §11.)

---

## 6. The span model & tags

| Span | Name | `ActivityKind` | Tags | Status |
|------|------|----------------|------|--------|
| **Call** | `SleipnirCall` | `Internal` | `rpc.system`=`"sleipnir"`, `rpc.service`=`Controller`, `rpc.method`=`Method`, `sleipnir.request_id`=`Id` (only if non-empty), `sleipnir.binary.length`=`BinaryData.Length` (only if non-empty) | `Ok`/`Error` via `SetCallStatus`; `exception.*` on throw | 
| **Batch** | `SleipnirBatch` | `Internal` | `rpc.system`=`"sleipnir"`, `sleipnir.batch.mode`=`mode.ToString()` (overwritten to `"DependencyBatches"` on auto-detect), `sleipnir.batch.count`=`Count` | (parent span; children are per-request `SleipnirCall`) |

Call tags: `SleipnirTracing.StartCall()`. Batch tags: `SleipnirTracing.StartBatch()`.
The batch parent spawns one `SleipnirCall` child per request (Site 3); verified
by `SleipnirTracingTests.Batch_EmitsParentAndChildActivities`
(`SleipnirTests/Unit/Core/SleipnirTracingTests.cs`).

The `rpc.system`/`rpc.service`/`rpc.method` tags follow the OpenTelemetry
**RPC semantic conventions**. A domain error (a returned `SleipnirResponse` with
a non-2xx code) sets `Error` status **without** `exception.*` tags — only a
thrown exception records the exception tags
(`SleipnirTracingTests.SingleCall_DomainError_SetsErrorStatus_WithoutExceptionTags`).

---

## 7. `Sleipnir.Telemetry` — the opt-in package

**Project:** `SleipnirTelemetry/SleipnirTelemetry.csproj` — `PackageId` =
`Sleipnir.Telemetry`, `net8.0`, `ProjectReference` to `SleipnirCore` only. The
csproj comment states this grants access to the public `ActivitySourceName`;
the instrumentation lives in the engine, always-on without an SDK dependency;
this package only brings the OTel SDK and subscribes the source name. OTel
packages locked at 1.16.0, plus `OpenTelemetry.Exporter.Prometheus.AspNetCore`
1.16.0-beta.1.

**Extension:** `SleipnirTelemetry/SleipnirTelemetryServiceExtensions.cs` →
`SleipnirTelemetryServiceExtensions`.

```csharp
public static IServiceCollection AddSleipnirTelemetry(
    this IServiceCollection services, Action<SleipnirTelemetryOptions>? configure = null)
```

`AddSleipnirTelemetry()` is the single tracing integration point:
`builder.AddSource(SleipnirTracing.ActivitySourceName)` (comment: subscribe the
Sleipnir source). Optional instrumentation gates:
`AddAspNetCoreInstrumentation()` if `options.IncludeAspNetCore`,
`AddHttpClientInstrumentation()` if `options.IncludeHttpClient`. Exporters:
`AddConsoleExporter()` when `Exporter == Console`; else
`AddOtlpExporter(o => o.Endpoint = new Uri(options.OtlpEndpoint))`.

It also wires the **metrics column**: `services.AddOpenTelemetry().WithMetrics(
builder => { builder.AddMeter(SleipnirMetrics.MeterName)... })`, with the same
Console/Otlp exporter scheme. Metrics are covered in `OBSERVABILITY_REFERENCE.md`.

### `SleipnirTelemetryOptions` — `SleipnirTelemetry/SleipnirTelemetryOptions.cs`

| Member | Type | Default | Where |
|--------|------|---------|-------|
| `SleipnirExporter` enum | `Console \| Otlp` | — | `SleipnirTelemetryOptions.SleipnirExporter` |
| `ServiceName` | `string` | `"Sleipnir"` | `SleipnirTelemetryOptions.ServiceName` |
| `Exporter` | `SleipnirExporter` | `Otlp` | `SleipnirTelemetryOptions.Exporter` |
| `OtlpEndpoint` | `string?` | — | `SleipnirTelemetryOptions.OtlpEndpoint` |
| `IncludeAspNetCore` | `bool` | `true` | `SleipnirTelemetryOptions.IncludeAspNetCore` |
| `IncludeHttpClient` | `bool` | `true` | `SleipnirTelemetryOptions.IncludeHttpClient` |

**Prometheus pull model (metrics, out of scope):**
`SleipnirTelemetry/SleipnirPrometheusExtensions.cs` →
`AddSleipnirPrometheusMetrics()` and
`UseSleipnirPrometheusScrapingEndpoint(path, requireAuth)`. See
`OBSERVABILITY_REFERENCE.md`.

---

## 8. The `Sleipnir` ↔ `Trame` rename

Before the rename, the source/meter name was `"Trame"` and the operation names
were `TrameCall`/`TrameBatch`. The rename isolated Sleipnir spans from
Heimdall/Walhalla consumer sources (`CHANGELOG.md` §"[1.0.0] - 2026-08-11 —
Renamed from Trame" → "Telemetry" bullet):

- `ActivitySource`/`Meter` name `"Trame"` → `"Sleipnir"`.
- Operation names `TrameCall`/`TrameBatch` → `SleipnirCall`/`SleipnirBatch`.
- Migration (`CHANGELOG.md` §"[1.0.0]" migration steps): "If you reference the
  telemetry source by name, add `AddSource("Sleipnir")` (instead of `"Trame"`) to
  your OpenTelemetry tracing pipeline."

So a consumer still subscribing to `"Trame"` sees **no** Sleipnir spans —
subscribe to `"Sleipnir"`.

---

## 9. Test isolation — the process-global `ActivityListener`

The `ActivityListener` is **process-global** and captures Activities emitted by
other invoker-based tests running in parallel. The telemetry tests start the
OTel-SDK subscription which subscribes to `"Sleipnir"` and would falsify the
`NoListener` test and the `probe != null` assertions
(`SleipnirTests/Unit/Core/SleipnirTracingTests.cs` →
`NoListener_StartActivityReturnsNull_IsCostNeutral` + the probe assertions).
Therefore tracing and telemetry tests share the **`sleipnir-tracing` xUnit
collection** — serialized only among themselves; the rest of the assembly
parallelizes normally.

**Collection definition:** `SleipnirTests/Unit/Core/SleipnirTracingTests.cs` →
`SleipnirTracingCollectionDefinition` (`[CollectionDefinition("sleipnir-tracing")]`).
Sole definition in the repo.

**Tests in the collection:**

| Test file | Where |
|-----------|-------|
| `SleipnirTracingTests.cs` | `[Collection("sleipnir-tracing")]` on the test class |
| `SleipnirTelemetryExtensionsTests.cs` | `[Collection("sleipnir-tracing")]` on the test class |
| `SleipnirConnectionRegistryTests.cs` | `[Collection("sleipnir-tracing")]` (gauge/meter tests join the same collection) |
| `TransportToggleTests.cs` (Integration) | `[Collection("sleipnir-tracing")]` |
| `SubscriptionStoreTests.cs` | `[Collection("sleipnir-tracing")]` |

**Test-harness activity for isolation** — the `ActivityCapture` private sealed
class (`SleipnirTracingTests.cs` → `ActivityCapture`):

- `HarnessSourceName = "Sleipnir.Tests.Harness"` (`ActivityCapture.HarnessSourceName`).
- The listener filters `source.Name == SleipnirTracing.ActivitySourceName ||
  source.Name == HarnessSourceName`; samples `AllDataAndRecorded`; records only
  Sleipnir-source activities.
- The harness activity is started from a **separate** source
  `_harnessSource.StartActivity("test-harness", ActivityKind.Internal)!` and
  becomes `Activity.Current`, so all Sleipnir activities of the test become its
  children.
- `Mine()` returns only `Activities.Where(a => IsDescendantOf(a, _harness))`
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
(`SleipnirCore/Tracing/SleipnirMetrics.cs` → `Meter`/`MeterName`,
`docs/design/phase-1-interceptor-pipeline.md` §"Entscheidung 4 — Meter-Name
`"Sleipnir"`"). The two are independent: tracing does not require metrics and
vice versa; both are cost-neutral without a listener/SDK.

> **Metrics/test-flake note (memory `sleipnir-telemetry-test-flake`):**
> `Gauges_Read_Current_Registry_Values`
> (`SleipnirTests/Unit/Telemetry/SleipnirConnectionRegistryTests.cs`) reads the
> observable gauges via a `MeterListener` and asserts `connections == 2`,
> `subscriptions == 3`. Under parallel integration hosts the process-global
> `Current` registry races; it passes in isolation. The mitigation is the
> process-global `Current` registry read
> (`SleipnirCore/Tracing/SleipnirMetrics.cs` → `SetConnectionRegistry`) +
> collection serialization. The literal label `"sleipnir-telemetry-test-flake"`
> is **not** in the repo (it is a memory label). Not a regression — don't chase
> it.

---

## 11. Diagnostics & troubleshooting catalog

### Common mistakes

- **Subscribing the wrong source name.** After the rename, the source is
  `"Sleipnir"`, not `"Trame"`. Add `AddSource("Sleipnir")` (`CHANGELOG.md`
  §"[1.0.0]" migration steps). A consumer still on `"Trame"` sees no spans.
- **Expecting spans from the batch path via an interceptor.** The batch path
  bypasses the interceptor pipeline (`SleipnirInvoker.cs` → `InvokeDi(IEnumerable<…>)`
  WARN comment); batch spans come from the inline instrumentation, not a user
  interceptor. A user `ISleipnirInterceptor` runs only on the single-call path.
- **Confusing `SleipnirTelemetryInterceptor` with the instrumentation site.**
  The interceptor **reuses** the invoker-opened `context.Activity`
  (`SleipnirTelemetryInterceptor.cs` → `InvokeAsync`); it does not create spans.
  It exists for metrics/logging convenience on the single-call path.
- **Expecting `exception.*` tags on a business error.** A returned
  `SleipnirResponse` with a non-2xx code sets `Error` status but records **no**
  `exception.*` tags; only a thrown exception does
  (`SleipnirTracingTests.SingleCall_DomainError_SetsErrorStatus_WithoutExceptionTags`).

### Doc-bugs addressed

- **`CLAUDE.md` §"Distributed Tracing" named the third instrumentation site
  "ExecuteSingleInvocation".** No method of that exact name exists in
  `SleipnirInvoker.cs`; the name survives only in comments in
  `ExecuteAuthorized`'s doc comment. The actual per-request span sites are
  `ExecuteAuthorized` and `TraceCallError`. The single-call helper
  `ExecuteSingleInvocationSimple` opens no span. **Fixed** — `CLAUDE.md`
  §"Distributed Tracing" now names `ExecuteAuthorized`.
- **`CLAUDE.md` §"Distributed Tracing" referenced "two parallel registration
  factories (`AddSleipnir` + `AddSleipnirCore`)".** `AddSleipnirCore` was deleted
  in 1.1.2 (R1); only `AddSleipnir` remains. The "no synchronization" claim is now
  trivially true (tracing is static, not DI-registered). **Fixed** —
  `CLAUDE.md` §"Distributed Tracing" now references only `AddSleipnir`.

---

## 12. How it is verified (the tests)

| Test file | Covers |
|-----------|--------|
| `SleipnirTests/Unit/Core/SleipnirTracingTests.cs` | Always-on instrumentation via the public surface (source name `"Sleipnir"` + in-box `ActivityListener`, no OTel SDK, no `InternalsVisibleTo`): `SingleCall_EmitsActivityWithRpcTags_AndOkStatus`, `SingleCall_DomainError_SetsErrorStatus_WithoutExceptionTags`, `SingleCall_EmptyId_OmitsRequestIdTag`, `SingleCall_BinaryData_RecordsLengthTag`, `NoListener_StartActivityReturnsNull_IsCostNeutral`, `Batch_EmitsParentAndChildActivities`, `Batch_WithDependencyMapping_SetsDependencyBatchesMode`, `Batch_ThrowingStream_RecordsExceptionAndErrorStatus`. Throwing fixture `ThrowingSleipnirController`. Collection definition: `SleipnirTracingCollectionDefinition`. |
| `SleipnirTests/Unit/Telemetry/SleipnirTelemetryExtensionsTests.cs` | `AddSleipnirTelemetry`: Console exporter subscribes the Sleipnir source; gates off (AspNetCore/HttpClient false) still subscribes; OTLP with endpoint builds the provider without throwing. Helpers `StartHostedServicesAsync`, `StopAndDisposeAsync`. |
| `SleipnirTests/Unit/Telemetry/SleipnirConnectionRegistryTests.cs` | `SleipnirConnectionRegistry` lock-free `Interlocked` counters (concurrent inc/dec, RecordCall success/failure, RecordBatch/EventDrop accumulation, GetSnapshot, StartedAtUtc) + `SleipnirMetrics` gauges `sleipnir.ws.connections`/`sleipnir.subscriptions.active` via `MeterListener` (`Gauges_Read_Current_Registry_Values`). |

---

## 13. Relationship to other docs

| Doc | Covers (tracing-relevant) |
|-----|----------------------------|
| `CLAUDE.md` | "Distributed Tracing" section — the architecture summary. **Two points were stale (now fixed — see §11)**: the "ExecuteSingleInvocation" third-site name (no such method) and the "AddSleipnir + AddSleipnirCore" two-factory claim (`AddSleipnirCore` deleted in 1.1.2). The rest (inline instrumentation, cost-neutrality, `RecordException` rationale, `SleipnirServer` not referencing `Sleipnir.Telemetry`, collection isolation) is accurate. |
| `README.md` | §"Features at a glance" — "OpenTelemetry distributed tracing" + "OpenTelemetry metrics — `Meter "Sleipnir"`" bullets; §"Packages" — `Sleipnir.Telemetry` matrix row; §"Server (NuGet)" — the optional `Sleipnir.Telemetry` `PackageReference`. |
| `README_DETAILS.md` | §"Features" — "Interceptor-Pipeline" + "Observability" bullets (the latter: metrics/endpoints). |
| `STABILITY.md` | §"1.3 Builder and options" — built-in interceptors incl. `SleipnirTelemetryInterceptor`; §"2. Experimental surface" — telemetry `sleipnir.*` instruments experimental, Prometheus `/metrics` as the durable contract. |
| `CHANGELOG.md` | §"[1.0.0] — Renamed from Trame" → "Telemetry" bullet (`Trame`→`Sleipnir` rename); §"[1.0.0]" migration steps (`AddSource("Sleipnir")`); §"Added — OpenTelemetry Metrics (Phase 1)"; "`AddSleipnirTelemetry` now subscribes the metrics column" entry; "R1 — fluent overload routed through canonical `AddTrame`" entry (`AddSleipnirCore` deletion). |
| `docs/design/phase-1-interceptor-pipeline.md` | §"Bestand (Fakten)" (`SleipnirTracing` at eight sites), §"Entscheidung 4 — Meter-Name `"Sleipnir"`" (shared with `ActivitySource`), §"Abgrenzung (was Phase 1 nicht macht)" (no change to the tracing-span model), §"Die drei Speziallocken werden zu Interceptors" (`SleipnirTelemetryInterceptor` consolidation intent). |
| `docs/audits/2026-08-08-consolidation-roadmap.md` | §"Status board" + §"R1 — Delete drifted `AddSleipnirCore`; route the fluent overload through the canonical `AddSleipnir`". |
| `SleipnirTelemetry/README.md` | Package README (the `PackageReadmeFile` — `SleipnirTelemetry.csproj` → `PackageReadmeFile` + `None Include="README.md"` items). |
| `OBSERVABILITY_REFERENCE.md` | **The companion doc** — metrics (`SleipnirMetrics`), the connection registry, the `/observability` + `/metrics` HTTP endpoints, and the Prometheus pull model. |

> **No dedicated `docs/**/tracing*.md` design file exists** beyond
> `docs/design/phase-1-interceptor-pipeline.md` (which covers tracing as one of
> its concerns). This reference consolidates the tracing side.