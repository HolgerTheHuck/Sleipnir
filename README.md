# Sleipnir

> **A code-first framework for building command-oriented Web APIs on .NET.**
>
> One contract. Multiple transports. **Server-side dependency chaining.**

[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![NuGet](https://img.shields.io/nuget/v/Sleipnir.Server.svg)](https://www.nuget.org/packages/Sleipnir.Server)

> **⚠ Renamed from Trame.** This project was renamed to avoid a collision with Kitware's Python framework `trame`. The old `Trame.*` NuGet packages are deprecated → use the matching `Sleipnir.*` packages. See [CHANGELOG — 1.0.0](CHANGELOG.md#100---2026-08-11--renamed-from-trame) for the full migration notes (package IDs, namespaces, routes, telemetry, env vars).

Sleipnir is designed for **command-oriented Web APIs**. Unlike resource-oriented frameworks, Sleipnir models **commands** as the primary abstraction and lets dependent commands exchange **typed JSON fragments** within a single request.

Your C# classes are the contract — no `.proto`, no IDL, no code generation. The same call runs over REST, WebSocket, or SignalR, consumable from any language. Runtime discovery generates the contract directly from your code and powers a built-in Developer UI.

> **The name.** *Sleipnir* — Odin's eight-legged horse in Norse mythology, who carries the god across all nine realms in a single stride. A multi-transport metaphor: one framework bearing commands across REST, WebSocket, and SignalR. (Named to match the sibling projects Walhalla and Heimdall.)

---

## ⚡ The headline feature: server-side dependency chaining

Business applications are built around commands: `CreateOrder`, `ApproveInvoice`, `CancelBooking`. Traditional RPC forces the client to orchestrate chains: call A → parse → extract → call B → repeat. Sleipnir moves that glue to the server and collapses the whole chain into **one roundtrip** — a provider exposes a typed fragment of its result, a consumer references it as an `@alias` placeholder, and the server resolves the dependency graph before anything runs:

```csharp
var batch = new SleipnirMultiRequest
{
    Requests =
    [
        SleipnirCall.Init("Customer", "Search")
            .With("France")
            .Exposes("$.items[*].id", "customerIds")   // expose the result fragment
            .ToRequest(),

        SleipnirCall.Init("Order", "GetOpenByCustomerIds")
            .WithAlias("@customerIds")                 // consume it — server-resolved
            .ToRequest()
    ]
};
```

The second call receives the array of customer ids produced by the first — no client-side looping, no intermediate roundtrips. The engine topologically sorts the batch, runs independent commands in parallel, propagates failures to dependents instead of executing them with missing data, and gates every fragment transfer with type-binding checks (Weak/Strict/Paranoid).

```mermaid
flowchart LR
    A["Customer.Search<br/>(France)"] -- "exposes $.items[*].id<br/>→ @customerIds" --> B["Order.GetOpenByCustomerIds<br/>(@customerIds)"]
    C["Audit.Log<br/>(independent)"] --> P["one roundtrip"]
    A --> P
    B --> P
```

- **Why it matters:** N dependent calls become 1 roundtrip — the same problem GraphQL solves, with plain RPC semantics and no query language to learn.
- **Type-safe variant:** `Sleipnir.Client.Linq` wires `Dep<T>` → `Arg<T>` at compile time (below).
- **Full spec:** [`DEPENDENCY_BINDING.md`](DEPENDENCY_BINDING.md) — extraction, binding modes, failure propagation.

---

### Packages

| Package | NuGet | npm | What |
|---|---|---|---|
| **`Sleipnir.Server`** | ✅ | — | All-in-one server meta-package (all transports + DevUI). |
| `Sleipnir.Client` | ✅ | — | C# client (REST + WebSocket + SignalR, fluent builder, events). |
| `Sleipnir.Telemetry` | ✅ | — | Optional OpenTelemetry SDK bootstrap. |
| **`sleipnir-client`** | — | ✅ | TypeScript/JavaScript client (REST + WebSocket, isomorphic). |

> Pick `Sleipnir.Server` for everything; reference a single transport package directly (e.g. `Sleipnir.Rest`) to skip the rest. Full package matrix: [Installation](#installation).

---

## 🚀 Get started in 10 minutes

### 1. Install the templates

```bash
dotnet new install Sleipnir.Templates
```

### 2. Create and run a server

```bash
dotnet new sleipnir-server -n HelloSleipnir
cd HelloSleipnir
dotnet run --launch-profile https
```

Open `https://localhost:5001/Sleipnir` — the built-in Developer UI lets you browse the API and make live calls.

### 3. Call it from anywhere

```bash
curl -k -X POST https://localhost:5001/api/sleipnir/json \
  -H "Content-Type: application/json" \
  -d '{"controller":"Greeting","method":"Hello","params":[{"parameterName":"name","data":"Sleipnir"}],"id":"1"}'
```

> Want a full server + Svelte SPA? Use `dotnet new sleipnir-server-spa` instead.  
> Want a drop-in sample that uses Sleipnir from NuGet (no repo clone needed)? See [`samples/HelloSleipnir`](samples/HelloSleipnir) — a minimal server with `<PackageReference Include="Sleipnir.Server" Version="1.1.0" />`.

> **New to the framework?** The [`guide/`](guide/) folder is a progressive, runnable
> **10-chapter tutorial** — one growing 3-tier app (Sleipnir API + Blazor Pflege-Backend +
> Svelte Endkunden-Portal) from your first controller through batching, `@alias` chaining,
> JWT auth, and a live `[SleipnirEvent]` price feed with cross-transport resume. Clone the
> repo and follow [`guide/README.md`](guide/README.md) → "Start at chapter 1"; every
> chapter has a 3-command "Try it" that runs the step you just read. Theme:
> *"Sleipnir & REST — best friends"*.

---

## Why Sleipnir?

Business applications are built around commands: `CreateOrder`, `ApproveInvoice`, `CancelBooking`, `AssignRole`, `GenerateInvoice`. Sleipnir models them as first-class, batchable, chainable operations — see [the headline feature above](#-the-headline-feature-server-side-dependency-chaining).

Full reasoning and trade-offs: [`BEST_PRACTICES.md`](BEST_PRACTICES.md).

---

## Typed LINQ client (`Sleipnir.Client.Linq`)

The `@alias` chain above is powerful but stringly typed — a mistyped JsonPath or a placeholder wired into the wrong parameter compiles fine and fails at runtime. `Sleipnir.Client.Linq` closes that gap at **compile time**, over **generated service-contract interfaces**.

**Tier 1 — typed `@alias` wiring.** A `Dep<T>` only fits the `Arg<T>` it was built from; the JsonPath is built from a selector lambda, so it cannot be mistyped:

```csharp
using Sleipnir.Client.Linq;
using Sleipnir.Linq.Contracts; // generated IOrderService, Order, …

var linq = new SleipnirLinqClient(restClient);

var create = linq.Build((IOrderService c) => c.Create(new CreateOrderDto { CustomerId = 7 }));
Dep<int> orderId = create.Expose();                 // "$" → Dep<int>

var fetch  = linq.Build((IOrderService c) => c.GetById(orderId));   // Arg<int> accepts Dep<int>
Dep<string> status = fetch.Expose(o => o!.Status);  // "$.status" → Dep<string>

var responses = await linq.SendAsync(new SleipnirBatch(create, fetch));
var order = linq.ResultOf<Order>(fetch, responses);
```

**Tier 2 — eager-load a navigation graph.** An EF-Core-shaped façade over the *same* native wire — `.Include`/`.ThenInclude` declare edges that are compile-checked against the contract, then compiled into an ordinary `@alias`/`dependencyMapping` multi-request (the server sees no query):

```csharp
var query = linq.From((ICustomerService c) => c.SelectCustomers())
    .Include(c => c.Kontakt)            // reference nav, checked against Customer
        .ThenInclude(k => k.Ansprechpartner)   // checked against the Kontakt leaf
    .Include(c => c.Bestellungen);      // sibling collection nav

List<Customer> customers = linq.Materialize(query, await linq.SendAsync(query.Build()));
```

The `[SleipnirNavigation]` edges the façade navigates are **generated from your server DTOs through discovery** — annotate the server property once, and the `sleipnir-linq` codegen resolves, drift-checks, and re-emits the matching client attribute. No hand-annotation, no drift.

Details: [`LINQ_QUERY.md`](LINQ_QUERY.md).

---

## Sleipnir + REST — not a replacement, a complement

Sleipnir doesn't replace your REST API. It **sits next to it** on the same host, sharing the
service layer. Your C# service stays the single source of truth; two thin facades expose it:

```csharp
// REST facade (minimal API) — for resources, binary, OpenAPI/Swagger, legacy clients
app.MapGet("/api/customers/{id}", async (int id, ICustomerService s, CancellationToken ct) =>
    Results.Ok(await s.GetById(id, ct)));

// Sleipnir facade — for commands, batch, dependency chaining, multi-transport
[SleipnirController("Customer")]
public class CustomerController(ICustomerService service)
{
    [SleipnirMethod("GetById")] public Task<Customer?> GetById(int id, CancellationToken ct)
        => service.GetById(id, ct);
}
```

**Use each for what it does well:**

| Use REST for | Use Sleipnir for |
|---|---|
| A single resource by id (`GET /api/orders/42`) | A screen with multiple dependent calls in one roundtrip |
| Cacheable GETs, proxy- and curl-friendly ops | Command fan-out with per-call isolation |
| Large binary uploads/downloads, streaming | A typed contract shared across REST/WebSocket/SignalR |
| Webhook receivers, OpenAPI/Swagger, legacy clients | `.NET`-to-`.NET` binary channel (SignalR+MessagePack) |

**OpenAPI/Swagger** comes from the REST side automatically (Swashbuckle/NSwag) — Sleipnir
doesn't need its own OpenAPI emitter. **Legacy clients** that can't use a Sleipnir client get a
plain ASP.NET controller over the same service — no Sleipnir runtime surface, no second protocol.
Both coexist on one host, both above the same services where the bulk logic lives.

Details and migration paths: [`BEST_PRACTICES.md`](BEST_PRACTICES.md) §4 *Interplay with REST*.

**Serving media** (images, video, file downloads) follows the same split: Sleipnir is the *authority* —
a command returns the resource URL and gates permission — and a co-hosted `GET /avatars/{id}.png`
endpoint (same host, same DI, same auth pipeline) is the *delivery*, with a CDN/static store in front.
Sleipnir deliberately does **not** put raw bytes or `GET` media routes in the RPC contract — see
[Serving Media & Non-RPC Resources](README_DETAILS.md#serving-media--non-rpc-resources-images-video-downloads).

---

## Code-first contract

```csharp
using SleipnirCore.Attributes;

[SleipnirController("Customer")]
public class CustomerController(ICustomerService service)
{
    [SleipnirMethod("Create")]
    public async Task<int> Create(string name, CancellationToken ct)
        => await service.Add(name, ct);

    [SleipnirMethod("GetById")]
    public async Task<Customer?> GetById(int id, CancellationToken ct)
        => await service.GetById(id, ct);
}
```

No separate schema. No code generation step for the contract itself. Runtime discovery exposes it at `GET /api/sleipnir/discovery`.

---

## Three-line server wiring

```csharp
using SleipnirHub.Extensions;
using SleipnirServer;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSleipnir(o => o.UseSignalR = true);

var app = builder.Build();
app.UseSleipnirTransports();
app.MapSleipnir();   // REST + WebSocket + SignalR hub + Developer UI
app.Run();
```

Step-by-step onboarding: [`GETTING_STARTED.md`](GETTING_STARTED.md).

---

## Multi-transport, same call

| Transport | Endpoint | Best for |
|-----------|----------|----------|
| REST | `POST /api/sleipnir/json` | Stateless, load-balanced |
| WebSocket | `wss://host/sleipnirws` | Persistent, low-latency |
| SignalR | `/sleipnirhub` | Browser clients, auto-reconnect |

Clients:

- **C#** — `Sleipnir.Client` NuGet
- **TypeScript/JavaScript** — `npm i sleipnir-client`
- **Anything else** — the wire protocol is plain JSON: [`PROTOCOL.md`](PROTOCOL.md)

---

## Developer UI

A built-in web UI at **`/Sleipnir`** turns runtime discovery into a working console:

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
- **Typed LINQ client** — `Sleipnir.Client.Linq`: `Dep<T>`/`Arg<T>` compile-time type-safe `@alias`
  wiring, and `SleipnirQuery<T>.Include(...).ThenInclude(...)` eager-load navigation over a known
  return — the `[SleipnirNavigation]` edges are generated from the server DTOs through discovery
  (no hand-annotation)
- Runtime discovery + Developer UI
- REST, WebSocket, SignalR — same contract
- Batch execution (parallel, serial, topological)
- Streaming with `IAsyncEnumerable<T>`
- **Server-push events** with `IObservable<T>` — `[SleipnirEvent]` + `SubscribeAsync` (1.1.0)
- Binary support
- **Policy-based authorization** — `[SleipnirAuthorise(Policy=…)]` via `IAuthorizationService`, 403 vs 401 (1.1.0)
- **Interceptor pipeline** — `ISleipnirInterceptor` + `SleipnirInvocationContext` (Auth/Telemetry/Logging built-in) (1.1.0)
- **Error taxonomy** — `SleipnirError.Category` (InvalidArgument/Unauthenticated/PermissionDenied/NotFound/...) (1.1.0)
- **OpenTelemetry metrics** — `Meter "Sleipnir"` (`sleipnir.call.duration/count`, `sleipnir.event.dropped`, ...) (1.1.0)
- **Client test-doubles** — `SleipnirInMemoryClient` for unit tests without a server (1.1.0)
- Rate-limiting, expression-tree invocation (no reflection per call)
- OpenTelemetry distributed tracing
- JSON-RPC 2.0 compatibility mode

## Server-push events

Alongside request/response calls, Sleipnir pushes server→client events over WebSocket. Mark a
method with `[SleipnirEvent]` (instead of `[SleipnirMethod]`) and return `IObservable<T>`;
clients subscribe and receive typed values until they unsubscribe or the source completes.

```csharp
[SleipnirController("Chat")]
public class ChatController(IChatService service)
{
    [SleipnirEvent("MessageReceived")]            // server-push event
    public IObservable<Message> MessageReceived(int chatId, CancellationToken ct)
        => service.SubscribeMessages(chatId, ct);
}
```

```csharp
// C# WebSocket client
using var sub = await client.SubscribeAsync<Message>("Chat", "MessageReceived", new object?[] { 42 });
sub.Subscribe(onNext: m => Console.WriteLine($"{m.From}: {m.Text}"));
```

WebSocket-only in v1; events are not chainable (`@alias`/`exposes` apply to call results, not
push streams). Full guide + lifecycle/backpressure: [`README_DETAILS.md`](README_DETAILS.md) →
"Server-Push Events"; wire spec: [`PROTOCOL.md`](PROTOCOL.md) → "Server-Push Events".

---

## Installation

### Packages

| Package | NuGet | npm | What |
|---|---|---|---|
| **`Sleipnir.Server`** | ✅ `1.1.0` | — | All-in-one server meta-package (all transports + DevUI). Pulls Core/Hub/Rest/WebSocket/DeveloperUi transitively. |
| `Sleipnir.Core` | ✅ `1.1.0` | — | Execution engine (invoker, discovery, dependency resolver). |
| `Sleipnir.Hub` | ✅ `1.1.0` | — | SignalR transport + `AddSleipnir`/`UseSleipnir` host. |
| `Sleipnir.Rest` | ✅ `1.1.0` | — | REST / JSON minimal-API transport. |
| `Sleipnir.WebSocket` | ✅ `1.1.0` | — | RFC 6455 WebSocket transport. |
| `Sleipnir.DeveloperUi` | ✅ (via Server) | — | Built-in Developer UI (served by host; included in `Sleipnir.Server`). |
| `Sleipnir.Telemetry` | ✅ `1.1.0` | — | Optional OpenTelemetry SDK bootstrap (OTLP/Console exporters). |
| `Sleipnir.Client` | ✅ `1.1.0` | — | C# client (REST + WebSocket + SignalR, fluent builder). |
| `Sleipnir.Client.Linq` | ✅ `1.1.0` | — | Typed LINQ client: `Dep<T>`/`Arg<T>` type-safe `@alias` wiring + `SleipnirQuery<T>` `.Include`/`.ThenInclude` eager-load façade. |
| **`sleipnir-client`** | — | ✅ `1.1.1` | TypeScript/JavaScript client (REST + WebSocket, isomorphic). |
| `sleipnir-codegen` | — | ✅ `1.2.0` | CLI: typed client stubs from discovery (`sleipnir-gen --lang ts\|js\|cs\|py --transport rest\|ws\|both`). |
| `Sleipnir.Generator` | ✅ `1.1.1` | — | Roslyn source generator (typed C# client from `contract.sleipnir.json`). |
| `Sleipnir.Server.Codegen` | ✅ `1.1.1` | — | Server-side contract export + drift-check (build-time). |

> **Pick `Sleipnir.Server` when you want everything.** Reference a single transport package
> directly (e.g. `Sleipnir.Rest` for REST-only, `Sleipnir.WebSocket` for a non-.NET client) to skip
> the rest. See [`README_DETAILS.md`](README_DETAILS.md) *Project Structure*.

### Server (NuGet)

```xml
<!-- All transports + Developer UI -->
<PackageReference Include="Sleipnir.Server" Version="1.1.0" />

<!-- Optional: OpenTelemetry bootstrap -->
<PackageReference Include="Sleipnir.Telemetry" Version="1.1.0" />

<!-- C# client -->
<PackageReference Include="Sleipnir.Client" Version="1.1.0" />
```

### Client (npm)

```bash
# TypeScript / JavaScript client
npm i sleipnir-client
```

---

## Documentation

| Document | What you will find |
|----------|--------------------|
| [`GETTING_STARTED.md`](GETTING_STARTED.md) | Empty directory → running DevUI |
| [`guide/`](guide/README.md) | **Progressive runnable tutorial** — 10 chapters, one growing 3-tier app (API + Blazor admin + Svelte portal): onboarding → codegen → batching → chaining → auth → live events. Start at [`guide/README.md`](guide/README.md). |
| [`README_DETAILS.md`](README_DETAILS.md) | Full feature reference |
| [`BEST_PRACTICES.md`](BEST_PRACTICES.md) | When to use Sleipnir, batch vs. REST loop, design patterns |
| [`PROTOCOL.md`](PROTOCOL.md) | Wire format and casing contract |
| [`SECURITY_GUIDE.md`](SECURITY_GUIDE.md) | Auth, hardening, north-bound security |
| [`DEPENDENCY_BINDING.md`](DEPENDENCY_BINDING.md) | Alias resolution, failure propagation, binding modes |
| [`JSONRPC_COMPAT.md`](JSONRPC_COMPAT.md) | JSON-RPC 2.0 compatibility |
| [`CODEGEN_ONBOARDING.md`](CODEGEN_ONBOARDING.md) | Build-time contract and typed clients |
| [`LINQ_QUERY.md`](LINQ_QUERY.md) | `Sleipnir.Client.Linq` — typed `Dep<T>` wiring + `SleipnirQuery<T>` navigation façade |
| [`ROADMAP.md`](ROADMAP.md) | What is planned |

### Consolidated lookup references

Single-file references that put **all knobs, parameters, failure modes, and diagnostics for one area in one place** — use these when something does not work and you need to look it up fast (the docs above are tutorial/overview-shaped; these are lookup-shaped). Each links out to the deeper specs.

| Reference | What it covers |
|----------|----------------|
| [`CODEGEN_REFERENCE.md`](CODEGEN_REFERENCE.md) | Build-time contract loop, `contract.sleipnir.json`, Path A (.NET `Sleipnir.Server.Codegen` + Roslyn `Sleipnir.Generator`) and Path B (`sleipnir-gen` Node CLI), all CLI parameters, emitter output shapes, drift gate |
| [`TRANSPORT_REFERENCE.md`](TRANSPORT_REFERENCE.md) | REST / WebSocket / SignalR / SSE-over-REST endpoints, wire formats, `SleipnirTransportRouter` auto/fallback, client backends, `--transport` capability semantics |
| [`EVENTS_REFERENCE.md`](EVENTS_REFERENCE.md) | `[SleipnirEvent]`, `IObservable<T>`, ephemeral/durable subscriptions, `EventFrame` wire (event/complete/error), backpressure strategies, cross-transport resume (`Last-Event-Id`) |
| [`DEPENDENCY_BINDING_REFERENCE.md`](DEPENDENCY_BINDING_REFERENCE.md) | `@alias` chaining, JsonPath extraction, Weak/Strict/Paranoid binding modes, casing regimes, provider-failure propagation |
| [`DISCOVERY_REFERENCE.md`](DISCOVERY_REFERENCE.md) | Runtime discovery, `DiscoveryInfo`/`TypeRef` schema, contract inference (Weg C), `[SleipnirDataContract]` override, `discoveryVersion` no-drift gate |
| [`TRACING_TELEMETRY_REFERENCE.md`](TRACING_TELEMETRY_REFERENCE.md) | `SleipnirTracing` ActivitySource, instrumentation sites, `Sleipnir.Telemetry` opt-in, OTel exporter wiring |
| [`OBSERVABILITY_REFERENCE.md`](OBSERVABILITY_REFERENCE.md) | `/observability` JSON + `/metrics` Prometheus two-surface model, `SleipnirConnectionRegistry`, double-bookkeeping, gauge semantics |

---

## Requirements

- .NET 8.0+
- ASP.NET Core 8.0+

## License

MIT
