# Trame

> **A code-first framework for building command-oriented Web APIs on .NET.**
>
> One contract. Multiple transports. **Server-side dependency chaining.**

[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

Trame is designed for **command-oriented Web APIs**. Unlike resource-oriented frameworks, Trame models **commands** as the primary abstraction and lets dependent commands exchange **typed JSON fragments** within a single request.

Your C# classes are the contract — no `.proto`, no IDL, no code generation. The same call runs over REST, WebSocket, or SignalR, consumable from any language. Runtime discovery generates the contract directly from your code and powers a built-in Developer UI.

> **The name.** *Trame* (French, /tʁam/) — the weft, the cross-threads that hold a fabric together. Trame weaves multiple transports and chained calls into one framework.

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
> Want a drop-in sample in this repo? See [`samples/HelloTrame`](samples/HelloTrame).

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
- Binary support
- Authorization, rate-limiting, interceptor pipeline
- Expression-tree invocation (no reflection per call)
- OpenTelemetry distributed tracing
- JSON-RPC 2.0 compatibility mode

---

## Installation

```xml
<!-- Server: all transports + Developer UI -->
<PackageReference Include="Trame.Server" Version="1.0.0" />

<!-- Optional: OpenTelemetry bootstrap -->
<PackageReference Include="Trame.Telemetry" Version="1.0.0" />

<!-- C# client -->
<PackageReference Include="Trame.Client" Version="1.0.0" />
```

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
