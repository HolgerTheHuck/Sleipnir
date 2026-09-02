# Sleipnir.Telemetry

Optional OpenTelemetry SDK bootstrap for [Sleipnir](../README.md) — the code-first,
multi-transport RPC framework for .NET 8. The engine (`Sleipnir.Core`) is instrumented
with an always-on `ActivitySource` named `"Sleipnir"` and is cost-neutral without a
listener. This package brings the **OpenTelemetry SDK** and wires that source to
exporters, so a Sleipnir server emits traces without the consumer hand-rolling the
OTel pipeline.

## What's in here

- Subscribes to the `Sleipnir` `ActivitySource` (the public name
  `SleipnirCore.Tracing.SleipnirTracing.ActivitySourceName`).
- OTLP + Console exporters.
- `AspNetCore` + `HttpClient` instrumentation, so incoming HTTP and outbound calls
  nest inside the Sleipnir spans.
- All OTel packages pinned to the same `1.18.0` lockstep.

## Install

```xml
<PackageReference Include="Sleipnir.Telemetry" Version="1.4.2" />
```

Targets `net8.0`. Depends on `Sleipnir.Core`. The instrumentation itself lives in the
engine; this package only brings the SDK and subscribes — it adds no Sleipnir behavior
of its own.

## Where it fits

Opt-in: a server without this package still produces the `Sleipnir` activity source, it
just has no listener attached. Consumers can also ignore this package and call
`AddOpenTelemetry().WithTracing(b => b.AddSource("Sleipnir"))` themselves. See the
[root README](../README.md).