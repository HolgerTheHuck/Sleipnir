# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.1.2] - 2026-08-08

### Fixed
- **R1 — fluent overload routed through canonical `AddTrame` (correctness)** — the fluent
  `AddTrame(TrameOptions, Action<TrameControllerBuilder>)` overload had drifted into a parallel
  `AddTrameCore` that skipped the canonical wiring (camelCase wire + `TrameResponseJsonConverter`,
  SignalR setup, built-in interceptors, north-bound pass-throughs, rate limiter). `AddTrameCore` is
  deleted; the overload now delegates to `AddTrame` and disables the bulk auto-scan via a new
  `TrameOptions.AutoDiscoverControllers` flag (default `true`). The fluent contract stays explicit
  (only `Add<T>()` / `FromAssemblies` controllers register).
- **R2 — fluent builder applies controller registrations to DI (correctness)** —
  `TrameControllerBuilder.Add<T>()` / `FromAssemblies` only registered controllers with the invoker
  and never with `IServiceCollection`, so a controller resolved per-call via
  `IServiceScopeFactory.CreateScope()` was missing from DI. Each builder call now writes the
  service descriptor immediately (scoped by default; `Add<T>(lifetime)` / `AddSingleton<T>()` /
  factory variants honored).
- **R3 — WebSocket correlation (correctness)** — three client-visible faults on the WebSocket
  transport. (1) A `TrameMultiRequest` without per-request ids hung forever: the client generated a
  correlation id but never wrote it into any request, the server echoed `""`, and the strict
  dispatcher dropped the response. The client now assigns an id to every id-less request before
  serializing. (2) Error frames were anonymous `{ code, data }` envelopes carrying the message in
  `data` with no `id`/`error`, so a C# client could not correlate them and never surfaced the
  message as a `TrameException`. Every error path now builds a real `TrameResponse` (`Code` +
  `Id` + `TrameError`) and extracts the correlation id up-front (top-level id, or the first
  request's id for a multi). (3) The catch-all handler returned `400` for internal failures; it is
  now `500` (server error, generic message — no leak). A latent throw in the client's
  `TryDispatchEventFrame` on array-root batch responses is also guarded.
- **R4 — `TcsHolder.SetCanceled` → `TrySetCanceled` (correctness)** — the pending-call
  cancellation registration called `SetCanceled`, which throws if the TCS was already completed by
  a racing response. It now uses `TrySetCanceled`, and the cancellation propagates the caller's
  token faithfully (`OperationCanceledException.CancellationToken` is the caller's, not an internal
  one), so a cancelled pending call no longer corrupts the client — the connection stays usable for
  follow-up calls.

### Changed
- **R5 — interceptor batch-bypass is now loud (docs + startup warning)** — `ITrameInterceptor`
  runs on the single-call path only in 1.1.x; batch request elements (`/json/multi`, WebSocket
  multi, JSON-RPC batch) bypass the interceptor seam. Authorization is unaffected (enforced
  structurally in the serial auth pre-pass), but custom interceptor logic is silently skipped on
  batches. `UseTrame` now logs a one-time warning when a user registers custom interceptors
  (`Interceptors`/`BatchInterceptors`), and the interface XML docs + `SECURITY.md` /
  `SECURITY_GUIDE.md` document the limitation. Routing the batch path through the pipeline is
  tracked for 1.2 (`ROADMAP.md` R7). `ITrameBatchInterceptor` still has no consumer.

### Tests
- **R6 — regression coverage for the 1.1.1 "kritisch" fixes and the fluent overload.** The 1.1.1
  thread-safety (single-sender channel) and batch policy-auth fixes shipped without regression
  tests; the fluent overload (R1/R2) had none either. Added:
  `WebSocketConcurrentSendTests` (50 concurrent `EchoAsync` calls + an active event subscription on
  one connection — asserts every received frame is a complete JSON document, each call correlates
  to its own echo, and all events survive the concurrent traffic; exercises the single-sender
  channel that the 1.1.1 fix introduced), `BatchPolicyAuthTests` (parallel + topological batch
  policy evaluation, the null-evaluator fail-closed branch, and dependent propagation when a
  policy-denied provider is skipped), `WebSocketCorrelationTests` (malformed JSON returns a
  structured correlated error frame, not an anonymous data envelope), `WebSocketTransportTests`
  multi-without-ids completion, `TrameWebSocketClientReconnectTests` faithful cancellation
  propagation, `TrameInterceptorBypassWarningTests` (startup warning fires/silent), and
  `FluentRegistrationTests` (only explicitly-added controllers register; DI resolution;
  camelCase wire + `RequireAuthentication` parity with canonical `AddTrame`). `CONTRIBUTING.md`
  now requires a regression test for every bug fix.

## [1.1.1] - 2026-08-08

### Fixed
- **Thread-safety: konkurrierende WebSocket-Sends (kritisch)** — `SendTextAsync`/`SendErrorAsync`
  (Middleware) und `SendLoopAsync` (SubscriptionManager) sendeten beide direkt via
  `webSocket.SendAsync` ohne Lock → korrupte Frames bei gleichzeitigen Calls + Events.
  Fix: alle Sends durch den gemeinsamen `_sendChannel` des `TrameSubscriptionManager`
  (`EnqueueSendAsync`). Single-Sender-SendLoop ist jetzt der einzige Sender.
- **Policy-Auth im Batch-Pfad (kritisch, Security)** — `[TrameAuthorise(Policy=…)]` wurde im
  Batch-Pre-Pass nicht ausgewertet (nur im Single-Call-Pfad via Interceptor).
  Fix: `CheckAuthorisation` um Policy-Evaluation ergänzt via `PolicyEvaluator`-Delegate
  (von `AddTrame` gesetzt, wenn `IAuthorizationService` verfügbar). TrameCore bleibt frei
  von der Abhängigkeit.
- **Send-Channel-Kapazität** — war fix 100 (`_subscriptions.Count` ist 0 im Ctor). Fix:
  `bufferCapacity + 256`.
- **Magic Numbers in `TrameSubscriptionManager`** — `409`/`404`/`200` → `TrameErrorCodes`.

### Added
- **TrameAuthorizationInterceptorTests** (8 tests) — Policy-Auth isoliert mit Mock-
  `IAuthorizationService`: 401/403/Policy-Success/Policy-Fail/NoAuthService-500.
- **TrameInMemoryClientTests** (6 tests) — On/On<T>/OnError/404/Batch/CallBinary-NSE.

### Changed
- **`ITrameBatchInterceptor` Dead-Code-Dokumentation** — WARN-Kommentar im Invoker: Batch-
  Interceptors werden aktuell nicht aufgerufen; Pipeline = v1.2 geplant. API bleibt erhalten.

## [1.1.0] - 2026-08-07

### Added — Interceptor Pipeline (Phase 1)
- **`ITrameInterceptor` + `TrameInvocationContext`** — unified interceptor pipeline for Auth, Telemetry, Logging, Validation, custom. Context carries `Request`, `HttpContext`, `InvokeInfo`, `Response`, `Activity`, `CancellationToken`. Signature changed from `(TrameRequest, Delegate, CT)` to `(Context, Delegate)` — breaking for custom interceptors (experimental per `STABILITY.md` §2). Built-in interceptors: `TrameAuthorizationInterceptor`, `TrameTelemetryInterceptor`, `TrameLoggingInterceptor` (registered in fixed order Auth → Telemetry → Logging, outer to inner).
- **`TrameOptions.Interceptors` / `.BatchInterceptors`** — collections for user interceptors; `RegisterBuiltInInterceptors` (default `true`) toggles built-ins.
- **`ITrameBatchInterceptor`** — batch-level interceptor surface (experimental; no built-in batch interceptor ships yet).

### Added — Policy-based Authorization (Phase 1)
- **`[TrameAuthorise(Policy = "...")]`** — ASP.NET Core `IAuthorizationService`-based policy evaluation (`resource: null` in v1.1). `IAuthorizationService` optional; Policy without service = 500 (config error).
- **403 Forbidden vs 401 Unauthorized** — `ForbiddenAccessException` (new) distinguishes "authenticated but role/policy denied" (403, `PermissionDenied`) from "not authenticated" (401, `Unauthenticated`). `TrameResults.Forbidden()` (new). Breaking for callers that treated all auth failures as 401.
- **`TrameAuthorizationInterceptor`** — built-in Auth interceptor; evaluates `[TrameAuthorise]`/`[TrameAnonymous]`/`RequireAuthentication` + `Policy`.

### Added — Error Taxonomy (Phase 1)
- **`TrameErrorCodes`** — stable named constants for numeric codes (replaces magic numbers in `TrameResults` + Invoker).
- **`TrameErrorCategory`** — semantic enum (`InvalidArgument`/`Unauthenticated`/`PermissionDenied`/`NotFound`/`Conflict`/`FailedPrecondition`/`ResourceExhausted`/`Internal`/`Unavailable`/`Cancelled`), gRPC-aligned. Additive field `TrameError.Category` (Key 4, `JsonPropertyName "category"`, default `None`) — existing 1.0.0 clients ignore it.
- **`TrameResults.Error(code, message, category, details)`** — new category overload; convenience methods set category automatically.
- **`ERROR_CATALOG.md`** — authoritative catalog of codes + categories.
- **JSON-RPC mapping on Category** — `JsonRpcAdapter.MapErrorCode` uses `TrameErrorCategory` (primary), falls back to numeric code for `None` (1.0.0 compat). Resolves the string-prefix coupling to Invoker error messages.

### Added — OpenTelemetry Metrics (Phase 1)
- **`TrameMetrics` (`Meter "Trame"`)** — `trame.call.duration` (Histogram ms), `trame.call.count`, `trame.error.count` (Counter), `trame.batch.fan_out` (Histogram), `trame.batch.count` (Counter). OTel RPC semantic conventions. Cost-neutral without MetricReader.
- **`TrameTelemetryInterceptor`** — built-in; Tracing (am Context-Activity, no double-count) + Metrics + structured logging with OTel field names.
- **Batch-path metrics** — `RecordBatch` in `InvokeDi(IEnumerable)`, `RecordCall` in `ExecuteAuthorized`/`TraceCallError`.

### Added — Events / Server-Push (Phase 3, experimental)
- **`[TrameEvent]`** attribute — marks a subscribe method returning `IObservable<T>`.
- **Discovery `kind:"event"`** — `IObservable<T>` declared as event (analog to `kind:"stream"` for `IAsyncEnumerable<T>`).
- **`ITrameCore.SubscribeAsync`** — resolves + auth + binds + invokes the method, returns the raw `IObservable<object?>` (not serialized).
- **`TrameSubscriptionManager`** (WS) — pro-connection, bounded Channel + drop-oldest, Send-Loop, Auto-Cleanup on disconnect. Event/complete/error frames: `{type:"event",subscriptionId,eventId,data}`.
- **WS Dispatcher** — `kind:"subscribe"`/`kind:"unsubscribe"` recognition; Calls (without `kind`) unchanged (1.0.0 compat).
- **`trame.event.dropped`** metric — backpressure (bounded buffer + drop-oldest).
- **`TrameWebSocketClient.SubscribeAsync<T>`** — client-side subscribe; returns `TrameSubscription<T>` (IObservable). `TrameSubject<T>` (no System.Reactive dependency). Event-frame dispatch in `DispatchResponse`. Reconnect → `ResubscribeAllAsync` (client-side re-subscribe, new subscriptionIds).
- **`TrameInMemoryClient`** — `ITrameClient` test-double for unit tests without a server. `On`/`On<T>`/`OnError` handler registration.
- **C# Codegen** — `CsEmitter.EmitMethod` recognizes `kind:"event"` → `SubscribeAsync<T>` instead of `Call`.
- **WS-only in v1** — SignalR and REST-Long-Polling out of scope. `Last-Event-Id`-resume is v1.x+. See `docs/design/phase-3-events.md` (8 decisions).

### Added — Adoption & Documentation
- **`STABILITY.md`** — stable vs. experimental surface, compatibility rules, versioning model.
- **`ROADMAP.md` "Benutzbarkeit-Roadmap"** — phase plan (0–4) with dependency graph.
- **`docs/design/phase-1-interceptor-pipeline.md`** + **`phase-3-events.md`** — design docs with decisions.
- **README** — "Trame + REST — not a replacement, a complement" section; package matrix; NuGet badge; 1.1.0 features in "Features at a glance".
- **`BEST_PRACTICES.md` §1.6** — compression guidance (transport layer, not Trame).
- **Repository pattern** — `INotificationStore` in `samples/01-notification-chat` (North-Bound Secure Store, Phase 2).

### Changed
- **Discovery types are now structured, language-neutral `TypeRef` objects**, not .NET type-name strings. `MethodMeta.ReturnType`, `ParameterMeta.ParameterType`, and `PropertyMeta.PropertyType` are `TypeRef` (`kind` ∈ `scalar | array | set | map | stream | event | ref | opaque | void`), carrying element/key/value sub-refs, occurrence-level `nullable`, and — for `opaque` — an informational `nativeName`. `Dictionary<K,V>` → `{kind:"map",...}`, `HashSet<T>` → `{kind:"set",...}`, `IAsyncEnumerable<T>` → `{kind:"stream",...}`, `IObservable<T>` → `{kind:"event",...}`, `byte[]` → `{kind:"scalar", name:"bytes"}`. This closes the former "known gaps" (collection-kind, nullability, enum members, default values, stream) and unblocks non-C# producers: the wire shape no longer leaks .NET generic syntax. Authoritative spec: [`docs/discovery-schema.md`](docs/discovery-schema.md).
- **`discoveryVersion` field** atop `DiscoveryInfo` (additive-only compatibility rule; `"1"`). Consumers `assertDiscoveryShape` accepts known versions and rejects unknown ones loudly.
- **Enums on the wire.** Enum types register in `discovery.types` as `TypeMeta` with `kind:"enum"` + `members:[{name,value}]`; a usage site is `{kind:"ref", ref:"<enumKey>"}`. Trame serializes enums as their underlying **integer**, so an enum ref is wire-numeric — the members are documentation only; codegen does not emit native enum declarations.
- **`ParameterMeta.defaultValue`** carries a C# compile-time constant for parameters with a default, read from `ParameterInfo.HasDefaultValue`.
- **No-drift conformance gate.** `TrameTests/Integration/DiscoveryContractTests.cs` runs the Story-01 server out-of-process, fetches `GET /api/trame/discovery`, and asserts structural equality against the committed golden `clients/codegen/test/fixtures/story01-discovery.json` (derived from real wire output, never hand-edited). Fail-on-diff in CI; regen mode via `TRAME_REGEN_GOLDEN=1`. The codegen's `assertDiscoveryShape` validates the full `TypeRef` shape on ingress. Together these pin behavior and contract so they cannot silently diverge.

### Fixed
- **`TrameDiscoveryService.InvalidateCache()` is now wired into `TrameInvoker.Register`.** A registration after the first `GetDiscoveryInfo()` previously stayed invisible until app restart; the cache is now invalidated on every new registration so newly registered controllers/methods appear on the next discovery call.

## [1.0.0] - 2026-07-11

First public release. The wire format, error semantics, and parameter binding are
reconciled with `PROTOCOL.md` so the public contract is consistent and stable.

### Background

Trame was created to solve a fundamental mismatch: REST is designed for resource-oriented APIs (CRUD on nouns), but real-world applications often need to call *actions* (RPC-style). REST forces awkward patterns like `POST /api/customer/42/add-address`, N+1 roundtrips for dependent calls, and no way to batch multiple calls.

**Why not gRPC?** gRPC was not well-supported when Trame was created (no native browser support, immature .NET tooling), and its schema-first approach (`.proto` files + code generation) conflicts with .NET's natural code-first workflow. In .NET, your C# classes already define the contract — maintaining a separate IDL is unnecessary overhead.

Trame takes inspiration from GraphQL (dependency resolution, batch queries) and gRPC (method-oriented calling) but stays within the .NET ecosystem as a **code-first** framework: no `.proto` files, no code generation, just attributes on C# classes.

### Added
- Multi-transport RPC framework: REST, WebSocket, SignalR
- Batch-call support: parallel and serial execution of multiple RPC calls in one roundtrip
- Dependency-chaining: `@alias` placeholders resolved server-side via JsonPath
- Topological sort with cycle detection for dependency-aware batch execution
- Streaming support: `IAsyncEnumerable<T>` and `Task<IAsyncEnumerable<T>>`
- Interceptor pipeline: `ITrameInterceptor` with built-in `TrameLoggingInterceptor`
- Discovery/MEX: `GET /api/trame/discovery` returns full API metadata
- Developer UI: built-in web interface at `/Trame`
- Rate-limiting: configurable fixed-window rate limiter
- Authorization: `[TrameAuthorise]` attribute with role-based access
- Expression-Tree invocation: pre-compiled method delegates for performance
- Fluent client API: `TrameCall.Init().With().Exposes().WithAlias().ToRequest()`
- `TrameDataContract`, `TrameDocumentation`, `TrameExample` attributes for API metadata
- Isomorphic TypeScript/JavaScript client (REST + WebSocket) under [`clients/ts/`](clients/ts/) (`npm i trame-client`)
- Client: `TrameCall.Param(string parameterName, object? value)` named overload, so callers can bind by explicit server-side parameter name regardless of order
- Support for dotted controller namespaces (e.g. `[TrameController("Customer.Address.Contact")]`) — verified and tested; no fixed two-level routing limit
- Cardinality caps: `MaxParameterArrayLength` (default 1000) and `MaxResultElementCount` (default 10000) protect against runaway batch/array payloads
- `[TrameDataContract(Exclude = true)]` — opt-out override for discovery inference (force-opaque despite belonging to a controller assembly)
- `[TrameController("name", AutoDiscover = false)]` — opt-out from the auto-discovery bulk scans in `AddTrame` / `UseTrame` / `TrameControllerBuilder.FromAssemblies`. Such controllers must be registered explicitly via `Register<T>()` or `TrameControllerBuilder.Add<T>()`. Useful for test fixtures and manual-only registration.
- **Binding modes** (`TrameOptions.AliasBindingMode`): `Weak` (default, duck-typed), `Strict` (`@alias` params must be fully covered at the top level → 400 on missing), and `Paranoid` (Strict plus: checks *all* parameters including literals, and recurses into nested objects and array elements → 400 on any missing property at any depth). The safe subset fan-out binds in all three; cross-kind is 400 in all three; widening stays accepted. Closes the object→object silent-default hazard opt-in, at increasing strictness. Spec: `DEPENDENCY_BINDING.md` §7; tests: `AliasBindingStrictTests.cs`, `AliasBindingParanoidTests.cs`.
- **Dependent propagation on provider failure.** In a dependency batch, when a provider is unauthorized, returns a non-2xx, or doesn't expose the fragment it declared, its dependents no longer run — each gets a `400` naming the provider, alias, and cause instead of hitting the missing alias at runtime with an uninformative `Unresolved`. Messages: `Dependency '@a' unavailable: no provider exposes '@a'.` / `… provider '<id>' was unauthorized (401).` / `… provider '<id>' returned HTTP <code>.` / `… provider '<id>' did not expose '@a'.` Propagation is transitive: one failed provider cancels its whole branch (a skipped provider has no `ExposedDependencies`, so its own dependents are caught in the next batch). Spec: `DEPENDENCY_BINDING.md` §9; tests: `ParallelAuthPropagationTests.cs`.
- **Distributed tracing via OpenTelemetry.** An always-on `ActivitySource` named `Trame` instruments every call and batch with `TrameCall` / `TrameBatch` spans following the OTel RPC semantic conventions (`rpc.system=trame`, `rpc.service`, `rpc.method`, `trame.request_id`, `trame.binary.length`, `trame.batch.*`, plus `exception.*` tags on escaping failures). Cost-neutral: `StartActivity` returns `null` without a listener, and it is not DI-registered. Export opt-in via the optional `Trame.Telemetry` package (`AddTrameTelemetry`, with OTLP/Console exporters and AspNetCore/HttpClient instrumentation) or your own `AddOpenTelemetry().AddSource("Trame")` — the source name is the only integration point. See *Distributed Tracing* in the README.
- 292 unit and integration tests
- **JSON-RPC 2.0 compatibility adapter.** Opt-in (`TrameOptions.EnableJsonRpcCompat`, default off) `POST /api/trame/jsonrpc` endpoint that maps JSON-RPC 2.0 requests onto the same `TrameInvoker`: `method` is `Controller.Method` (split at the last dot), `params` object → named / array → positional (bound by `num`), `id` (number/string) echoed with its original type, batches in Parallel, notifications emit no response (all-notification batch → `HTTP 204`). Trame codes map to the JSON-RPC error ranges — routing `404` (Controller/Method not found) → `-32601`, business `404` → `-32000`, `401`/`403` → `-32001`, `400`/`422` → `-32602`, `500` → `-32603` — in the `200` envelope (JSON-RPC-conformant). Two capability methods bridge to the native surface: `trame.discover` (→ DiscoveryInfo) and `trame.capabilities` (→ static strengths manifest). Limitations: no `@alias` chaining, no execution-mode selection, no binary out-of-band, no streaming — graduate to the native wire for those. An adoption lure for the established JSON-RPC client ecosystem; users see Trame's strengths as they grow. Spec + Trame-vs-JSON-RPC differences table: `JSONRPC_COMPAT.md`; tests: `JsonRpcAdapterTests.cs` + `JsonRpcTransportTests.cs` (43 tests).

### Fixed
- **Parameter binding (P0):** The fluent client API sent `ParameterName = "param0/param1"`, but the server bound strictly by the real C# parameter name — a mismatch silently fell back to defaults with `200 OK`. The server now binds each method parameter by name first, then falls back to the positional `num` index (counting non-`CancellationToken` parameters). `byte[]` parameters are never bound positionally (injected from `binaryData`).
- **Duplicate parameter name:** A duplicate `parameterName` no longer throws inside `ToDictionary`; it returns a `400 Bad Request`.
- **Error semantics (P0):**
  - Authorization failures return `401 Unauthorized` (previously `405 Method Not Allowed`, which was dead/wrong and has been removed).
  - `TrameError.requestId` is populated from the originating request `id` on every non-2xx response, so failures can be correlated even within batch calls.
  - `TrameError.details` (stack trace) is populated only when detailed errors are enabled (`EnableDetailedErrors` option or `IHostEnvironment.IsDevelopment()`); in production the error `message` is generic and does not leak exception internals. `TargetInvocationException` no longer leaks the inner exception message.
  - `void` / `Task`-without-result methods now return `204 No Content` with `data = null` (previously returned a localized success string as `data`).
- **REST transport (P0):** Cancelled requests (`OperationCanceledException`) return `499` on both the Minimal-API and MVC endpoints. The HTTP envelope over REST is always `200 OK` with the `TrameResponse` (including any error) in the body — the logical `code` is a body field, not the HTTP status (JSON-RPC style).
- **Wire language (P0):** All wire-facing strings (`TrameError.message`, error `data`, transport problem titles) are English. German is retained only for internal logs and comments, per the project convention.
- **`TrameCall.WithAlias`:** Removed the unused `fallbackValue` parameter. An unresolvable `@alias` is a hard error (returns `400`), not a silent default.
- `TrameDiscoveryService`: `RegisterType()` was not called for non-generic `[TrameDataContract]` return types and parameters, causing `KeyNotFoundException`.
- `IAsyncEnumerable<T>` interface detection: now checks `GetInterfaces()` for compiler-generated state machine types.
- **`HttpContext` removed from the parallel fan-out.** Authorization and route lookup now run in a serial pre-pass before the `Task.WhenAll` execution in both `ExecuteInParallel` and `ExecuteInDependencyBatches`, so the framework never touches the shared, non-thread-safe `HttpContext` concurrently. Batch authorization is **per request**: a `401` affects only that request, not the whole batch (JSON-RPC-conformant). User code that retrieves the context via `IHttpContextAccessor` must treat it as read-only within parallel batches.
- **Exposes extraction is gated on success.** A provider populates `ExposedDependencies` only on a `2xx` response. Any non-2xx — a business error returned via `TrameResults` (`NotFound`/`BadRequest`/`Error(ProblemDetails)`/…), a thrown exception (→ `500`), or a `401`/missing-controller/missing-method decision from the serial auth pre-pass — leaves `ExposedDependencies` empty, even if the error payload itself contains fields the declared JsonPath would match (e.g. a `ProblemDetails` body with `title`/`status`). No value is ever extracted from an error payload and forwarded to a dependent (the dependent gets the propagation `400` instead). Closes a data-leak where an error response whose payload matched the path would otherwise have surfaced a fragment. Test: `ParallelAuthPropagationTests.Topology_ProviderErrorWithData_ExposesNothing_DependentGetsHttpCode`.

### Changed
- **Duplicate Trame names now fail fast at startup.** `TrameInvoker.Register` throws `InvalidOperationException` when two `[TrameMethod]`s on the same controller share a name, or two `[TrameController]`s app-wide share a name. Previously the collision was silently resolved first-registered-wins (non-deterministic, since `GetMethods()` order is not guaranteed). Dispatch remains name-only (`{Controller}_{Method}`); Trame does **not** resolve overloads by parameter signature — model C# overloads with distinct names (`add`, `addAll`). This is a breaking change only for apps that unintentionally relied on the silent shadowing. See *Known Limitations* in the README.
- **Discovery: signature inference as default (Weg C).** `[TrameDataContract]` is no longer required — discovery automatically expands any class type that appears in a method signature and whose assembly belongs to the registered controllers' assemblies (property schema, example, nested types). Types from other assemblies (BCL, Trame framework envelope, third-party) stay opaque. `[TrameDataContract]` is now an optional override: bare = force-expand (e.g. for third-party types you don't own), `Exclude = true` = force-opaque. No effect on the RPC call path — only discovery metadata.
- `TrameInvoker` exposes `EnableDetailedErrors`, wired from `TrameOptions.EnableDetailedErrors` or `IHostEnvironment.IsDevelopment()` during `AddTrame` registration.
- `PROTOCOL.md` rewritten: status codes are logical body codes (envelope-at-200); `405` removed; `401` for auth; `403` marked roadmap; `204` for void; transport-level `429`/`499`/`400` clarified; `TrameError.requestId` added to the TypeScript client type.
- Consolidated all shared models (`TrameRequest`, `TrameResponse`, `TrameMultiRequest`, `TrameParameter`, `ExecutionMode`) into `TrameCommon`.
- Unified exception handling with `TrameException` + `TrameError`.
- Migrated from `Newtonsoft.Json` to `System.Text.Json` as sole JSON serializer.
- `Trame.Common` multi-targets `net8.0` and `netstandard2.1`.

### Removed
- `TrameGrpc` transport stub project — gRPC as a Trame transport was never implemented and contradicts the code-first/no-IDL positioning. gRPC remains only as a benchmark comparison baseline in the sample + bench (`Trame/Grpc/`, `TrameBench/`).
- `MethodNotAllowed()` (405) and the unused `Forbidden()` factory from `TrameInvoker`.
- `Newtonsoft.Json` dependency from `TrameRest`.
- Duplicate model definitions from `TrameCore` and `TrameClient`.
- Duplicate `TrameException` from `TrameClient`.

### Performance
- Trame REST is 2.5x-16x faster than classic REST for single calls
- Trame Batch is 7x faster than REST parallel for 10 calls
- Trame WebSocket is 2.3x faster than REST for persistent small calls
- Trame Dependency-Chain replaces N+1 pattern: 1 roundtrip instead of 3
- Consistently 3-9x less memory allocation compared to REST