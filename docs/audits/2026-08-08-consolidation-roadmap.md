# Consolidation Roadmap — Post-Analysis 2026-08-08

> Detailed execution plan derived from the full code/security/packaging analysis of
> 2026-08-08 (v1.1.1, commit `d0c071c`). This file **extends** `ROADMAP.md`: the
> Benutzbarkeit-Roadmap's Phase 1 was declared landed, but the analysis shows it is only
> half-landed (batch path bypasses the interceptor pipeline, metrics asymmetry, several
> silent no-ops). This roadmap inserts a **consolidation track** before Phase 2/3 work
> continues.
>
> Priority rule: items R1–R6 fix verified correctness/security defects and ship as a
> hotfix train. R7–R10 are architectural consolidation. R11–R13 are process & ecosystem.
>
> **Amendments:** `2026-08-22-dependency-chaining-audit.md` extends this roadmap with
> dependency-chaining findings (D1–D7) and amendments to R8b/R9.3/R9.4/R9.6 — read both
> together for the affected items.
>
> **Global Definition of Done (every item):** regression test(s) for the defect being
> fixed (the 1.1.1 hotfixes shipped without any — that must not repeat), CHANGELOG entry,
> docs updated where user-visible, all new user-facing text in English.

---

## Status board

| # | Item | Train | Severity/Effort | Depends on |
|---|------|-------|-----------------|------------|
| R1 | Delete drifted `AddSleipnirCore`, route fluent overload through canonical `AddSleipnir` | 1.1.2 | critical / S | — |
| R2 | Apply `_serviceRegistrations` to DI (fluent builder actually works) | 1.1.2 | critical / S | R1 |
| R3 | WebSocket correlation: multi-without-ids hang + id-less error frames | 1.1.2 | critical / M | — |
| R4 | `TcsHolder.SetCanceled` → `TrySetCanceled` | 1.1.2 | critical / XS | — |
| R5 | Interceptor batch-bypass: loud docs + startup warning | 1.1.2 | high / XS | — |
| R6 | Regression tests for both 1.1.1 "kritisch" fixes + fluent overload | 1.1.2 | high / M | R1, R2 |
| R7 | Route the batch path through the interceptor pipeline (Phase 1 completion) | 1.2 | major / L | R1–R6 |
| R8 | Security hardening set (Origin allowlist, DoS caps, event-path fixes, gates) | 1.2 | high / L | R7 (shared files) |
| R9 | Core consolidation (invoker seams, dedup, caches, English wire messages) | 1.2 | major / L | R7 (avoid double-touching invoker) |
| R10 | Client unification (error surface, disposal, options, InMemory semantics) | 1.2 | major / M-L | R3, R4 |
| R11 | CI: JS tests, security automation, Windows leg, coverage | 1.3 | medium / M | — |
| R12 | Packaging: XML docs, SourceLink, CPM, stub cleanup | 1.3 | medium / M | R13 (language decision) |
| R13 | Docs & release process (version drift, CHANGELOG language, security guide) | 1.3 | medium / S-M | — |

Effort scale: XS < 1 h · S ≤ 1 day · M 1–3 days · L 3–5 days.

---

# Train 1 — hotfix/1.1.2 (correctness & security defects)

Branch from `main` as `hotfix/1.1.2` (precedent: `hotfix/1.1.1`). All six items are
small, independently shippable, and fix defects that are **verified in code**.

## R1 — Delete drifted `AddSleipnirCore`; route the fluent overload through the canonical `AddSleipnir`

**Why (critical).** `SleipnirHub/Extensions/SleipnirControllerBuilder.cs:136-184` re-implements
registration and has drifted in seven load-bearing ways:

1. Missing `ConfigureHttpJsonOptions` → REST wire becomes PascalCase and the
   `SleipnirResponseJsonConverter` (`DataBytes` byte-identity) is absent — **the wire contract
   depends on which `AddSleipnir` overload the user calls**.
2. `o.MaximumParallelInvocationsPerClient = options.MaximumParallelInvocationsPerClient`
   unconditionally (line 145): the default is 0 and SignalR throws at startup ("must be ≥ 1").
   The canonical path's guard exists precisely because this bug already bit once — it was
   never back-ported.
3. `AddMessagePackProtocol()` without `JsonElementResolver` → `JsonElement` diverges from
   canonical path and from the client.
4. Missing pass-throughs: `RequireAuthentication`, `MaximumBatchSize`,
   `MaxDependencyPathLength`, `AllowRecursiveDescent`, `PolicyEvaluator` → north-bound
   hardening silently off.
5. Built-in interceptors not registered (only `SleipnirLoggingInterceptor`) → no
   `SleipnirAuthorizationInterceptor`.
6. `SleipnirOptions` not added as DI singleton → `MapSleipnir` sees null options → hub never
   mapped, REST rate-limit/JSON-RPC toggles silently off.

**Change.**

- Delete the private `AddSleipnirCore` method entirely.
- `AddSleipnir(services, options, configureControllers)` calls the canonical
  `SleipnirServiceCollectionExtension.AddSleipnir(services, options)`, then registers the builder.
- The canonical path auto-scans all AppDomain assemblies; the fluent overload's contract is
  explicit registration. Add an **additive** opt-out, e.g.
  `SleipnirOptions.AutoDiscoverControllers = true` (default keeps current behavior), and have
  the fluent overload set it to `false` for its call — or accept auto-scan plus explicit
  adds (registration is idempotent for the same type, and `AutoDiscover = false`
  controllers are excluded from the scan anyway). Decide at implementation: the explicit
  opt-out is cleaner and matches the fluent intent.

**Files.** `SleipnirHub/Extensions/SleipnirControllerBuilder.cs`,
`SleipnirHub/Extensions/SleipnirServiceCollectionExtension.cs`, `SleipnirHub/Extensions/SleipnirOptions.cs`.

**Tests.** Startup test: fluent `AddSleipnir` with `UseSignalR = true` and default options
builds the provider without throwing (covers the line-145 crash). Options-parity test:
host built via fluent overload produces camelCase wire and honors `RequireAuthentication`.

**Acceptance.** One registration implementation remains; the fluent overload is
behaviorally identical to the canonical one plus explicit controller registration.

**Compatibility.** `AutoDiscoverControllers` (if added) is additive with a safe default
(STABILITY.md §3.2). No public API removal.

## R2 — Apply `_serviceRegistrations` to DI

**Why (critical).** `SleipnirControllerBuilder.cs:19,91-99`: `_serviceRegistrations` is
write-only. `FromAssemblies()`, `Add<T>()`, `Add<T>(lifetime)`, `AddSingleton<T>()` never
register the controller with `IServiceCollection`, so `scope.ServiceProvider.GetService`
returns null → every call fails with "Controller not registered in DI"; `AddSingleton<T>`'s
lifetime is silently ignored. Only the factory overload works.

**Change.** Register with DI immediately at builder-call time (the builder already holds
`_services` — same as the factory overload does): each `Add<T>`/`FromAssemblies` does
`_services.Add(new ServiceDescriptor(type, type, lifetime))`. Keep `_registrations` for
the `UseSleipnir`-time invoker registration. Delete the now-dead `_serviceRegistrations` list.

**Tests.** Integration: a fluent-registered controller answers a call end-to-end;
`AddSingleton<T>` yields one instance across two calls; `AutoDiscover = false` controllers
are excluded from `FromAssemblies` but register via `Add<T>`.

**Acceptance.** Every builder method has an observable, tested effect.

## R3 — WebSocket correlation: multi-without-Ids hang + id-less error frames

**Why (critical — "hangs forever" class).**

- Client (`SleipnirClient/Sleipnir/SleipnirWebSocketClient.cs:240-243`): for `SleipnirMultiRequest`
  the correlation id is `mr.Requests?.FirstOrDefault()?.Id ?? NextId()` — a freshly generated
  id is **never written into any request**, so the server echoes `""` and the client's
  strict dispatcher drops the response. With default `callTimeout = null` the caller hangs
  forever. REST fills ids correctly; SignalR needs none — same call, fundamentally
  different behavior per transport.
- Server (`SleipnirWebSocket/SleipnirWebSocketMiddleware.cs:104,152,170,206,214,225,241,246`):
  every error path serializes an anonymous `{ code = 400, data = "..." }` — no `id`, no
  `error` envelope, catch-all reports internal errors as 400, message lands in `data` where
  `SleipnirException` never looks.

**Change.**

- Client: mirror the REST logic — before serializing, assign `NextId()` to every request
  in a multi that lacks an id (documents the caller-object mutation, R10).
- Middleware: extract `id` (and `kind`) up-front from the already-parsed `JsonDocument`;
  build all error frames as a real `SleipnirResponse { Code, Error = new SleipnirError { Message }, Id }`.
  Use 500 for the catch-all. Keep messages generic unless detailed errors are on.
- Note: the client mutates caller-owned requests — acceptable, but document it on `Call`.

**Wire compatibility.** Adding `id`/`error` to error frames is additive. Changing the
catch-all 400→500 is a bug fix against the stable error catalog (500 is in the stable
mapping); note in CHANGELOG.

**Tests.** WS multi without ids completes (this is the regression that would have caught
the bug). Server-side parse error reaches the awaiting caller as `SleipnirException` with the
message instead of hanging (use a short call timeout in the test to fail loud on
regression). Batch-cap exceeded → immediate correlated 400.

**Effort.** M — touches the two most delicate pieces of the WS stack; tests must use the
existing real-Kestrel fixture pattern.

## R4 — `TcsHolder.SetCanceled` → `TrySetCanceled`

**Why.** `SleipnirClient/Sleipnir/SleipnirWebSocketClient.cs:94`: `Tcs.SetCanceled()` (non-Try)
races with the reader thread's `TrySetResult` (line 397). The loser throws
`InvalidOperationException` inside a thread-pool cancellation callback — unobserved,
potentially process-terminating. `SetResult`/`SetException` already use `Try*`.

**Change.** `public void SetCanceled() => Tcs.TrySetCanceled();` — prefer the overload taking
the cancellation token so `OperationCanceledException.CancellationToken` is faithful.

**Tests.** Hard to race deterministically; at minimum a cancellation test through the
existing disposed-socket `socketFactory` hook (pattern from `SleipnirWebSocketClientReconnectTests`).

**Effort.** XS.

## R5 — Interceptor batch-bypass: loud documentation + startup warning

**Why (high footgun).** `ISleipnirInterceptor` is documented as the seam "for ... validation,
auth, rate-limiting" but never runs on any batch path (`/json/multi`, WS multi, JSON-RPC
batch). `ISleipnirBatchInterceptor` has no consumer at all. A user building tenant isolation
on the seam sees green single-call tests and silent bypass on batches. STABILITY.md §2 is
honest about it; the interface docs and security guide are not.

**Change (1.1.2 scope — the real fix is R7).**

- Rewrite the `ISleipnirInterceptor`/`ISleipnirBatchInterceptor` XML docs to state exactly where
  they run today.
- Add the limitation to `SECURITY_GUIDE.md` and `SECURITY.md`.
- At `UseSleipnir` time: if `options.Interceptors.Count > 0`, log a **warning** once —
  "user interceptors currently run on single calls only, not batch elements (tracked for
  1.2)". Do **not** hard-fail (would be a breaking behavior change).

**Effort.** XS.

## R6 — Regression tests for the 1.1.1 "kritisch" fixes + fluent overload

**Why (process).** CHANGELOG calls both 1.1.1 fixes critical, yet: `PolicyEvaluator` appears
nowhere in `SleipnirTests` (the batch pre-pass policy fix is untested), and the single-sender
WS channel has no concurrency test. A security hotfix without a regression test is the
repo's biggest process gap.

**Tests to add.**

1. `BatchPolicyAuthTests`: invoker with a `PolicyEvaluator` fixture; `[SleipnirAuthorise(Policy=…)]`
   methods called in Parallel batch and in a topological batch — assert per-request
   401/403, that successful siblings still run, and that dependents of a denied provider
   get the propagation 400.
2. `WebSocketConcurrentSendTests`: N parallel calls (e.g. 50) plus an active event
   subscription on one connection; assert every received frame parses as JSON and ids are
   unique/uninterleaved (frame integrity is exactly what the single-sender channel fixes).
3. Fluent-overload integration test (delivers with R1/R2).

**Effort.** M. **Also adopt the rule:** no hotfix merges without a regression test —
write it into `CONTRIBUTING.md`.

---

# Train 2 — release/1.2.0 (consolidation)

## R7 — Route the batch path through the interceptor pipeline (Phase 1 completion)

**Why.** The framework's flagship path (parallel batch + dependency chaining) bypasses the
extension seam; single calls emit no metrics with the default interceptor set; auth truth
is split between a dead `SleipnirAuthorizationInterceptor` (`context.InvokeInfo` is only ever
set in tests) and `SleipnirInvoker.CheckAuthorisation`. This is the central architectural
tension the analysis found: the feature Sleipnir sells has the thinnest pipeline coverage.

**Design decisions (make first, in `docs/design/phase-1-interceptor-pipeline.md`).**

1. **Where do batch-element interceptors run?** Recommendation: in the **serial pre-pass**
   (`ResolveAndAuthorizeAsync` stage), wrapping authorization + setup, before the parallel
   fan-out. Rationale: interceptors may touch `HttpContext`; the execution phase is
   context-free by construction. Document the contract: batch-element interceptors run
   serially, before execution, and must not wrap long-running work. Interceptors that need
   to wrap actual execution (timing) execute a no-op continuation that defers to
   `ExecuteAuthorized` — measure via `SleipnirInvocationContext` timestamps instead of
   delegate wrapping.
2. **Single auth enforcement point.** Extract the logic of `CheckAuthorisation` into one
   internal evaluator used by both the invoker and `SleipnirAuthorizationInterceptor`;
   populate `SleipnirInvocationContext.InvokeInfo` on all paths so the interceptor is live
   code, then delete the duplicated policy logic (the 500-on-missing-evaluator path must
   fire in exactly one place).
3. **Metrics symmetry.** Emit `sleipnir.call.*` in the single-call path too (either a real
   telemetry interceptor in the default set or direct emission mirroring `ExecuteAuthorized`).
4. **`ISleipnirBatchInterceptor`:** wire a consumer (batch metrics/logging move into built-in
   batch interceptors) or remove the interface. STABILITY.md §2 allows settling in a minor
   with a CHANGELOG note; prefer wiring.
5. **Binary parity:** either route `InjectBinaryParameters` through the batch paths or
   document the asymmetry as a known limitation (currently silent null).

**Tests.** Per-element interceptor invocation in all three batch modes (order, serial
execution, HttpContext availability); single-call metrics emitted; auth behavior identical
before/after across all paths (the R6 tests are the safety net); user interceptor sees
batch elements.

**Acceptance.** One pipeline, one auth evaluator, one metrics story; STABILITY.md §2
updated to promote the batch-path pipeline from experimental.

**Effort.** L. **Risk:** the pre-pass constraint (HttpContext serial) is the hardest part —
keep the structure that exists today and slide the pipeline into it, do not re-architect it.

## R8 — Security hardening set

Sub-items, all in `SleipnirWebSocket`/`SleipnirHub`/`SleipnirCore`/`SleipnirServer` + docs + templates.

| # | Item | Detail | Effort |
|---|------|--------|--------|
| 8a | **Origin allowlist on WS upgrade (CSWSH)** | New `SleipnirOptions.WebSocketAllowedOrigins` (null = accept all, current behavior); reject upgrade with 403 when `Origin` present and not listed. `SECURITY_GUIDE.md`: "cookie auth + WS requires this" — CORS does not protect WebSockets. Add the SignalR equivalent note. Tests: allowed/rejected origin upgrades. | M |
| 8b | **`MaximumBatchSize` posture** | STABILITY §3.6 forbids tightening the default within 1.x → keep 0, but: set an explicit value (e.g. 64) in the **template** and **samples**, elevate the recommendation in `SECURITY.md`, record "non-zero default" as a 2.0 candidate in `ROADMAP.md`. | XS |
| 8c | **JsonPath O(M·N) fix** | Parse the provider result `JsonNode` **once per response** and evaluate all mappings against it (`DependencyResolver.ExtractValue` overload taking a parsed node). New additive option `MaxDependencyMappingsPerRequest` — default **0 (unlimited)** in 1.x per §3.6, documented recommendation 16; non-zero default is a 2.0 candidate. Test: N mappings against one large result — assert single parse (via counter seam or benchmark). | M |
| 8d | **SignalR hub batch-cap gate** | Early 400-style rejection on `DoWorkMany` like REST/WS/JSON-RPC (uniform entrance semantics); also fix the uncaught backstop exception path. | XS |
| 8e | **`MaxSubscriptionsPerConnection`** | New additive option, default 0 (unlimited) + docs in 1.x (`SleipnirSubscriptionManager.cs:72-112`); a proxy cannot cap this, so the option matters. Test: cap rejection. | S |
| 8f | **Never drop RPC responses** | `SleipnirSubscriptionManager` send channel is `DropOldest` — a hot event subscription can evict queued call *responses* → client hangs. Give responses a bypass/separate writer path; only event frames may drop. Test: hot subscription + slow reader, response still delivered. | M |
| 8g | **Revive the drop metric** | With `DropOldest`, `Writer.TryWrite` always succeeds → `sleipnir.event.dropped` and the "buffer full" warning are dead code. Detect saturation (reader count vs capacity) before write, or switch events to `DropWrite` where failure is observable. Keep the metric honest or remove the claim from STABILITY §2. | S |
| 8h | **Gate event `OnError` messages** | `SleipnirSubscriptionManager.cs:241` serializes raw `Exception.Message` to all subscribers regardless of `EnableDetailedErrors`. Plumb the flag in; generic "subscription error" otherwise. Test: throwing observable leaks nothing in production mode. | S |
| 8i | **WS binary frames & oversize close** | Count/reject binary frames deliberately (they're currently silently swallowed); on oversize, send `CloseAsync(1009 MessageTooBig)` before returning instead of aborting. | S |
| 8j | **Obsolete `SleipnirWebAppExtension.AddSleipnir(IEndpointRouteBuilder)`** | Maps the hub without `RequireAuthorization`/rate limiting — mark `[Obsolete("Use MapSleipnir...")]`, removal in 2.0. | XS |
| 8k | **Rate-limiting footgun** | `MapSleipnir` cannot reliably detect a missing `UseRateLimiter` middleware → log a loud warning when `RateLimitPermitLimit > 0`; templates get `UseRateLimiter`/`UseAuthentication`/`UseAuthorization` stubs matching `samples/server` . | S |
| 8l | **Sample cert trap** | `#if DEBUG` guard around `ServerCertificateCustomValidationCallback => true` in `samples/01-notification-chat`, plus a warning comment block. | XS |
| 8m | **WS rate-limit gap documented** | WS (the primary channel) is never rate-limited; either implement a per-connection token bucket or document the gap prominently in `SECURITY.md` (decision at implementation; documenting is the 1.2 floor). | S |

**Acceptance.** Every sub-item has a test or an explicit doc placement; `SECURITY.md`
checklist updated; no default tightened within 1.x (all tightenings queued for 2.0 in
`ROADMAP.md`).

## R9 — Core consolidation (`SleipnirInvoker` + duplication + caches)

**Why.** 2054-line god class kept alive by commentary; the analysis found per-request
reflection, double JSON round-trips, and five copies of route lookup with drift already
present. Do **after R7** so the invoker is restructured once, not twice.

**Items.**

1. **English wire messages.** Replace the German client-facing strings
   (`DependencyGraphBuilder.cs:91-92`; `SleipnirInvoker.cs:1521-1523,1814,1868-1870,1231`).
   Own convention compliance; international clients see them.
2. **Extract seams** (no behavior change): `AliasResolver` (~lines 686-1233),
   `ParameterBinder` (~1409-1566), `ResponseFactory` (~1838-2023). One commit per seam,
   tests stay green throughout.
3. **Kill the identity duplications:** shared `RequestKeys.Of(request)` for route key +
   `GraphKey` (currently duplicated between invoker and `DependencyGraphBuilder` with a
   "must stay in sync" comment — the exact condition for a latent divergence bug);
   `TryResolveRoute(...)` helper collapsing the 5 route-lookup copies; `EnsureRequestId`
   collapsing the 8 backfill copies.
4. **Single alias grammar.** One parser function used by `ContainsAlias`, the replacer, and
   `DependencyGraphBuilder.CollectAliases` — today they disagree (`" @x"` is detected but
   never substituted; alias names stop at different boundaries). Includes the serial-path
   `String.Empty` key collapse: give the serial path the same GraphKey fallback as the topo
   path.
5. **Reflection off the hot path:** cache `ParameterInfo[]` on `InvokeInfo` at
   registration; `ConcurrentDictionary<Type, string[]>` for `RequiredPropertyNames`; cache
   the closed-generic streaming consumer per element type instead of
   `MakeGenericType`/`GetMethod`/`Invoke` + `dynamic` per call.
6. **Alias replacement without string round-trip:** `JsonNode.DeepClone()` instead of
   `ToJsonString()`+`Parse`; replace in place where the parent slot is already known.
7. **Config shape:** introduce an immutable options record passed at construction
   (mirroring `SleipnirOptions`); keep the existing settable properties as bridges but document
   "set before first use"; the setters become the migration path, removed in 2.0.

**Tests.** Existing suite is the safety net (66 invoker tests); add micro-benchmarks or
allocation counters for items 5-6 (`SleipnirBench` entries) so the perf claim is measured.

**Acceptance.** No method >100 lines in the hot path; zero per-request reflection; one
implementation of alias grammar, route key, and request-id logic; all wire text English.

## R10 — Client unification

**Why.** The advertised invariant "a call behaves identically on all wires" currently
breaks in at least seven rows of the consistency matrix.

**Items.**

1. **REST client hygiene** (`SleipnirRestJsonClient.cs`): never set `Timeout` on a
   caller-owned `HttpClient` (owned path only; otherwise per-request linked CTS);
   `using var response`; wrap transport failures in `SleipnirException` with
   `OperationCanceledException` passthrough (mirrors WS/SignalR — the promised uniform
   surface); multi-HTTP-error returns one error element **per request** (not a single
   synthetic one) or throws — decide, document; `_disposed` guard on all public methods.
2. **Read-loop teardown race** (`SleipnirWebSocketClient.cs:310-375`): only run
   `CancelAllPending`/`StartReconnect` on transport termination (flag it), not on ordered
   reader replacement; `ReconnectLoopAsync` sets `Connected` when it early-returns on an
   already-open socket. Test: manual reconnect during pending call — no spurious
   cancellation, state converges to `Connected`.
3. **One set of JSON options.** Consolidate the four copies (base instance, WS static
   shadow, `SleipnirResponseParser.SubOptions`, `SleipnirMultiCallResponse.JsonOptions`) into one
   `internal static` wire-options object; either honor or delete the dead
   `SleipnirClientBase(options)` configurability.
4. **Reconnect delays:** single shared constant (SleipnirCommon), plus jitter to avoid
   thundering-herd reconnects.
5. **`SleipnirInMemoryClient`:** match production failure semantics (`Call<T>` throws
   `SleipnirException` on non-2xx; no `.Result` blocking) — a test double that diverges
   silently undermines its purpose. Breaking for consumers' tests: CHANGELOG migration note.
6. **`SleipnirSubscription<T>`:** add `IAsyncDisposable` (unsubscribe with
   `CancellationToken.None`); sync `Dispose` stays documented best-effort without
   sync-over-async blocking on a possibly-dead token.
7. **`SleipnirResponse` lazy `JsonDocument`:** implement `IDisposable` (documented, opt-in) or
   materialize owned data; pooled parse buffers must not be pinned for the response's life.
8. **`SleipnirCall` nits:** use the shared wire options in `SerializeToNode`; test positional
   naming (`param{i}`) explicitly.
9. **SignalR:** propagate caller ct into `Connect()`/`StartAsync`; preserve inner exception
   on transient connect failures; `_disposed` guard on `Call`.

**Acceptance.** Consistency matrix (error surface, disposal, timeouts, dispose-behavior)
has no red rows; tests per row where a divergence existed.

---

# Train 3 — release/1.3.0 (process & ecosystem)

## R11 — CI

1. **JS job (high):** `clients/ts` and `clients/codegen` each get `npm ci && npm run
   typecheck && npm test` (19 vitest files — including the golden fixtures the .NET parity
   gate compares against; today the TS side can drift with CI green).
2. **Security automation:** `dependabot.yml` (nuget, npm, github-actions); CodeQL workflow
   (scheduled); make the vulnerable-package check **gate** the build (`dotnet list package
   --vulnerable` prints but exits 0 — parse output or use `--include-transitive` with a
   fail condition).
3. **Windows leg** for the test job (the reconnect tests carry Windows-specific timing
   commentary; dev happens on Windows, CI only on Ubuntu).
4. **Coverage:** actually collect (`--collect:"XPlat Code Coverage"`) and upload
   (Codecov or artifact) — coverlet is referenced but never exercised; either wire it up or
   drop the package.
5. **Pack hygiene:** `dotnet pack -o artifacts`, push only from that dir; include
   `Sleipnir.Templates` in the release train (packed & published, version aligned with the
   packages, not pinned at 1.0.0) or document manual publishing.
6. **Version source of truth:** derive the package version from the tag (already) and make
   `Directory.Build.props`'s `<Version>` either follow (MinVer/`git describe`) or drop it
   locally — today it is a second, driftable source.

## R12 — Packaging

1. `<GenerateDocumentationFile>true</GenerateDocumentationFile>` in `Directory.Build.props`
   — 53 production files carry XML docs that never reach NuGet consumers. Sequence with the
   language decision (R13): enable now, migrate German XML docs incrementally (tracked list:
   SleipnirCommon 14, SleipnirCore 13, SleipnirClient 10, SleipnirHub 5, SleipnirRest 4, SleipnirWebSocket 4,
   SleipnirTelemetry 2, SleipnirServer 1).
2. **SourceLink.GitHub + `ContinuousIntegrationBuild` + embed untracked sources** —
   replaces the current snupkg-without-SourceLink approach (PDBs with local absolute paths,
   no source stepping).
3. **Central Package Management** (`Directory.Packages.props`): collapse duplicated
   versions (MessagePack ×3, SignalR ×2, Swashbuckle ×2, STJ ×2); resolve the deliberate
   MessagePack 2.5.302/3.1.8 diamond with an automated test (today "proven" only by a
   spike).
4. Delete the stub `Program.cs` (`Main() => 0`) + `launchSettings.json` in `SleipnirHub` and
   `SleipnirRest`; harmonize `LangVersion` in props; add `PackageTags`/icon/project URL.
5. `Sleipnir.csproj` (sample host): remove the duplicate `Grpc\` folder item; consider moving
   the integration-test host out of the gRPC-bearing sample (widens the test dependency
   surface).

## R13 — Documentation & release process

1. **Version drift fix (high user impact):** templates and samples pin `Sleipnir.Server
   1.0.0` — `dotnet new sleipnir-server` today produces a project without the 1.1.1 security
   fixes. Introduce a release checklist/script that bumps README.md, README_DETAILS.md, the
   ten package READMEs, `CODEGEN_ONBOARDING.md`, `samples/**`, and `templates/**` in one
   pass (or templatize the version placeholder).
2. **CHANGELOG language:** English from 1.1.2 on (1.1.1 shipped in German; earlier entries
   English).
3. Archive `RELEASE-PLAN.md` (pre-1.0 planning doc) to `docs/history/`.
4. `SECURITY_GUIDE.md` additions: WS rate-limit gap (8m), cookie-auth+WS (8a),
   envelope-at-200 infra visibility (auth failures invisible to proxies/WAFs), `ws://`
   downgrade warning for the client; interceptor batch-bypass (R5, replaced by R7 notes
   once landed).
5. Consider an English version of the `ROADMAP.md` Benutzbarkeit section (the rest of the
   doc set is English; `ARCHITECTURE.de.md` is the intentional bilingual exception).
6. `ROADMAP.md`: record the 2.0-candidate defaults this roadmap identified (non-zero
   `MaximumBatchSize`, `MaxDependencyMappingsPerRequest`, `MaxSubscriptionsPerConnection`,
   immutable invoker config, removal of obsoleted APIs).

---

## Relationship to `ROADMAP.md`

Insert this track **between Phase 1 and Phase 2** of the Benutzbarkeit-Roadmap:

- Phase 1 (interceptor pipeline / policies / error taxonomy) is only complete after
  **R5 + R7**; until then Phase 2 (Secure Store) has no reliable auth seam to build on and
  Phase 3 (Events) would institutionalize the `SleipnirSubscriptionManager` defects (8f-8h).
- The three "wenn nur drei Dinge" picks stay valid; this roadmap adds the missing zeroth
  step: **fix what the audit proved broken (R1-R6), then complete the seam (R7).**
- Phase 4 polish (P1 NuGet-first sample, P3 idempotency) lands naturally in the 1.3 train
  (R11-R13 fix the template/sample drift that P1 depends on).

## Effort summary

| Train | Content | Estimate |
|-------|---------|----------|
| 1.1.2 | R1–R6 | 3–5 working days |
| 1.2.0 | R7–R10 | 9–14 working days |
| 1.3.0 | R11–R13 | 4–6 working days |
