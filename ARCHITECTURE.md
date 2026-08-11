# Sleipnir Architecture

> German version: [ARCHITECTURE.de.md](ARCHITECTURE.de.md)

## Design Philosophy

Sleipnir was born from a simple observation: **REST is designed for resources, not for RPC.**

When calling actions (not CRUD on nouns), REST forces awkward patterns:
- URL paths that pretend to be nouns: `/api/customer/42/add-address`
- N+1 roundtrips for dependent calls: client waits for A, then calls B, then C
- No batching: 10 method calls = 10 HTTP requests with 10× overhead

Sleipnir takes inspiration from **GraphQL** (dependency resolution, batch queries) and **gRPC** (method-oriented calling, binary transport) but stays within the .NET/ASP.NET Core ecosystem.

### Why Not gRPC?

Two key reasons Sleipnir exists instead of just using gRPC:

1. **gRPC was not well-supported** at creation time. Browser support required gRPC-Web proxies, and .NET gRPC tooling was still immature. Sleipnir supports REST, WebSocket, and SignalR out of the box — browsers can use WebSocket or REST without proxies.

2. **Code-first, not schema-first.** gRPC requires `.proto` files — a separate IDL that generates C# stubs. This means maintaining a second source of truth: the `.proto` and the C# implementation. Sleipnir uses attributes on plain C# classes: `[SleipnirController]`, `[SleipnirMethod]`, `[SleipnirDataContract]`. The C# code *is* the contract. Discovery metadata is generated at runtime, not compile time.

The core design principles:
1. **Method-oriented**: Controllers and methods, not routes and verbs
2. **Batch by default**: Multiple calls in one roundtrip, parallel or serial
3. **Server-side dependency resolution**: `@alias` chaining inspired by GraphQL field selection
4. **Transport-agnostic**: The same `SleipnirRequest` works over REST, WebSocket, or SignalR
5. **Zero-reflection invocation**: Expression Trees compile method calls at startup
6. **Code-first**: No IDL, no `.proto` files, no code generation — C# classes are the schema

## Overview

Sleipnir is a protocol-agnostic RPC engine that sits between your business logic and multiple transport layers. The core engine (`SleipnirCore`) resolves method calls via pre-compiled Expression Trees, while transports (REST, WebSocket, SignalR) handle the wire protocol.

```
┌──────────────────────────────────────────────────────────┐
│                      Client                                │
│  SleipnirCall.Init("Ctrl", "Method").With(args).ToRequest()   │
│         │                                                  │
│    ┌────┴────┬────────────┬──────────────┐                 │
│    │ REST    │ WebSocket  │  SignalR     │  Transports     │
│    │ Client  │  Client   │  Client      │                 │
│    └────┬────┴────┬───────┴──────┬──────┘                 │
└─────────┼─────────┼──────────────┼────────────────────────┘
          │         │              │
          ▼         ▼              ▼
┌──────────────────────────────────────────────────────────┐
│                    Server (ASP.NET Core)                   │
│  ┌──────────┐ ┌─────────────┐ ┌────────────┐               │
│  │ SleipnirRest │ │ SleipnirWebSocket│ │ SleipnirHub   │  Transports   │
│  │ Endpoint │ │  Middleware  │ │ (SignalR) │               │
│  └────┬─────┘ └──────┬──────┘ └─────┬────┘               │
│       └──────────────┼───────────────┘                     │
│                      ▼                                     │
│        ┌─────────────────────────────┐                    │
│        │      ISleipnirCore (Invoker)      │  Core             │
│        │  ┌─────────────────────────┐  │                    │
│        │  │  Expression-Tree Call   │  │                    │
│        │  │  Parameter Resolution   │  │                    │
│        │  │  Authorization Check    │  │                    │
│        │  │  Interceptor Pipeline   │  │                    │
│        │  │  Dependency Resolver    │  │                    │
│        │  │  Discovery Service     │  │                    │
│        │  └─────────────────────────┘  │                    │
│        └──────────┬──────────────────┘                    │
│                   ▼                                        │
│        ┌─────────────────────────────┐                    │
│        │   [SleipnirController] classes   │  User Code         │
│        │   with [SleipnirMethod] methods  │                    │
│        └─────────────────────────────┘                    │
└────────────────────────────────────────────────────────────┘
```

## Request/Response Flow

### Single Call
1. Client creates `SleipnirRequest` with controller name, method name, and JSON parameters
2. Transport sends the request (HTTP POST, WebSocket frame, or SignalR hub invocation)
3. `SleipnirInvoker` looks up the controller and method in the invoke cache
4. Interceptor pipeline runs (logging, tracing, etc.)
5. Authorization is checked (`[SleipnirAuthorise]`)
6. Parameters are deserialized from JSON and matched by name
7. Method is invoked via pre-compiled Expression Tree delegate
8. Result is serialized to JSON and wrapped in `SleipnirResponse`
9. Response is sent back through the transport

### Batch Call (Multi-Request)
1. Client sends `SleipnirMultiRequest` with multiple `SleipnirRequest` items and an `ExecutionMode`
2. If any request has `DependencyMapping`, auto-detection switches to topological batch execution
3. **Parallel mode**: All requests execute concurrently via `Task.WhenAll`
4. **Serial mode**: Requests execute sequentially with dependency resolution
5. **Dependency-Batch mode**: `DependencyGraphBuilder` creates execution batches:
   - Level 0: All requests without dependencies → parallel
   - Level 1: Requests depending only on Level 0 results → parallel
   - Level N: Requests depending on Level N-1 results → parallel
6. Each level runs in parallel, levels run sequentially

### Dependency Chaining
1. Request A declares `DependencyMapping: { "alias" → "$" }` (result-relative JSON path; `$` is the whole result, `$.Property` a property, `$[0].Id` a list element)
   - After execution, the server extracts values from A's response using JsonPath
   - Extracted values are stored in `ExposedDependencies: { "alias" → value }`
2. Request B uses `@alias` as a parameter placeholder
   - The server replaces `@alias` with the actual value from A's `ExposedDependencies`
3. This enables 3 dependent calls in a single HTTP roundtrip

### Dependency Chaining Limits
`@alias` is **single-valued and non-expanding** (intentional boundary, see PROTOCOL.md → Limits):
1. Each `dependencyMapping` entry extracts the **first** JsonPath match only — `$[*].id` yields the first element, not all (`DependencyResolver.ExtractValue` returns `Matches.First()`).
2. The server never spawns requests from an array result — **no server-side fan-out**. Cardinality is never increased.
3. Paths are case-sensitive against the server's **camelCase** output (`$[0].id`, not `$[0].Id`).
4. To pass a collection to one call, expose `$` and bind to a collection-typed parameter (`int[]`/`List<T>`).
5. For "load all by id" prefer a batch-get endpoint (`GetByIds(int[])`); any future `Map`/`ForEach` mode must be bounded (`MaxFanOut`, bounded concurrency, per-element results, read-only default).
6. The server self-protects against cardinality blow-up via two caps on `SleipnirOptions` — `MaxParameterArrayLength` (default 1000) and `MaxResultElementCount` (default 10000), each `0` = unlimited. Body-size limits do not cover the server-generated passthrough; these caps do. See PROTOCOL.md → Limits for details.

## Core Components

### SleipnirInvoker (`SleipnirCore`)
- Singleton service, thread-safe
- `ConcurrentDictionary` caches: controller types and compiled `InvokeInfo`
- `CompileInvocation()`: Expression Trees create `Func<object, object?[], object?>` per method
- `BuildParameters()`: Deserializes JSON parameters, matches by parameter name, injects `CancellationToken`
- `ExecuteMethod()`: Creates DI scope, resolves controller instance, invokes compiled delegate
- Handles sync, async (Task/Task<T>), void, and `IAsyncEnumerable<T>` return types

### SleipnirDiscoveryService (`SleipnirCore`)
- Generates `DiscoveryInfo` with all controllers, methods, parameters, and types
- Types are emitted as structured, language-neutral `TypeRef` objects (`kind` ∈ `scalar | array | set | map | ref | stream | opaque | void`), not .NET type-name strings — versioned by an additive-only `discoveryVersion` field. Authoritative spec: [`docs/discovery-schema.md`](docs/discovery-schema.md). Enums register as a `TypeMeta` with `kind:"enum"` + `members`; a usage site is `{kind:"ref", ref:"<enumKey>"}`. Sleipnir serializes enums as their underlying integer, so an enum ref is wire-numeric (the members are documentation only).
- Contract types are **inferred from method signatures**: any class type from a controller assembly is fully expanded (property schema, example, nested types); `[SleipnirDataContract]` is an optional override (bare = force-expand, `Exclude = true` = force-opaque). Types from other assemblies (BCL, framework envelope, third-party) stay opaque.
- Extracts `[SleipnirDocumentation]` summaries and `[SleipnirExample]` JSON examples
- Cached with invalidation on new registrations

### DependencyGraphBuilder (`SleipnirCore`)
- Topological sort of requests based on `DependencyMapping` and `@alias` usage
- Groups independent requests into parallel batches (level-based execution)
- Cycle detection throws `InvalidOperationException` with involved request IDs

### Interceptor Pipeline (`SleipnirCore`)
- `ISleipnirInterceptor`: `InvokeAsync(request, next, ct)`
- Pipeline wraps the actual method execution in reverse order
- Built-in: `SleipnirLoggingInterceptor` (traces call duration)
- Custom interceptors registered via DI: `services.AddSingleton<ISleipnirInterceptor, MyInterceptor>()`

### Transports

| Transport | Project | Wire Protocol | Key Features |
|-----------|---------|---------------|--------------|
| REST | SleipnirRest | HTTP/1.1 + JSON | Minimal APIs at `/api/sleipnir/json`, `/api/sleipnir/json/multi`, `/api/sleipnir/discovery` |
| WebSocket | SleipnirWebSocket | RFC 6455 + JSON text frames | Auto-detects single vs. multi-request, multi-frame support |
| SignalR | SleipnirHub | WebSocket + MessagePack | Hub methods `DoWork()` / `DoWorkMany()`, auto-reconnect |

### Client Library (`SleipnirClient`)
- `ISleipnirClient` interface: `Call(SleipnirRequest)`, `Call<T>(SleipnirRequest)`, `Call(SleipnirMultiRequest)`
- `SleipnirRestJsonClient`: HTTP-based, connection pooling, `IDisposable`
- `SleipnirWebSocketClient`: Persistent connection, `SemaphoreSlim` for thread safety, `IAsyncDisposable`
- `SleipnirSignalrClient`: Auto-reconnect with exponential backoff, MessagePack protocol
- `SleipnirCall`: Fluent builder with `.Named()`, `.Exposes()`, `.WithAlias()`, `.With()`, `.Add()`, `.ToRequest()`
- `SleipnirClientBase`: Shared `Call<T>()` with auto-deserialization and `SleipnirException` on errors

## Error Model

```csharp
SleipnirResponse {
    int Code;                    // HTTP-like status code
    string? Data;                // JSON result or error message
    byte[]? Content;             // Binary payload
    string? Id;                  // Request correlation ID
    Dictionary<string, string>? ExposedDependencies;  // For chaining
    SleipnirError? Error;            // Structured error (when Code != 200)
    bool IsSuccess;               // true if 200-299
}

SleipnirError {
    int Code;
    string Message;
    string? Details;             // Stack trace in Development only
    string? RequestId;
}
```

Clients throw `SleipnirException` with `SleipnirError` on non-2xx responses.

## Attributes

| Attribute | Target | Purpose |
|-----------|--------|---------|
| `[SleipnirController("name")]` | Class | Marks a class as an RPC controller |
| `[SleipnirMethod("name")]` | Method | Marks a method as remotely callable |
| `[SleipnirAuthorise]` | Method | Requires authentication (optional role) |
| `[SleipnirDataContract]` | Class | Optional discovery override (bare = force-expand / `Exclude = true` = force-opaque); default is signature inference via the controller-assembly boundary |
| `[SleipnirDocumentation("summary")]` | Class/Method/Param | XML-like doc for discovery |
| `[SleipnirExample("json")]` | Class | Example JSON for developer UI |

## Extension Points

1. **Custom Interceptor**: Implement `ISleipnirInterceptor`, register in DI
2. **Custom Transport**: Implement `ISleipnirClient` (client) or create middleware (server)
3. **Custom Authorisation**: Extend `SleipnirAuthoriseAttribute.OnAuthorization()`
4. **Discovery Enhancement**: Extend `SleipnirDiscoveryService.BuildDiscoveryInfo()`