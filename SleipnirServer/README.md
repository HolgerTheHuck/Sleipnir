# Sleipnir.Server

The one-stop server meta-package for [Sleipnir](../README.md) — the code-first,
multi-transport RPC framework for .NET 8. It aggregates every transport and the
Developer UI behind a single `PackageReference`, so an ASP.NET Core app gets
`AddSleipnir` + `UseSleipnirTransports` + `MapSleipnir` without referencing each transport
individually.

## What it brings (transitively)

- `Sleipnir.Core` — the execution engine (`ISleipnirCore`).
- `Sleipnir.Hub` — SignalR transport + the `AddSleipnir` / `UseSleipnir` integration host.
- `Sleipnir.Rest` — REST / JSON minimal-API transport (`MapSleipnirEndpoints`).
- `Sleipnir.WebSocket` — RFC 6455 WebSocket transport.
- `Sleipnir.DeveloperUi` — the in-browser Developer UI served by the host.

## Install

```xml
<PackageReference Include="Sleipnir.Server" Version="1.4.3" />
```

Targets `net8.0`. Because it pulls the `Microsoft.NET.Sdk.Web` transports, the
consumer is an ASP.NET Core app.

## Where it fits

Pick `Sleipnir.Server` when you want everything. If you only need one transport (e.g.
REST-only, or WebSocket-only for a non-.NET client), reference that transport
package directly instead and skip the rest. See the [root README](../README.md) and
[GETTING_STARTED.md](../GETTING_STARTED.md).