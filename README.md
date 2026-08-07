# Trame

> **A code-first framework for building command-oriented Web APIs on .NET.**
>
> One contract. Multiple transports. **Server-side dependency chaining.**

[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![NuGet](https://img.shields.io/nuget/v/Trame.Server.svg)](https://www.nuget.org/packages/Trame.Server)

Trame is designed for **command-oriented Web APIs**. Unlike resource-oriented frameworks, Trame models **commands** as the primary abstraction and lets dependent commands exchange **typed JSON fragments** within a single request.

Your C# classes are the contract — no `.proto`, no IDL, no code generation. The same call runs over REST, WebSocket, or SignalR, consumable from any language. Runtime discovery generates the contract directly from your code and powers a built-in Developer UI.

> **The name.** *Trame* (French, /tʁam/) — the weft, the cross-threads that hold a fabric together. Trame weaves multiple transports and chained calls into one framework.

### Packages

| Package | NuGet | npm | What |
|---|---|---|---|
| **`Trame.Server`** | ✅ | — | All-in-one server meta-package (all transports + DevUI). |
| `Trame.Client` | ✅ | — | C# client (REST + WebSocket + SignalR, fluent builder, events). |
| `Trame.Telemetry` | ✅ | — | Optional OpenTelemetry SDK bootstrap. |
| **`trame-client`** | — | ✅ | TypeScript/JavaScript client (REST + WebSocket, isomorphic). |

> Pick `Trame.Server` for everything; reference a single transport package directly (e.g. `Trame.Rest`) to skip the rest. Full package matrix: [Installation](#installation).

---

## 🚀 Get started in 10 minutes

### 1. Install the templates

```bash
dotnet new install Trame.Templates
```

### 2. Create and run a server

```bash
dotnet new trame-server -n HelloTrame
cd HelloTrame
dotnet run --launch-profile https
```

Open `https://localhost:5001/Trame` — the built-in Developer UI lets you browse the API and make live calls.

### 3. Call it from anywhere

```bash
curl -k -X POST https://localhost:5001/api/trame/json \
  -H "Content-Type: application/json" \
  -d '{"controller":"Greeting","method":"Hello","params":[{"parameterName":"name","data":"Trame"}],"id":"1"}'
```

> Want a full server + Svelte SPA? Use `dotnet new trame-server-spa` instead.  
> Want a drop-in sample that uses Trame from NuGet (no repo clone needed)? See [`samples/HelloTrame`](samples/HelloTrame) — a minimal server with `<PackageReference Include="Trame.Server" Version="1.0.0" />`.

---

## Why Trame?

Business applications are built around commands: `CreateOrder`, `ApproveInvoice`, `CancelBooking`, `AssignRole`, `GenerateInvoice`.

Traditional RPC forces the client to orchestrate chains: call A → parse → extract → transform → call B → repeat. Trame moves that glue to the server and collapses the whole chain into **one roundtrip**.

```csharp
var batch = new TrameMultiRequest
{
    Mode = ExecutionMode.Serial,
    Requests =
    [
        TrameCall.Init("Customer", "Search")
            .With("France")
            .Exposes("$.items[*].id", "customerIds")
            .ToRequest(),

        TrameCall.Init("Order", "GetOpenByCustomerIds")
            .WithAlias("@customerIds")
            .ToRequest()
    ]
};
```

The second call receives the array of customer ids produced by the first — no client-side looping, no intermediate roundtrips.

Full reasoning and trade-offs: [`BEST_PRACTICES.md`](BEST_PRACTICES.md).

---

## Trame + REST — not a replacement, a complement

Trame doesn't replace your REST API. It **sits next to it** on the same host, sharing the
service layer. Your C# service stays the single source of truth; two thin facades expose it:

```csharp
// REST facade (minimal API) — for resources, binary, OpenAPI/Swagger, legacy clients
app.MapGet("/api/customers/{id}", async (int id, ICustomerService s, CancellationToken ct) =>
    Results.Ok(await s.GetById(id, ct)));

// Trame facade — for commands, batch, dependency chaining, multi-transport
[TrameController("Customer")]
public class CustomerController(ICustomerService service)
{
    [TrameMethod("GetById")] public Task<Customer?> GetById(int id, CancellationToken ct)
        => service.GetById(id, ct);
}
```

**Use each for what it does well:**

| Use REST for | Use Trame for |
|---|---|
| A single resource by id (`GET /api/orders/42`) | A screen with multiple dependent calls in one roundtrip |
| Cacheable GETs, proxy- and curl-friendly ops | Command fan-out with per-call isolation |
| Large binary uploads/downloads, streaming | A typed contract shared across REST/WebSocket/SignalR |
| Webhook receivers, OpenAPI/Swagger, legacy clients | `.NET`-to-`.NET` binary channel (SignalR+MessagePack) |

**OpenAPI/Swagger** comes from the REST side automatically (Swashbuckle/NSwag) — Trame
doesn't need its own OpenAPI emitter. **Legacy clients** that can't use a Trame client get a
plain ASP.NET controller over the same service — no Trame runtime surface, no second protocol.
Both coexist on one host, both above the same services where the bulk logic lives.

Details and migration paths: [`BEST_PRACTICES.md`](BEST_PRACTICES.md) §4 *Interplay with REST*.

---

## Code-first contract

```csharp
using TrameCore.Attributes;

[TrameController("Customer")]
public class CustomerController(ICustomerService service)
{
    [TrameMethod("Create")]
    public async Task<int> Create(string name, CancellationToken ct)
        => await service.Add(name, ct);

    [TrameMethod("GetById")]
    public async Task<Customer?> GetById(int id, CancellationToken ct)
        => await service.GetById(id, ct);
}
```

No separate schema. No code generation step for the contract itself. Runtime discovery exposes it at `GET /api/trame/discovery`.

---

## Three-line server wiring

```csharp
using TrameHub.Extensions;
using TrameServer;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddTrame(o => o.UseSignalR = true);

var app = builder.Build();
app.UseTrameTransports();
app.MapTrame();   // REST + WebSocket + SignalR hub + Developer UI
app.Run();
```

Step-by-step onboarding: [`GETTING_STARTED.md`](GETTING_STARTED.md).

---

## Multi-transport, same call

| Transport | Endpoint | Best for |
|-----------|----------|----------|
| REST | `POST /api/trame/json` | Stateless, load-balanced |
| WebSocket | `wss://host/tramews` | Persistent, low-latency |
| SignalR | `/tramehub` | Browser clients, auto-reconnect |

Clients:

- **C#** — `Trame.Client` NuGet
- **TypeScript/JavaScript** — `npm i trame-client`
- **Anything else** — the wire protocol is plain JSON: [`PROTOCOL.md`](PROTOCOL.md)

---

## Developer UI

A built-in web UI at **`/Trame`** turns runtime discovery into a working console:

- Browse controllers and types
- Open many calls at once (tabs persist across reloads)
- Build serial batches and `@alias` chains visually
- Generate TypeScript and C# client code
- History, workspace snapshots, and standalone builds

Details: [`README_DETAILS.md#developer-ui`](README_DETAILS.md#developer-ui)

---

## Features at a glance

- Command-oriented, code-first Web APIs
- Server-side dependency chaining (scalar, array, object, nested graph)
- Runtime discovery + Developer UI
- REST, WebSocket, SignalR — same contract
- Batch execution (parallel, serial, topological)
- Streaming with `IAsyncEnumerable<T>`
- **Server-push events** with `IObservable<T>` — `[TrameEvent]` + `SubscribeAsync` (1.1.0)
- Binary support
- **Policy-based authorization** — `[TrameAuthorise(Policy=…)]` via `IAuthorizationService`, 403 vs 401 (1.1.0)
- **Interceptor pipeline** — `ITrameInterceptor` + `TrameInvocationContext` (Auth/Telemetry/Logging built-in) (1.1.0)
- **Error taxonomy** — `TrameError.Category` (InvalidArgument/Unauthenticated/PermissionDenied/NotFound/...) (1.1.0)
- **OpenTelemetry metrics** — `Meter "Trame"` (`trame.call.duration/count`, `trame.event.dropped`, ...) (1.1.0)
- **Client test-doubles** — `TrameInMemoryClient` for unit tests without a server (1.1.0)
- Rate-limiting, expression-tree invocation (no reflection per call)
- OpenTelemetry distributed tracing
- JSON-RPC 2.0 compatibility mode

---

## Installation

### Packages

| Package | NuGet | npm | What |
|---|---|---|---|
| **`Trame.Server`** | ✅ `1.0.0` | — | All-in-one server meta-package (all transports + DevUI). Pulls Core/Hub/Rest/WebSocket/DeveloperUi transitively. |
| `Trame.Core` | ✅ `1.0.0` | — | Execution engine (invoker, discovery, dependency resolver). |
| `Trame.Hub` | ✅ `1.0.0` | — | SignalR transport + `AddTrame`/`UseTrame` host. |
| `Trame.Rest` | ✅ `1.0.0` | — | REST / JSON minimal-API transport. |
| `Trame.WebSocket` | ✅ `1.0.0` | — | RFC 6455 WebSocket transport. |
| `Trame.DeveloperUi` | ✅ (via Server) | — | Built-in Developer UI (served by host; included in `Trame.Server`). |
| `Trame.Telemetry` | ✅ `1.0.0` | — | Optional OpenTelemetry SDK bootstrap (OTLP/Console exporters). |
| `Trame.Client` | ✅ `1.0.0` | — | C# client (REST + WebSocket + SignalR, fluent builder). |
| **`trame-client`** | — | ✅ `trame-client` | TypeScript/JavaScript client (REST + WebSocket, isomorphic). |
| `trame-codegen` | — | ✅ (soon) | CLI: typed client stubs from discovery (`trame-gen --lang ts\|js\|cs\|py`). |
| `Trame.Generator` | ✅ (soon) | — | Roslyn source generator (typed C# client from `contract.trame.json`). |
| `Trame.Server.Codegen` | ✅ (soon) | — | Server-side contract export + drift-check (build-time). |

> **Pick `Trame.Server` when you want everything.** Reference a single transport package
> directly (e.g. `Trame.Rest` for REST-only, `Trame.WebSocket` for a non-.NET client) to skip
> the rest. See [`README_DETAILS.md`](README_DETAILS.md) *Project Structure*.

### Server (NuGet)

```xml
<!-- All transports + Developer UI -->
<PackageReference Include="Trame.Server" Version="1.0.0" />

<!-- Optional: OpenTelemetry bootstrap -->
<PackageReference Include="Trame.Telemetry" Version="1.0.0" />

<!-- C# client -->
<PackageReference Include="Trame.Client" Version="1.0.0" />
```

### Client (npm)

```bash
# TypeScript / JavaScript client
npm i trame-client
```

---

## Documentation

| Document | What you will find |
|----------|--------------------|
| [`GETTING_STARTED.md`](GETTING_STARTED.md) | Empty directory → running DevUI |
| [`README_DETAILS.md`](README_DETAILS.md) | Full feature reference |
| [`BEST_PRACTICES.md`](BEST_PRACTICES.md) | When to use Trame, batch vs. REST loop, design patterns |
| [`PROTOCOL.md`](PROTOCOL.md) | Wire format and casing contract |
| [`SECURITY_GUIDE.md`](SECURITY_GUIDE.md) | Auth, hardening, north-bound security |
| [`DEPENDENCY_BINDING.md`](DEPENDENCY_BINDING.md) | Alias resolution, failure propagation, binding modes |
| [`JSONRPC_COMPAT.md`](JSONRPC_COMPAT.md) | JSON-RPC 2.0 compatibility |
| [`CODEGEN_ONBOARDING.md`](CODEGEN_ONBOARDING.md) | Build-time contract and typed clients |
| [`ROADMAP.md`](ROADMAP.md) | What is planned |

---

## Requirements

- .NET 8.0+
- ASP.NET Core 8.0+

## License

MIT
