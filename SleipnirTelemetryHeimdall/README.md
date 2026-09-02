# Sleipnir.Telemetry.Heimdall

Optional **built-in Heimdall telemetry backend** for [Sleipnir](https://github.com/HolgerTheHuck/Sleipnir).
It turns [Heimdall](https://github.com/HolgerTheHuck/Heimdall) — an embeddable, OpenTelemetry-compatible
.NET observability stack — into a turn-key, in-process backend for Sleipnir's RPC engine.

Sleipnir already instruments every call with a BCL `ActivitySource`/`Meter` named `"Sleipnir"` and keeps
the OTel backend pluggable (the engine references only `System.Diagnostics`). This package subscribes that
source/meter to an **embedded Heimdall SQLite sink** and maps the Heimdall dashboard + PromQL engine, so a
Sleipnir host gets traces, metrics and (optionally) logs with no collector, no external database, and no
network hop. It is the turn-key alternative to `Sleipnir.Telemetry`'s OTLP/Console + Prometheus-scrape
producers — **Heimdall replaces the `/api/sleipnir/metrics` scrape** as the single Prometheus surface.

`SleipnirServer` and `SleipnirCore` do not reference this package or Heimdall; you opt in by referencing it
and calling one method — the same backend-agnostic doctrine as `Sleipnir.Telemetry`.

## Wiring

```csharp
using SleipnirTelemetryHeimdall;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSleipnir(options => { /* ... */ });
builder.Services.AddSleipnirHeimdallTelemetry(o =>
{
    o.ServiceName = "MyApi";
    o.DataPath = "heimdall-otel.db"; // SQLite file; default retention 7 days
    // o.IncludeLogs = false; // turn off the ILogger → Heimdall bridge
});

var app = builder.Build();
app.UseStaticFiles();              // serves the dashboard's static assets
app.UseRouting();
app.MapSleipnirEndpoints();        // Sleipnir's own REST/WS/SSE endpoints
app.MapSleipnirHeimdall("/otel");   // dashboard at /otel, PromQL API at /otel/api/v1/*
app.Run();
```

Do **not** also call `Sleipnir.Telemetry`'s `AddSleipnirPrometheusMetrics()` / `UseSleipnirPrometheusScrapingEndpoint()`
when using Heimdall — Heimdall's PromQL engine under `/otel/api/v1/*` is the single Prometheus surface (it also
derives RED metrics from Sleipnir server spans). The JSON snapshot at `/api/sleipnir/observability`
(gated by `SleipnirOptions.EnableObservability`) is independent and may stay enabled — it exposes runtime
connection/subscription counts, not OTel signals.

## Options (`SleipnirHeimdallOptions`)

| Property | Default | Notes |
|---|---|---|
| `ServiceName` | `"Sleipnir"` | `service.name` on the OTel resource. |
| `ServiceVersion` | `null` | `service.version` on the OTel resource. |
| `DataPath` | `"heimdall-otel.db"` | SQLite file path (→ `SQLiteTelemetryOptions.DataPath`). |
| `RetentionDays` | `7` | Retention window (→ `SQLiteTelemetryOptions.RetentionDays`). |
| `IncludeAspNetCore` | `true` | ASP.NET Core inbound HTTP instrumentation on traces. |
| `IncludeHttpClient` | `true` | HttpClient outbound instrumentation on traces. |
| `IncludeLogs` | `true` | Bridge `ILogger` → OTel → Heimdall (Sleipnir's logging interceptor included). |

For custom resource attributes, sampling, runtime instrumentation, or a different Heimdall storage backend,
skip this package and wire `AddOpenTelemetry().WithTracing(b => b.AddSource("Sleipnir"))` + Heimdall's
`UseHeimdallExporter` directly.

## Logs note

`IncludeLogs` attaches the Heimdall log exporter and bridges `ILogger` into the OTel logger provider from
the service collection. If log records do not appear in the dashboard after startup, additionally call on the
host's `WebApplicationBuilder`:

```csharp
builder.Logging.AddOpenTelemetry(o => { o.IncludeFormattedMessage = true; o.IncludeScopes = true; });
```

## Production security

The `/otel` dashboard and `/otel/api/v1/*` Prometheus API are **unauthenticated by default**. Protect them in
production — chain `.RequireAuthorization(...)` on the `MapSleipnirHeimdall` result, gate the prefix with an
authorization middleware, or front the host with a reverse proxy. (An integrated `ISleipnirCore.RequireAuthentication`
gate, mirroring `Sleipnir.Telemetry`'s scrape auth, is a planned follow-up.)

## Versions

Heimdall `1.3.0` and OpenTelemetry `1.18.0` (the same lockstep as `Sleipnir.Telemetry`, so the two never conflict).
Targets `net8.0`; Heimdall's `net8.0`/`net9.0`/`net10.0` assets are consumed at the net8.0 TFM.