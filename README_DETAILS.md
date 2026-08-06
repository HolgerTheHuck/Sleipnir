# Trame

> **Detailed reference.** For the landing page, see [README.md](README.md).

**Trame** is a code-first, multi-transport framework for **command-oriented web APIs** on .NET 8+. Your C# classes *are* the contract; discovery metadata is generated at runtime — no `.proto`, no IDL. The same call runs over REST, WebSocket, or SignalR, and is consumable from any language, not .NET only.

What sets Trame apart is **dependency chaining**: send several calls in a single roundtrip and let later calls reuse results from earlier ones. One request exposes a value via `Exposes("$", "newId")` (a result-relative JSON path), the next consumes it as `WithAlias("@newId")`, and the server resolves the `@alias` placeholders against prior responses — no client-side glue code, no extra roundtrips. A workflow that needs a customer's new ID to create their order completes in one roundtrip instead of three.

> **The name.** *Trame* (French, /tʁam/) is the weft — the cross-threads in weaving that hold a fabric together. Trame weaves multiple transports (REST, WebSocket, SignalR) and chained calls into one coherent framework.

[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

---

## Resources, Commands, and Trame's direction

REST is so much *the* standard that "wrong" is the wrong word for it — it simply isn't shaped for command calls. For resource-oriented APIs, CRUD on nouns like `/customers/42` or `/orders` is exactly right. **Are REST, gRPC, or GraphQL wrong, then? No — each was built for a shape of API it handles well.** Trame simply takes a different direction: a *command-oriented* web API where the following come up naturally:

- **Verb-noun mismatch**: `POST /api/customer/42/add-address` isn't RESTful, but it's what a command-oriented API actually needs
- **N+1 roundtrips for dependent data**: a customer, then their orders, then each order's items is 3 sequential HTTP roundtrips — the client waits on each before sending the next
- **No batching**: 10 method calls means 10 separate HTTP requests and connections
- **No dependency resolution**: when call A returns an ID that call B needs, the client manually parses A, extracts the ID, and passes it to B — every time

GraphQL addresses composition with a schema, resolvers, and a separate type system — and does it well, at the cost of that schema-first layer.

None of this is a knock on REST. Its ubiquity forced everyone — for a generation — to think in terms of a *web API*: resources, verbs, status codes, content negotiation, stateless scaling. That discipline was extremely helpful and is now second nature. The friction above is what shows up when that same resource-oriented shape is pressed into *command-oriented* work.

Trame doesn't strip the web API away — it keeps it. Calls travel over HTTP with JSON, through standard ASP.NET Core middleware, and the contract is consumable from any language, not just .NET. To honor that web-API layer rather than treat it as plumbing, Trame ships **runtime discovery** (`GET /api/trame/discovery` returns the full contract: controllers, methods, parameter schemas, documentation, examples) and a **developer UI** that lets you browse and try calls in the browser. That makes Trame a discoverable, language-neutral, command-oriented web API — not a closed channel between two .NET ends. What it drops is only the resource orientation: commands stop having to masquerade as nouns.

## Trame vs gRPC — a different direction

gRPC is a strong, sensible choice for RPC on .NET. Trame isn't a rejection of it — it takes a different direction on three axes:

1. **History.** When Trame started, gRPC-Web (for browsers) required a proxy, native browser support was missing, and the .NET tooling was still immature. That's no longer true today; it's the historical reason Trame exists.
2. **Code-first, not schema-first.** gRPC requires `.proto` files, a code generator, and a separate build step — you write the schema in a DSL, generate C# stubs, then implement them. That's a deliberate, well-justified choice in gRPC. Trame goes the other way: in .NET your C# classes *already are* the schema, so it keeps a single definition and generates discovery metadata at runtime.
3. **Transport choice.** gRPC standardizes on one wire — HTTP/2 + Protobuf, great for server-to-server — and reaches browsers via gRPC-Web. Trame lets the *same* call run over REST, WebSocket, or SignalR, so you pick the transport per deployment, not per call.

Trame is **code-first**: you write C# classes and methods, decorate them with attributes, and the framework handles the rest. No `.proto` files, no code generation, no separate schema. Discovery metadata is generated at runtime from your code.

## How Does Trame Compare to JSON-RPC?

[JSON-RPC 2.0](https://www.jsonrpc.org/) is the closest spiritual ancestor to Trame — both are method-oriented, not resource-oriented, and both use JSON. So why not just use JSON-RPC?

| | JSON-RPC 2.0 | **Trame** |
|---|---|---|
| Method-oriented calling | ✅ | ✅ |
| Batch calls | ✅ (JSON array) | ✅ |
| Dependency-chaining | ❌ calls are independent | ✅ `@alias` resolution |
| Type contracts | ❌ untyped `params` | ✅ signature inference (`[TrameDataContract]` optional) |
| API discovery | ❌ (needs OpenRPC) | ✅ built-in `/api/trame/discovery` |
| Documentation | ❌ (external) | ✅ `[TrameDocumentation]` + `[TrameExample]` (class/method level in v1) |
| Streaming | ❌ | ✅ `IAsyncEnumerable<T>` |
| Authorization | ❌ (transport-level) | ✅ `[TrameAuthorise]` |
| Error model | ✅ basic (`code` + `message`) | ✅ `TrameError` with details + requestId |
| .NET integration | partial (StreamJsonRpc & libs) | ✅ native ASP.NET Core middleware |
| Transport definitions | ❌ (spec is format-only) | ✅ REST + WebSocket + SignalR defined |
| Pre-compiled invocation | ❌ (reflection) | ✅ Expression Trees |

**In short**: JSON-RPC defines a *wire format*, Trame is a *framework*. JSON-RPC tells you how to format the JSON, but doesn't help you discover methods, resolve dependencies, stream results, authorize calls, or integrate with .NET DI. Trame was built without knowing JSON-RPC existed, but ended up solving a similar core problem — a command-oriented web API on .NET — with a different emphasis: discovery, dependency chaining, streaming, authorization, and DI integration built into the framework rather than left to the caller.

## Why Trame?

| | REST | gRPC | GraphQL | JSON-RPC | **Trame** |
|---|---|---|---|---|---|
| Designed for RPC | ❌ | ✅ | partial | ✅ | ✅ |
| Code-first (no IDL) | ✅ | ❌ (`.proto`) | ❌ (schema) | ✅ | ✅ |
| Browser-native (no proxy) | ✅ | ❌ (gRPC-Web) | ✅ | ✅ | ✅ |
| Multi-transport | ❌ | partial (gRPC-Web via proxy) | ✅ HTTP + WS subscriptions | format-only | ✅ same call over REST + WebSocket + SignalR |
| Batch-calls | ❌ | ❌ | ✅ | ✅ | ✅ |
| Dependency-chaining | ❌ | ❌ | ✅ | ❌ | ✅ |
| Type contracts | ❌ | ✅ | ✅ | ❌ | ✅ |
| Streaming | ❌ | ✅ | ✅ | ❌ | ✅ `IAsyncEnumerable<T>` |
| Interceptor-pipeline | ❌ | ✅ | ✅ (HTTP/socket + field middleware) | ❌ | ✅ |
| API-Discovery | OpenAPI | ✅ (reflection) | Introspection | ❌ | ✅ built-in |
| Developer-UI | Swagger | grpcui | GraphiQL | ❌ | ✅ |
| Rate-limiting | manual | ❌ | partial (concurrency + cost) | ❌ | ✅ built-in |

> **On transport, honestly:** GraphQL is not HTTP-only — the spec (and Hot Chocolate on .NET) supports subscriptions over WebSocket, and gRPC reaches browsers via gRPC-Web. The distinction is *what the transports carry*. In GraphQL, HTTP serves queries/mutations and WebSocket serves subscriptions — different operation types on different channels. In Trame the **same** call runs over REST, WebSocket, or SignalR with identical wire semantics; you pick the transport per deployment, not per operation. Trame's actual differentiator is **dependency chaining** (above), not multi-transport.

Trame was designed from the ground up as a **command-oriented web API** — not a REST wrapper, but a proper framework for command-oriented APIs that happens to support REST as one of several transports:

1. **Method-oriented, not resource-oriented**: `[TrameController("Customer")]` + `[TrameMethod("AddOrder")]` — no URL routing, no HTTP verbs to choose, no DTO mapping
2. **Batch calls**: Send 10 calls in one roundtrip — the server executes them and returns 10 responses
3. **Dependency chaining** (GraphQL-inspired): One request exposes values via `Exposes("$", "orderId")` (result-relative JSON path), the next uses `WithAlias("@orderId")` — the server resolves dependencies in a single roundtrip. A wildcard path `Exposes("$[*].id", "ids")` collects **all** matches into an array and injects it as one list-typed parameter — `Search → GetByIds(@ids)` in a single roundtrip, like a server-side `Select`
4. **Multi-transport**: REST for compatibility, WebSocket for low-latency persistent connections, SignalR for browser clients — same API, different wire protocols
5. **Expression-Tree invocation**: Methods are pre-compiled to delegates at registration time — no reflection per call

| | REST | gRPC | SignalR | **Trame** |
|---|---|---|---|---|
| Multi-Transport | no | partial (gRPC-Web) | no | **yes** (REST + WebSocket + SignalR) |
| Batch-Calls | no | no | no | **yes** |
| Dependency-Chaining | no | no | no | **yes** |
| Streaming | no | yes | yes | **yes** (IAsyncEnumerable<T>) |
| Interceptor-Pipeline | no | yes | yes (hub filters) | **yes** |
| API-Discovery | OpenAPI | yes (reflection) | no | **yes** (built-in) |
| Developer-UI | Swagger | grpcui | no | **yes** |
| Rate-Limiting | manual | no | no | **yes** (built-in) |

## Performance

Benchmarked with BenchmarkDotNet on .NET 8.0, Intel Core i7-12700H:
| Scenario | REST | Trame REST | Trame WebSocket | Speedup |
|----------|------:|----------:|---------------:|--------:|
| Single Call (GetCustomerById) | 606 us | 627 us | **267 us** | **2.3x** |
| Batch 10x (parallel) | 4,906 us | 974 us | **693 us** | **7.1x** |
| Dependency-Chain (3 calls, 1 roundtrip) | 206 ms | 205 ms | **204 ms** | replaces 3 roundtrips |

> Full benchmarks: see TrameBench/BENCHMARK-RESULTS.md

## Quick Start

### Server

```csharp
using TrameHub.Extensions;
using TrameServer;

var builder = WebApplication.CreateBuilder(args);

// Register Trame with all transports (hub + REST + WebSocket + DevUI)
builder.Services.AddTrame(o =>
{
    o.UseSignalR = true;
    o.UseMessagePack = true;
    o.MaximumParallelInvocationsPerClient = 100;
});

var app = builder.Build();
app.UseTrameTransports();   // WebSocket + controller registration
app.MapTrame();             // REST (/api/trame) + DevUI (/Trame) + SignalR hub (/tramehub)
app.Run();
```

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
var request = TrameCall.Init("Customer", "GetById")
    .With(42)
    .ToRequest();
var response = await client.Call(request);

// Typed call
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

## Transports

| Transport | Endpoint | Use Case |
|-----------|----------|----------|
| **REST** | POST /api/trame/json | Stateless calls, load-balanced scenarios |
| **WebSocket** | ws://host/tramews | Persistent connections, low latency, many small calls |
| **SignalR** | /tramehub hub | Browser clients, auto-reconnect, MessagePack binary |

## Developer UI

`MapTrame()` serves a built-in single-page web UI at **`/Trame`** (configurable via the `developerUiPath` argument). It is the working console over the runtime discovery — the thing Swagger gives you only fleetingly, here kept across sessions. No backend of its own: it talks to the same `/api/trame` endpoints as any other client, so everything you can do in the UI you can do from code.

**Layout.** Three resizable panes (persisted in `localStorage`): a left *Explorer* (Discovery tree of controllers + methods, collapsible, and the full type tree below a horizontal splitter), a center *Editor*, and a right *Result*. Plus a top bar (connection, auth, codegen, dependency builder, history, refresh, theme) and a collapsible history panel along the bottom.

**Explorer.** Browses the live discovery: controllers are collapsible with method counts, a filter narrows controllers/methods by name, and the Types tree expands each discovered contract into its properties. Clicking a method opens a tab.

**Tabs.** Every open call is a tab that keeps its parameters, the request JSON (kept in sync with the parameter form), the last response, the call log, status, and duration. Tabs are plain JSON and persist across reloads (`trame-tabs`). Three tab kinds:

- **Request** — a single method call with a parameter form + raw JSON editor.
- **Dependency Builder** — a visual composer for serial `@alias` batches: each step is a `TrameRequest`, exposes become `dependencyMapping`, alias-params become `@alias` placeholders. The one place that has both the provider return schema and the consumer parameter schema (from discovery), so it runs a **static checker** — see [DevUI static checker](#devui-static-checker).
- **Codegen** — emits TypeScript and C# client code for the current call or batch, copy-to-clipboard.

**History.** The last 100 calls (request, response, duration, error) live in `trame-history` and show in a collapsible bottom panel. Replay is one click.

**Connection & Auth.** Base URL + API path (defaults to same-origin `/` / `api/trame` when embedded; empty in the standalone build so you point at a remote server). Bearer token set via the Auth panel — applied to every call (Discovery, single, batch) through the client facade, stored in `trame-bearer`, never exported.

**Workspace snapshots.** The full workset is serializable: ⚙ → Export writes a JSON snapshot with **connection + open tabs + active tab + theme + split layout + history** to a file named `trame-workspace-YYYY-MM-DD-HHMM.json`. On Chromium (Edge/Chrome) the native save dialog lets you pick folder and filename; Firefox/Safari fall back to a plain download. ⚙ → Import restores it **live** — theme, split sizes, tabs, and history come back without a reload (the connection change re-fetches discovery). Version 2 format; version 1 snapshots (connection + tabs only) still import. The Bearer token is deliberately **not** part of the snapshot — after import, the currently set token stays active.

**Standalone build.** `npm run build:standalone` (in `TrameDeveloperUi/`) emits `dist-standalone/` with relative asset paths — deploy it to any static host (GitHub Pages, a CDN, a local `serve`) and point the Connection setting at a remote Trame server. CORS must be open on the target server for the DevUI's origin. Useful for trying a server without standing up the .NET host, or for hosting a shared console against a known endpoint.

> The DevUI ships as the `TrameDeveloperUi` project (see [Project Structure](#project-structure)) and is part of the `Trame.Server` package — no extra install.

## Features

- **Multi-Transport**: Same API over REST, WebSocket, and SignalR
- **Batch-Calls**: Execute multiple calls in a single roundtrip (parallel or serial)
- **Dependency-Chaining**: Chain calls with @alias placeholders resolved server-side; wildcard paths (`$[*].Id`) fan a result set into a single list-typed parameter
- **Streaming**: IAsyncEnumerable<T> support, serialized as JSON arrays
- **Interceptor-Pipeline**: ITrameInterceptor for logging, tracing, caching, validation
- **Discovery/MEX**: GET /api/trame/discovery returns full API metadata
- **Developer-UI**: Built-in web UI at `/Trame` for browsing, testing, batch-building, codegen, and saving whole worksets — see [Developer UI](#developer-ui)
- **Rate-Limiting**: Built-in fixed-window rate limiter
- **Authorization**: [TrameAuthorise] attribute with role-based access
- **Expression-Tree Invocation**: Pre-compiled method delegates for maximum performance

## Binary

A method may take a `byte[]` parameter or return `byte[]`. The bytes travel **out of band from the JSON `data` field** — request bytes in `binaryData`, response bytes in `content` — so they never compete with structured arguments and are never duplicated into `data`. The wire encoding depends on the transport:

| Transport | `binaryData` (request) | `content` (response) |
|---|---|---|
| REST (JSON) | base64 in JSON | base64 in JSON |
| WebSocket (JSON text frames) | base64 in JSON | base64 in JSON |
| SignalR (MessagePack) | native MessagePack `bin` | native MessagePack `bin` |

Base64 is avoided **only on the SignalR/MessagePack channel**. REST and WebSocket carry JSON text, so binary is base64-encoded there (~33 % overhead), bounded by the message-size limits. The WebSocket transport deliberately accepts only text frames — native binary frames are not honored.

**Limits:** REST 1 MB request body; WebSocket 1 MB per message (hardcoded); SignalR `MaximumReceiveMessageSize` in `TrameOptions` (defaults to SignalR's limit when unset). The cardinality caps `MaxParameterArrayLength` / `MaxResultElementCount` do not apply to `byte[]` — they guard collection sizes, not binary size.

**Practical guidance:** Binary over REST or WebSocket is fine for payloads up to the limits. For large or frequent binary, run a plain REST or WebSocket endpoint alongside Trame — there is nothing about binary that requires the RPC channel. Streamed/chunked binary upload and a streamed `byte[]` response are not in v1; see [ROADMAP.md](ROADMAP.md) for the v1.x+ binary-transfer plan.

**Client support:** TypeScript ships `withBinary(Uint8Array)` and `callBinary()`; SignalR clients (C# + TS) carry `byte[]` natively. The C# REST/WebSocket client offers `CallBinary()` for responses, but the fluent builder has **no `WithBinary` yet** — set `request.BinaryData` directly until it lands. See [PROTOCOL.md](PROTOCOL.md) for exact field semantics.

## Cross-Platform

Trame's wire protocol is fully specified in [PROTOCOL.md](PROTOCOL.md) — JSON-based, no binary dependency. This enables implementations in any language:

- **C#**: A first-class client ships as the `Trame.Client` NuGet package — REST, WebSocket, and SignalR behind one `ITrameClient` surface, fluent `TrameCall` builder, batches, dependency chaining, binary. See [`TrameClient/README.md`](TrameClient/README.md).
- **TypeScript/JavaScript**: A ready-to-use isomorphic client ships in [`clients/ts/`](clients/ts/) (`npm i trame-client`) — REST + WebSocket, fluent + functional API, runs in the browser and Node.js. See [`clients/ts/README.md`](clients/ts/README.md).
- **Python**: Use the REST endpoint with `requests`, or `websockets` library
- **Go/Rust**: Any HTTP or WebSocket client works

The `/api/trame/discovery` endpoint returns full type metadata, enabling auto-generated clients.

> The wire format and a minimal hand-rolled example are documented in [PROTOCOL.md](PROTOCOL.md); for real use prefer the `clients/ts/` package.

## Dependency Chaining — Binding, Types & Casing

> This is the user-facing overview. The **dedicated, precise specification** of the
> provider→consumer JSON mapping is [`DEPENDENCY_BINDING.md`](DEPENDENCY_BINDING.md),
> and the executable spec is
> [`TrameTests/Unit/Core/AliasBindingTests.cs`](TrameTests/Unit/Core/AliasBindingTests.cs).

Dependency chaining exchanges **typed JSON fragments** between commands in one batch. A provider exposes a fragment via a result-relative JsonPath (`.Exposes("$..id", "orderId")`); a consumer references it as a placeholder (`.WithAlias("@orderId")`). The server resolves placeholders before the consumer runs. This section describes exactly what crosses the wire and what happens when the shapes do not match — the short version for the landing page is in [README.md](README.md#safety); the authoritative spec is in [PROTOCOL.md](PROTOCOL.md#alias-serialization--type-binding).

### The binding pipeline

For each `@alias` parameter the server performs three steps, in order:

1. **Extract** — evaluate the provider's JsonPath against the provider's *serialized result* (a JSON document, camelCase). `DependencyResolver.ExtractValue` follows JsonPath.Net match-count semantics: a path with **one** match yields that match as-is (a scalar, or an entire array/object when the match itself is one — `$.items` over `{"items":[1,2,3]}` yields the array `[1,2,3]`); a path with **more than one** match (`$[*].id`, `$..id`) collects all matches into a single JSON array.
2. **Inject** — the extracted JSON fragment replaces the `@alias` placeholder in the consumer's parameter payload.
3. **Bind** — `System.Text.Json` deserializes that JSON fragment into the consumer's actual CLR parameter type, with the same options as every other call.

The fragment is **never** re-serialized through the consumer's type and back. It is the exact bytes the provider produced, fed straight into the consumer's deserializer. That is why object→object works by duck-typing (below) and why a type mismatch surfaces as a deserialization failure, not a silent conversion.

### The four outcomes

What happens at step 3 depends on the fragment's JSON kind versus the destination parameter:

| Fragment → Parameter | Runtime behavior | Response |
|---|---|---|
| **Compatible** (same kind, or object→object with overlapping properties) | Binds normally | 2xx |
| **Cross-kind scalar** (e.g. JSON-string into `int`, JSON-number into `bool`) | `System.Text.Json` throws | **400** `cannot be converted` |
| **Object → object, missing property** | STJ duck-types: overlapping properties bind case-insensitively, extra provider properties are ignored, **missing** properties are silently defaulted — value types to `0`/`false`/`DateTime.MinValue`, reference types to `null` | 2xx (silent default) |
| **Unresolved** (JsonPath matched nothing, or alias never exposed) | No value to bind | **400** `Unresolved` |

Two things to internalize:

- **There is no silent cross-kind conversion.** Trame deliberately does **not** enable `AllowReadingFromString`, so a JSON string is not coerced into a number and vice versa. That would be convenient but error-prone; a cross-kind mismatch is a hard 400 instead. Number→number widening (`int`→`long`, `int`→`double`) is fine — it stays within the JSON-number kind.
- **Object→object is the one silent case, and it is directional.** Passing a provider `{Id, Name}` into a consumer that takes `{Id}` is **safe** — the consumer is a subset, the extra `Name` is ignored, nothing is missing, no default happens. Passing a provider `{Id}` into a consumer that takes `{Id, Active}` is **dangerous**: `Active` (a value type) is silently set to `false` and the call succeeds with wrong business data. Missing *reference-type* properties default to `null`, which is usually visible quickly; missing *value-type* properties are the insidious case. This is inherent to JSON duck-typing — Trame has no schema to enforce structural equality at runtime, by design (the C# classes are the contract, discovered at runtime). The DevUI checker (below) catches it statically where both schemas are known.

**Subset fan-out is a feature, not just a hazard.** The silent-drop direction is genuinely useful: load a `Customer` once, expose the whole object as one alias (`$` → `@customer`), and feed the *same* `@customer` into several subsequent commands whose parameter types are each shaped to receive only the fields they need — a `CustomerId { public int Id; }` parameter here, a `CustomerName { public string Name; }` parameter there, a full `Customer` parameter elsewhere. Each consumer duck-types the overlapping properties; the rest silently drop. One provider fragment, many typed consumers, no per-field exposes. The one rule to know: this works because each consumer **parameter is an object type** (a class declaring the wanted property the same way the provider does). If a consumer parameter were a **bare scalar** (`int id`, `string name`), injecting the whole `{…}` object would be a cross-kind **400**, not a silent drop — for bare scalars, expose per field (`$.id` → `@cid`, `$.name` → `@cname`) instead. You just need to know which of the two you are doing.

**Don't want silent defaults? Pick a stricter mode.** The silent-default direction is the one
case weak binding swallows. Three modes are available via `TrameOptions.AliasBindingMode`:

- **Weak** (default) — duck-typed, silent defaults. The subset fan-out above relies on it.
- **Strict** — each `@alias`-sourced parameter requires **full coverage** at the top level: every public read-write property the consumer type declares must be present in the fragment, else `400`. Literals are *not* re-checked; nested objects are *not* descended into.
- **Paranoid** — Strict's two remaining gaps closed: it checks **all** parameters (including literals the caller sent) **recursively** at every depth. A missing value-type property inside a present nested object, or inside any element of a `List<T>` parameter, is a `400` rather than a silent `0`/`false`. This is the closest Trame gets to schema validation without a schema language — use it when a silent default at *any* depth is a correctness KO-criterion. It runs on every call and recurses the fragment, so it is the most expensive mode.

The safe subset direction (consumer ⊆ fragment — the fan-out above) still binds in all three
modes; only the dangerous reverse (consumer ⊋ fragment) becomes a `400`. Cross-kind is `400`
in all modes. Widening (`int`→`long`) is accepted in all modes. See
[DEPENDENCY_BINDING.md §7](DEPENDENCY_BINDING.md#7-binding-modes--weak-strict-paranoid).

Returned `TrameResponse` objects from controllers are **not** gated by `EnableDetailedErrors` — their `Code`/`Data`/`Error` pass through verbatim. Only the generic 500 path (an unexpected throw) hides details in production.

### Casing contract

.NET and JavaScript handle casing differently, and Trame sits between them. There are **three independent casing regimes**, each applying to a different part of the call:

| Regime | Applies to | Casing | Consequence |
|---|---|---|---|
| **Parameter NAME binding** | matching `params` entries to method parameters | **case-sensitive, ordinal** | `{"parameterName":"CustomerId"}` binds to `int CustomerId`, **not** to `int customerId`. Send the exact C# parameter name. |
| **Parameter VALUE properties** | JSON properties inside an object argument or result | **read case-insensitive, written camelCase** | STJ reads `{"Id":1}` and `{"id":1}` into `int Id` equally. The server *writes* results camelCase (`id`, `customerId`). |
| **JsonPath extraction** | the `.Exposes("$.…", …)` path against a result | **case-sensitive** | The path runs against the camelCase wire document, so it must use camelCase: `$.customerId`, `$.items[*].id`. A PascalCase `$.Id` matches **nothing** → `Unresolved`. |

Practical consequences for cross-language callers:

- **JS reading C# results:** works without effort. The server emits camelCase, which is what JS expects. `result.id`, `result.customerId`.
- **JS sending object arguments to C#:** works either way. STJ reads property names case-insensitively, so `{id: 1}` and `{Id: 1}` both bind to `int Id`. Send camelCase for consistency with the wire.
- **C# reading JS-sent arguments:** works either way, same reason.
- **JsonPath in `.Exposes(...)`:** must be camelCase. This is the one place case-sensitivity bites, because the path is evaluated against the already-serialized camelCase document, not against the C# property names. The DevUI suggests camelCase paths for exactly this reason.

"Why not drop case-sensitivity entirely?" is tempting but not achievable in one place: parameter *names* are matched by a case-sensitive dictionary key for dispatch determinism (Trame dispatches by name only — overloading is by distinct `[TrameMethod]` names, see [Known Limitations](#known-limitations-v1)), while *values* go through STJ's case-insensitive property matching. Making names case-insensitive would introduce ambiguity with no schema to resolve it. The two regimes are kept separate and each is internally consistent.

### DevUI static checker

The dependency builder in the developer UI (`/Trame`) is the one place that has **both** schemas — the provider's return type and the consumer's parameter type — from discovery. It runs a static type/casing/structural check (`dependencyCheck.ts`) that reproduces the runtime rules above:

- **Expose paths** are validated against the provider's return schema — a PascalCase path like `$.Id` is flagged because it will not match the camelCase wire output.
- **`@alias` bindings** are checked against the provider's exposed shape:
  - cross-kind scalar mismatch → **error** (will 400);
  - object→object with a missing *value-type* property → **warn** (silent default, the insidious case);
  - object→object with a missing *reference* property → **warn** (will be `null`);
  - object→object with a kind mismatch on an *overlapping* property → **error** (will 400);
  - object→object where the consumer is a subset of the provider → **no finding** (safe, extra provider properties are ignored);
  - array/scalar cardinality mismatch (e.g. a multi-match path into a scalar parameter) → **error**, with a fix hint (`$[0]` vs `$[*]`).

The check is **non-blocking** — "Send anyway" stays available, because the runtime shape can differ from the static schema (polymorphism, dynamic results, opaque third-party return types). It surfaces inline under the affected expose/parameter and in a summary box. For opaque return types (BCL/third-party without a `[TrameDataContract]` override) it warns that the path cannot be statically verified, rather than claiming a false green.

The checker sources its types from the structured, language-neutral `TypeRef` on the wire ([`docs/discovery-schema.md`](docs/discovery-schema.md)) — it imports the shape model (`shapeFromRef`/`returnShape`/`paramShape`/`propertyShape`) and the scalar tables from `trame-codegen`, the single source of truth, instead of re-parsing .NET type-name strings. Consequence: a `ref` to an enum now resolves to its underlying **integer** (Trame serializes enums as numbers), so an enum-typed `@alias` target is checked as a JSON number, not flagged as an opaque `unknown`.

### Binary does not flow through aliases

`byte[]` travels out-of-band (`binaryData` / `content`), never in the JSON `data` field, so a `byte[]` parameter cannot be the target of an `@alias`. Aliases carry JSON fragments only. See [Binary](#binary).

### Provider failure & dependent propagation

The sections above describe the happy path: a provider ran and exposed its fragments. The other half of the contract — what happens when the **provider itself fails** — is well-defined and **propagated**, not a crash:

| Provider outcome | Dependent's result |
|---|---|
| Succeeded and exposed the consumed alias | binds normally → `2xx` |
| Unauthorized (`401`) | `400` — `Dependency '@a' unavailable: provider '<id>' was unauthorized (401).` |
| Any other non-2xx (`400`/`404`/`500`/…) | `400` — `Dependency '@a' unavailable: provider '<id>' returned HTTP <code>.` |
| Succeeded but did not expose the alias (JsonPath matched nothing, `null`/void return) | `400` — `Dependency '@a' unavailable: provider '<id>' did not expose '@a'.` |
| No provider in the batch exposes that alias (dangling) | `400` — `Dependency '@a' unavailable: no provider exposes '@a'.` |

- **The dependent does not run.** When a provider is known to have failed or not exposed, the consumer's method is not invoked — the server short-circuits it with the explanatory `400`, naming the provider, alias, and cause, instead of letting it reach the missing alias at runtime with an uninformative `Unresolved dependencies`.
- **Propagation is transitive.** A skipped provider produces no `exposedDependencies`, so it is itself a "did not expose" case for *its* dependents, which are skipped in turn. One unauthorized provider at the root cancels the whole branch beneath it.
- **Scope.** Propagation applies on the **topological** path (auto-detected whenever any request carries a `dependencyMapping`). The **Serial** path has no providers by definition, so a dangling `@alias` there keeps the legacy `400 Unresolved dependencies: <alias>.`. **Parallel** has no providers and does not resolve `@alias` at all.

### Authorization in batches — per request, serial pre-pass

Authorization is checked **per request**, not per batch. A `401` on one request does not abort the others — each response is independent (JSON-RPC-conformant), so a batch may mix unauthenticated reads with `[TrameAuthorise]` writes and return a mixed result array. Only the dependency chain is coupled (above).

`HttpContext` is not thread-safe, yet every request in a batch shares the same incoming context. The server runs the `[TrameAuthorise]` check **serially in a pre-pass** before the parallel `Task.WhenAll` fan-out; the parallel execution never touches `HttpContext`. Auth is cheap (claims reads), so this does not regress parallel throughput. **User-code contract:** controllers that obtain the context via `IHttpContextAccessor`, and overrides of `OnAuthorization`, must treat it as **read-only** in a parallel batch (no writes to `Items`/response/request-body) — the framework's own concurrent access is eliminated by the pre-pass, but user code is the caller's responsibility. Full spec: [`DEPENDENCY_BINDING.md §9`](DEPENDENCY_BINDING.md#9-provider-failure--dependent-propagation).

## Known Limitations (v1)

Trame v1 is intentionally focused. The following are deliberate scope decisions, not bugs — documented here so adopters can plan around them:

- **No built-in API versioning.** The `controller` field is a free-form string. There is no framework-level versioning mechanism. The recommended convention is to encode the version in the controller name, e.g. `[TrameController("Customer.v1")]`, so a v2 can coexist without breaking v1 clients. Build-time version enforcement via a source generator (the `wsdl.exe` model) is planned for v1.1 — see [ROADMAP.md](ROADMAP.md).
- **No Native AOT.** Method discovery uses runtime reflection, and invocation uses `Expression.Compile`-produced delegates. This is incompatible with Native AOT trimming. Standard (JIT) publishing is the supported deployment model.
- **REST streaming is materialized.** An `IAsyncEnumerable<T>` result is fully consumed server-side and serialized as a JSON array before the REST response is sent. True streamed delivery over REST is not supported — use the WebSocket or SignalR transport for streaming semantics.
- **Attribute-based registration only.** Controllers and methods are discovered via `[TrameController]` / `[TrameMethod]` attribute scanning. Direct/fluent registration of individual handlers in code (without attributes) is on the roadmap, not in v1.
- **No parameter-based overloading.** A call is addressed by `Controller.Method` only — the parameter set is **not** part of the dispatch key, so Trame does not resolve C# overloads by signature over the wire. Two `[TrameMethod]`s on the same controller, or two `[TrameController]`s app-wide, must therefore carry distinct names; otherwise `TrameInvoker.Register` throws `InvalidOperationException` at startup rather than silently shadowing one with the other (the dispatch key is `{Controller}_{Method}`, purely name-based). Model what would be C# overloads with distinct Trame names (`add`, `addAll`, `addRange`). To exclude a controller from the auto-discovery bulk scan (e.g. a test fixture you register only by hand), set `[TrameController("name", AutoDiscover = false)]` and register it explicitly via `Register<T>()` or `TrameControllerBuilder.Add<T>()`.
- **Dotted namespaces, not URL hierarchies.** The `controller` field accepts a dotted namespace (e.g. `Customer.Address.Contact`) to express arbitrarily deep groupings, but it is a single string key — it is not translated into a nested URL path. Routing stays two-part (`controller` + `method`) at the protocol level.
- **Error details in development only.** `TrameError.details` (stack trace) is populated only when `EnableDetailedErrors` is set or the host runs in Development. In production, error messages are intentionally generic and do not leak exception internals.
- **Binary is base64 over REST and WebSocket.** Only the SignalR/MessagePack channel carries `byte[]` without base64; the WebSocket transport honors text frames only. Binary is bounded by the transport message-size limits (REST 1 MB, WebSocket 1 MB). For large or frequent binary, run a plain REST or WebSocket endpoint alongside Trame — see [Binary](#binary) and the v1.x+ plan in [ROADMAP.md](ROADMAP.md).
- **`byte[]` parameters bind first-match-only.** A method with more than one `byte[]` parameter receives `binaryData` in only the first; the others are not filled.
- **No streamed binary.** `byte[]` responses are buffered into `content`; a `ContentStream` field exists on the model but is not wired by any transport in v1.
- **C# fluent builder has no `WithBinary`.** Set `request.BinaryData` directly for binary uploads from C# until the helper lands (see [ROADMAP.md](ROADMAP.md)).

## Project Structure

| Project | Description |
|---------|-------------|
| TrameCommon | Shared models, attributes, exceptions |
| TrameCore | Invoker, discovery, dependency resolver, interceptors |
| TrameHub | SignalR transport; AddTrame() and low-level UseTrame() live in `TrameHub.Extensions` |
| TrameRest | REST transport (Minimal APIs + MVC controller) |
| TrameWebSocket | WebSocket transport (RFC 6455, JSON text frames) |
| TrameClient | Client library (REST, WebSocket, SignalR clients) |
| TrameDeveloperUi | Built-in developer web UI |
| TrameTelemetry | Optional OpenTelemetry SDK bootstrap (subscribes the `Trame` source, wires OTLP/Console exporters + AspNetCore/HttpClient instrumentation) |
| Trame | Sample application |
| TrameTests | Unit + integration tests |
| TrameBench | Performance benchmarks |

## Installation (NuGet)

```xml
<!-- Server: all transports (hub + REST + WebSocket + DevUI) -->
<PackageReference Include="Trame.Server" Version="1.0.0" />

<!-- Optional: OpenTelemetry SDK bootstrap -->
<PackageReference Include="Trame.Telemetry" Version="1.0.0" />

<!-- Or individual transports -->
<PackageReference Include="Trame.Core" Version="1.0.0" />
<PackageReference Include="Trame.Hub" Version="1.0.0" />
<PackageReference Include="Trame.Rest" Version="1.0.0" />
<PackageReference Include="Trame.WebSocket" Version="1.0.0" />

<!-- Client -->
<PackageReference Include="Trame.Client" Version="1.0.0" />
```

### Distributed Tracing

Trame instruments the engine directly in `TrameCore` via a public `ActivitySource` named **`Trame`** (`TrameCore.Tracing.TrameTracing.ActivitySourceName`). The instrumentation is always on but cost-neutral — `ActivitySource.StartActivity` returns `null` when nothing subscribes, so there is no allocation or bookkeeping unless a listener (the OTel SDK or a custom `ActivityListener`) is attached. No NuGet dependency is added to `TrameCore`/`TrameServer` for this; `System.Diagnostics` is in-box for .NET 8.

Spans emitted (OTel RPC semantic conventions):

- **`TrameCall`** — one per request, started in both the single-call path (`InvokeDi(TrameRequest)`) and the per-request batch path (`ExecuteSingleInvocation`). Tags: `rpc.system=trame`, `rpc.service`, `rpc.method`, `trame.request_id` (only when `Id` is non-empty), `trame.binary.length` (only when `BinaryData` is non-empty). Status set from the response (`Ok` when `IsSuccess`, else `Error` with `Error.Message`). On an escaping exception the catch adds `exception.type`/`exception.message`/`exception.stacktrace` (via `TrameTracing.RecordException`, which replaces `Activity.RecordException` — the extension is not resolvable in a net8.0 class library).
- **`TrameBatch`** — one per batch, started in `InvokeDi(IEnumerable<TrameRequest>)`. Tags: `rpc.system=trame`, `trame.batch.mode` (`Parallel`/`Serial`, or `DependencyBatches` when auto-detect routes to topological execution), `trame.batch.count`. The per-request `TrameCall` spans are children via `Activity.Current` parenting — a single batch yields one parent + N children.

`Trame.Telemetry` is the optional package that boots the OTel SDK: `AddTrameTelemetry` calls `AddOpenTelemetry().WithTracing(b => b.AddSource("Trame") …)` with configurable service name, OTLP/Console exporter, and AspNetCore/HttpClient instrumentation gates. `TrameServer` does **not** reference it, keeping the OTel SDK dependencies out of the all-in-one bundle. Consumers who need custom samplers/resources/exporters skip `AddTrameTelemetry` and call `AddOpenTelemetry().WithTracing(b => b.AddSource("Trame"))` directly — the source name is the only integration point.

## Requirements

- .NET 8.0+
- ASP.NET Core 8.0+

## License

MIT