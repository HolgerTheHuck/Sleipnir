# Sleipnir Transport — User Reference

A consolidated lookup reference for **everything transport** in Sleipnir: the three
wires (REST, WebSocket, SignalR) plus SSE-over-REST events, the unified
`SleipnirTransportRouter` that selects between them at runtime, the capability
bundles, the auto-fallback probe, the escape hatches, cross-transport event
subscription and resume, the server registration flow, and every transport-related
option on `SleipnirOptions`.

This is a **reference**, not a tutorial. When something does not work, look here
first — config tables, endpoint tables, the exact public API with symbol-grounded
citations, a diagnostics/troubleshooting catalog, and a map of where the deeper
docs live. For onboarding read `GETTING_STARTED.md`; for the marketing-shaped
overview read `README.md`; for the wire-level spec read `PROTOCOL.md`. This doc
consolidates those and links back for depth.

All citations anchor on the file path plus the named symbol (method, property, or
field) or a short verbatim quote — never on line numbers, which drift on every
edit. Code-facing text is English per `CLAUDE.md`.

## Table of contents

1. [The transport model](#1-the-transport-model)
2. [Endpoints & wire formats](#2-endpoints--wire-formats)
3. [The unified client — `SleipnirTransportRouter`](#3-the-unified-client--sleipnirtransportrouter)
4. [Capability values & what they bundle](#4-capability-values--what-they-bundle)
5. [The individual backends](#5-the-individual-backends)
6. [Events & subscriptions across transports](#6-events--subscriptions-across-transports)
7. [Server registration & `SleipnirOptions`](#7-server-registration--sleipniroptions)
8. [Usage patterns](#8-usage-patterns)
9. [Configuration reference (all knobs)](#9-configuration-reference-all-knobs)
10. [Diagnostics & troubleshooting catalog](#10-diagnostics--troubleshooting-catalog)
11. [How it is verified (the gates)](#11-how-it-is-verified-the-gates)
12. [Relationship to other docs](#12-relationship-to-other-docs)

---

## 1. The transport model

Sleipnir is code-first: the C# controllers decorated with `[SleipnirController]`/`[SleipnirMethod]`
*are* the contract. **One contract, three wires.** Every transport deserializes the
incoming `SleipnirRequest`/`SleipnirMultiRequest` and delegates to the same
`ISleipnirCore.InvokeDi()` — so routing, authorization, interceptors, dependency
chaining and batching behave identically regardless of which transport carried the
call. `CLAUDE.md` §"Transports" has the one-line transport table.

| Transport | Calls | Events | Binary | Wire |
|-----------|-------|--------|--------|------|
| REST | yes (`POST /json`, `/json/multi`) | via SSE-over-REST | `byte[]` in `BinaryData` | HTTP/1.1 + JSON |
| WebSocket | yes (JSON text frames) | yes (subscribe/unsubscribe frames) | no native binary frames | RFC 6455 + JSON text |
| SignalR | yes (`DoWork`/`DoWorkMany` hub methods) | yes (hub-streaming `SubscribeAsync`) | `byte[]` via MsgPack | WebSocket + MessagePack |
| SSE-over-REST | **no** (events only) | yes | no | `text/event-stream` |

SSE is not a standalone call transport — it cannot carry calls. It exists purely
as the events channel for the `rest` and `all` capability bundles, and as the
graceful-degradation fallback when WebSocket is unavailable. SSE is therefore
**not** a value of the `SleipnirTransport` profile enum; the raw SSE backend is
reachable only through the `Sse` escape hatch on the router
(`SleipnirClient/Sleipnir/SleipnirTransportRouter.cs` — `SleipnirTransport` enum +
`Sse` property).

The runtime selection layer is `SleipnirTransportRouter`: the generated client
(`SleipnirClient`/`SleipnirGeneratedClient`) wraps a router, so the public call
surface is byte-identical across every capability — transport is chosen at
runtime, not at codegen time. See `CODEGEN_REFERENCE.md` §6.4 ("Switching
transport at runtime") for the codegen side.

---

## 2. Endpoints & wire formats

Default prefix `/api/sleipnir` (param `prefix` to `MapSleipnirEndpoints`,
`SleipnirRest/SleipnirEndpointExtensions.cs`).

### REST — `SleipnirRest/SleipnirEndpointExtensions.cs` (`MapSleipnirEndpoints`)

| Route | Method | Body / params |
|-------|--------|---------------|
| `{prefix}/json` | POST | `SleipnirRequest` → `SleipnirResponse` |
| `{prefix}/json/multi` | POST | `SleipnirMultiRequest` → `SleipnirResponse[]` |
| `{prefix}/discovery` | GET | – → `DiscoveryInfo` (deterministic serialization) |
| `{prefix}/observability` | GET | – → JSON snapshot (opt-in `EnableObservability`) |
| `{prefix}/jsonrpc` | POST | JSON-RPC 2.0 object/array (opt-in `EnableJsonRpcCompat`) |

- `Content-Type: application/json`; request body limit **1 MB**
  (the `[RequestSizeLimit(1_048_576)]` attribute on the `/json` route).
- Rate limiting applied only when `RateLimitPermitLimit > 0` (policy name
  `"sleipnir"`).
- REST client posts to `{serverBase}/{apiPath}/json` and `.../json/multi`
  (`SleipnirClient/Sleipnir/SleipnirRestJsonClient.cs` — `Call` / `Call(multi)`).

### SSE-over-REST — `SleipnirRest/SleipnirSseEndpointExtensions.cs` (`MapSleipnirSseEndpoints`)

Mapped from the REST group when `UseSse` is on (the `UseSse` branch of
`MapSleipnirEndpoints` in `SleipnirEndpointExtensions.cs`).

| Route | Method | Purpose |
|-------|--------|---------|
| `{prefix}/events/{controller}/{method}` | GET | fresh subscribe; method args as query params |
| `{prefix}/events/{subscriptionId}` | GET | resume; `Last-Event-Id:` header (and `?lastEventId=` fallback) |

Response: `text/event-stream`, `Cache-Control: no-cache`,
`X-Accel-Buffering: no` (set in `MapSleipnirSseEndpoints`). Each logical frame
becomes an SSE block (`id:`/`event:`/`data:`); the **first block is `event: ack`**
(`SleipnirRest/Sse/SleipnirSseConnection.cs` — first frame `event: ack`).

> SSE supports **named params only**; positional params and binary are
> WS/SignalR-only (`SleipnirTransportRouter.cs` `SubscribeAsync` SSE-unpack
> branch; `PROTOCOL.md` §"Transport selection (client)").

### WebSocket — `SleipnirWebSocket`

- Middleware path default **`/sleipnirws`**
  (`SleipnirWebSocket/SleipnirWebSocketExtensions.cs` — `UseSleipnirWebSocket`).
- Accepts the WS upgrade; **1 MB max message** (`MaxMessageSize = 1_048_576` in
  `SleipnirWebSocket/SleipnirWebSocketMiddleware.cs`); rejects unauthenticated
  upgrades when `RequireAuthentication` (the auth check in `SleipnirWebSocketMiddleware`).
- Wire: **JSON text frames** (no native binary frames —
  `SleipnirWebSocket/README.md`, `README_DETAILS.md` §"Known Limitations (v1)").
  Messages are auto-detected as multi (`requests` + `mode` fields) vs single;
  subscribe/unsubscribe detected via a `kind` field — all in the receive loop of
  `SleipnirWebSocketMiddleware`.

### SignalR — `SleipnirHub`

- Hub mapped at **`/sleipnirhub`**
  (`SleipnirHub/Extensions/SleipnirWebAppExtension.cs` `MapSleipnirHub`;
  `SleipnirServer/SleipnirPipelineExtensions.cs` `MapSleipnir`).
- Hub methods: `DoWork(SleipnirRequest) → SleipnirResponse?` and
  `DoWorkMany(SleipnirMultiRequest?) → IEnumerable<SleipnirResponse>`
  (`SleipnirHub/Hub/SleipnirHub.cs` — `DoWork` / `DoWorkMany`).
- Event subscribe is the streaming
  `SubscribeAsync(SleipnirRequest, string?, long?, [EnumeratorCancellation] CancellationToken) → IAsyncEnumerable<string>`
  (`SleipnirHub.cs` `SubscribeAsync`). The first yielded item is the `ack` frame,
  then `event`/`complete`/`error` frames (the `yield return` sites in
  `SleipnirHub.SubscribeAsync`) — the **same serialized string frames WS/SSE
  emit**, so cross-transport resume works.
- Wire: WebSocket + **MessagePack binary** (opt-out via `UseMessagePack=false`;
  `SleipnirHub/README.md`; `SleipnirHub/Extensions/SleipnirServiceCollectionExtension.cs`
  `AddMessagePackProtocol` branch).

### JSON-RPC 2.0 compat adapter — `SleipnirRest/JsonRpc/`

- Single endpoint `POST {prefix}/jsonrpc`, opt-in via
  `SleipnirOptions.EnableJsonRpcCompat` (default `false`)
  (`SleipnirEndpointExtensions.cs` `MapSleipnirEndpoints` jsonrpc branch;
  `SleipnirHub/Extensions/SleipnirOptions.cs` — `EnableJsonRpcCompat`).
- Reads raw body (object = single, array = batch); **always 200** with the
  JSON-RPC envelope in the body, **204** only when every item was a notification
  (`JsonRpcDispatcher.cs` `Dispatch`).
- Capability methods dispatched directly: `sleipnir.discover` (auth-gated when
  `RequireAuthentication`) and `sleipnir.capabilities` (`JsonRpcDispatcher.cs`
  capability-method dispatch; `JsonRpcAdapter.cs` `BuildCapabilities`).
- `method` is `Controller.Method` (split at the last dot); `params` object → named,
  array → positional (by `num`); `id` echoed with original type.
- Error-code map: routing 404 → `-32601`, business 404 → `-32000`,
  `400/422 → -32602`, `401/403 → -32001`, `500 → -32603`, parse → `-32700`,
  invalid → `-32600` (`JsonRpcAdapter.cs` error-code map; `CLAUDE.md` §"Transports").
- **Limitations:** no `@alias` chaining, Parallel-only (no execution-mode
  selection), no binary out-of-band, no streaming — graduate to the native wire
  (`CLAUDE.md` §"Transports"; `PROTOCOL.md` §"JSON-RPC 2.0 Compatibility"). Full
  spec: `JSONRPC_COMPAT.md`.

---

## 3. The unified client — `SleipnirTransportRouter`

File: `SleipnirClient/Sleipnir/SleipnirTransportRouter.cs`.

```csharp
public class SleipnirTransportRouter : SleipnirClientBase, IAsyncDisposable
public SleipnirTransportRouter(SleipnirRouterOptions opts) : base()
```

- Throws `ArgumentException` if `BaseUrl` is blank (ctor guard).
- Instantiates backends per `HasBackend(...)`. A non-`Auto`
  `DefaultTransport` resolves the profile immediately; `Auto` is lazy.

### `SleipnirRouterOptions` — ctor input

| Field | Type | Default | Notes |
|-------|------|---------|-------|
| `BaseUrl` | `string` | *(required)* | Throws if blank |
| `Capability` | `SleipnirBundleCapability` | `All` | Selects which backends are bundled |
| `DefaultTransport` | `SleipnirTransport` | `Auto` | Initial profile |
| `Bearer` | `string?` | `null` | Auth token |
| `CallTimeout` | `TimeSpan?` | `null` | Per-call timeout |
| `ProbeTimeout` | `TimeSpan?` | `1500ms` | WS handshake probe for `auto` |
| `ApiPath` | `string` | `"api/sleipnir"` | REST/SSE prefix |
| `WsPath` | `string` | `"sleipnirws"` | WS path |
| `HubPath` | `string` | `"sleipnirhub"` | SignalR hub path |
| `ReconnectDelays` | `TimeSpan[]?` | per-backend defaults | Empty array disables reconnect |
| `ResumePolicy` | `ResumePolicy?` | `null` | Event resume decision delegate |
| `RestHttpClient` | `HttpClient?` | `null` | Inject a custom `HttpClient` for REST+SSE |

### Auto probe + fallback

```csharp
public async Task NegotiateAsync(CancellationToken ct = default)
```

`RunAutoNegotiationAsync`:

- If `_ws == null` → fall back to `Rest` immediately.
- Otherwise probe the WS handshake under `_probeTimeout` (default **1500 ms**)
  via `_ws.ConnectAsync(probeCts.Token)`.
  - Success → profile `Ws`.
  - Failure/timeout → profile `Rest`.
- If the chosen fallback backend is not bundled, `throw NotBundled(Auto)`.
- Lazy and concurrent-safe via `_negotiateLock`.

The headline auto-fallback case: **WS dies → calls fall back to REST, events
resume over SSE.**

### Switching transport at runtime

```csharp
public async Task UseTransportAsync(SleipnirTransport t, CancellationToken ct = default)
```

`Auto` clears `_profile` and re-runs negotiation; any other value goes through
`ResolveProfile`, which throws `NotBundled(t)` — a `SleipnirException` — if the
required backend was not bundled for the current capability.

### Escape hatches (read-only, `null` if not bundled)

```csharp
public SleipnirRestJsonClient?  Rest    { get; }
public SleipnirWebSocketClient? Ws      { get; }
public SleipnirSseClient?       Sse     { get; }
public SleipnirSignalrClient?   Signalr { get; }
public string? ActiveTransport { get; }            // lowercase profile name, null until resolved
```

Use these to reach a raw bundled backend directly (e.g. a REST-only call from an
`all` client, or the SSE client for an events-only consumer).

### Routing

- Calls: `Call(SleipnirRequest?)`, `Call(SleipnirMultiRequest?)`. `CallBackend()`
  maps `Ws`/`Signalr` to their backend, default `Rest`.
- Subscribe: `SubscribeAsync<T>(SleipnirRequest?, ResumePolicy?, CancellationToken)`.
  SSE unpacks the request (controller/method/params → query params); WS/SignalR
  pass it straight through (the SSE-unpack branch of `SubscribeAsync`).
  `EventBackend()` maps `Ws`/`Signalr`, default `Rest` (SSE).
- Resume: `ResumeAsync<T>(string subscriptionId, long lastEventId, ResumePolicy?, CancellationToken)`.
  **Resuming into WebSocket throws `NotSupportedException`** with guidance to
  switch to `rest`/`auto` (the WS guard in `ResumeAsync`). See §6.
- `SetBearer(string?)` fans the bearer to **SSE + SignalR only**.
- `DisposeAsync`: disposes WS + SignalR (async), SSE + REST (sync), then
  `_negotiateLock`.

---

## 4. Capability values & what they bundle

### `SleipnirBundleCapability` (`SleipnirTransportRouter.cs`)

| Value | Bundles | Use |
|-------|---------|-----|
| `Rest` | REST calls + SSE events | HTTP-only, proxy-safe clients |
| `Ws` | WS calls + WS events | WebSocket-only, no fallback |
| `All` | REST + WS + SSE | Enables `auto` (WS → REST+SSE fallback) — **default** |
| `Signalr` | `All` + SignalR | Opt-in SignalR add-on (hub-streaming events) |

### `SleipnirTransport` — the runtime profile enum

`Auto, Rest, Ws, Signalr`. There is **no `Sse` profile** (SSE cannot carry calls);
the raw SSE backend is reachable only via the `Sse` escape hatch.

### Backend selection — `HasBackend` switch

| Backend | Bundled for capabilities |
|---------|--------------------------|
| `SleipnirRestJsonClient` (rest) | `Rest`, `All`, `Signalr` |
| `SleipnirWebSocketClient` (ws) | `Ws`, `All`, `Signalr` |
| `SleipnirSseClient` (sse) | `Rest`, `All`, `Signalr` |
| `SleipnirSignalrClient` (signalr) | `Signalr` only |

### Where the capability string is set

- **Codegen CLI**: `--transport rest|ws|all|signalr` (default `all`)
  (`clients/codegen/README.md` — `--transport` flag; `PROTOCOL.md`
  §"Transport selection (client)"). TS/JS emitters canonicalize deprecated
  aliases `sse → rest`, `both → all` (`clients/codegen/src/emitters/ts.ts`
  `canonicalizeCapability`, `js.ts` `canonicalizeCapability`).
- **C# codegen core**: `EmitCsOptions.Capability` (default `"all"`,
  `Sleipnir.Codegen.Core/Model.cs` `EmitCsOptions`); the C# emitter bakes the
  literal (`CsEmitter.cs` `Emit`).
- **.NET Roslyn source generator** (`Sleipnir.Generator`): does **not** parse a
  `--transport` flag — emits with default `EmitCsOptions` (capability `all`)
  (`Sleipnir.SourceGenerator/CodegenSeam.cs` `Emit`;
  `SleipnirClientGenerator.cs` `Generate`). The only generator global option read
  is `build_property.sleipnircontractfile` (`SleipnirClientGenerator.cs`
  `ReadAdditionalFiles`).
- **Runtime**: the generated root ctor wraps
  `new SleipnirRouterOptions { ... Capability = SleipnirBundleCapability.<Cap> }`
  (`CsEmitter.cs` `Emit` — the root-ctor `SleipnirRouterOptions` block).

---

## 5. The individual backends

All implement `ISleipnirClient` (`SleipnirClient/Sleipnir/ISleipnirClient.cs`)
and derive from `SleipnirClientBase` (`SleipnirClientBase.cs`), which provides
shared `Call<T>()` and `CallBinary()` that throw `SleipnirException` on non-2xx,
and default `SubscribeAsync`/`ResumeAsync` that throw `NotSupportedException`
(the throwing defaults in `SleipnirClientBase`).

### `SleipnirRestJsonClient` — `SleipnirRestJsonClient.cs`

`public class ... : SleipnirClientBase, ISleipnirClient, IDisposable` — **calls
only**.

- `Call`, `Call(SleipnirMultiRequest?)`. No `SubscribeAsync`/`ResumeAsync`
  override (inherits the throwing base).
- Owns an `HttpClient` with `SocketsHttpHandler.PooledConnectionLifetime = 2 min`.
  **Thread-safe by construction** (HttpClient) — no `SemaphoreSlim`.
- No auto-reconnect (stateless HTTP). `IDisposable`, not `IAsyncDisposable`.

### `SleipnirWebSocketClient` — `SleipnirWebSocketClient.cs`

`public class ... : SleipnirClientBase, ISleipnirClient, IAsyncDisposable` —
calls + events.

- `ConnectAsync`, `Call`, `Call(multi)`.
- Subscribe: positional
  `SubscribeAsync<T>(string controller, string method, object?[]? args, ResumePolicy?, CancellationToken)`
  and unified `SubscribeAsync<T>(SleipnirRequest?, ResumePolicy?, CancellationToken)`.
- **No `ResumeAsync<T>` override** — resuming into WS throws (router guards it
  explicitly; see §6).
- Thread-safety: `_sendLock`, `_connectLock`, `ConcurrentDictionary` for pending
  requests, subscriptions, subscribe-requests.
- Auto-reconnect with backoff
  `DefaultReconnectDelays = {2,2,5,5,10,10,30,30s,1,1,5min}`; exhausted
  → `Disconnected` + log `"WebSocket reconnect exhausted — connection stays offline."`;
  in-flight calls rejected on drop; new calls during reconnect wait on the same
  in-flight task. **Empty `reconnectDelays` disables reconnect**.
- State enum `SleipnirConnectionState { Disconnected, Connecting, Connected, Reconnecting }`
  (`SleipnirConnectionState.cs`).
- `IAsyncDisposable.DisposeAsync`.
- No constructor `bearer` convenience — inject a pre-configured
  `ClientWebSocket`/`socketFactory` (`SleipnirClient/README.md` — bearer-injection note).

### `SleipnirSseClient` — `SleipnirSseClient.cs`

`public sealed class ... : SleipnirClientBase, ISleipnirClient, IDisposable` —
**events only**.

- `Call`/`Call(multi)` throw `NotSupportedException`
  (`"SleipnirSseClient is an events-only transport..."`).
- `SubscribeAsync<T>(SleipnirRequest?, ResumePolicy?, CancellationToken)`,
  `ResumeAsync<T>(string subscriptionId, long lastEventId, ResumePolicy?, CancellationToken)`,
  `SetBearer`.
- Backoff `DefaultReconnectDelays = {0,1,2,5,10,15,30s}`;
  `HttpClient.Timeout = InfiniteTimeSpan`; empty array disables reconnect.
- Resume URL `{apiPath}/events/{subscriptionId}?lastEventId=…` with the
  `Last-Event-Id:` header (built in `ResumeAsync`); **410 Gone** on a GC'd durable
  id degrades resume → Fresh once, or is terminal on a pure-resume (the
  410-handling branch in `ResumeAsync`).
- `IDisposable.Dispose`.

> **Doc-bug note:** `CLAUDE.md` earlier documented this client as
> `IAsyncDisposable`. The code is `IDisposable` (synchronous `Dispose` disposing
> the `HttpClient`). This reference follows the code; `CLAUDE.md` has since been
> corrected — see §10.

### `SleipnirSignalrClient` — `SleipnirSignalrClient.cs`

`public class ... : SleipnirClientBase, ISleipnirClient, IAsyncDisposable` —
calls + events.

- Calls via hub invoke `"DoWork"` / `"DoWorkMany"`.
- Subscribe `SubscribeAsync<T>(SleipnirRequest?, ResumePolicy?, CancellationToken)`
  and `ResumeAsync<T>(string, long, ResumePolicy?, CancellationToken)` — both via
  hub `StreamAsync<string>("SubscribeAsync", args, ct)`. First stream item is the
  `ack`, then event/complete/error frames (`HandleFrame`).
- Thread-safety: `_connectLock = new SemaphoreSlim(1,1)`; `_activeSubs`
  `ConcurrentDictionary`.
- Auto-reconnect is SignalR's own `WithAutomaticReconnect` with the same backoff
  schedule; `Reconnecting`/`Reconnected` handlers **re-stream active subs in
  resume mode**; a stream-end during reconnect is left for `Reconnected`.
- `IAsyncDisposable.DisposeAsync`.
- MessagePack: **client defaults `useMessagePack: true`** with a custom
  `JsonElementResolver` to avoid double-wrapping. Note the server
  `SleipnirOptions.UseMessagePack` default is `false` — see the mismatch note in
  §10.

### `SleipnirSubscription<T>` — `SleipnirSubscription.cs`

`sealed class ... : IObservable<T>, IDisposable`. Wraps a `SleipnirSubject<T>`;
`Dispose` sends unsubscribe best-effort with a 5 s timeout.

---

## 6. Events & subscriptions across transports

### The subscribe/resume surface

- **`SubscribeAsync<T>`** — on `ISleipnirClient` (`ISleipnirClient.cs`); base
  default throws `NotSupportedException` (`SleipnirClientBase.cs`); overridden by
  WS (`SleipnirWebSocketClient.cs`), SSE (`SleipnirSseClient.cs`), SignalR
  (`SleipnirSignalrClient.cs`), and the router (`SleipnirTransportRouter.cs`).
- **`ResumeAsync<T>(subscriptionId, lastEventId, ResumePolicy?, CancellationToken)`**
  — `ISleipnirClient.cs`; base default throws (`SleipnirClientBase.cs`);
  overridden by SSE (`SleipnirSseClient.cs`), SignalR (`SleipnirSignalrClient.cs`),
  and the router (`SleipnirTransportRouter.cs`). **WS does not override it** →
  resuming into WS throws (router guards; see §6).

### `ResumePolicy` & `ResumeDecision` — `ResumeDecision.cs`

```csharp
public enum ResumeDecision { Fresh, Resume, Drop }
public record struct SubscriptionResumeContext(
    string Controller, string Method, string SubscriptionId, long? LastEventId);
public delegate ResumeDecision? ResumePolicy(SubscriptionResumeContext);
```

Consulted on WS reconnect (`SleipnirWebSocketClient.cs` reconnect path) and SSE
reconnect (`SleipnirSseClient.cs` reconnect path). Returning `null` means
"framework default". SignalR re-streams in resume mode **without consulting the
policy** (`SleipnirSignalrClient.cs` `Reconnected` handler).

| Decision | Meaning |
|----------|---------|
| `Fresh` | Re-subscribe from scratch (new id, no replay) |
| `Resume` | Resume by id + lastEventId (server replays the durable buffer) |
| `Drop` | Abandon the subscription |
| `null` | Framework default for that transport |

### Last-Event-Id resume

- **SSE**: resume URL `{apiPath}/events/{subscriptionId}?lastEventId=…` with the
  `Last-Event-Id:` header on the GET (`SleipnirSseClient.cs` `ResumeAsync`);
  server reads the header then the query fallback
  (`SleipnirSseEndpointExtensions.cs` resume route handler).
- **WS**: resume sends `subscriptionId` + `lastEventId` fields inside the
  `subscribe` frame (`SleipnirWebSocketClient.cs` `ResubscribeAllAsync`; server
  extraction `SleipnirWebSocketMiddleware.cs` subscribe-frame handler).
- **SignalR**: `StreamAsync("SubscribeAsync", req, resumeId?, lastEventId?, ct)`
  (`SleipnirSignalrClient.cs` `ResumeAsync`).

### Cross-transport resume

Durable subscriptions live in the **process-wide** `SleipnirSubscriptionStore`
(`SleipnirCore/Services/SleipnirSubscriptionStore.cs`), shared by all three event
transports (`Lookup`/`Attach`/`Detach`/`Destroy`/`BeginCreate`/`OnAttached`). A
subscription created over WS is resumable over SSE/SignalR and vice-versa
(`SleipnirSseClient.cs`, `SleipnirSignalrClient.cs`, `SleipnirHub.cs` — each
stores into the shared `SleipnirSubscriptionStore`).

The first frame yielded/streamed is always `EventFrame.Ack(subscriptionId, replayedFrom)`
(`SleipnirCore/Events/EventFrame.cs`); subsequent frames are the same serialized
strings every transport emits.

### Intentionally deferred: WS-direction resume

Resuming **into** WebSocket (`router.ResumeAsync` with the active event backend =
WS) throws `NotSupportedException` with guidance to switch to `rest`/`auto`
(`SleipnirTransportRouter.cs` `ResumeAsync` WS guard; `PROTOCOL.md`
§"Transport selection (client)"). Reason: a WS resume frame needs the original
controller/method/params (not carried by a `SleipnirSubscription` handle). WS
**reconnect** re-subscribe is supported (`ResubscribeAllAsync` in
`SleipnirWebSocketClient.cs`); resume-by-id into a fresh WS connection is not.
The blessed cross-transport bridge is the **SSE direction**.

> **Server restart:** the durable store is **in-process** — subscriptions do not
> survive a server restart (`README_DETAILS.md` §"Resume (Last-Event-Id) — resumable events").

---

## 7. Server registration & `SleipnirOptions`

### `AddSleipnir` (services) — `SleipnirHub/Extensions/SleipnirServiceCollectionExtension.cs` (`AddSleipnir`)

Two overloads: `Action<SleipnirOptions>` and `SleipnirOptions`.

1. Registers `SleipnirOptions` as singleton.
2. Eagerly creates + registers `SleipnirConnectionRegistry`.
3. Registers `SleipnirSubscriptionStore` singleton (durable store).
4. If `UseSignalR`: `AddSignalR(...)` with hub-option overrides; if
   `UseMessagePack`: `AddMessagePackProtocol` with `JsonElementResolver.Instance`.
5. Configures Minimal-API JSON options host-wide (camelCase, relaxed encoder,
   `SleipnirResponseJsonConverter`).
6. Registers the rate limiter with the `"sleipnir"` fixed-window policy when
   `RateLimitPermitLimit > 0`.
7. Registers `ISleipnirCore` (`SleipnirInvoker`) singleton with all propagated
   options.
8. Registers built-in interceptors (Auth → Telemetry → Logging) when
   `RegisterBuiltInInterceptors`; user interceptors appended inner.
9. Auto-discovers `[SleipnirController]` types as scoped when
   `AutoDiscoverControllers`.

### `UseSleipnir`

Resolves `ISleipnirCore`, applies a `SleipnirControllerBuilder` if registered,
otherwise auto-registers `[SleipnirController]` types with the invoker
(`UseSleipnir` auto-register branch). Warns when user interceptors are registered
(the batch path bypasses the interceptor pipeline — auth still enforced)
(`UseSleipnir` interceptor-warning branch).

### `UseSleipnirTransports` — `SleipnirServer/SleipnirPipelineExtensions.cs` (`UseSleipnirTransports`)

Reads `SleipnirOptions`; if `UseWebSocket != false`, calls `UseWebSockets()` +
`UseSleipnirWebSocket(webSocketPath)` (default `/sleipnirws`); always calls
`UseSleipnir()` (controller registration). Emits a startup introspection log
`Sleipnir transports: REST=…, WebSocket=…, SignalR=…, SSE=…` (the introspection
log in `UseSleipnirTransports`).

### `MapSleipnir` — `SleipnirServer/SleipnirPipelineExtensions.cs` (`MapSleipnir`)

- If `UseRest != false`: `MapSleipnirEndpoints(...)` with
  `enableRateLimiting`/`enableJsonRpcCompat`/`enableObservability`/`signalREnabled`/`useWebSocket`/`useSse`/`sseBufferCapacity`
  threaded from options (the `MapSleipnirEndpoints` call in `MapSleipnir`) and
  `MapSleipnirDeveloperUi`.
- If `UseSignalR == true`: `MapHub<SleipnirHub>(hubPath)` (default `/sleipnirhub`)
  with `RequireAuthorization` when `RequireAuthentication` and
  `RequireRateLimiting("sleipnir")` when `RateLimitPermitLimit > 0` (the
  `MapHub<SleipnirHub>` block in `MapSleipnir`).

### MessagePack opt-in

`SleipnirOptions.UseMessagePack` → `AddMessagePackProtocol` with
`JsonElementResolver.Instance` on the server
(`SleipnirServiceCollectionExtension.cs` `AddMessagePackProtocol` branch);
mirrored on the C# SignalR client (`SleipnirSignalrClient.cs` MessagePack setup).

### CORS

**Not configured by Sleipnir.** No `AddCors`/`UseCors` call in
`AddSleipnir`/`UseSleipnirTransports`/`MapSleipnir`. Hosts wire it themselves
(e.g. `samples/server/Program.cs`, `guide/server/Program.cs` — the `UseCors`
call). Guidance: `BEST_PRACTICES.md` §"1.5 Host and proxy".

### The canonical 3-call wiring

```csharp
builder.Services.AddSleipnir(options => { /* SleipnirOptions */ });
var app = builder.Build();
app.UseSleipnirTransports();   // UseWebSockets + UseSleipnir + (UseSleipnirWebSocket)
app.MapSleipnir();             // REST endpoints (+ SSE) + SignalR hub + DevUI
app.Run();
```

(`GETTING_STARTED.md` §"2. Wire Sleipnir in `Program.cs`".)

---

## 8. Usage patterns

### 8.1 Single call (generated typed client)

The generated client wraps a `SleipnirTransportRouter`; the surface is identical
regardless of capability:

```csharp
using var client = new SleipnirClient(new SleipnirClientOptions {
    BaseUrl = "https://localhost:5001",
    Bearer = token,            // if needed
});
var order = await client.Order.GetById(42);
```

### 8.2 Picking the bundle at construction (raw router)

```csharp
using var router = new SleipnirTransportRouter(new SleipnirRouterOptions {
    BaseUrl = "https://localhost:5001",
    Capability = SleipnirBundleCapability.Rest,   // HTTP-only, proxy-safe
    // Capability = SleipnirBundleCapability.Ws,  // WS-only, no fallback
    // Capability = SleipnirBundleCapability.All, // default — auto WS→REST+SSE
    // Capability = SleipnirBundleCapability.Signalr,
});
var res = await router.Call(new SleipnirRequest { Controller = "Order", Method = "GetById", Params = ... });
```

### 8.3 Switching transport at runtime + escape hatch

```csharp
await router.UseTransportAsync(SleipnirTransport.Rest);   // force REST
var direct = router.Sse;                                   // raw SSE backend (events-only)
await router.UseTransportAsync(SleipnirTransport.Auto);    // back to auto (re-probes WS)
```

### 8.4 Server-push events + resume after fallback

```csharp
var sub = await router.SubscribeAsync<Quote>(
    SleipnirCall.Init("Market", "Stream").ToRequest(),
    resumePolicy: ctx => ResumeDecision.Resume,
    cancellationToken: ct);
sub.Subscribe(q => Console.WriteLine(q.Symbol));
// ... WS dies → router auto-falls back to REST+SSE; events resume over SSE by id+lastEventId ...

// Later, explicit cross-transport resume (e.g. after process restart of the consumer):
var lastId = sub.LastEventId;
await router.ResumeAsync<Quote>(sub.SubscriptionId, lastId, ...);
```

> `ResumeAsync` into a WS-active router throws — switch to `rest`/`auto` first
> (see §6).

### 8.5 Events-only consumer (SSE escape hatch)

```csharp
using var sse = new SleipnirSseClient(new SleipnirSseClientOptions { BaseUrl = "...", Bearer = token });
var sub = await sse.SubscribeAsync<Quote>(req, ...);
// sse.Call(...) throws NotSupportedException — events only.
```

---

## 9. Configuration reference (all knobs)

### Server — `SleipnirHub/Extensions/SleipnirOptions.cs`

| Option | Type | Default | Role |
|--------|------|---------|------|
| `UseRest` | `bool` | `true` | Gates REST endpoint group + DevUI |
| `UseWebSocket` | `bool` | `true` | Gates `UseWebSockets` + `UseSleipnirWebSocket` |
| `UseSse` | `bool` | `true` | Gates the SSE `/events/...` endpoints |
| `UseSignalR` | `bool` | `false` | Opt-in SignalR transport + hub mapping |
| `UseMessagePack` | `bool` | `false` | SignalR MessagePack protocol (server) |
| `EnableJsonRpcCompat` | `bool` | `false` | Registers `POST {prefix}/jsonrpc` |
| `EnableObservability` | `bool` | `false` | Registers `GET {prefix}/observability` |
| `RequireAuthentication` | `bool` | `false` | North-bound default-deny (WS upgrade, `/discovery`, `/observability`, JSON-RPC `sleipnir.discover`, hub `RequireAuthorization`) |
| `RateLimitPermitLimit` | `int` | `0` (off) | Fixed-window `"sleipnir"` permit limit; `>0` enables it on REST + hub |
| `RateLimitWindowSeconds` | `int` | `10` | Fixed-window window size |
| `MaximumReceiveMessageSize` | `long?` | `null` (SignalR default) | SignalR hub max message size |
| `StreamBufferCapacity` | `int?` | `null` | SignalR `HubOptions.StreamBufferCapacity` |
| `MaximumParallelInvocationsPerClient` | `int` | `0` | SignalR; only applied when `>0` (else SignalR throws at startup) |
| `MaximumBatchSize` | `int` | `0` (off) | Fan-out DoS cap; enforced at REST `/json/multi`, WS multi, JSON-RPC batch |
| `EventBufferCapacity` | `int?` | `null` (fb 100) | Per-subscription event send-buffer (WS/SSE/SignalR) |
| `EventBackpressureStrategy` | `EventBackpressureStrategy` | `DropOldest` | Overflow strategy |
| `EventReplayBufferCapacity` | `int?` | `null` (fb 1000) | Durable replay ring capacity |
| `EventResumeTtl` | `TimeSpan?` | `null` (fb 60s) | Idle durable subscription TTL |
| `EventMaxDurableSubscriptions` | `int?` | `null` (fb 10 000) | Process-wide durable cap (over-cap → 503) |

Transport-adjacent (not transport itself, but on the same options object):
`AliasBindingMode` (default `Weak`), `MaxDependencyPathLength`,
`AllowRecursiveDescent`, `MaxParameterArrayLength`, `MaxResultElementCount`,
`AutoDiscoverControllers`, `EnableDetailedErrors`. See `DEPENDENCY_BINDING.md`
for the alias ones.

### Client — `SleipnirRouterOptions` (`SleipnirTransportRouter.cs`)

See §3 table. Defaults: `Capability = All`, `DefaultTransport = Auto`,
`ProbeTimeout = 1500ms`, `ApiPath = "api/sleipnir"`, `WsPath = "sleipnirws"`,
`HubPath = "sleipnirhub"`.

### Reconnect schedules (per backend)

| Backend | `DefaultReconnectDelays` | Where | Disable |
|----------|--------------------------|-------|---------|
| WS | `{2,2,5,5,10,10,30,30s,1,1,5min}` | `SleipnirWebSocketClient.cs` | empty array |
| SSE | `{0,1,2,5,10,15,30s}` | `SleipnirSseClient.cs` | empty array |
| SignalR | same schedule via `WithAutomaticReconnect` | `SleipnirSignalrClient.cs` | (SignalR-managed) |
| REST | — (stateless) | — | n/a |

### Message-size caps

| Surface | Cap | Source |
|---------|-----|--------|
| REST body | 1 MB | `SleipnirEndpointExtensions.cs` (`[RequestSizeLimit(1_048_576)]`) |
| WS message | 1 MB | `SleipnirWebSocketMiddleware.cs` (`MaxMessageSize`) |
| SignalR | `MaximumReceiveMessageSize` (default per SignalR) | `SleipnirOptions.cs` (`MaximumReceiveMessageSize`) |
| Batch fan-out | `MaximumBatchSize` (default unlimited) | `SleipnirOptions.cs` (`MaximumBatchSize`) |

Compression per transport: `BEST_PRACTICES.md` §"1.6 Compression — enable at the
transport, not in Sleipnir". Binary per transport: `BEST_PRACTICES.md`
§"2.1 `byte[]` travels out of band", `README.md` (binary-per-transport section).

---

## 10. Diagnostics & troubleshooting catalog

### Errors

**`SleipnirException`** (`SleipnirCommon/Exceptions/SleipnirException.cs`) is the
unified exception for all transport + invocation errors; carries `SleipnirError?`.
`SleipnirError` carries `Code`, `Message`, `RequestId`, `Category`
(`SleipnirCommon/Models/SleipnirError.cs`). See `ERROR_CATALOG.md` for the
category-aware handling (499 = client closed on REST).

| Symptom | Cause / message | Where |
|---------|-----------------|-------|
| `Sleipnir transport 'X' is not available: the client was generated with --transport <cap>...` | `UseTransportAsync`/profile resolve hit a backend not bundled for the capability (`NotBundled`) | `SleipnirTransportRouter.cs` — `NotBundled` |
| `Sleipnir transport 'X' is not a valid profile.` | Unknown `SleipnirTransport` value | `SleipnirTransportRouter.cs` — `ResolveProfile` |
| `SleipnirTransportRouter: BaseUrl is required.` | Blank `BaseUrl` | `SleipnirTransportRouter.cs` — ctor guard |
| `NotSupportedException` on `ResumeAsync` into WS | WS-direction resume deferred — switch to `rest`/`auto` | `SleipnirTransportRouter.cs` — `ResumeAsync` WS guard |
| `NotSupportedException` "events-only transport..." on SSE `Call` | `SleipnirSseClient` carries events only | `SleipnirSseClient.cs` |
| `"Not connected to server."` (SignalR) | Hub not connected before a call/subscribe | `SleipnirSignalrClient.cs` |
| `"WebSocket reconnect exhausted — connection stays offline."` | Reconnect backoff exhausted | `SleipnirWebSocketClient.cs` |
| `"SSE resume target gone (410): subscription expired."` | Durable id GC'd; pure-resume is terminal, mixed degrades once | `SleipnirSseClient.cs` |
| `"Malformed SSE ack block."` | First SSE block was not a valid `ack` | `SleipnirSseClient.cs` |
| `503` on subscribe | Durable subscription cap exceeded (`EventMaxDurableSubscriptions`) | `SleipnirOptions.cs` — `EventMaxDurableSubscriptions` |

### Known limitations & gotchas

- **MessagePack default mismatch:** `SleipnirOptions.UseMessagePack` defaults
  `false` (server), but the C# `SleipnirSignalrClient` defaults `useMessagePack:
  true` (`SleipnirSignalrClient.cs` — `useMessagePack` default). If the server does
  not enable MessagePack, the SignalR client must be constructed with
  `useMessagePack: false` or the handshake will fail.
- **`MaximumParallelInvocationsPerClient`** must be `>0` if set, otherwise
  SignalR throws at startup (`SleipnirServiceCollectionExtension.cs` `AddSignalR`
  validation).
- **WS has no native binary frames** — JSON text only (`SleipnirWebSocket/README.md`).
  Use REST/SignalR for `byte[]`.
- **SSE named-params only** — positional/binary are WS/SignalR-only
  (`SleipnirTransportRouter.cs` `SubscribeAsync` SSE-unpack branch).
- **Durable store is in-process** — no resume across a server restart
  (`README_DETAILS.md` §"Resume (Last-Event-Id) — resumable events").
- **Batch paths bypass user interceptors** — REST `/json/multi`, WS multi,
  JSON-RPC batch skip the interceptor pipeline (auth still enforced)
  (`SleipnirServiceCollectionExtension.cs` `UseSleipnir` interceptor-warning branch).
- **`WebSocketAllowedOrigins` (CSWSH protection) is planned, not implemented**
  (`docs/audits/2026-08-08-consolidation-roadmap.md` — `WebSocketAllowedOrigins`
  entry).
- **Python stays REST-only** (no async WS/SSE runtime in the codegen client)
  (`clients/codegen/README.md`, `sleipnir-unified-transport` memory).
- **Deprecated capability aliases** `sse` (→ `rest`) and `both` (→ `all`) kept
  for one minor version; scheduled for removal next major
  (`clients/codegen/src/emitters/ts.ts` `canonicalizeCapability`).

### Doc-bugs addressed

- `CLAUDE.md` §"Client Library (`SleipnirClient`)" called `SleipnirSseClient`
  `IAsyncDisposable`; the code is `IDisposable` (`SleipnirSseClient.cs`). **Fixed**
  — `CLAUDE.md` now reads `IDisposable`. (An earlier draft of this note also
  cited `README_DETAILS.md`, but that passage concerns REST streaming
  materialization, not `SleipnirSseClient` disposal — no disposal claim exists in
  `README_DETAILS.md`.)

---

## 11. How it is verified (the gates)

- **Unit (C#):** `SleipnirTests/Unit/Client/SleipnirTransportRouterTests.cs`
  (9 tests, incl. NotBundled + WS-resume-throws); client backend tests.
- **Integration (C#):** `SleipnirTests/Integration/ResumeTests.cs` (cross-transport
  resume), `SleipnirTests/Integration/SignalRHubStreamTests.cs` (fresh+complete,
  resume-replay, auth-reject).
- **TS runtime:** `clients/ts/test/unit/` — `router.test.ts`, `signalr.test.ts`,
  `sse.test.ts`, `websocket.test.ts` (99 tests total).
- **Codegen parity:** `CsCodegenParityTests` (byte-for-byte vs
  `clients/codegen/test/snapshots/story01.cs`); the two C# emitters
  (`clients/codegen/src/emitters/cs.ts` and `Sleipnir.Codegen.Core/CsEmitter.cs`)
  must stay in parity.
- **Known flake:** `Gauges_Read_Current_Registry_Values` (telemetry, not
  transport) fails under parallel integration hosts, passes in isolation — not a
  regression.

Shipped as v1.4.0 (2026-08-20, PR #15 → main `b4866ef`). See the
`sleipnir-unified-transport`, `sleipnir-signalr-phase3`, and
`sleipnir-cross-transport-resume` internal notes for the release history.

---

## 12. Relationship to other docs

| Doc | Covers (transport-relevant) |
|-----|------------------------------|
| `README.md` | Multi-transport thesis, package matrix, server quick start, transport table, binary-per-transport |
| `README_DETAILS.md` | Transports, Server-Push Events, Resume Last-Event-Id, REST Events SSE, Known Limitations v1 |
| `PROTOCOL.md` | Wire spec: Transports, Server-Push Events, REST Events SSE, transport selection + capability→backend table |
| `JSONRPC_COMPAT.md` | JSON-RPC 2.0 adapter: enable, wire shape, endpoint, error-code map, limitations |
| `BEST_PRACTICES.md` | Host/proxy + message-size caps, compression per transport, binary per transport, REST interplay |
| `STABILITY.md` | Stable vs experimental surface: WS/hub paths, transport toggles, client runtime stability, Phase R/S status |
| `GETTING_STARTED.md` | Canonical 3-call wiring |
| `CODEGEN_REFERENCE.md` | `--transport` capability semantics, generated client wraps `SleipnirTransportRouter` |
| `ERROR_CATALOG.md` | Transport-uniform `SleipnirError.Category`, 499 = client closed |
| `CLAUDE.md` | Transport table, JSON-RPC adapter summary, client library |
| `SleipnirClient/README.md` | Full client reference: backends, router, escape hatches, events, resume limitations, binary, errors |
| `SleipnirRest/README.md` / `SleipnirWebSocket/README.md` / `SleipnirHub/README.md` | Per-transport package details |
| `docs/stories/03-the-same-contract-three-wires.md` | "Same contract, three wires" narrative |
| `docs/stories/05-realtime-push-events.md` | `[SleipnirEvent]` push story, wire frame type |

For the **TypeScript/JavaScript** client router specifically, see
`clients/ts/README.md` (router, escape hatches, events) and
`clients/codegen/README.md` (the `--transport` CLI flag).