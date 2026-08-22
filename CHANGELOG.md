# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.4.2] — 2026-08-22

### Fixed — Dependency-chaining hardening (audit 2026-08-22, D1–D7)

Seven fail-loud gates and corrections close all findings of the dependency-chaining
audit. Full audit + execution plan:
[`docs/audits/2026-08-22-dependency-chaining-audit.md`](docs/audits/2026-08-22-dependency-chaining-audit.md);
spec updates in [`DEPENDENCY_BINDING.md`](DEPENDENCY_BINDING.md) §1, §7, §9, §10.

**Batch-entry gates (fail-loud, per-request `400`s for the whole batch):**

- **Duplicate alias providers rejected (D1).** Two requests exposing the same alias made
  resolution nondeterministic (availability check vs. fragment merge could disagree on the
  provider). Such batches are now rejected at batch entry —
  `Duplicate alias '@a': provided by '<key1>' and '<key2>'. …` — and no controller method runs.
- **Duplicate request keys rejected, all batch modes (D3).** Two requests sharing a graph key
  (same non-empty id; two id-less requests on one route; an id equal to another request's
  `Controller.Method` fallback) silently corrupted alias resolution and response correlation.
  Now: `Duplicate request key '<key>': requests '<a>' and '<b>' resolve to the same graph key. …`
  Same route twice with distinct ids remains legal.
- **Self-dependency rejected at graph build (D7).** A request consuming its own alias can
  never succeed; it now gets a specific `400`
  (`Request '<key>' depends on its own alias '@a'. …`) instead of a late runtime failure.

**Alias grammar (D2):**

- The three internal detection sites disagreed (one trimmed leading whitespace, one didn't,
  a third variant in the graph builder); `" @x"` was detected but never substituted. All
  sites now share one grammar (`AliasGrammar`): only `'@' + [A-Za-z0-9_]+` at string start is
  an alias; `"@a.b"` refers to alias `a`; **`@@text` is the escape for the literal string
  `@text`** (unescaped centrally before binding, on all paths).

**Binding & propagation correctness (D4–D6):**

- **Serial path authorizes before alias resolution (D4)** — parity with the topological path:
  an unauthorized request with an unresolvable alias gets its earned `401`, not a `400` that
  would leak the route's mapping shape to an unauthorized caller.
- **Strict/Paranoid honor STJ metadata (D5):** `[JsonIgnore]` properties are no longer
  demanded as required (false-positive `400`s gone); `[JsonPropertyName]` renames are compared
  under the wire name; property metadata cached per type.
- **Honest extraction-failure diagnostics (D6):** an invalid provider JsonPath now yields
  `failed to extract '@a' (<reason>)` to dependents instead of the misleading "did not expose".
  Reasons stay generic (no path/payload leak).

#### Breaking changes

- Strings with leading whitespace before `@` (`" @x"`) were previously *detected* as aliases
  by one internal check but never substituted — a silent dead end. They are now consistently
  literals.
- String parameter values starting with `@` were previously unusable as literals (blocked or
  mis-substituted). Use the new `@@` escape: send `"@@mention"` for the literal `"@mention"`.
- Strict/Paranoid error messages name camelCase **wire names** (`'name'`, `'address.zip'`)
  instead of CLR names (`'Name'`, `'Address.Zip'`).

Executable specs: `SleipnirTests/Unit/Core/AliasCollisionTests.cs`,
`AliasGrammarTests.cs`, `GraphKeyCollisionTests.cs`, `DependencyAuditD4toD7Tests.cs`.

## [1.4.0] — 2026-08-20

### Changed — Unified transport: one interface, runtime transport selection

The generated Sleipnir client no longer forces the transport to be chosen at
**codegen time**. `--transport` is now a **bundle-capability** selector (which
backends to bundle), not a client-shape selector — the public class, method
signatures, and options type are **byte-identical across all values**; transport
is chosen **at runtime**.

- **`--transport rest|ws|all|signalr`** (ts|js|cs; default `all`, was `rest`):
  - `rest` = REST calls + SSE events (HTTP-only, proxy-safe).
  - `ws` = WebSocket calls + WebSocket events.
  - `all` = REST + WS + SSE — enables the `auto` default (probe WebSocket, fall
    back to REST+SSE on failure).
  - `signalr` = `all` + SignalR (opt-in add-on; events via hub-streaming
    `IAsyncEnumerable<T>` mapping the existing `IObservable<T>` pipeline).
  - Deprecated aliases kept one minor version: `sse`→`rest`, `both`→`all`.
- **Runtime `SleipnirTransportRouter`** (TS `clients/ts/src/transport-router.ts`;
  C# `SleipnirClient/Sleipnir/SleipnirTransportRouter.cs`): the generated
  `SleipnirClient`/`SleipnirGeneratedClient` wraps a router. `auto` probes
  WebSocket once (lazy, reuses the probe socket as the live WS); `useTransport()`
  / `UseTransportAsync()` switches the profile explicitly. Escape hatches
  (`rest`/`ws`/`sse`/`signalr`) reach the raw bundled backends. The WS-vs-SSE
  subscribe mismatch (SSE carries method args as query params, no body) is
  bridged once, in the router, so the generated stub stays transport-agnostic.
- **C#**: the generated `SleipnirGeneratedClient` now wraps
  `SleipnirTransportRouter` (was `SleipnirRestJsonClient`). Event methods no
  longer throw `NotImplementedException` — they build a regular `Call` (the
  request is identical to a call method) and the consumer subscribes via
  `Subscribe<T>(call)` (routes to the active event backend). New C# runtime:
  `SleipnirSseClient` (events over `text/event-stream`, `Last-Event-Id` resume);
  `ISleipnirClient` gained `SubscribeAsync<T>` / `ResumeAsync<T>` (cross-transport
  resume). `cs` is no longer REST-only at codegen — `rest|ws|all|signalr` all valid
  (only `py` stays REST-only, httpx).
- **SignalR opt-in** (`--transport signalr`): server hub-streaming
  `SubscribeAsync` (`IAsyncEnumerable<string>`; first item is the `ack`, then
  event/complete/error frames reusing the shared durable store → cross-transport
  resume); TS `SleipnirSignalrClient` + C# `SleipnirSignalrClient` wired into the
  router. `@microsoft/signalr` is an **optional peer dependency** (dynamic import;
  the default bundle does not pull it).
- **Cross-transport resume** (durable subscriptions, process-wide store):
  resuming INTO WebSocket is unsupported (the WS resume frame needs the original
  controller/method) — switch to `rest`/`auto` to resume over SSE, or use
  SignalR. SSE ↔ SignalR resume by id.

#### Migration
- If you pinned `--transport rest` (the old default), the new default `all`
  changes which backends are bundled but **not the client surface** — your code
  compiles unchanged; `auto` now negotiates WS first. To keep the old HTTP-only
  behaviour, pass `--transport rest` (or `useTransport("rest")` at runtime).
- `--transport sse` / `--transport both` still work (canonicalized with a
  deprecation warning) and will be removed next major.
- C# event consumers: replace `throw new NotImplementedException(...)` stubs with
  `await client.Subscribe<T>(client.X.MyEvent(...))`.

## [1.3.1] - 2026-08-19

### Added — Events over REST: SSE (`text/event-stream`) transport
- **Server-push events (`[SleipnirEvent]` + `IObservable<T>`, previously WebSocket/SignalR-only)
  now also ship over REST via Server-Sent Events** — for clients behind corporate
  proxies/firewalls that block WebSocket upgrades. SSE travels over plain HTTP/1.1 and
  reuses the exact Phase R resume machinery (`Last-Event-Id`, durable subscriptions, the
  replay ring) already delivered over WebSocket.
- **Two REST endpoints** under the existing `/api/sleipnir` group (opt-in via
  `SleipnirOptions.UseSse`, default `true`):
  - `GET /api/sleipnir/events/{controller}/{method}` — fresh subscribe; method arguments
    travel as **query parameters** (GET has no body), each JSON-encoded so the server
    re-parses the native type. `RequireAuthentication` gate (401); per-method
    `[SleipnirAuthorise]` runs at subscribe time.
  - `GET /api/sleipnir/events/{subscriptionId}` — resume, with `Last-Event-Id:` header
    (and/or `?lastEventId=`). Unknown / GC'd / TTL-expired durable id → **410 Gone** (the
    client falls back to a fresh subscribe); resume re-runs authorization against the
    original route (a revoked role does not silently resume).
- **Wire mapping** — each logical event frame `{type:"event"|"complete"|"error",
  subscriptionId, eventId[, data][, message]}` is emitted as an SSE block
  `id:{eventId}` / `event:{type}` / `data:{frame}\n\n`. The first event is an `ack`:
  `id: 0` / `event: ack` / `data: {subscriptionId[, replayedFrom]}` — written before any
  live frame (the same ack-before-first-frame invariant as the WS race fix). `EventSource`
  auto-sends `Last-Event-Id` on reconnect; native `EventSource` works for cookie-auth
  hosts, but **cannot set a Bearer header** → the supported auth path is the fetch-based
  TS client.
- **Backpressure mirrors WS** — unbounded durable tap → bounded `EventBuffer` (DropOldest,
  drop-counted via `sleipnir.event.dropped`) → `Response.Body`. No subscription-store
  change. **Cross-transport resume** — durable subscriptions live in the process-wide
  DI-singleton store, so a subscription created over WebSocket can be resumed over SSE and
  vice-versa with no extra wiring.
- **TS client** (`sleipnir-client`): new fetch-based `SleipnirSseClient` (`clients/ts/src/sse.ts`)
  — `fetch` + `ReadableStream` decode, auto-reconnect to the resume URL with
  `Last-Event-Id`, dedup by `eventId`, 410 → degrade to fresh. Reuses the WebSocket
  `ResumeDecision`/`ResumePolicy` shape. Exposed from `index.ts`. Tests:
  `clients/ts/test/unit/sse.test.ts` (fresh / reconnect-resume / dedup / auth-header /
  410-degrade / drop-policy / unsubscribe-abort — 7 tests).
- **Codegen** (`sleipnir-codegen`): new `--transport sse` (REST calls + SSE events — the
  REST-only-with-events mode). `sse` without event methods is byte-identical to `rest`
  (no SSE client wired), so existing rest/ws/both snapshots are unchanged. The generated
  `_subscribe` adapter destructures the `SleipnirRequest` into the SSE client's
  `(controller, method, handlers, params)` shape. TS + JS emitters; `--selfcheck`
  validates SSE trees. README transport docs updated.

### Fixed — Events: cold-observable frame-ordering race (ack must precede the first event)
- A **cold** `IObservable<T>` could emit its first `OnNext` synchronously inside the
  `Subscribe` call, *before* the subscribe `ack` (carrying the `subscriptionId`) was
  enqueued — so the client received event frames referencing a `subscriptionId` it had
  not yet learned. The WS subscribe paths now subscribe the observer in the pre-check
  (buffering into the per-subscription buffer) and write the `ack` first, then drain the
  buffer — guaranteeing ack-before-first-frame in all three WS subscribe paths. The SSE
  transport inherits the same invariant (the `ack` SSE event is written before the pump
  drains). Safety-net test: `Subscribe_AckArrivesBeforeFirstEventFrame_SynchronousCold`.

### Changed — Versioning: NuGet + npm move in lockstep
- NuGet and npm now share **one version** and move together. Server-only changes still
  bump and dispatch the npm packages (identical content) so the numbers stay aligned.
- This release converges the drift: NuGet `1.2.1 → 1.3.1`, npm `1.3.0 → 1.3.1`. No npm
  `1.3.0` republish.

## [1.2.1] - 2026-08-19

### Fixed — Events: value-type-element `IObservable<T>` was wrongly rejected at subscribe time
- **A `[SleipnirEvent]` method returning `IObservable<T>` with a value-type `T` (e.g.
  `IObservable<int>`) was rejected at subscribe time** with "does not return an
  `IObservable<T>` — not a subscribable event", although it is a perfectly valid event.
  The subscribe-time check used the covariant `result is IObservable<object?>` test, which
  relies on `IObservable<out T>` covariance — and covariance does **not** apply to value-type
  elements, so `IObservable<int>` is not assignable to `IObservable<object?>`. Reference-type
  elements (`IObservable<ChatStreamEvent>`) happened to work because covariance covers them.
- `SubscribeAsync` now uses `TryAsObservableObject`: reference-type elements still take the
  zero-overhead covariance cast (the `SubscriptionManager` sees exactly the original source);
  value-type elements get a `BoxingObservableAdapter` + `BoxingObserver<T>` that boxes each `T`
  to `object?`. Reflection runs once per subscribe (not per element); the per-`OnNext` boxing is
  unavoidable since the downstream `SubscriptionManager` consumes `IObservable<object?>`.
- Regression test: `SleipnirInvokerTests.Subscribe_ToValueTypeEvent_ReturnsObservableAndPushesBoxedInts`.
- Server-side only (no client/wire change); no npm publish. Discovered by the owner after 1.2.0.

## [1.2.0] - 2026-08-19

### Added — Codegen: `sleipnir-gen --selfcheck` drift gate (npm `sleipnir-codegen`)
- New `--selfcheck` mode regenerates the client tree from `--discovery` in memory and
  compares it against the committed tree at `--out`; exits `4` on drift (a missing or
  changed generated file), `0` if clean, and **writes no files**. The client-side
  contract-drift gate — the CLI counterpart of the server-side MSBuild drift check
  (`ROADMAP.md` §3): a server change without regen leaves the client build green and
  the drift surfaces only at runtime as a `400`. The comparison is one-directional
  (generated ⊆ committed): a file in `--out` the emitter no longer produces is not
  flagged (`--out` may hold hand-written files); a removed controller still shows up
  as a `changed` entry. Requires `--out`; mutually exclusive with `--stdout`.
- Fix (dev-local `npm run typecheck`): `ts-compile.test.ts` had a `// @ts-expect-error`
  written as prose inside a comment block that TypeScript misread as an unused
  directive (TS2578); reworded so the directive is no longer misparsed. (Pre-existing
  on `main`; not a CI gate — CI runs the .NET suite, not `npm run typecheck`.)

### Added — Observability: `/metrics` scrape + `/observability` snapshot + DevUI panel (experimental)
- **`GET /api/sleipnir/metrics`** — opt-in Prometheus-text scrape endpoint exposing the
  `Meter "Sleipnir"` instruments (`sleipnir.call.duration/count`, `sleipnir.error.count`,
  `sleipnir.batch.fan_out/count`, `sleipnir.event.dropped`, and the new live gauges
  `sleipnir.ws.connections` / `sleipnir.subscriptions.active`). Wired from `Sleipnir.Telemetry`
  via `AddSleipnirPrometheusMetrics()` + `UseSleipnirPrometheusScrapingEndpoint(path, requireAuth)`.
  RequireAuth-gated like `/discovery`. The Prometheus-text interface is the durable contract —
  any scraper (Prometheus, Grafana Agent, VictoriaMetrics, or an embedded OTel stack) reads it;
  the OTel exporter behind it is the interim producer.
- **`GET /api/sleipnir/observability`** — opt-in JSON snapshot endpoint
  (`SleipnirOptions.EnableObservability`, default `false`) returning transport flags, active
  WebSocket connections, active event subscriptions, cumulative call/error/batch counters,
  dropped events, and uptime. RequireAuth-gated; not mapped when the flag is off (`404`).
- **`SleipnirConnectionRegistry`** (SleipnirCore, process-wide lock-free `Interlocked` counts)
  backs both the gauges and the JSON snapshot; wired eagerly in `AddSleipnir` and bumped from
  the WebSocket transport (connection accept/close, subscription add/remove, event drop). Keeps
  `/observability` readable without an OTel `MetricReader` (localized double-bookkeeping — the
  OTel Counters/Histograms are write-only).
- **`AddSleipnirTelemetry` now subscribes the metrics column** (`WithMetrics(b =>
  b.AddMeter("Sleipnir") …)`), closing the gap where the `sleipnir.*` instruments emitted into
  the void. Push (OTLP→collector→Grafana) and pull (Prometheus scrape) do not conflict.
- **Developer UI Observability tab** — a live panel polling `/observability` every ~2 s:
  transport pills, active connections/subscriptions with sparklines, cumulative counters,
  uptime, and a pointer to `/metrics` for the full instrument set.

### Added — Transport toggles + startup introspection log
- **`SleipnirOptions.UseRest`** and **`SleipnirOptions.UseWebSocket`** (both default `true`,
  non-breaking) now gate the unified pipeline: `UseRest=false` skips the REST endpoint group
  (`/json`, `/json/multi`, `/discovery`, JSON-RPC compat, Developer UI backend) → headless
  WebSocket/SignalR-only mode; `UseWebSocket=false` skips `UseWebSockets`+`UseSleipnirWebSocket`.
  `UseSignalR` was already opt-in — the three now form a symmetric transport-toggle trio. Only
  honored by the unified `UseSleipnirTransports`/`MapSleipnir` pipeline; hosts that call the
  low-level extensions directly bypass the toggles. See `STABILITY.md` §1.3.
- **Startup transport-introspection log** — `UseSleipnirTransports` now emits one
  `Information` line at startup naming the active transports
  (`Sleipnir transports: REST=True, WebSocket=True, SignalR=False`).
- **`/observability` snapshot reflects the WebSocket toggle** — the `transports.webSocket` field
  in the JSON observability snapshot now reports the configured `UseWebSocket` value (threaded
  from `SleipnirOptions` via `MapSleipnir`), instead of a hard-coded `true`. `transports.rest`
  stays `true` (the endpoint lives in the REST group, so REST is on whenever it is reachable);
  `transports.signalR` was already threaded.

### Added — Events: Last-Event-Id resume + server disconnect buffer (Phase R, experimental)
- **`[SleipnirEvent(Resumable = true)]`** opts an event in to **durable subscriptions** that
  survive a WebSocket disconnect: the `IObservable<T>` source is kept subscribed across
  disconnects, and a per-subscription replay ring buffer accumulates events produced during the
  gap. On reconnect the client sends `lastEventId` and the server replays the gap — the contract
  changes from at-most-once-while-disconnected to **at-least-once within the replay-buffer
  window** (the client dedups by `eventId`; events beyond the window are still lost and counted
  in `sleipnir.event.dropped`). Non-resumable events keep the v1 ephemeral behavior unchanged.
- **Client resume hook** — `ResumeDecision { Fresh, Resume, Drop }` + `ResumePolicy` delegate
  (C# `SleipnirClient.Sleipnir.ResumeDecision`; TS `clients/ts` `ResumeDecision` /
  `ResumePolicy`). Wired via the `SleipnirWebSocketClient` constructor `resumePolicy` and a
  per-`SubscribeAsync` override (TS: `onResume` on the WS client options / per-subscribe
  `SubscribeOptions.resumePolicy`). **Fresh** is the default (preserves v1 behavior); **Resume**
  sends `lastEventId` + the durable `subscriptionId`; **Drop** does not re-subscribe. A Resume on
  a non-resumable event, an unknown/GC'd id, or an over-cap/TTL-expired durable degrades to Fresh
  (new id; the client re-keys its handler and resets its dedup cursor).
- **Wire (additive, backward-compatible)** — the subscribe request gains optional `lastEventId`
  (long) and `subscriptionId` (the durable id, on resume only); the subscribe response gains
  `replayedFrom` (the first replayed eventId, absent on fresh). Event frames are unchanged.
- **Reconnect auth re-check** — a resume re-runs the same authorization a fresh subscribe runs,
  against the *original* event route recorded server-side at create time (not the client-claimed
  route). A role revoked during the disconnect gap does not silently resume: a 401/403 (or 404 if
  the route vanished) tears down the durable subscription and returns the error.
- **Knobs & limits** (on `SleipnirOptions`): `EventReplayBufferCapacity` (fallback 1000),
  `EventResumeTtl` (fallback 60s — an idle durable with no attached client is GC'd after this),
  `EventMaxDurableSubscriptions` (fallback 10 000 — over-cap creates return `503`). The durable
  store is in-process (no cross-restart persistence).
- **`SleipnirSubscriptionStore`** (SleipnirCore, DI singleton) holds the durable per-subscription
  state: the kept-alive source subscription, a stable monotonic `eventId` counter (not reset on
  reconnect), the bounded replay ring, a live-tap attach/detach, and TTL+cap GC.

### Added — Events: configurable per-event backpressure (experimental surface)
- **`SleipnirOptions.EventBufferCapacity`** (int?, fallback 100) and
  **`SleipnirOptions.EventBackpressureStrategy`** (enum, default `DropOldest`) now configure the
  per-subscription event buffer globally, and **`[SleipnirEvent(BufferCapacity = …,
  BackpressureStrategy = …)]`** overrides them per event. The four strategies:
  `DropOldest` (evict oldest — default, DoS-safe, keeps the subscription recent), `DropWrite`
  (drop newest — preserves the backlog, loses freshness), `Block` (block the producer until the
  consumer drains — lossless, but back-pressures the source thread; opt in deliberately),
  `Unbounded` (no cap, no DoS backstop). Previously every subscription used a hardcoded
  capacity-100 `DropOldest` buffer with no per-event override.

### Fixed — Events: `sleipnir.event.dropped` metric was dead code (experimental surface)
- The per-subscription buffer used `BoundedChannel(DropOldest)`, whose `TryWrite` returns `true`
  unconditionally (it evicts internally), so the `if (!TryWrite(...))` drop branch — the only call
  to `SleipnirMetrics.EventDropped` — was unreachable on saturation. Replaced with a custom
  `EventBuffer` that counts `DropOldest` evictions and `DropWrite` rejections accurately
  (`Block`/`Unbounded` never drop). The metric now reflects actual event loss.

### Changed — Events: `[SleipnirEvent]` is now the required marker (experimental surface)
- **`[SleipnirEvent]` is the required marker for server-push event methods.** In 1.1.0 the
  attribute was defined but never read at runtime — an event method was registered/discovered
  only because it carried `[SleipnirMethod]`, and event-ness was inferred from the `IObservable<T>`
  return type. As of this version, `SleipnirInvoker.Register` scans `[SleipnirEvent]` directly and
  enforces the contract at registration (fail-loud, like method-name uniqueness):
  - An `IObservable<T>` method must be marked `[SleipnirEvent]` (not `[SleipnirMethod]`); the old
    form is rejected with a migration message ("use `[SleipnirEvent]` for server-push events").
  - A `[SleipnirEvent]` method must return `IObservable<T>` directly (not `Task<IObservable<T>>`);
    otherwise registration throws.
  - `[SleipnirEvent]` and `[SleipnirMethod]` are mutually exclusive on one method; event and call
    names share the `{Controller}_{name}` dispatch namespace and must not collide.
- **Plain calls to event methods now return `400`** ("… is a server-push event; use
  `kind:\"subscribe\"`") instead of the previous opaque `500` ("Failed to serialize the response."
  — `System.Text.Json` cannot serialize an `IObservable<T>`).
- **Subscribe to a non-event method returns `400` without executing it.** Previously `SubscribeAsync`
  ran the call method as a side effect before the return-type check rejected it; the `IsEvent` guard
  now short-circuits before auth/bind/invoke.
- **Migration:** replace `[SleipnirMethod]` with `[SleipnirEvent]` on any `IObservable<T>` method.
  The wire format, `subscriptionId`/`eventId` frames, and client API are unchanged.
- **No SemVer major** — events are experimental (`STABILITY.md` §2); experimental-surface changes
  are noted in the changelog and do not require a major (§3.7). Consumer docs: `README_DETAILS.md`
  → "Server-Push Events"; wire spec: `PROTOCOL.md` → "Server-Push Events".

### Fixed (npm: `sleipnir-codegen@1.2.3`)
- **Added the missing package README.** `sleipnir-codegen`'s `package.json`
  declared `"README.md"` in `files`, but the file did not exist in the package
  root, so every published tarball (through 1.2.2) shipped without a README and
  npmjs.com showed "This package does not have a README." Added a README
  documenting the CLI (`sleipnir-gen --lang ts|js|cs|py --discovery …`), the four
  emitters and their output file sets / runtime dependencies / transport support,
  and the programmatic (browser-safe core + `sleipnir-codegen/node`) API. No
  code change.

## [1.1.4] - 2026-08-17

### Fixed (NuGet: `1.1.4` — `Sleipnir.Generator` + `Sleipnir.Codegen.Core`)
- **`BatchEntry.Alias(name)` in the .NET-native C# emitter now ensures the leading `@`.**
  The Roslyn source generator (`Sleipnir.Generator`) and the `SleipnirCodegen.EmitClient`
  path in `Sleipnir.Codegen.Core` emitted `public Alias Alias(string name) => new(name);`
  — the **bare name** — into the generated `SleipnirGenerated.cs`, so a C# consumer's typed
  batch chain compiled but sent `"ids"` on the wire where the server expected `"@ids"`:
  `ReplaceDependencyByAlias` never matched and the dependent call received an unresolved
  literal. This is the .NET-side twin of the npm `sleipnir-codegen@1.2.2` fix (the
  `CsEmitter` is a port of `clients/codegen/src/emitters/cs.ts`, "reproduced verbatim").
  Now `@`-normalized symmetrically with `Exposes` (which strips a leading `@` for the wire
  `dependencyMapping` key): `Alias("ids")` → `"@ids"` and `Alias("@ids")` → `"@ids"`. The
  committed TS `--lang cs` snapshot was regenerated to match, and the
  `CsCodegenParityTests` byte-for-byte gate stays green; a focused behavior assertion was
  added. **Not affected:** `Sleipnir.Client.Linq.Codegen` (the `sleipnir-linq` tool uses
  `EmitContracts`/`CsContractsEmitter`, which emits no `Alias` runtime) and
  `Sleipnir.Client.Linq` (the runtime builds `"@" + alias` correctly at the wire sites).
  `Sleipnir.Server.Codegen` (drift-check only) is unaffected.

### Fixed (npm: `sleipnir-codegen@1.2.2`)
- **`TypedRequest.alias()` now returns the `@alias` wire placeholder.** The generated
  `alias(name)` (TS `TypedRequest`, C# `BatchEntry.Alias`, Python `_BatchEntry.alias`)
  returned the **bare name** instead of `'@' + name`, so a typed batch chain compiled
  but sent `"ids"` on the wire where the server expected `"@ids"` — the server's
  `ReplaceDependencyByAlias` never matched, and the dependent call received an
  unresolved literal instead of the alias value. The bug was invisible to the
  existing tests because they used the `alias("@ids")` convention, where `return name`
  happened to return `"@ids"` (correct by accident). Now `@`-normalized symmetrically
  with `exposes` (which strips a leading `@` for the wire `dependencyMapping` key):
  `alias("ids")` → `"@ids"` and `alias("@ids")` → `"@ids"`. Adds a runtime guard
  importing the emitted module (both call styles) + PY/CS behavior assertions.

### Added (npm: `sleipnir-client@1.2.0`)
- **Dynamic bearer for rotating JWTs.** The `bearer` option on `SleipnirRestClient`,
  `SleipnirWebSocketClient`, and `createClient` now accepts `string | (() => string)`.
  A provider function is resolved fresh per REST call and per WS connect/reconnect, so a
  rotating access token is used without rebuilding the client. Both clients also expose
  `setBearer(b)` to swap the token at runtime (REST: next call; WS: next connect/reconnect
  — an already-open WS keeps its handshake token). `string` remains fully backward
  compatible. Exported `BearerProvider` type from `sleipnir-client`.

## [1.1.3] - 2026-08-17

### Fixed
- **`Sleipnir.Server.Codegen` now discovers controllers on .NET 10 (and 9) consumers.**
  The export tool targets `net8.0` and reflects the consumer's built server assembly via
  `Assembly.LoadFrom`. When the MSBuild target invoked it with the bare `dotnet
  <tool.dll>`, the tool process was pinned to the **net8 runtime** (its own
  `runtimeconfig.json`), which cannot load/reflect a **net10** server assembly's controller
  types — `GetTypes()` silently dropped them and discovery returned an **EMPTY contract**
  (`{"discoveryVersion":"1","controllers":[],"types":{}}`). The drift-check then passed
  *vacuously* (empty == empty), so the broken contract shipped unnoticed. Two fixes:
  1. `<RollForward>LatestMajor</RollForward>` is now baked into the tool's
     `runtimeconfig.json`, so `dotnet <tool.dll>` rolls up to the consumer's highest
     installed runtime (net10 on a .NET 10 consumer) and the `[SleipnirController]` scan
     succeeds. Safe for net8 consumers too (net8 is the highest runtime there).
  2. A **zero-controller guard** in the export tool: a server that ships a
     `contract.sleipnir.json` is expected to expose controllers, so an empty regenerated
     discovery now throws a tool error (exit 2) — the build breaks loudly instead of going
     green on an empty contract. Belt-and-suspenders for any future runtime-reflect failure.

  Root cause was the **runtime** mismatch (net8 tool process vs net10 server assembly),
  not an attribute-type-identity mismatch: `Sleipnir.Common`/`SleipnirCore` ship only a
  `net8.0` build, so a net10 NuGet consumer resolves the same net8 binary the tool
  co-locates — same assembly identity, same `[SleipnirController]` type — and the loader
  returns the already-loaded instance. Verified end-to-end on a NuGet-consuming net10
  server: bare `dotnet <tool.dll>` (roll-forward) finds the controller; an explicit
  net8-pinned process finds zero and trips the guard.

## [1.1.2] - 2026-08-17

### Fixed
- **`Sleipnir.Server.Codegen` nupkg now ships the full dependency closure** in `tasks/net8.0/`.
  The 1.1.1 package contained only the tool dll + `deps.json` + `runtimeconfig.json` — no
  runtime dependencies — while `deps.json` listed them as `type:"reference"` (expects
  co-located). The tool crashed on the consumer's machine with `FileNotFoundException:
  Microsoft.Extensions.DependencyInjection.Abstractions 8.0.0.0` (then SleipnirCore,
  SleipnirCommon), which broke the build-integrated drift gate (`SleipnirExportDriftCheck`,
  `AfterTargets="Build"`) — every `dotnet build` and `dotnet run` of a server project with
  the PackageReference plus a `contract.sleipnir.json` failed. The pack target now runs
  `dotnet publish --no-self-contained` into a staging dir and globs the complete,
  self-resolving framework-dependent app (SleipnirCore, SleipnirCommon,
  Microsoft.Extensions.DependencyInjection.Abstractions, JsonPath.Net, …) into
  `tasks/net8.0/`, so `dotnet <tool.dll>` resolves every dependency from that folder
  without probing the consumer's NuGet cache.

### Changed
- **`publish-npm` CI job is now `workflow_dispatch`-only** (no longer fires on `v*` tags).
  The two npm packages (`sleipnir-client`, `sleipnir-codegen`) version independently of the
  NuGet lockstep — a `v*` tag was stamping them to the tag version and regressing
  `sleipnir-codegen`'s `latest` below an already-shipped independent release. NuGet
  lockstep tags and npm dispatch are now fully decoupled.

## [1.1.1] - 2026-08-17

### Fixed
- **`Sleipnir.Rest` no longer pulls `Swashbuckle.AspNetCore`** — the reference was unused in the
  transport source but flowed transitively through `Sleipnir.Server` into consumer apps. Swashbuckle
  7.3.1 dragged in `Microsoft.OpenApi` 1.6.22, which collided with .NET 10's
  `Microsoft.AspNetCore.OpenApi` (requiring `Microsoft.OpenApi` 2.x) — causing restore/build
  conflicts for .NET 10 consumers. Removed; the Sleipnir REST transport never called Swashbuckle.
  Apps that want Swagger can add `Swashbuckle.AspNetCore` themselves.

## [1.1.0] - 2026-08-15

### Added
- **`Sleipnir.Client.Linq`** — typed client LINQ package: `Dep<T>`/`Arg<T>` compile-time type-safe
  `@alias` wiring (Tier 1) and the `SleipnirQuery<T>` `.Include`/`.ThenInclude`/`.Where`/`.Build`/
  `.Materialize` eager-load façade (Tier 2) over the existing `@alias`/`dependencyMapping` wire.
- **`[SleipnirNavigation]` one-declaration pipeline** — a server-side `[SleipnirNavigation]`
  (SleipnirCommon) on a DTO property flows through the discovery `navigation` field into the
  `sleipnir-linq` codegen, which drift-checks each edge (fetch/key/param/opaque-target) at generation
  time and emits the client-side `[SleipnirNavigation]` onto the generated contract DTOs. Generated
  clients now drive `SleipnirQuery<T>.Include(...)` without hand-annotating navigation edges.
- **Discovery `navigation` field** (additive, `discoveryVersion` stays `"1"`): optional
  `NavigationMeta` on `PropertyMeta` (`docs/discovery-schema.md` §13).

## [1.0.0] - 2026-08-11 — Renamed from Trame

The framework formerly known as **Trame** is now **Sleipnir**. The rename avoids a
collision with Kitware's established Python framework `trame` (IEEE-published,
ParaView-integrated). Sleipnir continues the Norse-theme naming of the sibling
projects Walhalla (SQL) and Heimdall (OTel), and will serve as their internal RPC
base, including a built-in OTel viewer.

This is a **new package line** — the old `Trame.*` NuGet packages are
**deprecated with a redirect** to the matching `Sleipnir.*` packages (left
listed so existing restores keep working; not unlisted).

### Changed (migration-relevant)
- **NuGet package IDs**: `Trame.*` → `Sleipnir.*` — `Sleipnir.Common`, `.Core`,
  `.Hub`, `.Rest`, `.WebSocket`, `.Client`, `.DeveloperUi`, `.Server`, `.Telemetry`,
  `.Generator`, `.Server.Codegen`. Version reset to 1.0.0 for the new line.
- **Namespaces & types**: `Trame*` → `Sleipnir*` — e.g. `ITrameCore` →
  `ISleipnirCore`, `TrameOptions` → `SleipnirOptions`, `TrameInvoker` →
  `SleipnirInvoker`, `TrameResults` → `SleipnirResults`, `TrameException` →
  `SleipnirException`, `TrameCall` → `SleipnirCall`.
- **Routes**: `/api/trame/json` → `/api/sleipnir/json`,
  `/api/trame/json/multi` → `/api/sleipnir/json/multi`,
  `/api/trame/discovery` → `/api/sleipnir/discovery`,
  `/tramews` → `/sleipnirws`, `/tramehub` → `/sleipnirhub`, DevUI `/Trame` →
  `/Sleipnir`.
- **JSON-RPC capabilities**: `trame.discover` / `trame.capabilities` →
  `sleipnir.discover` / `sleipnir.capabilities`; `rpc.system="trame"` →
  `"sleipnir"`.
- **Telemetry**: `ActivitySource` / `Meter` name `"Trame"` → `"Sleipnir"`,
  operation names `TrameCall` / `TrameBatch` → `SleipnirCall` / `SleipnirBatch`,
  rate-limit policy `"trame"` → `"sleipnir"`. (Isolates Sleipnir spans from
  Heimdall / Walhalla consumer sources.)
- **Contract file**: `contract.trame.json` → `contract.sleipnir.json`.
- **Environment variable**: `TRAME_REGEN_GOLDEN` → `SLEIPNIR_REGEN_GOLDEN`.
- **Roslyn diagnostic IDs**: `TRAME001` / `TRAME002` → `SLEIPNIR001` /
  `SLEIPNIR002`.
- **npm packages**: `trame-client` / `trame-codegen` / `trame-devui` →
  `sleipnir-client` / `sleipnir-codegen` / `sleipnir-devui`; CLI `trame-gen` →
  `sleipnir-gen`.
- **Repository**: `github.com/HolgerTheHuck/Trame` →
  `github.com/HolgerTheHuck/Sleipnir` (old URL auto-redirects; stars, issues,
  and history preserved).

### Migration
1. Replace `Trame.*` NuGet `PackageReference` entries with the matching
   `Sleipnir.*` (version `1.0.0`).
2. Update namespaces and type names (`Trame*` → `Sleipnir*`).
3. Update client / transport URLs to the new routes.
4. If you reference the telemetry source by name, add `AddSource("Sleipnir")`
   (instead of `"Trame"`) to your OpenTelemetry tracing pipeline.

The old `Trame.*` packages remain on nuget.org (deprecated, listed) so existing
restores are not broken, but they receive no further updates.

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