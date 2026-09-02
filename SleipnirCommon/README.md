# Sleipnir.Common

Shared foundation for [Sleipnir](../README.md) — the code-first, multi-transport RPC
framework for .NET 8. Every other Sleipnir package (core engine, transports, client,
DevUI) depends on this one. It has no transport and no engine: only the contract
surface that all of them share.

## What's in here

- **Wire models** — `SleipnirRequest`, `SleipnirResponse`, `SleipnirMultiRequest`,
  `SleipnirParameter`, `ExecutionMode`. These are the on-the-wire types for every
  transport (REST, WebSocket, SignalR) and for the client.
- **Attributes** — `[SleipnirController("name")]`, `[SleipnirMethod("name")]`,
  `[SleipnirAuthorise]`, `[SleipnirDataContract]`, `[SleipnirDocumentation]`,
  `[SleipnirExample]`. The C# classes decorated with these *are* the contract —
  there is no `.proto` and no IDL.
- **Results factory** — `SleipnirResults` (`Ok`, `NotFound`, `BadRequest`,
  `Error(code, message, details?)`) for returning business/domain errors as a
  `SleipnirResponse` with a controlled status code.
- **Exceptions** — `SleipnirException`, thrown by clients on non-2xx responses.

## Install

```xml
<PackageReference Include="Sleipnir.Common" Version="1.4.3" />
```

Targets `net8.0` and `netstandard2.1`. Brings only `MessagePack.Annotations`
(the wire models carry MessagePack attributes; the formatter implementations live
in `Sleipnir.Hub` / `Sleipnir.Client`, compiled against their own MessagePack version).

## Where it fits

You rarely reference `Sleipnir.Common` alone. Server apps pick a transport
(`Sleipnir.Rest`, `Sleipnir.WebSocket`, `Sleipnir.Hub`) or the meta-package `Sleipnir.Server`;
clients use `Sleipnir.Client`. All of them bring this package transitively. See the
[root README](../README.md) and [PROTOCOL.md](../PROTOCOL.md) for the wire contract.