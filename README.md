# Sleipnir

**A command-oriented API framework for .NET.**

> **Resources have REST. Operations have Sleipnir. Good applications can have both.**

Build strongly typed application APIs from ordinary C# methods — with generated C# and TypeScript clients, batching, server-side dependency chaining, multiple transports, runtime discovery and OpenTelemetry support.

**[Why Sleipnir?](docs/WHY_SLEIPNIR.md)** — Why command-oriented APIs exist, how Sleipnir complements REST, and which problems it is designed to solve.

**[Quick Start](#quick-start)** — Show me the code.

---

## Your code is the contract

A Sleipnir API starts with ordinary C# methods:

```csharp
[SleipnirController("Order")]
public class OrderController(IOrderService orders)
{
    [SleipnirMethod("CalculatePrice")]
    public Task<PriceResult> CalculatePrice(
        PriceRequest request,
        CancellationToken ct)
        => orders.CalculatePrice(request, ct);

    [SleipnirMethod("Approve")]
    public Task<Order> Approve(
        int orderId,
        CancellationToken ct)
        => orders.Approve(orderId, ct);
}
```

Sleipnir discovers that contract and can generate clients for **C# and TypeScript**.

The programming interface comes first.

The transport follows.

**Code generation should preserve intent, not rediscover it.**

---

## Where it gets interesting

Calling one remote method is easy.

Applications become harder when one operation depends on another.

Suppose a client needs to:

```text
Customer.Search("France")
        │
        │ customerIds
        ▼
Order.GetOpenByCustomerIds(...)
        │
        │ articleIds
        ▼
Article.GetAvailability(...)
```

The individual calls aren't the problem.

**The conversation between them is.**

Without server-side orchestration, the client has to:

```text
call A
wait
extract IDs

call B
wait
extract IDs

call C
wait
assemble result
```

With Sleipnir, the client can send the dependency graph in one request:

```csharp
var request = new SleipnirMultiRequest
{
    Requests =
    [
        SleipnirCall.Init("Customer", "Search")
            .Param("country", "France")
            .Exposes("$.items[*].id", "customerIds")
            .ToRequest(),

        SleipnirCall.Init("Order", "GetOpenByCustomerIds")
            .WithAlias("@customerIds")
            .Exposes("$.items[*].articleId", "articleIds")
            .ToRequest(),

        SleipnirCall.Init("Article", "GetAvailability")
            .WithAlias("@articleIds")
            .ToRequest()
    ]
};
```

Sleipnir resolves the graph on the server, executes commands in dependency order, parallelizes independent work and propagates failures to dependent operations.

**Fewer roundtrips. Less orchestration. Clearer intent.**

---

## Northbound and southbound

Sleipnir isn't limited to browser-to-server APIs.

The same command model works across application boundaries:

```text
                    generated TypeScript
                           │
                           ▼
Browser ─────────────► Application
                           │
                           │ generated C#
                           ▼
                     Business Service
                           │
                           │ generated C#
                           ▼
                     Backend Service
```

Use Sleipnir northbound to expose application operations to clients.

Use it southbound to call other services.

Use REST alongside it wherever resources are the natural abstraction.

**A network boundary shouldn't have to become a type-safety boundary.**

---

## One contract. Multiple transports.

The application contract doesn't depend on the transport.

Sleipnir supports:

| Transport     | Good fit                                              |
| ------------- | ----------------------------------------------------- |
| **REST**      | Stateless HTTP calls and infrastructure compatibility |
| **WebSocket** | Persistent, low-latency connections                   |
| **SignalR**   | Real-time .NET/browser applications                   |

Choose the transport that fits the connection without redesigning the application API.

---

## More than remote method calls

Sleipnir provides the infrastructure around command-oriented APIs:

* **Generated C# and TypeScript clients** — preserve the programming contract across network boundaries
* **Dependency chaining** — results from one operation become inputs to another
* **Batching** — execute multiple operations in one roundtrip
* **Parallel execution** — independent operations run concurrently
* **Code-first contracts** — C# methods define the API
* **Runtime discovery** — inspect the running contract
* **Multiple transports** — REST, WebSocket and SignalR
* **Events** — server-to-client communication
* **Developer UI** — explore and execute the API
* **OpenTelemetry** — tracing and metrics across command execution
* **Built-in observability backend** — opt-in [Heimdall](https://github.com/HolgerTheHuck/Heimdall) package: embedded dashboard + PromQL API, no collector required ([reference](HEIMDALL_REFERENCE.md))

---

## Quick Start

Install the templates:

```bash
dotnet new install Sleipnir.Templates
```

Create a server:

```bash
dotnet new sleipnir-server -n HelloSleipnir
cd HelloSleipnir

dotnet run --launch-profile https
```

Open the Developer UI:

```text
https://localhost:5001/Sleipnir
```

Browse the discovered API, execute commands, create batches and dependency chains, inspect results and generate client code.

---

## Installation

Already have an ASP.NET Core project? Add the packages directly.

**Server** — all transports + Developer UI:

```xml
<PackageReference Include="Sleipnir.Server" Version="1.4.3" />
```

**C# client:**

```xml
<PackageReference Include="Sleipnir.Client" Version="1.4.3" />
```

**TypeScript / JavaScript client:**

```bash
npm i sleipnir-client
```

**Telemetry with built-in dashboard** — optional, replaces the Prometheus scrape with an embedded Heimdall backend (dashboard + PromQL API under `/otel`, no collector):

```xml
<PackageReference Include="Sleipnir.Telemetry.Heimdall" Version="1.4.3" />
```

**Typed client generation:**

```bash
npm i -D sleipnir-codegen
```

> `Sleipnir.Server` is the all-in-one meta-package. Reference a single transport package directly (e.g. `Sleipnir.Rest`) to skip the rest — full package matrix in [`README_DETAILS.md`](README_DETAILS.md).

All `Sleipnir.*` packages ship to **nuget.org**: [`nuget.org/packages?q=Sleipnir`](https://www.nuget.org/packages?q=Sleipnir) — the TypeScript/JavaScript packages are on **npm**: [`npmjs.com/package/sleipnir-client`](https://www.npmjs.com/package/sleipnir-client).

Requires .NET 8.0+.

---

## Sleipnir and REST

This isn't an either/or decision.

A real application might expose:

```text
GET /api/orders/42
```

because an order is naturally a resource.

The same application might expose:

```text
Order.CalculatePrice(...)
Order.Validate(...)
Order.Approve(...)
```

because these express application operations.

Both can use the same application and domain services underneath:

```text
                    ┌──── REST API
                    │
Clients ────────────┤
                    │
                    └──── Sleipnir
                           │
                           ▼
                  Application Services
                           │
                           ▼
                         Domain
```

Use the abstraction that fits the interaction.

**[Read Why Sleipnir? →](docs/WHY_SLEIPNIR.md)**

---

## Documentation

### Start here

* [Why Sleipnir?](docs/WHY_SLEIPNIR.md)
* [Getting Started](GETTING_STARTED.md)
* [Guide](guide/README.md)
* [Samples](samples/)

### Core concepts

* [Dependency Binding](DEPENDENCY_BINDING.md)
* [LINQ Query Client](LINQ_QUERY.md)
* [Transport Reference](TRANSPORT_REFERENCE.md)
* [Architecture](ARCHITECTURE.md)

### Production

* [Security](SECURITY_GUIDE.md)
* [Observability](OBSERVABILITY_REFERENCE.md)
* [Best Practices](BEST_PRACTICES.md)
* [Stability](STABILITY.md)

### Reference

* [Full feature reference](README_DETAILS.md)
* [Wire protocol](PROTOCOL.md)
* [JSON-RPC 2.0 compatibility](JSONRPC_COMPAT.md)
* [Code generation onboarding](CODEGEN_ONBOARDING.md)
* [Roadmap](ROADMAP.md)

### Consolidated lookup references

Single-file references that put all knobs, parameters, failure modes, and diagnostics for one area in one place — use these when something does not work and you need to look it up fast.

| Reference | What it covers |
|----------|----------------|
| [`CODEGEN_REFERENCE.md`](CODEGEN_REFERENCE.md) | Build-time contract loop, `contract.sleipnir.json`, Path A (.NET `Sleipnir.Server.Codegen` + Roslyn `Sleipnir.Generator`) and Path B (`sleipnir-gen` Node CLI), all CLI parameters, emitter output shapes, drift gate |
| [`TRANSPORT_REFERENCE.md`](TRANSPORT_REFERENCE.md) | REST / WebSocket / SignalR / SSE-over-REST endpoints, wire formats, `SleipnirTransportRouter` auto/fallback, client backends, `--transport` capability semantics |
| [`EVENTS_REFERENCE.md`](EVENTS_REFERENCE.md) | `[SleipnirEvent]`, `IObservable<T>`, ephemeral/durable subscriptions, `EventFrame` wire (event/complete/error), backpressure strategies, cross-transport resume (`Last-Event-Id`) |
| [`DEPENDENCY_BINDING_REFERENCE.md`](DEPENDENCY_BINDING_REFERENCE.md) | `@alias` chaining, JsonPath extraction, Weak/Strict/Paranoid binding modes, casing regimes, provider-failure propagation |
| [`DISCOVERY_REFERENCE.md`](DISCOVERY_REFERENCE.md) | Runtime discovery, `DiscoveryInfo`/`TypeRef` schema, contract inference (Weg C), `[SleipnirDataContract]` override, `discoveryVersion` no-drift gate |
| [`TRACING_TELEMETRY_REFERENCE.md`](TRACING_TELEMETRY_REFERENCE.md) | `SleipnirTracing` ActivitySource, instrumentation sites, `Sleipnir.Telemetry` opt-in, OTel exporter wiring |
| [`OBSERVABILITY_REFERENCE.md`](OBSERVABILITY_REFERENCE.md) | `/observability` JSON + `/metrics` Prometheus two-surface model, `SleipnirConnectionRegistry`, double-bookkeeping, gauge semantics |
| [`HEIMDALL_REFERENCE.md`](HEIMDALL_REFERENCE.md) | `Sleipnir.Telemetry.Heimdall` backend, `SleipnirHeimdallOptions`, `/otel` surface (dashboard + PromQL), alert semantics, Heimdall/OTel version lockstep, backend choice vs `Sleipnir.Telemetry` |

---

## The short version

Sleipnir doesn't try to make everything a command.

And it doesn't try to replace REST.

It provides another abstraction for the parts of applications that are naturally about **doing things** — especially when those operations cross network boundaries or depend on each other.

> **Resources have REST. Operations have Sleipnir. Good applications can have both.**

Sleipnir is open source and licensed under the MIT License.
