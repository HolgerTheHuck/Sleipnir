# Sleipnir × Heimdall — User Reference

A consolidated lookup reference for the **built-in Heimdall telemetry backend**
(`Sleipnir.Telemetry.Heimdall`): what the package wires and what it deliberately
does not, the full `SleipnirHeimdallOptions` table, the `/otel` HTTP surface,
the alert-subsystem semantics (store registration, directories, evaluator
opt-in), the version-lockstep doctrine, security, a diagnostics catalog, and
how it is verified.

**Scope boundary:** the *engine* instrumentation (the always-on
`ActivitySource`/`Meter` named `"Sleipnir"` and the span model) is covered in
[`TRACING_TELEMETRY_REFERENCE.md`](TRACING_TELEMETRY_REFERENCE.md); the
`/api/sleipnir/observability` JSON snapshot in
[`OBSERVABILITY_REFERENCE.md`](OBSERVABILITY_REFERENCE.md). This doc covers the
**Heimdall backend package** — the turn-key alternative to
`Sleipnir.Telemetry`'s OTLP/Console + Prometheus-scrape producers.

This is a **reference**, not a tutorial. When a trace or metric does not appear
in the Heimdall dashboard, or the `/otel` surface misbehaves, look here first.

Code-facing text is English per `CLAUDE.md`. Citations are repo-relative file
paths anchored to durable symbols (no line numbers).

## Table of contents

1. [What this package is — and is not](#1-what-this-package-is--and-is-not)
2. [Wiring](#2-wiring)
3. [`SleipnirHeimdallOptions` — the full table](#3-sleipnirheimdalloptions--the-full-table)
4. [The `/otel` surface](#4-the-otel-surface)
5. [Alerts — stores, directories, evaluator](#5-alerts--stores-directories-evaluator)
6. [What lands in Heimdall (traces / metrics / logs)](#6-what-lands-in-heimdall-traces--metrics--logs)
7. [Version lockstep doctrine](#7-version-lockstep-doctrine)
8. [Security](#8-security)
9. [Diagnostics & troubleshooting catalog](#9-diagnostics--troubleshooting-catalog)
10. [How it is verified (the tests)](#10-how-it-is-verified-the-tests)
11. [Relationship to other docs](#11-relationship-to-other-docs)

---

## 1. What this package is — and is not

`SleipnirTelemetryHeimdall/SleipnirTelemetryHeimdall.csproj` (NuGet-ID
`Sleipnir.Telemetry.Heimdall`) is an **opt-in backend package**: it wires the
Sleipnir `ActivitySource` + `Meter` (and optionally `ILogger`) to an **embedded
Heimdall SQLite sink** ([Heimdall](https://github.com/HolgerTheHuck/Heimdall) —
Holger's embeddable .NET observability stack) and maps the Heimdall Blazor
dashboard + PromQL HTTP API under a shared prefix (default `/otel`).

It **is**:

- A no-infrastructure backend: no collector, no external database, no network
  hop — the OTel SDK exports in-process into a SQLite file.
- The Grafana-compatible PromQL surface: `GET {prefix}/api/v1/*` speaks the
  Prometheus HTTP API, so Grafana or any PromQL consumer can point at it.
- A **replacement** for `Sleipnir.Telemetry`'s Prometheus scrape producer —
  see §2.

It is **not**:

- A change to the engine. `SleipnirServer` and `SleipnirCore` do not reference
  this package or Heimdall; the instrumentation lives always-on and
  cost-neutral in `SleipnirCore` (`SleipnirCore/Tracing/`) and only the
  `ActivitySourceName`/`MeterName` are reachable from here — the same
  backend-agnostic doctrine as `Sleipnir.Telemetry`.
- The `/api/sleipnir/observability` JSON snapshot. That endpoint is independent
  (runtime connection/subscription counts, gated by
  `SleipnirOptions.EnableObservability`) and unaffected by this package.

---

## 2. Choosing a backend: `Sleipnir.Telemetry` vs `Sleipnir.Telemetry.Heimdall`

| | `Sleipnir.Telemetry` | `Sleipnir.Telemetry.Heimdall` |
|---|---|---|
| Storage | External (OTLP receiver, Console, Prometheus scrape) | Embedded SQLite file |
| Dashboard | None (bring your own — Grafana etc.) | Built-in Blazor dashboard at `/otel` |
| Prometheus surface | `GET /api/sleipnir/metrics` scrape | PromQL HTTP API at `{prefix}/api/v1/*` |
| Logs | via OTLP exporter | optional `ILogger` bridge into Heimdall |
| Web-bound | No | Yes (dashboard + endpoint routing) |

**Never wire both Prometheus surfaces.** If Heimdall is in use, do not also
call `Sleipnir.Telemetry`'s `AddSleipnirPrometheusMetrics()` /
`UseSleipnirPrometheusScrapingEndpoint()` — Heimdall's PromQL engine is the
single Prometheus surface (it also derives RED metrics from Sleipnir server
spans). `Sleipnir.Telemetry`'s OTLP/Console exporters remain a valid
*non-Prometheus* path and may coexist with Heimdall's exporter only if you wire
them yourself (§7).

---

## 3. `SleipnirHeimdallOptions` — the full table

All options are properties on `SleipnirTelemetryHeimdall/SleipnirHeimdallOptions.cs`
→ `public sealed class SleipnirHeimdallOptions`, set via the `configure` lambda
of `AddSleipnirHeimdallTelemetry(Action<SleipnirHeimdallOptions>?)`.

| Property | Default | Notes |
|---|---|---|
| `ServiceName` | `"Sleipnir"` | `service.name` on the OTel resource emitted to Heimdall. |
| `ServiceVersion` | `null` | `service.version` on the OTel resource. |
| `DataPath` | `"heimdall-otel.db"` | SQLite file path (→ `SQLiteTelemetryOptions.DataPath`). Alert files are derived relative to this path (§5). |
| `RetentionDays` | `7` | Retention window (→ `SQLiteTelemetryOptions.RetentionDays`). |
| `IncludeAspNetCore` | `true` | ASP.NET Core inbound HTTP instrumentation on traces. |
| `IncludeHttpClient` | `true` | HttpClient outbound instrumentation on traces. |
| `IncludeLogs` | `true` | Bridge `ILogger` → OTel → Heimdall (§6.3). |
| `EnableAlerting` | `false` | Start the periodic alert evaluator (rules → channels). The alert **stores and UI are always available**; this only gates evaluation. |
| `AlertingRulesDir` | derived | Directory for alert rule JSONs. Empty = `<DataPath-dir>/alerts/rules`. |
| `AlertingStateDir` | derived | Directory for the alert state store (`alertstate.json`). Empty = `<DataPath-dir>/alerts`. |

For full control (custom resource attributes, sampling, a different Heimdall
storage backend), skip this package and wire
`AddOpenTelemetry().WithTracing(b => b.AddSource("Sleipnir"))` plus Heimdall's
own `UseHeimdallExporter` directly — the source/meter name is the only
integration touchpoint, as with `Sleipnir.Telemetry`.

---

## 4. The `/otel` surface

`SleipnirTelemetryHeimdall/SleipnirHeimdallEndpointExtensions.cs` →
`MapSleipnirHeimdall(IEndpointRouteBuilder, string prefix = "/otel")` maps both
route groups under one prefix:

| Route | What |
|---|---|
| `{prefix}` | Heimdall Blazor dashboard (server-rendered SSR, no SignalR/JS requirement) |
| `{prefix}/api/v1/*` | Prometheus HTTP API (instant/range queries, buildinfo, discovery) — Grafana-compatible |
| `{prefix}/alerts` | Alert UI (rules + state, served from the file stores) |
| `/healthz` | Heimdall health endpoint (always anonymous) |

Call order matters: after `app.UseStaticFiles()` (dashboard assets) and
`app.UseRouting()` (endpoint routing). `MapSleipnirHeimdall` maps the
Prometheus API first and the dashboard second, additively.

The dashboard's static assets are served from `/_content/Heimdall.Blazor/*` via
the standard static-files middleware — a host without `UseStaticFiles()` gets a
dashboard with unstyled pages but working data.

---

## 5. Alerts — stores, directories, evaluator

The alert subsystem has two halves with **separate** gates:

- **Stores + UI (always on).** `AddSleipnirHeimdallTelemetry` registers
  `IAlertRuleStore`/`IAlertStateStore` (file-based) via Heimdall's
  `AddHeimdallAlerting`. Since Heimdall **1.3.1** an explicit registration is
  no longer *mandatory* (its `AddHeimdallDashboard` registers
  `TryAddSingleton` defaults), but we register explicitly because an explicit
  registration wins over `TryAdd` — this keeps the rule/state files
  **co-located with the SQLite database** (§3 derived directories) instead of
  Heimdall's working-directory defaults.
- **Evaluator (opt-in).** `EnableAlerting = true` starts the periodic
  `AlertEvaluator` HostedService (rules → channels). Off by default.

The alert evaluator runs outside any HTTP context and cannot read Heimdall's
per-request language cookie, so the notification language is pinned to
`"en"` (alert mails/webhooks are machine-readable anyway).

**History — why registration is explicit:** before Heimdall 1.3.1, the
dashboard mapped the `/alerts` endpoints unconditionally while their
`IAlertStateStore` parameter was only registered by `AddHeimdallAlerting`. An
unregistered store made ASP.NET fail endpoint inference ("Failure to infer one
or more parameters"), and because endpoint metadata is built lazily at the
first request, **one bad endpoint broke the host's entire routing table** —
every request 500ed. Heimdall 1.3.1 fixed this Heimdall-side (defaults +
fail-fast); the Sleipnir package keeps the explicit registration for the
directory/language control above, not as a workaround.

---

## 6. What lands in Heimdall (traces / metrics / logs)

### 6.1 Traces

`AddSource(SleipnirTracing.ActivitySourceName)` subscribes the always-on
`"Sleipnir"` source — the `SleipnirCall`/`SleipnirBatch` spans documented in
[`TRACING_TELEMETRY_REFERENCE.md`](TRACING_TELEMETRY_REFERENCE.md) §6 (tags:
`rpc.system=sleipnir`, `rpc.service`, `rpc.method`, batch mode/count, exception
tags on failures). Heimdall's RED metrics are derived from these server spans,
so call rates, durations and errors are visible without any explicit metric.

### 6.2 Metrics

`AddMeter(SleipnirMetrics.MeterName)` subscribes the `sleipnir.*` instruments
(call duration/count/error counters, batch fan-out, `sleipnir.ws.connections`
and `sleipnir.subscriptions.active` gauges — instrument table in
[`README_DETAILS.md`](README_DETAILS.md) §"Metrics & observability endpoints").
Query them from the dashboard or via PromQL
(`GET {prefix}/api/v1/query?query=sleipnir_call_count`).

### 6.3 Logs

`IncludeLogs` (default on) attaches the Heimdall log exporter to the OTel
logger provider and bridges `ILogger` into it — including Sleipnir's built-in
logging interceptor. If log records do not appear in the dashboard after
startup, additionally call on the host's `WebApplicationBuilder`:

```csharp
builder.Logging.AddOpenTelemetry(o =>
{
    o.IncludeFormattedMessage = true;
    o.IncludeScopes = true;
});
```

(`SleipnirHeimdallServiceExtensions.AddSleipnirHeimdallTelemetry` → the
`IncludeLogs` block; same note in the NuGet package README → "Logs note".)

---

## 7. Version lockstep doctrine

The package pins Heimdall `1.3.1` and OpenTelemetry `1.18.0`
(`SleipnirTelemetryHeimdall.csproj` → the `Heimdall.*` / `OpenTelemetry.*`
`PackageReference` group). The doctrine:

- **Heimdall pins OTel — Sleipnir follows.** `Heimdall.Sdk` pins an
  `OpenTelemetry` version; `Sleipnir.Telemetry` and
  `Sleipnir.Telemetry.Heimdall` reference the same version so the two packages
  never conflict. A Heimdall OTel bump must be mirrored in **both** Sleipnir
  telemetry packages in the same change.
- `Heimdall.Storage.SQLite` pulls `Microsoft.Data.Sqlite` transitively (10.0.11
  at the 1.3.1 pin) — expect its version to track Heimdall releases.
- Targets `net8.0`; Heimdall's `net8.0`/`net9.0`/`net10.0` assets are consumed
  at the net8.0 TFM.
- **Always against the NuGet package API, never against local Heimdall
  sources** — the source tree is typically ahead of the published package
  (e.g. `HeimdallAlertingOptions.Language` existed in source before it shipped).

---

## 8. Security

The `/otel` dashboard and `{prefix}/api/v1/*` are **unauthenticated by
default**. In production, either:

- chain `.RequireAuthorization(...)` on the `MapSleipnirHeimdall` result,
- gate the prefix with an authorization middleware, or
- front the host with a reverse proxy.

(`SleipnirHeimdallEndpointExtensions.MapSleipnirHeimdall` → `<remarks>`.) The
JSON observability snapshot at `/api/sleipnir/observability` is independent and
stays governed by its own `SleipnirOptions.EnableObservability` /
`RequireAuthentication` flags. Heimdall's own auth module
(`Heimdall:Auth:Enabled`, username/password for the UI, optional ApiKey for
ingest) can be enabled by wiring Heimdall's `AddHeimdallAuth` directly when
using this package through custom setup.

---

## 9. Diagnostics & troubleshooting catalog

| Symptom | Cause | Fix |
|---|---|---|
| Every request 500s with "Failure to infer one or more parameters … stateStore \| UNKNOWN" (Heimdall < 1.3.1) | The historical alert-store trap (§5) | Upgrade to Heimdall ≥ 1.3.1; keep the explicit `AddSleipnirHeimdallTelemetry` wiring |
| `InvalidOperationException` at startup naming `IAlertRuleStore`/`IAlertStateStore` | Heimdall ≥ 1.3.1 fail-fast: dashboard mapped without any store registration | Use `AddSleipnirHeimdallTelemetry` (registers them), or register the stores yourself before `MapSleipnirHeimdall` |
| Traces/calls missing in the dashboard | No listener on the `"Sleipnir"` source, or wrong prefix | Check `AddSleipnirHeimdallTelemetry` was called before `StartAsync`; dashboard queries the same sink instance the exporter writes to |
| Metrics present, but `sleipnir_*` queries empty | The Sleipnir Meter is subscribed but no Sleipnir call has happened yet, or the query name is wrong | Make a call first; instrument names are snake_case (`sleipnir_call_count`) |
| Logs missing in the dashboard | OTel logger provider not bridging the host's logging | §6.3 `builder.Logging.AddOpenTelemetry(...)` |
| Dashboard pages unstyled | `UseStaticFiles()` missing | Add it before routing (§4) |
| Prometheus scrape of `/api/sleipnir/metrics` and Heimdall both configured | Double Prometheus producers | Remove the `Sleipnir.Telemetry` scrape path; Heimdall is the single surface (§2) |

---

## 10. How it is verified (the tests)

`SleipnirTests/Integration/HeimdallTelemetryEndpointTests.cs` — three tests,
each building a real in-process Kestrel host with a unique temp SQLite path:

1. **`Heimdall_Prometheus_BuildInfoEndpoint_Responds`** — the PromQL engine and
   its endpoint mapping are live (`{prefix}/api/v1/status/buildinfo` →
   Prometheus success envelope).
2. **`Heimdall_PromQuery_ReturnsSuccessEnvelope`** — the Sleipnir Meter is
   subscribed to the Heimdall metric source (instant query returns a
   Prometheus `success` envelope; an empty result vector is still success).
3. **`Heimdall_Dashboard_RespondsUnderOtelPrefix`** — the dashboard root route
   renders (200, not 404).

The span→metric end-to-end assertion (make a Sleipnir call, then query
Heimdall for the exact span) is deliberately out of scope — the assertions
prove the wiring is live, not that a specific call produced a specific span.

---

## 11. Relationship to other docs

| Doc | Relationship |
|---|---|
| [`TRACING_TELEMETRY_REFERENCE.md`](TRACING_TELEMETRY_REFERENCE.md) | The span/meter model this package subscribes; `Sleipnir.Telemetry` (the alternative backend) in full detail. |
| [`OBSERVABILITY_REFERENCE.md`](OBSERVABILITY_REFERENCE.md) | The independent `/api/sleipnir/observability` + (legacy) `/api/sleipnir/metrics` surfaces. |
| `SleipnirTelemetryHeimdall/README.md` | The NuGet package README — condensed quick start; this doc is the consolidated reference. |
| [`CHANGELOG.md`](CHANGELOG.md) → Unreleased | Package introduction and the OTel 1.18.0 lockstep bump. |