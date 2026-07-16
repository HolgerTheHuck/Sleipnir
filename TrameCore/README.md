# Trame.Core

The execution engine of [Trame](../README.md) — the code-first, multi-transport RPC
framework for .NET 8. Transports and the DevUI do not dispatch themselves: they all
delegate to `ITrameCore` (registered as a singleton), which lives here.

## What's in here

- **`TrameInvoker`** (`ITrameCore`) — scans `[TrameController]` / `[TrameMethod]` at
  registration time, compiles each method into an expression-tree delegate
  (`Func<object, object?[], object?>`) cached in a `ConcurrentDictionary`, and runs
  calls through: interceptor pipeline → authorization → parameter deserialization →
  compiled delegate → result serialization. No reflection per call.
- **Batch execution** — `InvokeDi(IEnumerable<TrameRequest>, ExecutionMode)`:
  `Parallel` (`Task.WhenAll`), `Serial` (sequential with `@alias` resolution), or
  `Auto` (topological batches via `DependencyGraphBuilder` when any request carries a
  `DependencyMapping`). Handles `void` / `Task` / `Task<T>` / `IAsyncEnumerable<T>`.
- **`TrameDiscoveryService`** — builds `DiscoveryInfo` from the registered
  controllers at runtime: contract types (inferred from method signatures across the
  controller-assembly boundary, overridable with `[TrameDataContract]`),
  `[TrameDocumentation]` summaries, `[TrameExample]` samples. Cached, invalidated on
  new registrations. This JSON is the standard contract — see
  [CLIENT_GENERATION.md](../CLIENT_GENERATION.md).
- **Dependency resolver + graph** — `DependencyResolver.ExtractValue` (JsonPath,
  result-relative, match-count-aware) and `DependencyGraphBuilder` (Kahn topological
  sort, cycle detection, parallel batches).
- **Interceptors** — `ITrameInterceptor` / `TrameInvocationDelegate` middleware
  pipeline (reverse registration order); built-in `TrameLoggingInterceptor`.
- **Tracing** — `TrameCore.Tracing.TrameTracing`, an always-on `ActivitySource`
  named `"Trame"` (cost-neutral without a listener).

## Install

```xml
<PackageReference Include="Trame.Core" Version="1.0.0" />
```

Targets `net8.0`. Depends on `Trame.Common` and `JsonPath.Net` (for `@alias`
extraction). Authorization / hosting abstractions come from the ASP.NET Core shared
framework.

## Where it fits

Server apps do not reference `Trame.Core` directly — they pick a transport
(`Trame.Rest` / `Trame.WebSocket` / `Trame.Hub`) or `Trame.Server`, which bring it
transitively. See the [root README](../README.md), [ARCHITECTURE.md](../ARCHITECTURE.md),
and [DEPENDENCY_BINDING.md](../DEPENDENCY_BINDING.md).