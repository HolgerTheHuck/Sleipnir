# Sleipnir.Core

The execution engine of [Sleipnir](../README.md) — the code-first, multi-transport RPC
framework for .NET 8. Transports and the DevUI do not dispatch themselves: they all
delegate to `ISleipnirCore` (registered as a singleton), which lives here.

## What's in here

- **`SleipnirInvoker`** (`ISleipnirCore`) — scans `[SleipnirController]` / `[SleipnirMethod]` at
  registration time, compiles each method into an expression-tree delegate
  (`Func<object, object?[], object?>`) cached in a `ConcurrentDictionary`, and runs
  calls through: interceptor pipeline → authorization → parameter deserialization →
  compiled delegate → result serialization. No reflection per call.
- **Batch execution** — `InvokeDi(IEnumerable<SleipnirRequest>, ExecutionMode)`:
  `Parallel` (`Task.WhenAll`), `Serial` (sequential with `@alias` resolution), or
  `Auto` (topological batches via `DependencyGraphBuilder` when any request carries a
  `DependencyMapping`). Handles `void` / `Task` / `Task<T>` / `IAsyncEnumerable<T>`.
- **`SleipnirDiscoveryService`** — builds `DiscoveryInfo` from the registered
  controllers at runtime: contract types (inferred from method signatures across the
  controller-assembly boundary, overridable with `[SleipnirDataContract]`),
  `[SleipnirDocumentation]` summaries, `[SleipnirExample]` samples. Cached, invalidated on
  new registrations. This JSON is the standard contract — see
  [CLIENT_GENERATION.md](../CLIENT_GENERATION.md).
- **Dependency resolver + graph** — `DependencyResolver.ExtractValue` (JsonPath,
  result-relative, match-count-aware) and `DependencyGraphBuilder` (Kahn topological
  sort, cycle detection, parallel batches).
- **Interceptors** — `ISleipnirInterceptor` / `SleipnirInvocationDelegate` middleware
  pipeline (reverse registration order); built-in `SleipnirLoggingInterceptor`.
- **Tracing** — `SleipnirCore.Tracing.SleipnirTracing`, an always-on `ActivitySource`
  named `"Sleipnir"` (cost-neutral without a listener).

## Install

```xml
<PackageReference Include="Sleipnir.Core" Version="1.4.2" />
```

Targets `net8.0`. Depends on `Sleipnir.Common` and `JsonPath.Net` (for `@alias`
extraction). Authorization / hosting abstractions come from the ASP.NET Core shared
framework.

## Where it fits

Server apps do not reference `Sleipnir.Core` directly — they pick a transport
(`Sleipnir.Rest` / `Sleipnir.WebSocket` / `Sleipnir.Hub`) or `Sleipnir.Server`, which bring it
transitively. See the [root README](../README.md), [ARCHITECTURE.md](../ARCHITECTURE.md),
and [DEPENDENCY_BINDING.md](../DEPENDENCY_BINDING.md).