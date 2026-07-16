# Trame.Telemetry

Optional OpenTelemetry SDK bootstrap for [Trame](../README.md) — the code-first,
multi-transport RPC framework for .NET 8. The engine (`Trame.Core`) is instrumented
with an always-on `ActivitySource` named `"Trame"` and is cost-neutral without a
listener. This package brings the **OpenTelemetry SDK** and wires that source to
exporters, so a Trame server emits traces without the consumer hand-rolling the
OTel pipeline.

## What's in here

- Subscribes to the `Trame` `ActivitySource` (the public name
  `TrameCore.Tracing.TrameTracing.ActivitySourceName`).
- OTLP + Console exporters.
- `AspNetCore` + `HttpClient` instrumentation, so incoming HTTP and outbound calls
  nest inside the Trame spans.
- All OTel packages pinned to the same `1.16.0` lockstep.

## Install

```xml
<PackageReference Include="Trame.Telemetry" Version="1.0.0" />
```

Targets `net8.0`. Depends on `Trame.Core`. The instrumentation itself lives in the
engine; this package only brings the SDK and subscribes — it adds no Trame behavior
of its own.

## Where it fits

Opt-in: a server without this package still produces the `Trame` activity source, it
just has no listener attached. Consumers can also ignore this package and call
`AddOpenTelemetry().WithTracing(b => b.AddSource("Trame"))` themselves. See the
[root README](../README.md).