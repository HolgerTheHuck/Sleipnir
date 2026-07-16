# Trame.Server

The one-stop server meta-package for [Trame](../README.md) — the code-first,
multi-transport RPC framework for .NET 8. It aggregates every transport and the
Developer UI behind a single `PackageReference`, so an ASP.NET Core app gets
`AddTrame` + `UseTrameTransports` + `MapTrame` without referencing each transport
individually.

## What it brings (transitively)

- `Trame.Core` — the execution engine (`ITrameCore`).
- `Trame.Hub` — SignalR transport + the `AddTrame` / `UseTrame` integration host.
- `Trame.Rest` — REST / JSON minimal-API transport (`MapTrameEndpoints`).
- `Trame.WebSocket` — RFC 6455 WebSocket transport.
- `Trame.DeveloperUi` — the in-browser Developer UI served by the host.

## Install

```xml
<PackageReference Include="Trame.Server" Version="1.0.0" />
```

Targets `net8.0`. Because it pulls the `Microsoft.NET.Sdk.Web` transports, the
consumer is an ASP.NET Core app.

## Where it fits

Pick `Trame.Server` when you want everything. If you only need one transport (e.g.
REST-only, or WebSocket-only for a non-.NET client), reference that transport
package directly instead and skip the rest. See the [root README](../README.md) and
[GETTING_STARTED.md](../GETTING_STARTED.md).