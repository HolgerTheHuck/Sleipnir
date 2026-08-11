# Sleipnir.Rest

The REST transport for [Sleipnir](../README.md) — the code-first, multi-transport RPC
framework for .NET 8. Canonical wire format: HTTP/1.1 + JSON, over ASP.NET Core
minimal APIs.

## Endpoints

| Endpoint | Method | Purpose |
|---|---|---|
| `/api/sleipnir/json` | POST | single `SleipnirRequest` |
| `/api/sleipnir/json/multi` | POST | batch `SleipnirMultiRequest` |
| `/api/sleipnir/discovery` | GET | `DiscoveryInfo` JSON (the standard contract) |
| `/api/sleipnir/jsonrpc` | POST | optional JSON-RPC 2.0 compat adapter |

The JSON-RPC 2.0 adapter is opt-in via `SleipnirOptions.EnableJsonRpcCompat`; it is a
pure bidirectional translator with an error-code map. For `@alias` chaining,
execution-mode selection, binary out-of-band, and streaming, graduate to the native
wire — see [JSONRPC_COMPAT.md](../JSONRPC_COMPAT.md).

## What's in here

- `MapSleipnirEndpoints()` — registers the minimal-API endpoints above.
- `JsonRpcAdapter` / `JsonRpcDispatcher` — the JSON-RPC compat layer (pure
  translator + orchestration that calls `InvokeDi` in Parallel and applies the
  `200` / `204` envelope).

## Install

```xml
<PackageReference Include="Sleipnir.Rest" Version="1.0.0" />
```

Targets `net8.0` (`Microsoft.NET.Sdk.Web`). Depends on `Sleipnir.Core` (→ `Sleipnir.Common`).

## Where it fits

Use `Sleipnir.Rest` for HTTP/JSON interop (the easiest surface for browsers, curl, and
non-.NET clients). For WebSocket or SignalR see `Sleipnir.WebSocket` / `Sleipnir.Hub`; for
all transports plus the DevUI in one package see `Sleipnir.Server`. See the
[root README](../README.md) and [PROTOCOL.md](../PROTOCOL.md).