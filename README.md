# Trame

> **A code-first framework for building command-oriented Web APIs on .NET.**
>
> One contract. Multiple transports. **Server-side dependency chaining.**

[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

Trame is designed for **command-oriented Web APIs**. Unlike resource-oriented frameworks, Trame models **commands** as the primary abstraction and allows dependent commands to exchange **typed JSON fragments** within a single request.

Your C# classes are the contract — no `.proto`, no IDL, no code generation. The same call runs over REST, WebSocket, or SignalR, consumable from any language. Discovery metadata is generated at runtime.

> **The name.** *Trame* (French, /tʁam/) — the weft, the cross-threads that hold a fabric together. Trame weaves multiple transports and chained calls into one framework.

> **Getting started:** [GETTING_STARTED.md](GETTING_STARTED.md) · **Full reference:** [README_DETAILS.md](README_DETAILS.md) · **Wire format:** [PROTOCOL.md](PROTOCOL.md) · **Security:** [SECURITY_GUIDE.md](SECURITY_GUIDE.md) · **Best practices:** [BEST_PRACTICES.md](BEST_PRACTICES.md) · **Roadmap:** [ROADMAP.md](ROADMAP.md)

---

## Why Trame?

Business applications are built around commands: CreateOrder, ApproveInvoice, CancelBooking, AssignRole, GenerateInvoice.

REST models resources exceptionally well. Trame models **commands**.

---

## The missing piece

Traditional RPC executes one method after another. The client must: execute command A → parse the response → extract values → transform them → call command B → repeat.

Trame moves this orchestration to the server.

Note what this is — and isn't. The win is **not** "Trame parallelizes your reads": a REST client
can `Promise.all` too, and a bulk endpoint (`GetByIds`) is good API design you'd build in either
world. The win is the **cross-service dependency chain** — call *k*'s inputs come from call
*k−1*'s outputs, so no bulk endpoint can collapse it, and a REST client owns the
extract-and-await glue for every link. Trame takes that glue off the client and collapses the
whole chain to **one roundtrip** with server-side graph resolution. The bulk endpoints stay;
the glue between them goes. Full reasoning: [BEST_PRACTICES.md §4.2](BEST_PRACTICES.md#42-when-the-trame-batch-beats-the-rest-loop--and-where-the-win-actually-is).

---

## Dependency Chaining

Dependency Chaining is Trame's defining feature. A command may expose **any JSON fragment** from its result:

```csharp
.Exposes("$.items[*].id", "customerIds")
```

A later command consumes it directly:

```csharp
.WithAlias("@customerIds")
```

Aliases are **typed JSON fragments**. They may represent primitive values, arrays, complex objects, or nested object graphs. A wildcard path (`$[*].id`) collects **all** matches into an array and injects it as one list-typed parameter — a server-side `Select`.

### Example

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
            .Exposes("$.items[*].articleId", "articleIds")
            .ToRequest(),

        TrameCall.Init("Article", "GetAvailability")
            .WithAlias("@articleIds")
            .ToRequest()
    ]
};
```

Three dependent commands. One request. No client-side orchestration.

---

## More than RPC

Trame is more expressive than traditional RPC because commands may exchange typed data directly. At the same time, Trame deliberately avoids GraphQL's additional complexity. There is no schema language, no resolver model, no query language, no code generation. Your C# classes are the contract.

## Why not GraphQL?

GraphQL answers: *Which object graph do you want?* Trame answers: *Which commands do you want to execute?* The client describes a dependency graph between commands rather than an object graph.

---

## Code First

```csharp
using TrameCore.Attributes;

[TrameController("Customer")]
public class CustomerHandler
{
    [TrameMethod("Create")]
    public async Task<int> Create(string name) => 42;
}
```

Runtime discovery generates the contract directly from your code.

---

## Runtime Discovery

`GET /api/trame/discovery` returns controllers, methods, parameter contracts, examples, and documentation — without maintaining a separate schema. Types are carried as structured, language-neutral `TypeRef` objects (not .NET type-name strings), versioned via an additive-only `discoveryVersion` field — see [`docs/discovery-schema.md`](docs/discovery-schema.md) for the authoritative type-system spec. A built-in developer UI (`/Trame`) lets you browse and try calls in the browser.

---

## Developer UI

A built-in web UI at **`/Trame`** (served by `MapTrame()`) turns runtime discovery into a working console — not a Swagger page that forgets your call the moment you close it:

- **Browse** controllers (collapsible) and the full type tree straight from discovery.
- **Open many calls at once** — each method keeps its own tab with parameters, request JSON, the response, and the call log; tabs persist across reloads.
- **Batch & Dependency Builder** — assemble serial batches and `@alias` chains visually, with a static checker that flags cross-kind, missing-property, and casing problems *before* you send.
- **Codegen** — generate TypeScript and C# client code from the current call or batch.
- **History** — the last 100 calls with request, response, and duration.
- **Save the whole workset** — export a workspace snapshot (connection + open tabs + theme + split layout + history) to any folder (native save dialog on Chromium) and restore it later, live.
- **Standalone build** — `npm run build:standalone` produces a static DevUI that runs from any host (e.g. GitHub Pages) and points at a remote Trame server via the Connection setting.

The Bearer token is never written into an exported workspace.

> Full feature detail: [README_DETAILS.md#developer-ui](README_DETAILS.md#developer-ui)

---

## Multi Transport

The same contract works over REST, WebSocket, and SignalR. Choose the transport. Not the programming model.

| Transport | Endpoint | Use case |
|-----------|----------|----------|
| REST | `POST /api/trame/json` | Stateless calls, load-balanced scenarios |
| WebSocket | `ws://host/tramews` | Persistent connections, low latency, many small calls |
| SignalR | `/tramehub` hub | Browser clients, auto-reconnect, MessagePack binary |

---

## Safety

Dependency chaining is resolved at runtime: the server extracts a JSON fragment via the provider's JsonPath, injects it in place of the `@alias`, and binds it to the consumer's parameter with `System.Text.Json`. What happens on a shape mismatch is well-defined, not a generic crash:

| Fragment → Parameter | Runtime behavior |
|---|---|
| Compatible | Binds normally → 2xx |
| Cross-kind scalar (string→int, number→bool, …) | STJ throws → **400** (no silent coercion — `AllowReadingFromString` is off by design) |
| Object → object, missing property | Duck-typed: overlapping props bind, extras ignored, **missing** props silently defaulted (value types → `0`/`false`, references → `null`) → 2xx |
| Unresolved (path matched nothing / alias never exposed) | **400** `Unresolved` |

**Provider failure propagates.** When a provider is unauthorized, errors, or doesn't expose the fragment it declared, its dependents **don't run** — each gets a `400` naming the provider, alias, and cause (`Dependency '@a' unavailable: provider '<id>' was unauthorized (401).` / `… returned HTTP <code>.` / `… did not expose '@a'.`) instead of hitting the missing alias at runtime with an uninformative `Unresolved`. Propagation is transitive: one failed provider cancels its whole branch. Authorization is checked **per request** in a batch (a `401` doesn't abort the others — JSON-RPC-conformant), and runs in a **serial pre-pass** before the parallel fan-out so the parallel execution never touches the shared, non-thread-safe `HttpContext`. Full spec: [`DEPENDENCY_BINDING.md §9`](DEPENDENCY_BINDING.md#9-provider-failure--dependent-propagation).

The one silent case is **object→object, and it is directional**: provider `{Id, Name}` → consumer `{Id}` is safe (subset, nothing missing) — and **useful**: one whole-object alias (`$` → `@customer`) can feed several consumers whose parameter types each pick just the fields they need (`CustomerId { int Id; }`, `CustomerName { string Name; }`, …); the rest silently drop. The reverse — provider `{Id}` → consumer `{Id, Active}` — silently sets `Active` to `false` and succeeds with wrong data. This is inherent to JSON duck-typing; Trame has no runtime schema to enforce structural equality (your C# classes *are* the contract). The developer UI's dependency builder catches the dangerous direction **statically** where both schemas are known. For teams that want it loud at runtime too, three binding modes are available: **Weak** (default, duck-typed), **Strict** (top-level coverage of `@alias` params → 400 on missing), and **Paranoid** (full coverage of *all* parameters — including literals — recursively at every depth → 400 on any missing property anywhere). The safe subset fan-out binds in all three. Set via `TrameOptions.AliasBindingMode`. Full specification: [`DEPENDENCY_BINDING.md`](DEPENDENCY_BINDING.md).

**Casing matters in one place:** JsonPath in `.Exposes(...)` runs against the camelCase wire document, so it must be camelCase (`$.customerId`, `$.items[*].id`); a PascalCase `$.Id` matches nothing → `Unresolved`. Parameter *names* bind case-sensitively; object *value* properties are read case-insensitively. Full contract: [PROTOCOL.md#casing-contract](PROTOCOL.md#casing-contract).

To protect the server, Trame enforces **two configurable cardinality caps**, secure by default:

- **`MaxResultElementCount`** (default **10,000**) — caps the *producing* result (the source of a fan-out).
- **`MaxParameterArrayLength`** (default **1,000**) — caps the *consuming* parameter (the injected `@alias` array).

Either can be raised or disabled (`0` = unlimited). Dependency chaining is intended for bounded intermediate results, not unbounded server-side joins.

---

## Binary

A method may take or return `byte[]`. Bytes travel **out of band** from the JSON `data` field (`binaryData` request, `content` response).

| Transport | `binaryData` (request) | `content` (response) |
|-----------|------------------------|----------------------|
| REST (JSON) | base64 | base64 |
| WebSocket (JSON text frames) | base64 | base64 |
| SignalR (MessagePack) | native `bin` | native `bin` |

Limits: REST 1 MB body, WebSocket 1 MB/message, SignalR configurable. For large or frequent binary, run a plain REST/WebSocket endpoint alongside Trame. Streamed binary is not in v1 — see [ROADMAP.md](ROADMAP.md).

---

## Clients

- **C#** — `Trame.Client` NuGet: REST, WebSocket, SignalR behind one `ITrameClient`, fluent `TrameCall`, batches, chaining, binary. See [`TrameClient/README.md`](TrameClient/README.md).
- **TypeScript/JavaScript** — `npm i trame-client`: isomorphic REST + WebSocket, runs in browser and Node. See [`clients/ts/README.md`](clients/ts/README.md).
- **Other languages** — the wire protocol is JSON and fully specified in [PROTOCOL.md](PROTOCOL.md); any HTTP/WebSocket client works. `GET /api/trame/discovery` returns full type metadata for auto-generated clients.

---

## JSON-RPC 2.0 Compatibility

Any existing JSON-RPC 2.0 client can drive a Trame server as-is, opt-in:

```csharp
builder.Services.AddTrame(new TrameOptions { EnableJsonRpcCompat = true });
app.MapTrame();
```

```
POST /api/trame/jsonrpc          # {"jsonrpc":"2.0","method":"Customer.GetById","params":{"id":42},"id":1}
→ {"jsonrpc":"2.0","result":{...},"id":1}
```

`method` is `Controller.Method`; `params` object → named, array → positional; `id`
(number/string) is echoed with its original type; batches run in Parallel; notifications
emit no response (an all-notification batch → `HTTP 204`). Errors map to the JSON-RPC
ranges (routing `404` → `-32601`, business `404` → `-32000`, `401`/`403` → `-32001`, …)
and live in the `200` envelope, JSON-RPC-conformant.

This is an **adoption lure**: start with the JSON-RPC ecosystem you already have, then
graduate to the native wire for `@alias` chaining, execution-mode selection, binary
out-of-band, and streaming. Two capability methods bridge the gap — `trame.discover`
returns the full discovery metadata, `trame.capabilities` lists the native strengths
the compat mode doesn't expose. Full spec + a Trame-vs-JSON-RPC protocol-differences
table: [`JSONRPC_COMPAT.md`](JSONRPC_COMPAT.md).

---

## Features

- Command-oriented Web APIs
- Code-first (no `.proto`, no IDL, no code generation)
- Dependency chaining (scalar, array, object, nested graph)
- Runtime discovery + developer UI (browse, multi-tab calls, batch/dependency builder, codegen, history, workspace snapshots)
- REST, WebSocket, SignalR — same call
- Batch execution (parallel, serial, topological)
- Streaming (`IAsyncEnumerable<T>`)
- Binary support
- Interceptor pipeline, authorization, rate-limiting
- Expression-tree invocation (no reflection per call)
- Distributed tracing via OpenTelemetry

---

## Quick Start

> **Full step-by-step from an empty directory to a running DevUI: see
> [GETTING_STARTED.md](GETTING_STARTED.md).** What follows is the wiring in one block.

### Server

```csharp
using TrameHub.Extensions;
using TrameServer;

var builder = WebApplication.CreateBuilder(args);

// Serve the DevUI bundles from the neighboring TrameDeveloperUi project in Development.
builder.WebHost.UseStaticWebAssets();

// Register Trame with all transports (hub + REST + WebSocket + DevUI)
builder.Services.AddTrame(o =>
{
    o.UseSignalR = true;
    o.UseMessagePack = true;
    o.MaximumParallelInvocationsPerClient = 100;
});

var app = builder.Build();
app.UseStaticFiles();      // actually serves the DevUI static assets
app.UseRouting();
app.UseTrameTransports();   // WebSocket + controller registration
app.MapTrame();             // REST (/api/trame) + DevUI (/Trame) + SignalR hub (/tramehub)
app.Run();
```

After `dotnet run`, open **`/Trame`** in the browser — the Developer UI over live discovery.
(`UseStaticWebAssets()` is a Development hook; for `dotnet publish` see GETTING_STARTED.)

### Controller

```csharp
using TrameCore.Attributes;

[TrameController("Customer")]
public class CustomerHandler(CustomerService service)
{
    [TrameMethod("GetById")]
    public async Task<Customer?> GetById(int id, CancellationToken ct)
        => await service.GetById(id, ct);

    [TrameMethod("Create")]
    [TrameAuthorise]  // Optional: requires authentication
    public async Task<int> Create(string name)
        => await service.Add(name);
}
```

### Client

```csharp
using TrameClient.Trame;
using TrameCommon.Models;

// Choose your transport
var client = new TrameRestJsonClient("https://localhost:5001/");
// or: new TrameWebSocketClient("https://localhost:5001/")
// or: new TrameSignalrClient("https://localhost:5001/")

// Single call
var request = TrameCall.Init("Customer", "GetById").With(42).ToRequest();
var customer = await client.Call<Customer>(request);

// Batch with dependency chaining
var batch = new TrameMultiRequest
{
    Mode = ExecutionMode.Serial,
    Requests = new List<TrameRequest>
    {
        TrameCall.Init("Customer", "Create")
            .With("Alice").Named("step1")
            .Exposes("$", "newId")
            .ToRequest(),
        TrameCall.Init("Customer", "GetById")
            .WithAlias("@newId")
            .Named("step2")
            .ToRequest()
    }
};
var responses = await client.Call(batch);
```

### Build-time contract & typed clients (Node-free)

The discovery JSON *is* the contract. Trame ships a **.NET-native codegen build action** that
turns it into a compile-time boundary — no `.proto`, no Node:

- **Server side — `Trame.Server.Codegen`**: regenerates `contract.trame.json` from the built
  assembly on every build and **fails the build if it has drifted** from the runtime
  (`TRAME_REGEN_GOLDEN=1` to regenerate an intentional change).
- **Client side — `Trame.Generator`**: a Roslyn source generator that reads `contract.trame.json`
  and emits a typed `TrameGeneratedClient` **into the compilation** — controller/method accessors,
  POCOs, and `Batch`/`Exposes`/`Alias` chaining, all generated. A contract change breaks the client
  at compile time.

Both are NuGet-shipped and self-contained. Full onboarding (wiring, regen flow, CI, config,
troubleshooting): **[`CODEGEN_ONBOARDING.md`](CODEGEN_ONBOARDING.md)**.

---

## Performance

Benchmarked with BenchmarkDotNet on .NET 8.0, Intel Core i7-12700H:

| Scenario | REST | Trame REST | Trame WebSocket | Speedup |
|----------|------:|----------:|---------------:|--------:|
| Single Call (GetCustomerById) | 606 µs | 627 µs | **267 µs** | **2.3x** |
| Batch 10x (parallel) | 4,906 µs | 974 µs | **693 µs** | **7.1x** |
| Dependency-Chain (3 calls, 1 roundtrip) | 206 ms | 205 ms | **204 ms** | replaces 3 roundtrips |

> Full benchmarks: [TrameBench/BENCHMARK-RESULTS.md](TrameBench/BENCHMARK-RESULTS.md)

---

## Known Limitations (v1)

Trame v1 is intentionally focused. These are deliberate scope decisions, not bugs:

- **No Native AOT** — runtime reflection + `Expression.Compile` are incompatible with AOT trimming.
- **No built-in API versioning** — encode version in the controller name (`Customer.v1`); source-generator enforcement planned for v1.1.
- **REST streaming is materialized** — `IAsyncEnumerable<T>` is consumed server-side into a JSON array; use WebSocket/SignalR for streaming semantics.
- **Attribute-based registration only** — controllers/methods via `[TrameController]`/`[TrameMethod]`; fluent registration is on the roadmap.
- **No parameter-based overloading** — a call is addressed by `Controller.Method` only; the parameter set is **not** part of the dispatch key, so Trame does not resolve C# overloads by signature. Two `[TrameMethod]`s on the same controller, or two `[TrameController]`s app-wide, must have distinct names — otherwise `Register` throws `InvalidOperationException` at startup (no silent shadowing). Model overloads with distinct names (`add`, `addAll`). Opt a controller out of auto-discovery with `[TrameController("name", AutoDiscover = false)]`.
- **Binary is base64 over REST and WebSocket** — only SignalR/MessagePack carries `byte[]` natively; WebSocket honors text frames only.
- **Error details in development only** — `TrameError.details` is populated only with `EnableDetailedErrors` / Development.

> Full details, comparison tables, and project structure: [README_DETAILS.md](README_DETAILS.md). Roadmap: [ROADMAP.md](ROADMAP.md).

---

## Distributed Tracing

Trame emits OpenTelemetry spans for every call and every batch — always on, but cost-neutral: the `ActivitySource` named **`Trame`** returns `null` when no listener is subscribed, so there is zero overhead unless you opt in. Spans follow the OTel RPC semantic conventions:

- `TrameCall` — per request: `rpc.system=trame`, `rpc.service=<Controller>`, `rpc.method=<Method>`, `trame.request_id` (when set), `trame.binary.length` (for binary payloads), status `Ok`/`Error`, plus `exception.*` tags on escaping failures.
- `TrameBatch` — per batch: `trame.batch.mode` (`Parallel`/`Serial`/`DependencyBatches`), `trame.batch.count`. Per-request `TrameCall` spans are children of the batch span.

To export the spans, reference the optional **`Trame.Telemetry`** package and call `AddTrameTelemetry` next to `AddTrame`:

```csharp
using TrameHub.Extensions;
using TrameServer;
using TrameTelemetry;

builder.Services.AddTrame(o => o.UseSignalR = true);
builder.Services.AddTrameTelemetry(o =>
{
    o.ServiceName = "MyService";            // OTel resource service.name
    o.Exporter = TrameExporter.Otlp;        // or TrameExporter.Console for local diagnosis
    o.OtlpEndpoint = "http://localhost:4317"; // null → OTEL_EXPORTER_OTLP_ENDPOINT env var
    o.IncludeAspNetCore = true;             // inbound HTTP spans (default)
    o.IncludeHttpClient = true;              // outbound HTTP spans (default)
});
```

`AddTrameTelemetry` subscribes to the `Trame` source and wires the exporters. Prefer your own `AddOpenTelemetry()` setup with `AddSource("Trame")` if you need custom samplers, resources, or exporters — the source name is the only integration point.

## Installation

```xml
<!-- Server: all transports (hub + REST + WebSocket + DevUI) -->
<PackageReference Include="Trame.Server" Version="1.0.0" />

<!-- Optional: OpenTelemetry SDK bootstrap (OTLP/Console exporters + AspNetCore/HttpClient instrumentation) -->
<PackageReference Include="Trame.Telemetry" Version="1.0.0" />

<!-- Or individual transports -->
<PackageReference Include="Trame.Core" Version="1.0.0" />
<PackageReference Include="Trame.Hub" Version="1.0.0" />
<PackageReference Include="Trame.Rest" Version="1.0.0" />
<PackageReference Include="Trame.WebSocket" Version="1.0.0" />

<!-- Client -->
<PackageReference Include="Trame.Client" Version="1.0.0" />
```

## Requirements

- .NET 8.0+
- ASP.NET Core 8.0+

## License

MIT