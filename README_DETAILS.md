# Sleipnir

> **Detailed reference.** For the landing page, see [README.md](README.md).

**Sleipnir** is a code-first, multi-transport framework for **command-oriented web APIs** on .NET 8+. Your C# classes *are* the contract; discovery metadata is generated at runtime — no `.proto`, no IDL. The same call runs over REST, WebSocket, or SignalR, and is consumable from any language, not .NET only.

What sets Sleipnir apart is **dependency chaining**: send several calls in a single roundtrip and let later calls reuse results from earlier ones. One request exposes a value via `Exposes("$", "newId")` (a result-relative JSON path), the next consumes it as `WithAlias("@newId")`, and the server resolves the `@alias` placeholders against prior responses — no client-side glue code, no extra roundtrips. A workflow that needs a customer's new ID to create their order completes in one roundtrip instead of three.

> **The name.** *Sleipnir* — Odin's eight-legged horse in Norse mythology, who carries the god across all nine realms in a single stride. A multi-transport metaphor: one framework bearing commands across REST, WebSocket, and SignalR, with chained calls completing in a single roundtrip. (Named to match the sibling projects Walhalla and Heimdall.)

[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

---

## Resources, Commands, and Sleipnir's direction

REST is so much *the* standard that "wrong" is the wrong word for it — it simply isn't shaped for command calls. For resource-oriented APIs, CRUD on nouns like `/customers/42` or `/orders` is exactly right. **Are REST, gRPC, or GraphQL wrong, then? No — each was built for a shape of API it handles well.** Sleipnir simply takes a different direction: a *command-oriented* web API where the following come up naturally:

- **Verb-noun mismatch**: `POST /api/customer/42/add-address` isn't RESTful, but it's what a command-oriented API actually needs
- **N+1 roundtrips for dependent data**: a customer, then their orders, then each order's items is 3 sequential HTTP roundtrips — the client waits on each before sending the next
- **No batching**: 10 method calls means 10 separate HTTP requests and connections
- **No dependency resolution**: when call A returns an ID that call B needs, the client manually parses A, extracts the ID, and passes it to B — every time

GraphQL addresses composition with a schema, resolvers, and a separate type system — and does it well, at the cost of that schema-first layer.

None of this is a knock on REST. Its ubiquity forced everyone — for a generation — to think in terms of a *web API*: resources, verbs, status codes, content negotiation, stateless scaling. That discipline was extremely helpful and is now second nature. The friction above is what shows up when that same resource-oriented shape is pressed into *command-oriented* work.

Sleipnir doesn't strip the web API away — it keeps it. Calls travel over HTTP with JSON, through standard ASP.NET Core middleware, and the contract is consumable from any language, not just .NET. To honor that web-API layer rather than treat it as plumbing, Sleipnir ships **runtime discovery** (`GET /api/sleipnir/discovery` returns the full contract: controllers, methods, parameter schemas, documentation, examples) and a **developer UI** that lets you browse and try calls in the browser. That makes Sleipnir a discoverable, language-neutral, command-oriented web API — not a closed channel between two .NET ends. What it drops is only the resource orientation: commands stop having to masquerade as nouns.

## Sleipnir vs gRPC — a different direction

gRPC is a strong, sensible choice for RPC on .NET. Sleipnir isn't a rejection of it — it takes a different direction on three axes:

1. **History.** When Sleipnir started, gRPC-Web (for browsers) required a proxy, native browser support was missing, and the .NET tooling was still immature. That's no longer true today; it's the historical reason Sleipnir exists.
2. **Code-first, not schema-first.** gRPC requires `.proto` files, a code generator, and a separate build step — you write the schema in a DSL, generate C# stubs, then implement them. That's a deliberate, well-justified choice in gRPC. Sleipnir goes the other way: in .NET your C# classes *already are* the schema, so it keeps a single definition and generates discovery metadata at runtime.
3. **Transport choice.** gRPC standardizes on one wire — HTTP/2 + Protobuf, great for server-to-server — and reaches browsers via gRPC-Web. Sleipnir lets the *same* call run over REST, WebSocket, or SignalR, so you pick the transport per deployment, not per call.

Sleipnir is **code-first**: you write C# classes and methods, decorate them with attributes, and the framework handles the rest. No `.proto` files, no code generation, no separate schema. Discovery metadata is generated at runtime from your code.

## How Does Sleipnir Compare to JSON-RPC?

[JSON-RPC 2.0](https://www.jsonrpc.org/) is the closest spiritual ancestor to Sleipnir — both are method-oriented, not resource-oriented, and both use JSON. So why not just use JSON-RPC?

| | JSON-RPC 2.0 | **Sleipnir** |
|---|---|---|
| Method-oriented calling | ✅ | ✅ |
| Batch calls | ✅ (JSON array) | ✅ |
| Dependency-chaining | ❌ calls are independent | ✅ `@alias` resolution |
| Type contracts | ❌ untyped `params` | ✅ signature inference (`[SleipnirDataContract]` optional) |
| API discovery | ❌ (needs OpenRPC) | ✅ built-in `/api/sleipnir/discovery` |
| Documentation | ❌ (external) | ✅ `[SleipnirDocumentation]` + `[SleipnirExample]` (class/method level in v1) |
| Streaming | ❌ | ✅ `IAsyncEnumerable<T>` |
| Authorization | ❌ (transport-level) | ✅ `[SleipnirAuthorise]` |
| Error model | ✅ basic (`code` + `message`) | ✅ `SleipnirError` with details + requestId |
| .NET integration | partial (StreamJsonRpc & libs) | ✅ native ASP.NET Core middleware |
| Transport definitions | ❌ (spec is format-only) | ✅ REST + WebSocket + SignalR defined |
| Pre-compiled invocation | ❌ (reflection) | ✅ Expression Trees |

**In short**: JSON-RPC defines a *wire format*, Sleipnir is a *framework*. JSON-RPC tells you how to format the JSON, but doesn't help you discover methods, resolve dependencies, stream results, authorize calls, or integrate with .NET DI. Sleipnir was built without knowing JSON-RPC existed, but ended up solving a similar core problem — a command-oriented web API on .NET — with a different emphasis: discovery, dependency chaining, streaming, authorization, and DI integration built into the framework rather than left to the caller.

## Why Sleipnir?

| | REST | gRPC | GraphQL | JSON-RPC | **Sleipnir** |
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

> **On transport, honestly:** GraphQL is not HTTP-only — the spec (and Hot Chocolate on .NET) supports subscriptions over WebSocket, and gRPC reaches browsers via gRPC-Web. The distinction is *what the transports carry*. In GraphQL, HTTP serves queries/mutations and WebSocket serves subscriptions — different operation types on different channels. In Sleipnir the **same** call runs over REST, WebSocket, or SignalR with identical wire semantics; you pick the transport per deployment, not per operation. Sleipnir's actual differentiator is **dependency chaining** (above), not multi-transport.

Sleipnir was designed from the ground up as a **command-oriented web API** — not a REST wrapper, but a proper framework for command-oriented APIs that happens to support REST as one of several transports:

1. **Method-oriented, not resource-oriented**: `[SleipnirController("Customer")]` + `[SleipnirMethod("AddOrder")]` — no URL routing, no HTTP verbs to choose, no DTO mapping
2. **Batch calls**: Send 10 calls in one roundtrip — the server executes them and returns 10 responses
3. **Dependency chaining** (GraphQL-inspired): One request exposes values via `Exposes("$", "orderId")` (result-relative JSON path), the next uses `WithAlias("@orderId")` — the server resolves dependencies in a single roundtrip. A wildcard path `Exposes("$[*].id", "ids")` collects **all** matches into an array and injects it as one list-typed parameter — `Search → GetByIds(@ids)` in a single roundtrip, like a server-side `Select`
4. **Multi-transport**: REST for compatibility, WebSocket for low-latency persistent connections, SignalR for browser clients — same API, different wire protocols
5. **Expression-Tree invocation**: Methods are pre-compiled to delegates at registration time — no reflection per call

| | REST | gRPC | SignalR | **Sleipnir** |
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
| Scenario | REST | Sleipnir REST | Sleipnir WebSocket | Speedup |
|----------|------:|----------:|---------------:|--------:|
| Single Call (GetCustomerById) | 606 us | 627 us | **267 us** | **2.3x** |
| Batch 10x (parallel) | 4,906 us | 974 us | **693 us** | **7.1x** |
| Dependency-Chain (3 calls, 1 roundtrip) | 206 ms | 205 ms | **204 ms** | replaces 3 roundtrips |

> Full benchmarks: see SleipnirBench/BENCHMARK-RESULTS.md

## Quick Start

### Server

```csharp
using SleipnirHub.Extensions;
using SleipnirServer;

var builder = WebApplication.CreateBuilder(args);

// Register Sleipnir with all transports (hub + REST + WebSocket + DevUI)
builder.Services.AddSleipnir(o =>
{
    o.UseSignalR = true;
    o.UseMessagePack = true;
    o.MaximumParallelInvocationsPerClient = 100;
});

var app = builder.Build();
app.UseSleipnirTransports();   // WebSocket + controller registration
app.MapSleipnir();             // REST (/api/sleipnir) + DevUI (/Sleipnir) + SignalR hub (/sleipnirhub)
app.Run();
```

### Controller

```csharp
using SleipnirCore.Attributes;

[SleipnirController("Customer")]
public class CustomerHandler(CustomerService service)
{
    [SleipnirMethod("GetById")]
    public async Task<Customer?> GetById(int id, CancellationToken ct)
        => await service.GetById(id, ct);

    [SleipnirMethod("Create")]
    [SleipnirAuthorise]  // Optional: requires authentication
    public async Task<int> Create(string name)
        => await service.Add(name);
}
```

### Client

```csharp
using SleipnirClient.Sleipnir;
using SleipnirCommon.Models;

// Choose your transport
var client = new SleipnirRestJsonClient("https://localhost:5001/");
// or: new SleipnirWebSocketClient("https://localhost:5001/")
// or: new SleipnirSignalrClient("https://localhost:5001/")

// Single call
var request = SleipnirCall.Init("Customer", "GetById")
    .With(42)
    .ToRequest();
var response = await client.Call(request);

// Typed call
var customer = await client.Call<Customer>(request);

// Batch with dependency chaining
var batch = new SleipnirMultiRequest
{
    Mode = ExecutionMode.Serial,
    Requests = new List<SleipnirRequest>
    {
        SleipnirCall.Init("Customer", "Create")
            .With("Alice").Named("step1")
            .Exposes("$", "newId")
            .ToRequest(),
        SleipnirCall.Init("Customer", "GetById")
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
| **REST** | POST /api/sleipnir/json | Stateless calls, load-balanced scenarios |
| **WebSocket** | ws://host/sleipnirws | Persistent connections, low latency, many small calls |
| **SignalR** | /sleipnirhub hub | Browser clients, auto-reconnect, MessagePack binary |

## Developer UI

`MapSleipnir()` serves a built-in single-page web UI at **`/Sleipnir`** (configurable via the `developerUiPath` argument). It is the working console over the runtime discovery — the thing Swagger gives you only fleetingly, here kept across sessions. No backend of its own: it talks to the same `/api/sleipnir` endpoints as any other client, so everything you can do in the UI you can do from code.

**Layout.** Three resizable panes (persisted in `localStorage`): a left *Explorer* (Discovery tree of controllers + methods, collapsible, and the full type tree below a horizontal splitter), a center *Editor*, and a right *Result*. Plus a top bar (connection, auth, codegen, dependency builder, history, refresh, theme) and a collapsible history panel along the bottom.

**Explorer.** Browses the live discovery: controllers are collapsible with method counts, a filter narrows controllers/methods by name, and the Types tree expands each discovered contract into its properties. Clicking a method opens a tab.

**Tabs.** Every open call is a tab that keeps its parameters, the request JSON (kept in sync with the parameter form), the last response, the call log, status, and duration. Tabs are plain JSON and persist across reloads (`sleipnir-tabs`). Three tab kinds:

- **Request** — a single method call with a parameter form + raw JSON editor.
- **Dependency Builder** — a visual composer for serial `@alias` batches: each step is a `SleipnirRequest`, exposes become `dependencyMapping`, alias-params become `@alias` placeholders. The one place that has both the provider return schema and the consumer parameter schema (from discovery), so it runs a **static checker** — see [DevUI static checker](#devui-static-checker).
- **Codegen** — emits TypeScript and C# client code for the current call or batch, copy-to-clipboard.

**History.** The last 100 calls (request, response, duration, error) live in `sleipnir-history` and show in a collapsible bottom panel. Replay is one click.

**Connection & Auth.** Base URL + API path (defaults to same-origin `/` / `api/sleipnir` when embedded; empty in the standalone build so you point at a remote server). Bearer token set via the Auth panel — applied to every call (Discovery, single, batch) through the client facade, stored in `sleipnir-bearer`, never exported.

**Workspace snapshots.** The full workset is serializable: ⚙ → Export writes a JSON snapshot with **connection + open tabs + active tab + theme + split layout + history** to a file named `sleipnir-workspace-YYYY-MM-DD-HHMM.json`. On Chromium (Edge/Chrome) the native save dialog lets you pick folder and filename; Firefox/Safari fall back to a plain download. ⚙ → Import restores it **live** — theme, split sizes, tabs, and history come back without a reload (the connection change re-fetches discovery). Version 2 format; version 1 snapshots (connection + tabs only) still import. The Bearer token is deliberately **not** part of the snapshot — after import, the currently set token stays active.

**Standalone build.** `npm run build:standalone` (in `SleipnirDeveloperUi/`) emits `dist-standalone/` with relative asset paths — deploy it to any static host (GitHub Pages, a CDN, a local `serve`) and point the Connection setting at a remote Sleipnir server. CORS must be open on the target server for the DevUI's origin. Useful for trying a server without standing up the .NET host, or for hosting a shared console against a known endpoint.

> The DevUI ships as the `SleipnirDeveloperUi` project (see [Project Structure](#project-structure)) and is part of the `Sleipnir.Server` package — no extra install.

## Features

- **Multi-Transport**: Same API over REST, WebSocket, and SignalR
- **Batch-Calls**: Execute multiple calls in a single roundtrip (parallel or serial)
- **Dependency-Chaining**: Chain calls with @alias placeholders resolved server-side; wildcard paths (`$[*].Id`) fan a result set into a single list-typed parameter
- **Streaming**: IAsyncEnumerable<T> support, serialized as JSON arrays
- **Interceptor-Pipeline**: ISleipnirInterceptor for logging, tracing, caching, validation
- **Discovery/MEX**: GET /api/sleipnir/discovery returns full API metadata
- **Developer-UI**: Built-in web UI at `/Sleipnir` for browsing, testing, batch-building, codegen, and saving whole worksets — see [Developer UI](#developer-ui)
- **Rate-Limiting**: Built-in fixed-window rate limiter
- **Authorization**: [SleipnirAuthorise] attribute with role-based access
- **Expression-Tree Invocation**: Pre-compiled method delegates for maximum performance

## Binary

A method may take a `byte[]` parameter or return `byte[]`. The bytes travel **out of band from the JSON `data` field** — request bytes in `binaryData`, response bytes in `content` — so they never compete with structured arguments and are never duplicated into `data`. The wire encoding depends on the transport:

| Transport | `binaryData` (request) | `content` (response) |
|---|---|---|
| REST (JSON) | base64 in JSON | base64 in JSON |
| WebSocket (JSON text frames) | base64 in JSON | base64 in JSON |
| SignalR (MessagePack) | native MessagePack `bin` | native MessagePack `bin` |

Base64 is avoided **only on the SignalR/MessagePack channel**. REST and WebSocket carry JSON text, so binary is base64-encoded there (~33 % overhead), bounded by the message-size limits. The WebSocket transport deliberately accepts only text frames — native binary frames are not honored.

**Limits:** REST 1 MB request body; WebSocket 1 MB per message (hardcoded); SignalR `MaximumReceiveMessageSize` in `SleipnirOptions` (defaults to SignalR's limit when unset). The cardinality caps `MaxParameterArrayLength` / `MaxResultElementCount` do not apply to `byte[]` — they guard collection sizes, not binary size.

**Practical guidance:** Binary over REST or WebSocket is fine for payloads up to the limits. For large or frequent binary, run a plain REST or WebSocket endpoint alongside Sleipnir — there is nothing about binary that requires the RPC channel. Streamed/chunked binary upload and a streamed `byte[]` response are not in v1; see [ROADMAP.md](ROADMAP.md) for the v1.x+ binary-transfer plan.

**Client support:** TypeScript ships `withBinary(Uint8Array)` and `callBinary()`; SignalR clients (C# + TS) carry `byte[]` natively. The C# REST/WebSocket client offers `CallBinary()` for responses, but the fluent builder has **no `WithBinary` yet** — set `request.BinaryData` directly until it lands. See [PROTOCOL.md](PROTOCOL.md) for exact field semantics.

## Serving Media & Non-RPC Resources (images, video, downloads)

Sleipnir is a **command-oriented RPC framework**: a method is a command, the call is `POST` + JSON, the result is a typed JSON contract. Media — images, video, file downloads, generated PDFs — is **resource-oriented**: a browser-fetchable `GET` URL, cacheable, rangeable, CDN-friendly. These are two different shapes, and Sleipnir serves media by **co-hosting a plain HTTP endpoint next to the RPC channel**, not by putting raw bytes in the RPC envelope. This is the intended split, not a gap:

> **Sleipnir = authority** — metadata, permission, business logic, and *which URL* the resource lives at.
> **HTTP / CDN = delivery** — the raw bytes, streamed, with the right `Content-Type`, `ETag`, `Range`, and cache headers.

### Not a second framework

`app.MapGet(...)` beside `app.MapSleipnirEndpoints()` is **one ASP.NET host, one process, one DI container, one auth pipeline**. Sleipnir *is* a set of endpoints on that host; a Minimal API `GET` is another set on the same host. You are not introducing a second runtime — you are using the host you already have. (This is the same relationship gRPC has with its HTTP/2 host: nobody calls that "two frameworks.") See [Sleipnir + REST — a complement](README.md#sleipnir--rest--not-a-replacement-a-complement).

### The pattern

One shared service does the work; the Sleipnir controller is the authority (permission + the URL), the `GET` endpoint is the delivery.

```csharp
// Shared domain service — the single source of truth.
public class AvatarService(IUserPermissions perms)
{
    public async Task<bool> CanViewAsync(int viewer, int userId, CancellationToken ct)
        => await perms.CanViewAsync(viewer, userId, ct);

    public (Stream Stream, string ContentType) OpenAvatar(int userId)
        => (File.OpenRead($"./avatars/{userId}.png"), "image/png");
}

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSleipnir(...);
builder.Services.AddScoped<AvatarService>();
// Standard ASP.NET auth (e.g. JWT bearer) — applies to BOTH the Sleipnir channel
// and the media endpoint, so governance (auth, rate limit, tracing) is unified.
builder.Services.AddAuthentication().AddJwtBearer(...);
builder.Services.AddAuthorization();

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

// Sleipnir = authority: the command returns the resource URL (and gates permission
// in the RPC flow). The client learns *where* the media is from the typed call.
app.MapSleipnirEndpoints();

// Delivery: a browser-fetchable GET URL that streams the bytes with a real
// Content-Type. RequireAuthorization shares the same auth pipeline as Sleipnir.
app.MapGet("/avatars/{userId}.png",
    async (int userId, AvatarService svc, HttpContext ctx, CancellationToken ct) =>
{
    if (!await svc.CanViewAsync(ctx.User.GetUserId(), userId, ct))
        return Results.Forbid();
    var (stream, contentType) = svc.OpenAvatar(userId);
    return Results.Stream(stream, contentType);   // chunked, correct Content-Type
}).RequireAuthorization();

app.Run();
```

```csharp
[SleipnirController("User")]
public class UserController(AvatarService _avatars)
{
    [SleipnirMethod("AvatarUrl")]
    [SleipnirAuthorise]
    public string? GetAvatarUrl(int userId)
        => _avatars.CanViewAsync(...) is false ? null : $"/avatars/{userId}.png";
}
```

Client side: the typed call gives the URL; the browser fetches the bytes — `<img src={await client.user.avatarUrl(42)}>`. Put a CDN or a static-file store in front of `/avatars/*` to scale media off the app server entirely; Sleipnir stays the authority that hands out the reference.

### Why not `byte[]` / a raw return inside the RPC envelope

The RPC channel is the wrong shape for media, on every axis:

| Need | RPC envelope (`byte[]` / `content`) | Co-hosted `GET` endpoint |
|---|---|---|
| `<img src="…">` / `<video src="…">` | ✗ browsers cannot `GET` a `POST` | ✓ native |
| Wire cost | base64 in JSON (~33 % overhead) | raw bytes |
| Size | REST 1 MB / WS 1 MB message cap | unlimited (streamed/chunked) |
| Range / `206 Partial Content` (video seeking) | ✗ | ✓ `Results.Stream` + range headers |
| `ETag` / `304 Not Modified` / cache | ✗ RPC responses are non-cacheable | ✓ CDN + conditional GET |
| `Content-Type` / `Content-Disposition` | ✗ JSON envelope | ✓ native headers |
| Transport symmetry (REST/WS/SignalR identical) | ✓ but useless for media | n/a (REST is correct for browser media) |

`byte[]` in the envelope is fine for **small binary inside a call** (a thumbnail embedded in a result, a signed hash, a small file returned from a command). For anything a browser fetches as media, use the co-hosted `GET`.

### What is deliberately *not* in v1 (the boundary)

Sleipnir does **not** offer a `[SleipnirMethod]` that returns raw bytes as a browser `GET`, nor `SleipnirResults.Raw` / `.File` / `.Stream`, nor an auto-generated media route in discovery/codegen. This is a **deliberate boundary**, not an oversight:

- It would require a **second dispatcher verb** (`GET` + query-param binding) alongside the `POST` + JSON command path.
- It would create **transport asymmetry**: WebSocket/SignalR cannot serve browser media (`<img src>` is HTTP), so a "raw return" method would mean different contracts per transport — the one invariant Sleipnir otherwise preserves (all transports speak the same JSON contract).
- It opens the **HTTP-semantics slope**: a media endpoint without `Range` / `ETag` / `304` / `Cache-Control` is a toy for real frontends, and each header pulls in the next.
- Media delivery is **operationally better served** by a CDN/static store in front of a plain `GET` than by an RPC framework acting as a file server.

If you need media to appear inside the **typed/codegen client surface** (discovery + a generated `DownloadAsync`), that is a future *resource pillar* — explicitly experimental, not in v1. Track it in [ROADMAP.md](ROADMAP.md). Until then, the co-hosted `GET` pattern above is the supported way to serve media from a Sleipnir application.

## Cross-Platform

Sleipnir's wire protocol is fully specified in [PROTOCOL.md](PROTOCOL.md) — JSON-based, no binary dependency. This enables implementations in any language:

- **C#**: A first-class client ships as the `Sleipnir.Client` NuGet package — REST, WebSocket, and SignalR behind one `ISleipnirClient` surface, fluent `SleipnirCall` builder, batches, dependency chaining, binary. See [`SleipnirClient/README.md`](SleipnirClient/README.md).
- **TypeScript/JavaScript**: A ready-to-use isomorphic client ships in [`clients/ts/`](clients/ts/) (`npm i sleipnir-client`) — REST + WebSocket, fluent + functional API, runs in the browser and Node.js. See [`clients/ts/README.md`](clients/ts/README.md).
- **Python**: Use the REST endpoint with `requests`, or `websockets` library
- **Go/Rust**: Any HTTP or WebSocket client works

The `/api/sleipnir/discovery` endpoint returns full type metadata, enabling auto-generated clients.

> The wire format and a minimal hand-rolled example are documented in [PROTOCOL.md](PROTOCOL.md); for real use prefer the `clients/ts/` package.

## Dependency Chaining — Binding, Types & Casing

> This is the user-facing overview. The **dedicated, precise specification** of the
> provider→consumer JSON mapping is [`DEPENDENCY_BINDING.md`](DEPENDENCY_BINDING.md),
> and the executable spec is
> [`SleipnirTests/Unit/Core/AliasBindingTests.cs`](SleipnirTests/Unit/Core/AliasBindingTests.cs).

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

- **There is no silent cross-kind conversion.** Sleipnir deliberately does **not** enable `AllowReadingFromString`, so a JSON string is not coerced into a number and vice versa. That would be convenient but error-prone; a cross-kind mismatch is a hard 400 instead. Number→number widening (`int`→`long`, `int`→`double`) is fine — it stays within the JSON-number kind.
- **Object→object is the one silent case, and it is directional.** Passing a provider `{Id, Name}` into a consumer that takes `{Id}` is **safe** — the consumer is a subset, the extra `Name` is ignored, nothing is missing, no default happens. Passing a provider `{Id}` into a consumer that takes `{Id, Active}` is **dangerous**: `Active` (a value type) is silently set to `false` and the call succeeds with wrong business data. Missing *reference-type* properties default to `null`, which is usually visible quickly; missing *value-type* properties are the insidious case. This is inherent to JSON duck-typing — Sleipnir has no schema to enforce structural equality at runtime, by design (the C# classes are the contract, discovered at runtime). The DevUI checker (below) catches it statically where both schemas are known.

**Subset fan-out is a feature, not just a hazard.** The silent-drop direction is genuinely useful: load a `Customer` once, expose the whole object as one alias (`$` → `@customer`), and feed the *same* `@customer` into several subsequent commands whose parameter types are each shaped to receive only the fields they need — a `CustomerId { public int Id; }` parameter here, a `CustomerName { public string Name; }` parameter there, a full `Customer` parameter elsewhere. Each consumer duck-types the overlapping properties; the rest silently drop. One provider fragment, many typed consumers, no per-field exposes. The one rule to know: this works because each consumer **parameter is an object type** (a class declaring the wanted property the same way the provider does). If a consumer parameter were a **bare scalar** (`int id`, `string name`), injecting the whole `{…}` object would be a cross-kind **400**, not a silent drop — for bare scalars, expose per field (`$.id` → `@cid`, `$.name` → `@cname`) instead. You just need to know which of the two you are doing.

**Don't want silent defaults? Pick a stricter mode.** The silent-default direction is the one
case weak binding swallows. Three modes are available via `SleipnirOptions.AliasBindingMode`:

- **Weak** (default) — duck-typed, silent defaults. The subset fan-out above relies on it.
- **Strict** — each `@alias`-sourced parameter requires **full coverage** at the top level: every public read-write property the consumer type declares must be present in the fragment, else `400`. Literals are *not* re-checked; nested objects are *not* descended into.
- **Paranoid** — Strict's two remaining gaps closed: it checks **all** parameters (including literals the caller sent) **recursively** at every depth. A missing value-type property inside a present nested object, or inside any element of a `List<T>` parameter, is a `400` rather than a silent `0`/`false`. This is the closest Sleipnir gets to schema validation without a schema language — use it when a silent default at *any* depth is a correctness KO-criterion. It runs on every call and recurses the fragment, so it is the most expensive mode.

The safe subset direction (consumer ⊆ fragment — the fan-out above) still binds in all three
modes; only the dangerous reverse (consumer ⊋ fragment) becomes a `400`. Cross-kind is `400`
in all modes. Widening (`int`→`long`) is accepted in all modes. See
[DEPENDENCY_BINDING.md §7](DEPENDENCY_BINDING.md#7-binding-modes--weak-strict-paranoid).

Returned `SleipnirResponse` objects from controllers are **not** gated by `EnableDetailedErrors` — their `Code`/`Data`/`Error` pass through verbatim. Only the generic 500 path (an unexpected throw) hides details in production.

### Casing contract

.NET and JavaScript handle casing differently, and Sleipnir sits between them. There are **three independent casing regimes**, each applying to a different part of the call:

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

"Why not drop case-sensitivity entirely?" is tempting but not achievable in one place: parameter *names* are matched by a case-sensitive dictionary key for dispatch determinism (Sleipnir dispatches by name only — overloading is by distinct `[SleipnirMethod]` names, see [Known Limitations](#known-limitations-v1)), while *values* go through STJ's case-insensitive property matching. Making names case-insensitive would introduce ambiguity with no schema to resolve it. The two regimes are kept separate and each is internally consistent.

### DevUI static checker

The dependency builder in the developer UI (`/Sleipnir`) is the one place that has **both** schemas — the provider's return type and the consumer's parameter type — from discovery. It runs a static type/casing/structural check (`dependencyCheck.ts`) that reproduces the runtime rules above:

- **Expose paths** are validated against the provider's return schema — a PascalCase path like `$.Id` is flagged because it will not match the camelCase wire output.
- **`@alias` bindings** are checked against the provider's exposed shape:
  - cross-kind scalar mismatch → **error** (will 400);
  - object→object with a missing *value-type* property → **warn** (silent default, the insidious case);
  - object→object with a missing *reference* property → **warn** (will be `null`);
  - object→object with a kind mismatch on an *overlapping* property → **error** (will 400);
  - object→object where the consumer is a subset of the provider → **no finding** (safe, extra provider properties are ignored);
  - array/scalar cardinality mismatch (e.g. a multi-match path into a scalar parameter) → **error**, with a fix hint (`$[0]` vs `$[*]`).

The check is **non-blocking** — "Send anyway" stays available, because the runtime shape can differ from the static schema (polymorphism, dynamic results, opaque third-party return types). It surfaces inline under the affected expose/parameter and in a summary box. For opaque return types (BCL/third-party without a `[SleipnirDataContract]` override) it warns that the path cannot be statically verified, rather than claiming a false green.

The checker sources its types from the structured, language-neutral `TypeRef` on the wire ([`docs/discovery-schema.md`](docs/discovery-schema.md)) — it imports the shape model (`shapeFromRef`/`returnShape`/`paramShape`/`propertyShape`) and the scalar tables from `sleipnir-codegen`, the single source of truth, instead of re-parsing .NET type-name strings. Consequence: a `ref` to an enum now resolves to its underlying **integer** (Sleipnir serializes enums as numbers), so an enum-typed `@alias` target is checked as a JSON number, not flagged as an opaque `unknown`.

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

Authorization is checked **per request**, not per batch. A `401` on one request does not abort the others — each response is independent (JSON-RPC-conformant), so a batch may mix unauthenticated reads with `[SleipnirAuthorise]` writes and return a mixed result array. Only the dependency chain is coupled (above).

`HttpContext` is not thread-safe, yet every request in a batch shares the same incoming context. The server runs the `[SleipnirAuthorise]` check **serially in a pre-pass** before the parallel `Task.WhenAll` fan-out; the parallel execution never touches `HttpContext`. Auth is cheap (claims reads), so this does not regress parallel throughput. **User-code contract:** controllers that obtain the context via `IHttpContextAccessor`, and overrides of `OnAuthorization`, must treat it as **read-only** in a parallel batch (no writes to `Items`/response/request-body) — the framework's own concurrent access is eliminated by the pre-pass, but user code is the caller's responsibility. Full spec: [`DEPENDENCY_BINDING.md §9`](DEPENDENCY_BINDING.md#9-provider-failure--dependent-propagation).

## Server-Push Events (IObservable<T>)

Alongside request/response **calls**, Sleipnir supports server→client **push** via `IObservable<T>`
event methods. The server declares an event; clients subscribe to it over WebSocket and receive a
stream of typed values until they unsubscribe or the source completes. **WebSocket-only in v1**
(no REST, no SignalR event surface). The full wire spec is in [`PROTOCOL.md`](PROTOCOL.md) →
"Server-Push Events"; the design rationale is in [`docs/design/phase-3-events.md`](docs/design/phase-3-events.md).

> **Status:** experimental in v1 (`STABILITY.md` §2). The wire format, subscription lifecycle, and
> backpressure may settle in a minor version. `Last-Event-Id` resume and a server-side disconnect
> buffer are deferred to v1.x+.

### Events vs. calls vs. streams

| Surface | Direction | Bounded? | Marker | Transport |
|---------|-----------|----------|--------|-----------|
| **Call** | request/response | one result (or batch) | `[SleipnirMethod]` | REST, WS, SignalR |
| **Stream** | response, many items | finite — completes with the call | `[SleipnirMethod]` + `IAsyncEnumerable<T>` | WS, SignalR (REST materializes) |
| **Event** | server→client push | **unbounded** until unsubscribe/complete | `[SleipnirEvent]` + `IObservable<T>` | WS only |

A stream is *one call that yields many elements then ends*; an event is *subscribe once, receive
push values indefinitely*. Events are **not chainable** — there is no single result to feed into
`exposes`/`@alias`, so they cannot participate in dependency chains.

### Server: declare an event

Mark a method with `[SleipnirEvent("name")]` (the wire name, analogous to `[SleipnirMethod]`) and
return `IObservable<T>`. The method's parameters are **first-class subscription parameters** —
bound once at subscribe time, not per event.

```csharp
using SleipnirCore.Attributes;

[SleipnirController("Chat")]
public class ChatController(IChatService service)
{
    // A request/response call, unchanged.
    [SleipnirMethod("SendMessage")]
    public Task<Message> SendMessage(int chatId, string text, CancellationToken ct)
        => service.SendAsync(chatId, text, ct);

    // A server-push event. Returns IObservable<T>; chatId is the subscription parameter
    // ("push me every message in chat 42"). Auth runs at subscribe time like any call.
    [SleipnirAuthorise]
    [SleipnirEvent("MessageReceived")]
    public IObservable<Message> MessageReceived(int chatId, CancellationToken ct)
        => service.SubscribeMessages(chatId, ct);
}
```

Rules enforced **at registration** (fail-loud, like method-name uniqueness):

- The method must return `IObservable<T>` **directly** — not `Task<IObservable<T>>`. (Produce the
  observable asynchronously *inside* the method if needed; return it synchronously.)
- `[SleipnirEvent]` and `[SleipnirMethod]` are mutually exclusive on one method — use exactly one.
- An `IObservable<T>` method marked `[SleipnirMethod]` is rejected with a migration message
  ("use `[SleipnirEvent]` for server-push events"). A `[SleipnirEvent]` method not returning
  `IObservable<T>` is likewise rejected.
- The event name shares the `{Controller}_{name}` dispatch namespace with call names; a collision
  (two events, or an event and a call, with the same name on one controller) throws at startup.

`[SleipnirAuthorise]` / `[SleipnirAnonymous]` gate the **subscribe** request through the same auth
interceptor as calls. A plain call (`InvokeDi`) to an event method returns `400` with an
actionable message ("… is a server-push event; use `kind:\"subscribe\"`") rather than attempting
to serialize the observable.

### Client: subscribe (C# WebSocket client)

`SleipnirWebSocketClient.SubscribeAsync<T>(controller, method, args)` sends the subscribe request,
awaits the `subscriptionId`, and returns a `SleipnirSubscription<T>` — which **is an
`IObservable<T>`**. Consume it with any `IObserver<T>` (your own, or `System.Reactive` if you
prefer). Disposing the subscription sends `unsubscribe`.

```csharp
using var client = new SleipnirWebSocketClient(new Uri("ws://localhost:5000/sleipnirws"));

// Subscribe to the "MessageReceived" event for chat 42.
using var subscription = await client.SubscribeAsync<Message>(
    controller: "Chat",
    method:    "MessageReceived",
    args:      new object?[] { 42 });

// Consume the push stream. Each OnNext is one event payload.
var cts = new CancellationTokenSource();
using var observerRegistration = subscription.Subscribe(
    onNext:      msg => Console.WriteLine($"#{msg.Id} {msg.From}: {msg.Text}"),
    onError:     ex  => Console.WriteLine($"subscription error: {ex.Message}"),
    onCompleted: ()  => Console.WriteLine("source completed — subscription ended"));

// ... run until done ...
cts.Cancel();
// Disposing `subscription` (using above) sends unsubscribe and frees the server resource.
```

Notes on the C# surface:

- `SleipnirSubscription<T>` has no `System.Reactive` dependency — it ships its own minimal
  `SleipnirSubject<T>`. The `.Subscribe(onNext, onError, onCompleted)` overload above is the
  standard `IObservable<T>` extension from `System.Reactive`; if you don't reference Rx, implement
  `IObserver<T>` directly and pass it to `subscription.Subscribe(observer)`.
- Calling `.Subscribe(observer)` only attaches an observer to the local subject; it does **not**
  re-subscribe on the server. Disposing the *returned* `IDisposable` detaches the observer;
  disposing the `SleipnirSubscription<T>` itself sends `unsubscribe` to the server.
- **Auto-reconnect re-subscribes.** If the `SleipnirWebSocketClient` is configured with
  auto-reconnect, every active subscription is automatically re-issued with the original
  parameters after a reconnect, obtaining fresh `subscriptionId`s (see lifecycle below).

**Generated typed surface.** The `sleipnir-codegen` C# emitter recognizes `returnType.kind ==
"event"` in discovery and emits a typed `SubscribeAsync<T>` per event method, so the call above
becomes `client.Chat.MessageReceivedAsync(chatId: 42)` returning `SleipnirSubscription<Message>`.

### Client: subscribe (raw WebSocket, any language)

Any RFC 6455 client can subscribe by sending the JSON frames directly — useful from JS/Python/Go
without a typed client. See [`PROTOCOL.md`](PROTOCOL.md) → "Server-Push Events" for the
authoritative frame shapes; in summary:

```jsonc
// → subscribe
{ "kind": "subscribe", "controller": "Chat", "method": "MessageReceived",
  "params": [ { "parameterName": "chatId", "data": 42 } ], "id": "sub-1" }
// ← subscribe response
{ "code": 200, "data": { "subscriptionId": "7a3f9c1e…" }, "id": "sub-1" }
// ← event frames (correlated by subscriptionId, no id/code)
{ "type": "event", "subscriptionId": "7a3f9c1e…", "eventId": 1, "data": { "id": 1, "from": "alice", "text": "hi" } }
{ "type": "event", "subscriptionId": "7a3f9c1e…", "eventId": 2, "data": { "id": 2, "from": "bob",   "text": "yo" } }
// ← terminal frame (source completed)
{ "type": "complete", "subscriptionId": "7a3f9c1e…" }
// → unsubscribe early
{ "kind": "unsubscribe", "subscriptionId": "7a3f9c1e…", "id": "unsub-1" }
```

A client distinguishes an event frame from a call response by the presence of `type`
(`event`/`complete`/`error`) vs. `code` (call response).

### Subscription lifecycle & delivery semantics

- **`subscriptionId` is per-connection.** Each subscribe gets a fresh id; ids are not valid across
  connections.
- **Auto-cleanup on disconnect.** Closing the socket disposes every active subscription on that
  connection (the server-side observable subscription is disposed, freeing the source).
- **Reconnect → re-subscribe (client-side).** After a reconnect, the client re-issues subscribe
  requests for each active subscription with the original parameters; new `subscriptionId`s are
  issued. The C# `SleipnirWebSocketClient` does this automatically.
- **At-most-once-while-disconnected.** Events produced while the connection is down are **lost** —
  no server-side buffer in v1. This is the documented gap semantic; `Last-Event-Id` resume (the
  `eventId` is already monotonic per subscription to enable it) + server buffer are v1.x+.
- **Backpressure.** Each subscription has a per-subscription buffer whose capacity and overflow
  strategy are configurable. The global defaults are `SleipnirOptions.EventBufferCapacity` (fallback
  100) and `SleipnirOptions.EventBackpressureStrategy` (default `DropOldest`); both are overridable
  per event via `[SleipnirEvent(BufferCapacity = …, BackpressureStrategy = …)]`. The strategies:
  - **`DropOldest`** (default) — when full, evict the **oldest** buffered event to make room for the
    newest. Keeps the subscription recent and is DoS-safe. Best for current-state streams (prices,
    presence, telemetry).
  - **`DropWrite`** — when full, drop the **newest** event. Preserves the in-order backlog but loses
    the latest; the consumer falls behind. Best when the historical sequence matters more than freshness.
  - **`Block`** — when full, block the producer's `OnNext` until the consumer drains a slot. Lossless,
    but back-pressures the source thread — opt in deliberately with a producer that tolerates blocking.
  - **`Unbounded`** — no cap; nothing is dropped. Memory grows without bound if the consumer is slower
    than the producer (no DoS backstop). Use only for bounded-volume, short-lived subscriptions.
  `DropOldest` evictions and `DropWrite` rejections increment the `sleipnir.event.dropped` OpenTelemetry
  counter; `Block` and `Unbounded` never drop. (The counter is accurate as of 1.2.0 — the earlier
  `BoundedChannel(DropOldest)` path could not detect saturation, so the metric was dead code.)

### Cold vs. hot observables

The framework makes **no statement** about whether your `IObservable<T>` is cold or hot — it is a
pass-through. At subscribe time the invoker resolves the controller in a **fresh DI scope per
subscription**, invokes the compiled delegate once, and hands the returned `IObservable<T>` to
exactly one `Subscribe` call. It does **not** wrap in `Publish`/`RefCount`/`ReplaySubject`, so there
is no multicast facility. The cold/hot behavior is therefore entirely determined by what your
controller method returns:

- **Cold** (e.g. a fresh `Observable.Create(...)` or an EF query re-run per subscriber) — every
  subscriber gets an independent stream from its own start. The per-subscribe DI scope reinforces
  this for stateful controllers: a scoped producer is re-created per subscription.
- **Hot** (e.g. a shared `Subject<T>` / `IObservable<T>` backed by a long-running broadcast) — every
  subscriber attaches to the same running source and sees events produced after it subscribed;
  events produced before subscribe are not replayed (no server-side buffer in v1).

If you need shared-broadcast semantics, build them in the producer yourself (a singleton
`Subject<T>` injected into the controller) — Sleipnir will not infer or impose them. The
at-most-once-while-disconnected rule above applies to **both** kinds: a hot source keeps producing
while you are disconnected and those events are lost; a cold source simply restarts on re-subscribe.

### Discovery

Event methods appear in `DiscoveryInfo` like any method; event-ness is on the return type:

```json
{
  "methodName": "MessageReceived",
  "returnType": { "kind": "event", "element": { "kind": "ref", "ref": "Message" } },
  "parameters": [ { "parameterName": "chatId", "parameterType": { "kind": "scalar", "name": "int" } } ]
}
```

Detect a subscribable method by `returnType.kind == "event"`; the element type is
`returnType.element`. The `[SleipnirEvent]` name is the `methodName`.

### Migration from 1.1.x

In 1.1.x an event method was marked `[SleipnirMethod]` and event-ness was inferred from the
`IObservable<T>` return type; `[SleipnirEvent]` existed but was not read. As of 1.2.0
**`[SleipnirEvent]` is the required marker**: replace `[SleipnirMethod]` with `[SleipnirEvent]` on
any `IObservable<T>` method. The old form now fails at registration with a migration message.
Plain calls to event methods now return `400` (was: an opaque `500`); subscribing to a non-event
method returns `400` without executing it. This is an experimental-surface change (no SemVer
major) — see `CHANGELOG.md` and `STABILITY.md` §2/§3.7.

## Known Limitations (v1)

Sleipnir v1 is intentionally focused. The following are deliberate scope decisions, not bugs — documented here so adopters can plan around them:

- **No built-in API versioning.** The `controller` field is a free-form string. There is no framework-level versioning mechanism. The recommended convention is to encode the version in the controller name, e.g. `[SleipnirController("Customer.v1")]`, so a v2 can coexist without breaking v1 clients. Build-time version enforcement via a source generator (the `wsdl.exe` model) is planned for v1.1 — see [ROADMAP.md](ROADMAP.md).
- **No Native AOT.** Method discovery uses runtime reflection, and invocation uses `Expression.Compile`-produced delegates. This is incompatible with Native AOT trimming. Standard (JIT) publishing is the supported deployment model.
- **REST streaming is materialized.** An `IAsyncEnumerable<T>` result is fully consumed server-side and serialized as a JSON array before the REST response is sent. True streamed delivery over REST is not supported — use the WebSocket or SignalR transport for streaming semantics.
- **Attribute-based registration only.** Controllers and methods are discovered via `[SleipnirController]` / `[SleipnirMethod]` attribute scanning. Direct/fluent registration of individual handlers in code (without attributes) is on the roadmap, not in v1.
- **No parameter-based overloading.** A call is addressed by `Controller.Method` only — the parameter set is **not** part of the dispatch key, so Sleipnir does not resolve C# overloads by signature over the wire. Two `[SleipnirMethod]`s on the same controller, or two `[SleipnirController]`s app-wide, must therefore carry distinct names; otherwise `SleipnirInvoker.Register` throws `InvalidOperationException` at startup rather than silently shadowing one with the other (the dispatch key is `{Controller}_{Method}`, purely name-based). Model what would be C# overloads with distinct Sleipnir names (`add`, `addAll`, `addRange`). To exclude a controller from the auto-discovery bulk scan (e.g. a test fixture you register only by hand), set `[SleipnirController("name", AutoDiscover = false)]` and register it explicitly via `Register<T>()` or `SleipnirControllerBuilder.Add<T>()`.
- **Dotted namespaces, not URL hierarchies.** The `controller` field accepts a dotted namespace (e.g. `Customer.Address.Contact`) to express arbitrarily deep groupings, but it is a single string key — it is not translated into a nested URL path. Routing stays two-part (`controller` + `method`) at the protocol level.
- **Error details in development only.** `SleipnirError.details` (stack trace) is populated only when `EnableDetailedErrors` is set or the host runs in Development. In production, error messages are intentionally generic and do not leak exception internals.
- **Binary is base64 over REST and WebSocket.** Only the SignalR/MessagePack channel carries `byte[]` without base64; the WebSocket transport honors text frames only. Binary is bounded by the transport message-size limits (REST 1 MB, WebSocket 1 MB). For large or frequent binary, run a plain REST or WebSocket endpoint alongside Sleipnir — see [Binary](#binary) and the v1.x+ plan in [ROADMAP.md](ROADMAP.md).
- **`byte[]` parameters bind first-match-only.** A method with more than one `byte[]` parameter receives `binaryData` in only the first; the others are not filled.
- **No streamed binary.** `byte[]` responses are buffered into `content`; a `ContentStream` field exists on the model but is not wired by any transport in v1.
- **C# fluent builder has no `WithBinary`.** Set `request.BinaryData` directly for binary uploads from C# until the helper lands (see [ROADMAP.md](ROADMAP.md)).

## Project Structure

| Project | Description |
|---------|-------------|
| SleipnirCommon | Shared models, attributes, exceptions |
| SleipnirCore | Invoker, discovery, dependency resolver, interceptors |
| SleipnirHub | SignalR transport; AddSleipnir() and low-level UseSleipnir() live in `SleipnirHub.Extensions` |
| SleipnirRest | REST transport (Minimal APIs + MVC controller) |
| SleipnirWebSocket | WebSocket transport (RFC 6455, JSON text frames) |
| SleipnirClient | Client library (REST, WebSocket, SignalR clients) |
| SleipnirDeveloperUi | Built-in developer web UI |
| SleipnirTelemetry | Optional OpenTelemetry SDK bootstrap (subscribes the `Sleipnir` source, wires OTLP/Console exporters + AspNetCore/HttpClient instrumentation) |
| Sleipnir | Sample application |
| SleipnirTests | Unit + integration tests |
| SleipnirBench | Performance benchmarks |

## Installation (NuGet)

```xml
<!-- Server: all transports (hub + REST + WebSocket + DevUI) -->
<PackageReference Include="Sleipnir.Server" Version="1.1.0" />

<!-- Optional: OpenTelemetry SDK bootstrap -->
<PackageReference Include="Sleipnir.Telemetry" Version="1.1.0" />

<!-- Or individual transports -->
<PackageReference Include="Sleipnir.Core" Version="1.1.0" />
<PackageReference Include="Sleipnir.Hub" Version="1.1.0" />
<PackageReference Include="Sleipnir.Rest" Version="1.1.0" />
<PackageReference Include="Sleipnir.WebSocket" Version="1.1.0" />

<!-- Client -->
<PackageReference Include="Sleipnir.Client" Version="1.1.0" />
```

### Distributed Tracing

Sleipnir instruments the engine directly in `SleipnirCore` via a public `ActivitySource` named **`Sleipnir`** (`SleipnirCore.Tracing.SleipnirTracing.ActivitySourceName`). The instrumentation is always on but cost-neutral — `ActivitySource.StartActivity` returns `null` when nothing subscribes, so there is no allocation or bookkeeping unless a listener (the OTel SDK or a custom `ActivityListener`) is attached. No NuGet dependency is added to `SleipnirCore`/`SleipnirServer` for this; `System.Diagnostics` is in-box for .NET 8.

Spans emitted (OTel RPC semantic conventions):

- **`SleipnirCall`** — one per request, started in both the single-call path (`InvokeDi(SleipnirRequest)`) and the per-request batch path (`ExecuteSingleInvocation`). Tags: `rpc.system=sleipnir`, `rpc.service`, `rpc.method`, `sleipnir.request_id` (only when `Id` is non-empty), `sleipnir.binary.length` (only when `BinaryData` is non-empty). Status set from the response (`Ok` when `IsSuccess`, else `Error` with `Error.Message`). On an escaping exception the catch adds `exception.type`/`exception.message`/`exception.stacktrace` (via `SleipnirTracing.RecordException`, which replaces `Activity.RecordException` — the extension is not resolvable in a net8.0 class library).
- **`SleipnirBatch`** — one per batch, started in `InvokeDi(IEnumerable<SleipnirRequest>)`. Tags: `rpc.system=sleipnir`, `sleipnir.batch.mode` (`Parallel`/`Serial`, or `DependencyBatches` when auto-detect routes to topological execution), `sleipnir.batch.count`. The per-request `SleipnirCall` spans are children via `Activity.Current` parenting — a single batch yields one parent + N children.

`Sleipnir.Telemetry` is the optional package that boots the OTel SDK: `AddSleipnirTelemetry` calls `AddOpenTelemetry().WithTracing(b => b.AddSource("Sleipnir") …)` with configurable service name, OTLP/Console exporter, and AspNetCore/HttpClient instrumentation gates. `SleipnirServer` does **not** reference it, keeping the OTel SDK dependencies out of the all-in-one bundle. Consumers who need custom samplers/resources/exporters skip `AddSleipnirTelemetry` and call `AddOpenTelemetry().WithTracing(b => b.AddSource("Sleipnir"))` directly — the source name is the only integration point.

## Requirements

- .NET 8.0+
- ASP.NET Core 8.0+

## License

MIT