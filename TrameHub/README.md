# Trame.Hub

The SignalR transport and the server integration host for
[Trame](../README.md) — the code-first, multi-transport RPC framework for .NET 8.

## What's in here

- **SignalR transport** — a `/tramehub` hub that deserializes the incoming
  `TrameRequest` / `TrameMultiRequest` and delegates to `ITrameCore.InvokeDi()`.
  Wire format: WebSocket + MessagePack binary.
- **Server integration** — `AddTrame(TrameOptions)` / `UseTrame()`:
  registers `ITrameCore` as a singleton, registers the logging interceptor,
  auto-discovers every `[TrameController]` type across assemblies (skipping
  `AutoDiscover = false`) as scoped services, configures rate limiting, and
  optionally wires SignalR with MessagePack.
- **MessagePack formatters** — `JsonElement` / `JsonNode` / `TrameResponse`
  formatters compiled here against MessagePack 2.5.x (the version SignalR 8.0.28
  resolves; pinned to the patched 2.5.302 floor). The same source is shared with
  `Trame.Client` (against MessagePack 3.x there).

## Install

```xml
<PackageReference Include="Trame.Hub" Version="1.0.0" />
```

Targets `net8.0` (`Microsoft.NET.Sdk.Web`). Depends on `Trame.Core` (→ `Trame.Common`).

## Where it fits

Use `Trame.Hub` when you want SignalR as a transport, or when you want the
`AddTrame` / `UseTrame` integration entry points. For REST or plain-WebSocket
transports see `Trame.Rest` / `Trame.WebSocket`; for everything in one package see
`Trame.Server`. See the [root README](../README.md) and [PROTOCOL.md](../PROTOCOL.md).