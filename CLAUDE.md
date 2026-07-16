# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Test Commands

```bash
# Build the entire solution
dotnet build Trame.sln

# Run all tests
dotnet test TrameTests/TrameTests.csproj

# Run a specific test class
dotnet test TrameTests/TrameTests.csproj --filter "FullyQualifiedName~TrameInvokerTests"

# Run a single test method
dotnet test TrameTests/TrameTests.csproj --filter "FullyQualifiedName~TrameInvokerTests.MyTestMethod"

# Run benchmarks (Release mode required)
dotnet run -c Release --project TrameBench/TrameBench.csproj

# Run the sample application
dotnet run --project Trame/Trame.csproj
```

## Architecture

Trame is a **multi-transport RPC framework** for .NET 8 that is **code-first** (no `.proto` files, no IDL). The C# classes decorated with attributes *are* the contract. Discovery metadata is generated at runtime.

### Project Dependency Graph

```
TrameCommon (shared models, attributes, exceptions)
    ↑
TrameCore (invoker, discovery, dependency resolver, interceptors) ← depends on JsonPath.Net
    ↑
┌───────────┬──────────────┬──────────────┐
│ TrameRest  │ TrameWebSocket│  TrameHub     │  ← transport layers
│ (Minimal  │ (RFC 6455    │  (SignalR +  │
│  APIs)    │  middleware)  │   MsgPack)   │
└───────────┴──────────────┴──────────────┘
    ↑              ↑              ↑
TrameClient (REST, WebSocket, SignalR clients + fluent TrameCall builder)
    ↑
Trame (sample app) · TrameTests (xUnit + FluentAssertions) · TrameBench (BenchmarkDotNet)
```

### Core Engine (`TrameCore`)

**`TrameInvoker`** (registered as singleton `ITrameCore`) is the central execution engine:
- `Register<T>()` scans `[TrameController]` and `[TrameMethod]` attributes, compiles each method into an **Expression Tree delegate** (`Func<object, object?[], object?>`) stored in a `ConcurrentDictionary` cache. No reflection per call.
- `InvokeDi(TrameRequest)` runs a single call through the interceptor pipeline → authorization → parameter deserialization → compiled delegate invocation → result serialization.
- `InvokeDi(IEnumerable<TrameRequest>, ExecutionMode)` handles batch calls:
  - **Parallel**: `Task.WhenAll` over all requests.
  - **Serial**: sequential execution with `@alias` dependency resolution between calls.
  - **Auto-detect**: if any request has `DependencyMapping`, switches to topological batch execution via `DependencyGraphBuilder`.
- Handles `void`, `Task`, `Task<T>`, and `IAsyncEnumerable<T>` return types. Streaming results are consumed into a `List<T>` and serialized as a JSON array.

**HttpContext in batches — serial auth pre-pass + dependent propagation.** `HttpContext` is not thread-safe, yet every request in a batch shares the *same* incoming context (REST/WebSocket). The batch path therefore splits each invocation into two phases: **`ResolveAndAuthorizeAsync`** (controller/method lookup + `CheckAuthorisation` → `OnAuthorization(context)` — the *only* place the context is touched) runs **serially in a pre-pass** before the fan-out; **`ExecuteAuthorized`** (parameter binding, compiled delegate, `ExposedDependencies` extraction) runs **parallel via `Task.WhenAll` and never touches `HttpContext`**. `ExecuteMethod` creates its own DI scope per call, so it is parallel-safe by construction. The same split applies to the topological path, where the pre-pass runs per Kahn-ordered batch. **Batch failure is per-request** (JSON-RPC-conformant): a 401 on one request does not abort the others. **Dependent propagation** (topological path only — Parallel/Serial have no providers by definition): if a provider fails authorization, errors, or declares an alias it does not actually expose, its dependents do *not* run — they receive an explanatory `400 dependency '@a' unavailable: provider '<id>' …` (was unauthorized / returned HTTP <code> / did not expose / no provider exposes) instead of reaching the missing alias at runtime with the uninformative `Unresolved dependencies`. Transitivity falls out: a skipped provider has no `ExposedDependencies`, so its own dependents are caught in the next batch. **User-code contract:** a controller can still obtain the context via `IHttpContextAccessor` (the standard ASP.NET pattern; Trame does not register it but cannot prevent users from doing so). Because `AsyncLocal` flows into all `Task.WhenAll` children, user code in a parallel batch sees the same shared context — it must treat it as **read-only** (no writes to `Items`/Response/Request-Body). The framework's own concurrent context access is eliminated structurally by the pre-pass; this contract covers user overrides of `OnAuthorization` and any `IHttpContextAccessor` consumption in controller bodies.

**`DependencyGraphBuilder`** performs topological sort (Kahn's algorithm) on requests with `@alias` placeholders. Groups independent requests into parallel batches; detects cycles.

**`ITrameInterceptor`** / `TrameInvocationDelegate`: middleware pipeline pattern. Interceptors wrap the actual execution in reverse registration order. Built-in `TrameLoggingInterceptor` logs call duration.

**`TrameDiscoveryService`** generates `DiscoveryInfo` from registered controllers, including contract types, `[TrameDocumentation]` summaries, and `[TrameExample]` JSON samples. Contract types are **inferred from method signatures** by default (Weg C): any class type whose assembly belongs to the registered controllers' assemblies is fully expanded (property schema, example, nested types); types from other assemblies (BCL, framework envelope, third-party) stay opaque unless overridden. `[TrameDataContract]` is an optional override (bare = force-expand, `Exclude = true` = force-opaque). Cached with invalidation on new registrations.

### Transports

| Transport | Project | Endpoint | Wire Format |
|-----------|---------|----------|-------------|
| REST | TrameRest | `POST /api/trame/json`, `/api/trame/json/multi`, `GET /api/trame/discovery` | HTTP/1.1 + JSON |
| WebSocket | TrameWebSocket | `ws://host/tramews` | RFC 6455 + JSON text frames |
| SignalR | TrameHub | `/tramehub` hub | WebSocket + MessagePack binary |

All transports deserialize the incoming `TrameRequest`/`TrameMultiRequest` and delegate to `ITrameCore.InvokeDi()`.

**JSON-RPC 2.0 compat adapter** (`TrameRest/JsonRpc/`, opt-in via `TrameOptions.EnableJsonRpcCompat` → `POST /api/trame/jsonrpc`). `JsonRpcAdapter` is a *pure* bidirectional translator (JSON-RPC ↔ `TrameRequest`/`TrameResponse`, error-code map, `trame.capabilities` manifest); `JsonRpcDispatcher` is the orchestration (reads raw body, dispatches `trame.discover`/`trame.capabilities`, calls `InvokeDi` in **Parallel**, assembles single/batch, applies the `200`/`204` envelope). `method` is `Controller.Method` (split at the last dot), `params` object → named / array → positional (by `num`), `id` echoed with original type, notifications emit no response (all-notification → `204`). Error-code map distinguishes **routing** 404 (`"Controller '…'"/"Method '…'"` prefix → `-32601`) from **business** 404 (`→ -32000`); `400/422→-32602`, `401/403→-32001`, `500→-32603`, else `-32000`; parse→`-32700`, invalid→`-32600`. Limitations: no `@alias` chaining, no execution-mode selection (Parallel only), no binary out-of-band, no streaming — graduate to the native wire. Spec + differences table: `JSONRPC_COMPAT.md`.

### Client Library (`TrameClient`)

- `ITrameClient` interface with `Call(TrameRequest)`, `Call<T>(TrameRequest)`, `Call(TrameMultiRequest)`.
- `TrameClientBase`: shared `Call<T>()` with JSON deserialization and `TrameException` on errors.
- `TrameRestJsonClient`: `HttpClient`-based, `IDisposable`.
- `TrameWebSocketClient`: persistent `ClientWebSocket` connection, `SemaphoreSlim` for thread safety, `IAsyncDisposable`.
- `TrameSignalrClient`: SignalR hub connection with auto-reconnect and exponential backoff.
- `TrameCall`: fluent builder — `.Init("Controller", "Method").With(args).Named("id").Exposes("$", "alias").WithAlias("@alias", "default").ToRequest()`. The `Exposes` JSON path is **result-relative** (`$` = whole result, `$.Property`, `$[0].Id`); there is no `$.data` envelope level.

### Key Attributes (defined in `TrameCommon`)

- `[TrameController("name")]` — marks a class as an RPC controller. Optional `AutoDiscover = false` (default `true`) excludes it from the bulk auto-discovery scans in `AddTrame`/`UseTrame`/`FromAssemblies`; it must then be registered explicitly via `Register<T>()` or `TrameControllerBuilder.Add<T>()`. Controller names must be unique (see Name Uniqueness below).
- `[TrameMethod("name")]` — marks a method as remotely callable
- `[TrameAuthorise]` — requires authentication (optional role parameter)
- `[TrameDataContract]` — optional discovery override: bare = force-expand a type (e.g. a third-party type you want documented); `Exclude = true` = force-opaque. By default, discovery infers contract types from method signatures via the controller-assembly boundary.
- `[TrameDocumentation("summary")]` — XML-like doc for discovery
- `[TrameExample("json")]` — example JSON for developer UI

### Request/Response Model

Parameters are sent as `TrameParameter[]` (name+data pairs), matched by parameter name. `CancellationToken` is injected automatically. `byte[]` parameters receive raw binary from `TrameRequest.BinaryData`.

`TrameResponse` carries `Code` (HTTP-like status), `Data` (JSON result), `Content` (binary), `ExposedDependencies` (for chaining), and `TrameError` on failure. Clients throw `TrameException` on non-2xx responses.

### Error Handling

Controllers signal non-success two ways — use the right one:
- **Business/domain errors → return `TrameResponse`** via the `TrameResults` factory (`TrameCommon.Results`): `TrameResults.NotFound("…")`, `TrameResults.BadRequest("…")`, `TrameResults.Error(code, message, details?)`, `TrameResults.Ok(obj)`. The invoker passes a returned `TrameResponse` through verbatim (`ReturnResponse`), so your `Code`+`Data`+`Error` reach the client. The message is **not** gated by `EnableDetailedErrors`.
- **Unexpected/internal failures → throw**. Any throw becomes a generic `500` (no message leak); the stack only lands in `error.details` when `EnableDetailedErrors` (Development) is on.
- Do **not** throw `TrameException` to set a custom code — the server has no `catch(TrameException)`; it becomes generic 500. Control the code via `TrameResults.Error(...)`.

### Dependency Chaining

Request A declares `DependencyMapping: { "alias" → "$.Path" }` (a **result-relative** JsonPath — `$` is the whole serialized result, e.g. `$`, `$.Id`, `$[0].Id`; there is no `$.data` envelope level). After execution, values are extracted via `DependencyResolver.ExtractValue(result.Data, jsonPath)` (parsed against the result JSON) and stored in `ExposedDependencies` — **only on a `2xx` response**; a non-2xx result (business `TrameResults` error, thrown exception → 500, or a 401/missing-route from the serial auth pre-pass) leaves `ExposedDependencies` empty, so no value is ever extracted from an error payload even when it path-matches (a dependent then gets the propagation `400`, see below). `ExtractValue` is **match-count-aware**: a single-match path yields that match (scalar, or an array if the match itself is one); a multi-match path (`$[*].Id`, `$..Id`) collects all matches into a `JsonArray`, injected as one list-typed parameter (`List<T>`/`T[]`/`IEnumerable<T>`) — list fan-out into a *parameter*, never fan-out into N *requests*. Request B uses `@alias` as a parameter placeholder — the server resolves it before execution (`ResolveParameterValues` → `ReplaceDependencyByAlias`). Both the Serial (`ExecuteSequentially`) and the auto-detect topological-batch (`ExecuteInDependencyBatches`) paths resolve aliases against prior responses. This enables multiple dependent calls in a single roundtrip.

**Binding & casing** (dedicated spec: `DEPENDENCY_BINDING.md`; executable spec: `TrameTests/Unit/Core/AliasBindingTests.cs`; protocol-level summary in `PROTOCOL.md` → "Alias Serialization & Type Binding" / "Casing Contract"; user-facing in `README_DETAILS.md` → "Dependency Chaining — Binding, Types & Casing"): the extracted fragment is fed straight into the consumer's `System.Text.Json` deserializer — never re-serialized through the consumer type — so the four runtime outcomes are: compatible → 2xx; cross-kind scalar (no `AllowReadingFromString`) → 400; object→object duck-typed (missing value-type prop → silent default, the insidious case; missing reference prop → `null`; kind mismatch on an overlapping prop → 400); unresolved → 400. Casing has three independent regimes: parameter **names** bind case-sensitively (ordinal dict), object **value** properties are read case-insensitively / written camelCase, **JsonPath** is case-sensitive against the camelCase wire document (so `$.Id` PascalCase matches nothing → `Unresolved`; use `$.id`). The DevUI dependency builder (`TrameDeveloperUi/src/lib/utils/dependencyCheck.ts`) is the one place with both schemas (provider return + consumer parameter from discovery) and statically reproduces these rules — expose-path/casing, cross-kind, object→object subset/missing/kind-mismatch, array/scalar cardinality — as non-blocking inline warnings + a summary box ("Send anyway" stays; runtime shape may differ from the static schema).

**Binding modes** (`TrameOptions.AliasBindingMode`, `Weak` default | `Strict` | `Paranoid`, plumbed to `TrameInvoker.AliasBindingMode` like the cardinality caps): each is a superset of the previous in strictness. **Weak** = duck-typed, silent defaults. **Strict** = each `@alias`-sourced parameter must be fully covered at the **top level** — every public read-write property of the consumer type must be present in the fragment JSON (case-insensitive), else `400` "Strict alias binding: parameter 'P' (Type) requires property 'X'…"; literals are not re-checked, nested objects are not descended into. **Paranoid** = Strict plus (a) it checks **all** parameters including literals the caller sent, and (b) it checks **recursively**, descending into nested object properties and array elements (`GetCollectionElementType` for `List<T>`/`T[]`/`IEnumerable<T>`); a missing property at any depth, in any parameter → `400` "Paranoid binding: parameter 'P' (Type) is not fully covered by its fragment. Missing: 'P.X', 'P.Address.Zip', …". Strict is checked in `ResolveParameterValues` (Serial + topological-batch paths) via `StrictBindingCheck` using the consumer `ParameterInfo` from the route cache + the fragment `JsonNode` recorded during `@alias` substitution (`AliasReplacement`); Paranoid is checked in the same sites via `ParanoidBindingCheck` → `CollectMissing` (recursive) on the resolved parameter node (so it sees alias-replaced + literal params); cost-neutral in `Weak`. The safe subset direction (consumer ⊆ fragment, the fan-out) binds in all three modes; cross-kind is 400 in all modes; widening (`int`→`long`) is accepted in all modes. Tests: `TrameTests/Unit/Core/AliasBindingStrictTests.cs` + `TrameTests/Unit/Core/AliasBindingParanoidTests.cs`.

### Registration Flow

`AddTrame(TrameOptions)` in `TrameHub`:
1. Registers SignalR (optional, with MessagePack support)
2. Configures rate limiting (fixed-window, configurable)
3. Registers `ITrameCore` as singleton (`TrameInvoker`)
4. Registers `TrameLoggingInterceptor`
5. Auto-discovers all `[TrameController]` types across assemblies (skipping any with `AutoDiscover = false`) and registers them as scoped services

`UseTrame()` resolves `ITrameCore` and triggers controller registration (auto-discovery fallback, or `TrameControllerBuilder` if one was registered via the fluent `AddTrame(options, configureControllers)` overload). `MapTrameEndpoints()` (in `TrameRest`) adds the REST endpoints.

### Name Uniqueness (Registration-Time Hard Fail)

Trame dispatches by `"{Controller}_{Method}"` — purely name-based, **no parameter-based overload resolution**. `TrameInvoker.Register` therefore **throws `InvalidOperationException` at registration time** if:
- two methods on the same controller share a `[TrameMethod]` name, or
- two different controller types share a `[TrameController]` name.

Re-registering the *same* controller type is idempotent (no throw). To model what C# calls overloads, give each method a distinct `[TrameMethod]` name (`add`, `addAll`, …). A controller can opt out of auto-discovery via `[TrameController("name", AutoDiscover = false)]` — it is then excluded from the bulk scans in `AddTrame`/`UseTrame`/`FromAssemblies` and must be registered explicitly via `Register<T>()` or `TrameControllerBuilder.Add<T>()`.

### Test Project (`TrameTests`)

Uses **xUnit** + **FluentAssertions** + **Moq**. Tests are organized as:
- `Unit/Core/` — `TrameInvokerTests`, `DependencyGraphBuilderTests`, `DependencyResolverTests`, `TrameDiscoveryServiceTests`, `TrameTracingTests`
- `Unit/Telemetry/` — `TrameTelemetryExtensionsTests`
- `Unit/Client/` — `TrameClientTests`
- `Integration/` — `RestTransportTests` (uses `WebApplicationFactory`)
- `Fixtures/` — test controller definitions

### Code Conventions

- **All code-facing and user-facing text is in English** — comments, log messages, console output, domain error strings (`TrameResults.*` messages), and `[TrameDocumentation]` text. Trame targets the international market; German would exclude most readers. **Existing German comments/strings are legacy** — migrate opportunistically when touching the surrounding code, but this is not a 1.0 blocker. New code must be English.
- Controllers are registered via attribute scanning (`[TrameController]`), not manual registration.
- The `TrameInvoker` is a singleton — controllers are resolved per-call via `IServiceScopeFactory.CreateScope()`.
- Expression Tree compilation happens once at registration time, not per invocation.

### Distributed Tracing

`TrameCore.Tracing.TrameTracing` instruments the engine with an always-on `ActivitySource` named `"Trame"` (public class so `ActivitySourceName` is reachable from the optional `Trame.Telemetry` package; all other members are `internal`). Instrumentation lives **directly in `TrameInvoker`** at three sites (single-call `InvokeDi(TrameRequest)`, batch dispatcher `InvokeDi(IEnumerable<TrameRequest>)`, and per-request `ExecuteSingleInvocation`) — not as an `ITrameInterceptor`, because the batch path bypasses the interceptor pipeline. It is cost-neutral: `StartActivity` returns `null` without a listener, and it is not DI-registered, so the two parallel registration factories (`AddTrame` + `AddTrameCore`) need no synchronization. `Activity.RecordException` is unavailable in a net8.0 class library — `TrameTracing.RecordException` sets `exception.type`/`exception.message`/`exception.stacktrace` tags directly. `TrameServer` does **not** reference `Trame.Telemetry`; consumers opt in via `AddTrameTelemetry` (or their own `AddOpenTelemetry().WithTracing(b => b.AddSource("Trame"))`). Tracing/telemetry tests rely on the process-global `ActivityListener`, so they share the `trame-tracing` xUnit collection and use a test-harness activity to isolate their spans from parallel invoker-based tests.
