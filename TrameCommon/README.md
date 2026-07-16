# Trame.Common

Shared foundation for [Trame](../README.md) — the code-first, multi-transport RPC
framework for .NET 8. Every other Trame package (core engine, transports, client,
DevUI) depends on this one. It has no transport and no engine: only the contract
surface that all of them share.

## What's in here

- **Wire models** — `TrameRequest`, `TrameResponse`, `TrameMultiRequest`,
  `TrameParameter`, `ExecutionMode`. These are the on-the-wire types for every
  transport (REST, WebSocket, SignalR) and for the client.
- **Attributes** — `[TrameController("name")]`, `[TrameMethod("name")]`,
  `[TrameAuthorise]`, `[TrameDataContract]`, `[TrameDocumentation]`,
  `[TrameExample]`. The C# classes decorated with these *are* the contract —
  there is no `.proto` and no IDL.
- **Results factory** — `TrameResults` (`Ok`, `NotFound`, `BadRequest`,
  `Error(code, message, details?)`) for returning business/domain errors as a
  `TrameResponse` with a controlled status code.
- **Exceptions** — `TrameException`, thrown by clients on non-2xx responses.

## Install

```xml
<PackageReference Include="Trame.Common" Version="1.0.0" />
```

Targets `net8.0` and `netstandard2.1`. Brings only `MessagePack.Annotations`
(the wire models carry MessagePack attributes; the formatter implementations live
in `Trame.Hub` / `Trame.Client`, compiled against their own MessagePack version).

## Where it fits

You rarely reference `Trame.Common` alone. Server apps pick a transport
(`Trame.Rest`, `Trame.WebSocket`, `Trame.Hub`) or the meta-package `Trame.Server`;
clients use `Trame.Client`. All of them bring this package transitively. See the
[root README](../README.md) and [PROTOCOL.md](../PROTOCOL.md) for the wire contract.