# Sleipnir.Hub

The SignalR transport and the server integration host for
[Sleipnir](../README.md) — the code-first, multi-transport RPC framework for .NET 8.

## What's in here

- **SignalR transport** — a `/sleipnirhub` hub that deserializes the incoming
  `SleipnirRequest` / `SleipnirMultiRequest` and delegates to `ISleipnirCore.InvokeDi()`.
  Wire format: WebSocket + MessagePack binary.
- **Server integration** — `AddSleipnir(SleipnirOptions)` / `UseSleipnir()`:
  registers `ISleipnirCore` as a singleton, registers the logging interceptor,
  auto-discovers every `[SleipnirController]` type across assemblies (skipping
  `AutoDiscover = false`) as scoped services, configures rate limiting, and
  optionally wires SignalR with MessagePack.
- **MessagePack formatters** — `JsonElement` / `JsonNode` / `SleipnirResponse`
  formatters compiled here against MessagePack 2.5.x (the version SignalR 8.0.28
  resolves; pinned to the patched 2.5.302 floor). The same source is shared with
  `Sleipnir.Client` (against MessagePack 3.x there).

## Install

```xml
<PackageReference Include="Sleipnir.Hub" Version="1.4.2" />
```

Targets `net8.0` (`Microsoft.NET.Sdk.Web`). Depends on `Sleipnir.Core` (→ `Sleipnir.Common`).

## Where it fits

Use `Sleipnir.Hub` when you want SignalR as a transport, or when you want the
`AddSleipnir` / `UseSleipnir` integration entry points. For REST or plain-WebSocket
transports see `Sleipnir.Rest` / `Sleipnir.WebSocket`; for everything in one package see
`Sleipnir.Server`. See the [root README](../README.md) and [PROTOCOL.md](../PROTOCOL.md).