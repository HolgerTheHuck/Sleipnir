# Sleipnir Transport — User Reference

A consolidated lookup reference for **everything transport** in Sleipnir: the three
wires (REST, WebSocket, SignalR) plus SSE-over-REST events, the unified
`SleipnirTransportRouter` that selects between them at runtime, the capability
bundles, the auto-fallback probe, the escape hatches, cross-transport event
subscription and resume, the server registration flow, and every transport-related
option on `SleipnirOptions`.

This is a **reference**, not a tutorial. When something does not work, look here
first — config tables, endpoint tables, the exact public API with `path:line`
citations, a diagnostics/troubleshooting catalog, and a map of where the deeper
docs live. For onboarding read `GETTING_STARTED.md`; for the marketing-shaped
overview read `README.md`; for the wire-level spec read `PROTOCOL.md`. This doc
consolidates those and links back for depth.

All citations are `repo-relative/path.cs:line` against the repo root. Code-facing
text is English per `CLAUDE.md`.

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
call. `CLAUDE.md:70-76` has the one-line transport table.

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
(`SleipnirClient/Sleipnir/SleipnirTransportRouter.cs:22-27, 120`).

The runtime selection layer is `SleipnirTransportRouter`: the generated client
(`SleipnirClient`/`SleipnirGeneratedClient`) wraps a router, so the public call
surface is byte-identical across every capability — transport is chosen at
runtime, not at codegen time. See `CODEGEN_REFERENCE.md` §6.4 ("Switching
transport at runtime") for the codegen side.

---

## 2. Endpoints & wire formats

Default prefix `/api/sleipnir` (param `prefix` to `MapSleipnirEndpoints`,
`SleipnirRest/SleipnirEndpointExtensions.cs:33`).

### REST — `SleipnirRest/SleipnirEndpointExtensions.cs:32-166`

| Route | Method | Body / params | Line |
|-------|--------|---------------|------|
| `{prefix}/json` | POST | `SleipnirRequest` → `SleipnirResponse` | `:50-63` |
| `{prefix}/json/multi` | POST | `SleipnirMultiRequest` → `SleipnirResponse[]` | `:65-84` |
| `{prefix}/discovery` | GET | – → `DiscoveryInfo` (deterministic serialization) | `:86-99` |
| `{prefix}/observability` | GET | – → JSON snapshot (opt-in `EnableObservability`) | `:107-133` |
| `{prefix}/jsonrpc` | POST | JSON-RPC 2.0 object/array (opt-in `EnableJsonRpcCompat`) | `:153-162` |

- `Content-Type: application/json`; request body limit **1 MB**
  (`[RequestSizeLimit(1_048_576)]`, `:41`).
- Rate limiting applied only when `RateLimitPermitLimit > 0` (policy name
  `"sleipnir"`, `:45-48`).
- REST client posts to `{serverBase}/{apiPath}/json` and `.../json/multi`
  (`SleipnirClient/Sleipnir/SleipnirRestJsonClient.cs:57, 95`).

### SSE-over-REST — `SleipnirRest/SleipnirSseEndpointExtensions.cs:45-113`

Mapped from the REST group when `UseSse` is on
(`SleipnirEndpointExtensions.cs:141-144`).

| Route | Method | Purpose | Line |
|-------|--------|---------|------|
| `{prefix}/events/{controller}/{method}` | GET | fresh subscribe; method args as query params | `:48-76` |
| `{prefix}/events/{subscriptionId}` | GET | resume; `Last-Event-Id:` header (and `?lastEventId=` fallback) | `:79-110` |

Response: `text/event-stream`, `Cache-Control: no-cache`,
`X-Accel-Buffering: no` (`:121-128`). Each logical frame becomes an SSE block
(`id:`/`event:`/`data:`); the **first block is `event: ack`**
(`SleipnirRest/Sse/SleipnirSseConnection.cs:22-27`).

> SSE supports **named params only**; positional params and binary are
> WS/SignalR-only (`SleipnirTransportRouter.cs:267-272`, `PROTOCOL.md:1222-1225`).

### WebSocket — `SleipnirWebSocket`

- Middleware path default **`/sleipnirws`**
  (`SleipnirWebSocket/SleipnirWebSocketExtensions.cs:14-20`).
- Accepts the WS upgrade; **1 MB max message** (`MaxMessageSize = 1_048_576`,
  `SleipnirWebSocket/SleipnirWebSocketMiddleware.cs:80`); rejects unauthenticated
  upgrades when `RequireAuthentication` (`:67-71`).
- Wire: **JSON text frames** (no native binary frames —
  `SleipnirWebSocket/README.md:184`, `README_DETAILS.md:821`). Messages are
  auto-detected as multi (`requests` + `mode` fields) vs single (`:279-320`);
  subscribe/unsubscribe detected via a `kind` field (`:211-268`).

### SignalR — `SleipnirHub`

- Hub mapped at **`/sleipnirhub`**
  (`SleipnirHub/Extensions/SleipnirWebAppExtension.cs:12`,
  `SleipnirServer/SleipnirPipelineExtensions.cs:65, 98`).
- Hub methods: `DoWork(SleipnirRequest) → SleipnirResponse?` and
  `DoWorkMany(SleipnirMultiRequest?) → IEnumerable<SleipnirResponse>`
  (`SleipnirHub/Hub/SleipnirHub.cs:63-86`).
- Event subscribe is the streaming
  `SubscribeAsync(SleipnirRequest, string?, long?, [EnumeratorCancellation] CancellationToken) → IAsyncEnumerable<string>`
  (`SleipnirHub.cs:100-104`). The first yielded item is the `ack` frame, then
  `event`/`complete`/`error` frames (`SleipnirHub.cs:194, 204-211`) — the **same
  serialized string frames WS/SSE emit**, so cross-transport resume works.
- Wire: WebSocket + **MessagePack binary** (opt-out via `UseMessagePack=false`;
  `SleipnirHub/README.md:10`, `SleipnirServiceCollectionExtension.cs:97-105`).

### JSON-RPC 2.0 compat adapter — `SleipnirRest/JsonRpc/`

- Single endpoint `POST {prefix}/jsonrpc`, opt-in via
  `SleipnirOptions.EnableJsonRpcCompat` (default `false`)
  (`SleipnirEndpointExtensions.cs:26-30, 151-163`;
  `SleipnirHub/Extensions/SleipnirOptions.cs:207-214`).
- Reads raw body (object = single, array = batch); **always 200** with the
  JSON-RPC envelope in the body, **204** only when every item was a notification
  (`JsonRpcDispatcher.cs:31-60`).
- Capability methods dispatched directly: `sleipnir.discover` (auth-gated when
  `RequireAuthentication`) and `sleipnir.capabilities` (`JsonRpcDispatcher.cs:135-147`,
  `JsonRpcAdapter.cs:58`).
- `method` is `Controller.Method` (split at the last dot); `params` object → named,
  array → positional (by `num`); `id` echoed with original type.
- Error-code map: routing 404 → `-32601`, business 404 → `-32000`,
  `400/422 → -32602`, `401/403 → -32001`, `500 → -32603`, parse → `-32700`,
  invalid → `-32600` (`JsonRpcAdapter.cs:13-17`, `CLAUDE.md:78`).
- **Limitations:** no `@alias` chaining, Parallel-only (no execution-mode
  selection), no binary out-of-band, no streaming — graduate to the native wire
  (`CLAUDE.md:78`, `PROTOCOL.md:1190-1192`). Full spec: `JSONRPC_COMPAT.md`.

---

## 3. The unified client — `SleipnirTransportRouter`

File: `SleipnirClient/Sleipnir/SleipnirTransportRouter.cs`.

```csharp
public class SleipnirTransportRouter : SleipnirClientBase, IAsyncDisposable   // :75
public SleipnirTransportRouter(SleipnirRouterOptions opts) : base()            // :90
```

- Throws `ArgumentException` if `BaseUrl` is blank (`:92-93`).
- Instantiates backends per `HasBackend(...)` (`:99-109`). A non-`Auto`
  `DefaultTransport` resolves the profile immediately; `Auto` is lazy
  (`:111-113`).

### `SleipnirRouterOptions` — ctor input (`:41-61`)

| Field | Type | Default | Notes |
|-------|------|---------|-------|
| `BaseUrl` | `string` | *(required)* | Throws if blank |
| `Capability` | `SleipnirBundleCapability` | `All` | Selects which backends are bundled |
| `DefaultTransport` | `SleipnirTransport` | `Auto` | Initial profile |
| `Bearer` | `string?` | `null` | Auth token |
| `CallTimeout` | `TimeSpan?` | `null` | Per-call timeout |
| `ProbeTimeout` | `TimeSpan?` | `1500ms` | WS handshake probe for `auto` (`:50-51, 96`) |
| `ApiPath` | `string` | `"api/sleipnir"` | REST/SSE prefix |
| `WsPath` | `string` | `"sleipnirws"` | WS path |
| `HubPath` | `string` | `"sleipnirhub"` | SignalR hub path |
| `ReconnectDelays` | `TimeSpan[]?` | per-backend defaults | Empty array disables reconnect |
| `ResumePolicy` | `ResumePolicy?` | `null` | Event resume decision delegate |
| `RestHttpClient` | `HttpClient?` | `null` | Inject a custom `HttpClient` for REST+SSE |

### Auto probe + fallback

```csharp
public async Task NegotiateAsync(CancellationToken ct = default)              // :162-177
```

`RunAutoNegotiationAsync` (`:179-207`):

- If `_ws == null` → fall back to `Rest` immediately (`:182-187`).
- Otherwise probe the WS handshake under `_probeTimeout` (default **1500 ms**,
  `:96`) via `_ws.ConnectAsync(probeCts.Token)`.
  - Success → profile `Ws`.
  - Failure/timeout → profile `Rest`.
- If the chosen fallback backend is not bundled, `throw NotBundled(Auto)`
  (`:205-206`).
- Lazy and concurrent-safe via `_negotiateLock` (`:88`, `:166-176`).

The headline auto-fallback case: **WS dies → calls fall back to REST, events
resume over SSE.**

### Switching transport at runtime

```csharp
public async Task UseTransportAsync(SleipnirTransport t, CancellationToken ct = default)  // :210-221
```

`Auto` clears `_profile` and re-runs negotiation; any other value goes through
`ResolveProfile` (`:137-153`), which throws `NotBundled(t)` — a `SleipnirException`
(`:155-156`) — if the required backend was not bundled for the current capability.

### Escape hatches (read-only, `null` if not bundled)

```csharp
public SleipnirRestJsonClient?  Rest    { get; }   // :118
public SleipnirWebSocketClient? Ws      { get; }   // :119
public SleipnirSseClient?       Sse     { get; }   // :120
public SleipnirSignalrClient?   Signalr { get; }   // :121
public string? ActiveTransport { get; }            // :124  — lowercase profile name, null until resolved
```

Use these to reach a raw bundled backend directly (e.g. a REST-only call from an
`all` client, or the SSE client for an events-only consumer).

### Routing

- Calls: `Call(SleipnirRequest?)` (`:245-253`), `Call(SleipnirMultiRequest?)`
  (`:255-263`). `CallBackend()` maps `Ws`/`Signalr` to their backend, default
  `Rest` (`:229-234`).
- Subscribe: `SubscribeAsync<T>(SleipnirRequest?, ResumePolicy?, CancellationToken)`
  (`:273-283`). SSE unpacks the request (controller/method/params → query params);
  WS/SignalR pass it straight through (`:267-272`). `EventBackend()` maps
  `Ws`/`Signalr`, default `Rest` (SSE) (`:236-241`).
- Resume: `ResumeAsync<T>(string subscriptionId, long lastEventId, ResumePolicy?, CancellationToken)`
  (`:291-302`). **Resuming into WebSocket throws `NotSupportedException`** with
  guidance to switch to `rest`/`auto` (`:296-299`). See §6.
- `SetBearer(string?)` fans the bearer to **SSE + SignalR only** (`:307-313`).
- `DisposeAsync` (`:315-324`): disposes WS + SignalR (async), SSE + REST (sync),
  then `_negotiateLock`.

---

## 4. Capability values & what they bundle

### `SleipnirBundleCapability` (`SleipnirTransportRouter.cs:10-20`)

| Value | Bundles | Use |
|-------|---------|-----|
| `Rest` | REST calls + SSE events | HTTP-only, proxy-safe clients |
| `Ws` | WS calls + WS events | WebSocket-only, no fallback |
| `All` | REST + WS + SSE | Enables `auto` (WS → REST+SSE fallback) — **default** |
| `Signalr` | `All` + SignalR | Opt-in SignalR add-on (hub-streaming events) |

### `SleipnirTransport` — the runtime profile enum (`:28-38`)

`Auto, Rest, Ws, Signalr`. There is **no `Sse` profile** (SSE cannot carry calls);
the raw SSE backend is reachable only via the `Sse` escape hatch (`:22-27`).

### Backend selection — `HasBackend` switch (`:128-135`)

| Backend | Bundled for capabilities |
|---------|--------------------------|
| `SleipnirRestJsonClient` (rest) | `Rest`, `All`, `Signalr` |
| `SleipnirWebSocketClient` (ws) | `Ws`, `All`, `Signalr` |
| `SleipnirSseClient` (sse) | `Rest`, `All`, `Signalr` |
| `SleipnirSignalrClient` (signalr) | `Signalr` only |

### Where the capability string is set

- **Codegen CLI**: `--transport rest|ws|all|signalr` (default `all`)
  (`clients/codegen/README.md:37, 53`, `PROTOCOL.md:1200-1202`). TS/JS emitters
  canonicalize deprecated aliases `sse → rest`, `both → all`
  (`clients/codegen/src/emitters/ts.ts:45-48`, `js.ts:22`).
- **C# codegen core**: `EmitCsOptions.Capability` (default `"all"`,
  `Sleipnir.Codegen.Core/Model.cs:196-199`); the C# emitter bakes the literal
  (`CsEmitter.cs:30-39, 180`).
- **.NET Roslyn source generator** (`Sleipnir.Generator`): does **not** parse a
  `--transport` flag — emits with default `EmitCsOptions` (capability `all`)
  (`Sleipnir.SourceGenerator/CodegenSeam.cs:34`,
  `SleipnirClientGenerator.cs:89-91`). The only generator global option read is
  `build_property.sleipnircontractfile` (`SleipnirClientGenerator.cs:53-62`).
- **Runtime**: the generated root ctor wraps
  `new SleipnirRouterOptions { ... Capability = SleipnirBundleCapability.<Cap> }`
  (`CsEmitter.cs:180`).

---

## 5. The individual backends

All implement `ISleipnirClient` (`SleipnirClient/Sleipnir/ISleipnirClient.cs:11-46`)
and derive from `SleipnirClientBase` (`SleipnirClientBase.cs:7`), which provides
shared `Call<T>()` (`:28-64`) and `CallBinary()` (`:71-86`) that throw
`SleipnirException` on non-2xx, and default `SubscribeAsync`/`ResumeAsync` that
throw `NotSupportedException` (`:99-112`).

### `SleipnirRestJsonClient` — `SleipnirRestJsonClient.cs:12`

`public class ... : SleipnirClientBase, ISleipnirClient, IDisposable` — **calls
only**.

- `Call` (`:48-78`), `Call(SleipnirMultiRequest?)` (`:80-118`). No
  `SubscribeAsync`/`ResumeAsync` override (inherits the throwing base).
- Owns an `HttpClient` with `SocketsHttpHandler.PooledConnectionLifetime = 2 min`
  (`:32-39`). **Thread-safe by construction** (HttpClient) — no `SemaphoreSlim`.
- No auto-reconnect (stateless HTTP). `IDisposable`, not `IAsyncDisposable`
  (`:120-132`).

### `SleipnirWebSocketClient` — `SleipnirWebSocketClient.cs:23`

`public class ... : SleipnirClientBase, ISleipnirClient, IAsyncDisposable` —
calls + events.

- `ConnectAsync` (`:145-181`), `Call` (`:183-190`), `Call(multi)` (`:192-203`).
- Subscribe: positional
  `SubscribeAsync<T>(string controller, string method, object?[]? args, ResumePolicy?, CancellationToken)`
  (`:661-704`) and unified `SubscribeAsync<T>(SleipnirRequest?, ResumePolicy?, CancellationToken)`
  (`:713-756`).
- **No `ResumeAsync<T>` override** — resuming into WS throws (router guards it
  explicitly, `SleipnirTransportRouter.cs:296-299`).
- Thread-safety: `_sendLock` (`:47`), `_connectLock` (`:48`),
  `ConcurrentDictionary` for pending requests (`:49`), subscriptions (`:50`),
  subscribe-requests (`:51`).
- Auto-reconnect with backoff
  `DefaultReconnectDelays = {2,2,5,5,10,10,30,30s,1,1,5min}` (`:30-43`); exhausted
  → `Disconnected` + log `"WebSocket reconnect exhausted — connection stays offline."`
  (`:589-590`); in-flight calls rejected on drop (`:401`); new calls during
  reconnect wait on the same in-flight task (`:214-221`). **Empty
  `reconnectDelays` disables reconnect** (`:131-132`).
- State enum `SleipnirConnectionState { Disconnected, Connecting, Connected, Reconnecting }`
  (`SleipnirConnectionState.cs:7-20`).
- `IAsyncDisposable.DisposeAsync` (`:604-651`).
- No constructor `bearer` convenience — inject a pre-configured
  `ClientWebSocket`/`socketFactory` (`SleipnirClient/README.md:134-136`).

### `SleipnirSseClient` — `SleipnirSseClient.cs:33`

`public sealed class ... : SleipnirClientBase, ISleipnirClient, IDisposable` —
**events only**.

- `Call`/`Call(multi)` throw `NotSupportedException`
  (`"SleipnirSseClient is an events-only transport..."`, `:98-105`).
- `SubscribeAsync<T>(SleipnirRequest?, ResumePolicy?, CancellationToken)`
  (`:113-125`), `ResumeAsync<T>(string subscriptionId, long lastEventId, ResumePolicy?, CancellationToken)`
  (`:133-148`), `SetBearer` (`:95`).
- Backoff `DefaultReconnectDelays = {0,1,2,5,10,15,30s}` (`:36-45`);
  `HttpClient.Timeout = InfiniteTimeSpan` (`:82`); empty array disables reconnect
  (`:89-90`).
- Resume URL `{apiPath}/events/{subscriptionId}?lastEventId=…` with the
  `Last-Event-Id:` header (`:172-188`); **410 Gone** on a GC'd durable id
  degrades resume → Fresh once, or is terminal on a pure-resume (`:422-449`).
- `IDisposable.Dispose` (`:196-202`).

> **Doc-bug note:** `CLAUDE.md:86` and `README_DETAILS.md:816` document this
> client as `IAsyncDisposable`. The code is `IDisposable` (synchronous `Dispose`
> disposing the `HttpClient`). This reference follows the code. Fix those two
> docs when convenient.

### `SleipnirSignalrClient` — `SleipnirSignalrClient.cs:17`

`public class ... : SleipnirClientBase, ISleipnirClient, IAsyncDisposable` —
calls + events.

- Calls via hub invoke `"DoWork"` (`:105-135`) / `"DoWorkMany"` (`:140-170`).
- Subscribe `SubscribeAsync<T>(SleipnirRequest?, ResumePolicy?, CancellationToken)`
  (`:262-275`) and `ResumeAsync<T>(string, long, ResumePolicy?, CancellationToken)`
  (`:283-306`) — both via hub `StreamAsync<string>("SubscribeAsync", args, ct)`
  (`:444`). First stream item is the `ack`, then event/complete/error frames
  (`HandleFrame`, `:453-520`).
- Thread-safety: `_connectLock = new SemaphoreSlim(1,1)` (`:22`); `_activeSubs`
  `ConcurrentDictionary` (`:33`).
- Auto-reconnect is SignalR's own `WithAutomaticReconnect` with the same backoff
  schedule (`:79-85`); `Reconnecting`/`Reconnected` handlers **re-stream active
  subs in resume mode** (`:98-99, 308-324`); a stream-end during reconnect is
  left for `Reconnected` (`:388-405`).
- `IAsyncDisposable.DisposeAsync` (`:533-553`).
- MessagePack: **client defaults `useMessagePack: true`** (`:47, 69-77`) with a
  custom `JsonElementResolver` to avoid double-wrapping (`:74-76`). Note the
  server `SleipnirOptions.UseMessagePack` default is `false` — see the
  mismatch note in §10.

### `SleipnirSubscription<T>` — `SleipnirSubscription.cs:18`

`sealed class ... : IObservable<T>, IDisposable`. Wraps a `SleipnirSubject<T>`
(`:55-111`); `Dispose` sends unsubscribe best-effort with a 5 s timeout
(`:41-48`).

---

## 6. Events & subscriptions across transports

### The subscribe/resume surface

- **`SubscribeAsync<T>`** — on `ISleipnirClient` (`ISleipnirClient.cs:35`); base
  default throws `NotSupportedException` (`SleipnirClientBase.cs:99-102`);
  overridden by WS (`SleipnirWebSocketClient.cs:713`), SSE
  (`SleipnirSseClient.cs:113`), SignalR (`SleipnirSignalrClient.cs:262`), and the
  router (`SleipnirTransportRouter.cs:273`).
- **`ResumeAsync<T>(subscriptionId, lastEventId, ResumePolicy?, CancellationToken)`**
  — `ISleipnirClient.cs:45`; base default throws (`SleipnirClientBase.cs:109-112`);
  overridden by SSE (`SleipnirSseClient.cs:133`), SignalR
  (`SleipnirSignalrClient.cs:283`), and the router (`SleipnirTransportRouter.cs:291`).
  **WS does not override it** → resuming into WS throws (router guards,
  `SleipnirTransportRouter.cs:296-299`).

### `ResumePolicy` & `ResumeDecision` — `ResumeDecision.cs`

```csharp
public enum ResumeDecision { Fresh, Resume, Drop }          // :21, :24, :27
public record struct SubscriptionResumeContext(             // :35-39
    string Controller, string Method, string SubscriptionId, long? LastEventId);
public delegate ResumeDecision? ResumePolicy(SubscriptionResumeContext);  // :48
```

Consulted on WS reconnect (`SleipnirWebSocketClient.cs:854-859`) and SSE reconnect
(`SleipnirSseClient.cs:498-504`). Returning `null` means "framework default".
SignalR re-streams in resume mode **without consulting the policy**
(`SleipnirSignalrClient.cs:314-324`).

| Decision | Meaning |
|----------|---------|
| `Fresh` | Re-subscribe from scratch (new id, no replay) |
| `Resume` | Resume by id + lastEventId (server replays the durable buffer) |
| `Drop` | Abandon the subscription |
| `null` | Framework default for that transport |

### Last-Event-Id resume

- **SSE**: resume URL `{apiPath}/events/{subscriptionId}?lastEventId=…` with the
  `Last-Event-Id:` header on the GET (`SleipnirSseClient.cs:172-188`); server
  reads the header then the query fallback
  (`SleipnirSseEndpointExtensions.cs:95-96`).
- **WS**: resume sends `subscriptionId` + `lastEventId` fields inside the
  `subscribe` frame (`SleipnirWebSocketClient.cs:879-888`; server extraction
  `SleipnirWebSocketMiddleware.cs:233-243`).
- **SignalR**: `StreamAsync("SubscribeAsync", req, resumeId?, lastEventId?, ct)`
  (`SleipnirSignalrClient.cs:444`).

### Cross-transport resume

Durable subscriptions live in the **process-wide** `SleipnirSubscriptionStore`
(`SleipnirCore/Services/SleipnirSubscriptionStore.cs:43`), shared by all three
event transports (`Lookup`/`Attach`/`Detach`/`Destroy`/`BeginCreate`/`OnAttached`,
`:81, 95, 102, 110, 123, 264`). A subscription created over WS is resumable over
SSE/SignalR and vice-versa (`SleipnirSseClient.cs:28-31`,
`SleipnirSignalrClient.cs:280-282`, `SleipnirHub.cs:18-19`).

The first frame yielded/streamed is always `EventFrame.Ack(subscriptionId, replayedFrom)`
(`SleipnirCore/Events/EventFrame.cs:38-39`); subsequent frames are the same
serialized strings every transport emits.

### Intentionally deferred: WS-direction resume

Resuming **into** WebSocket (`router.ResumeAsync` with the active event backend =
WS) throws `NotSupportedException` with guidance to switch to `rest`/`auto`
(`SleipnirTransportRouter.cs:296-299`, `PROTOCOL.md:1233-1234`). Reason: a WS
resume frame needs the original controller/method/params (not carried by a
`SleipnirSubscription` handle). WS **reconnect** re-subscribe is supported
(`ResubscribeAllAsync`, `SleipnirWebSocketClient.cs:837-953`); resume-by-id into a
fresh WS connection is not. The blessed cross-transport bridge is the
**SSE direction**.

> **Server restart:** the durable store is **in-process** — subscriptions do not
> survive a server restart (`README_DETAILS.md:734`).

---

## 7. Server registration & `SleipnirOptions`

### `AddSleipnir` (services) — `SleipnirHub/Extensions/SleipnirServiceCollectionExtension.cs:36-256`

Two overloads: `Action<SleipnirOptions>` (`:36`) and `SleipnirOptions` (`:44`).

1. Registers `SleipnirOptions` as singleton (`:49`).
2. Eagerly creates + registers `SleipnirConnectionRegistry` (`:59-62`).
3. Registers `SleipnirSubscriptionStore` singleton (durable store) (`:69-75`).
4. If `UseSignalR`: `AddSignalR(...)` with hub-option overrides (`:85-95`); if
   `UseMessagePack`: `AddMessagePackProtocol` with `JsonElementResolver.Instance`
   (`:97-105`).
5. Configures Minimal-API JSON options host-wide (camelCase, relaxed encoder,
   `SleipnirResponseJsonConverter`) (`:114-119`).
6. Registers the rate limiter with the `"sleipnir"` fixed-window policy when
   `RateLimitPermitLimit > 0` (`:122-135`).
7. Registers `ISleipnirCore` (`SleipnirInvoker`) singleton with all propagated
   options (`:140-190`).
8. Registers built-in interceptors (Auth → Telemetry → Logging) when
   `RegisterBuiltInInterceptors` (`:198-219`); user interceptors appended inner
   (`:225-232`).
9. Auto-discovers `[SleipnirController]` types as scoped when
   `AutoDiscoverControllers` (`:239-253`).

### `UseSleipnir` — `:258-318`

Resolves `ISleipnirCore`, applies a `SleipnirControllerBuilder` if registered,
otherwise auto-registers `[SleipnirController]` types with the invoker
(`:304-315`). Warns when user interceptors are registered (the batch path
bypasses the interceptor pipeline — auth still enforced) (`:272-284`).

### `UseSleipnirTransports` — `SleipnirServer/SleipnirPipelineExtensions.cs:35-53`

Reads `SleipnirOptions`; if `UseWebSocket != false`, calls `UseWebSockets()` +
`UseSleipnirWebSocket(webSocketPath)` (default `/sleipnirws`); always calls
`UseSleipnir()` (controller registration). Emits a startup introspection log
`Sleipnir transports: REST=…, WebSocket=…, SignalR=…, SSE=…` (`:115-129`).

### `MapSleipnir` — `:62-106`

- If `UseRest != false`: `MapSleipnirEndpoints(...)` with
  `enableRateLimiting`/`enableJsonRpcCompat`/`enableObservability`/`signalREnabled`/`useWebSocket`/`useSse`/`sseBufferCapacity`
  threaded from options (`:80-88`) and `MapSleipnirDeveloperUi` (`:88`).
- If `UseSignalR == true`: `MapHub<SleipnirHub>(hubPath)` (default `/sleipnirhub`)
  with `RequireAuthorization` when `RequireAuthentication` and
  `RequireRateLimiting("sleipnir")` when `RateLimitPermitLimit > 0` (`:91-103`).

### MessagePack opt-in

`SleipnirOptions.UseMessagePack` → `AddMessagePackProtocol` with
`JsonElementResolver.Instance` on the server
(`SleipnirServiceCollectionExtension.cs:97-105`); mirrored on the C# SignalR
client (`SleipnirSignalrClient.cs:69-77`).

### CORS

**Not configured by Sleipnir.** No `AddCors`/`UseCors` call in
`AddSleipnir`/`UseSleipnirTransports`/`MapSleipnir`. Hosts wire it themselves
(e.g. `samples/server/Program.cs:31-35, 62`, `guide/server/Program.cs:42-44, 157`).
Guidance: `BEST_PRACTICES.md:89`.

### The canonical 3-call wiring

```csharp
builder.Services.AddSleipnir(options => { /* SleipnirOptions */ });
var app = builder.Build();
app.UseSleipnirTransports();   // UseWebSockets + UseSleipnir + (UseSleipnirWebSocket)
app.MapSleipnir();             // REST endpoints (+ SSE) + SignalR hub + DevUI
app.Run();
```

(`GETTING_STARTED.md:76-85`.)

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
> (`SleipnirTransportRouter.cs:296-299`).

### 8.5 Events-only consumer (SSE escape hatch)

```csharp
using var sse = new SleipnirSseClient(new SleipnirSseClientOptions { BaseUrl = "...", Bearer = token });
var sub = await sse.SubscribeAsync<Quote>(req, ...);
// sse.Call(...) throws NotSupportedException — events only.
```

---

## 9. Configuration reference (all knobs)

### Server — `SleipnirHub/Extensions/SleipnirOptions.cs`

| Option | Type | Default | Line | Role |
|--------|------|---------|------|------|
| `UseRest` | `bool` | `true` | `:81` | Gates REST endpoint group + DevUI |
| `UseWebSocket` | `bool` | `true` | `:106` | Gates `UseWebSockets` + `UseSleipnirWebSocket` |
| `UseSse` | `bool` | `true` | `:95` | Gates the SSE `/events/...` endpoints |
| `UseSignalR` | `bool` | `false` | `:69` | Opt-in SignalR transport + hub mapping |
| `UseMessagePack` | `bool` | `false` | `:67` | SignalR MessagePack protocol (server) |
| `EnableJsonRpcCompat` | `bool` | `false` | `:214` | Registers `POST {prefix}/jsonrpc` |
| `EnableObservability` | `bool` | `false` | `:229` | Registers `GET {prefix}/observability` |
| `RequireAuthentication` | `bool` | `false` | `:142` | North-bound default-deny (WS upgrade, `/discovery`, `/observability`, JSON-RPC `sleipnir.discover`, hub `RequireAuthorization`) |
| `RateLimitPermitLimit` | `int` | `0` (off) | `:123` | Fixed-window `"sleipnir"` permit limit; `>0` enables it on REST + hub |
| `RateLimitWindowSeconds` | `int` | `10` | `:128` | Fixed-window window size |
| `MaximumReceiveMessageSize` | `long?` | `null` (SignalR default) | `:7` | SignalR hub max message size |
| `StreamBufferCapacity` | `int?` | `null` | `:9` | SignalR `HubOptions.StreamBufferCapacity` |
| `MaximumParallelInvocationsPerClient` | `int` | `0` | `:65` | SignalR; only applied when `>0` (else SignalR throws at startup) |
| `MaximumBatchSize` | `int` | `0` (off) | `:154` | Fan-out DoS cap; enforced at REST `/json/multi`, WS multi, JSON-RPC batch |
| `EventBufferCapacity` | `int?` | `null` (fb 100) | `:21` | Per-subscription event send-buffer (WS/SSE/SignalR) |
| `EventBackpressureStrategy` | `EventBackpressureStrategy` | `DropOldest` | `:35` | Overflow strategy |
| `EventReplayBufferCapacity` | `int?` | `null` (fb 1000) | `:45` | Durable replay ring capacity |
| `EventResumeTtl` | `TimeSpan?` | `null` (fb 60s) | `:55` | Idle durable subscription TTL |
| `EventMaxDurableSubscriptions` | `int?` | `null` (fb 10 000) | `:63` | Process-wide durable cap (over-cap → 503) |

Transport-adjacent (not transport itself, but on the same options object):
`AliasBindingMode` (`:204`, default `Weak`), `MaxDependencyPathLength` (`:166`),
`AllowRecursiveDescent` (`:176`), `MaxParameterArrayLength` (`:185`),
`MaxResultElementCount` (`:193`), `AutoDiscoverControllers` (`:118`),
`EnableDetailedErrors` (`:5`). See `DEPENDENCY_BINDING.md` for the alias ones.

### Client — `SleipnirRouterOptions` (`SleipnirTransportRouter.cs:41-61`)

See §3 table. Defaults: `Capability = All`, `DefaultTransport = Auto`,
`ProbeTimeout = 1500ms`, `ApiPath = "api/sleipnir"`, `WsPath = "sleipnirws"`,
`HubPath = "sleipnirhub"`.

### Reconnect schedules (per backend)

| Backend | `DefaultReconnectDelays` | Line | Disable |
|----------|--------------------------|------|---------|
| WS | `{2,2,5,5,10,10,30,30s,1,1,5min}` | `SleipnirWebSocketClient.cs:30-43` | empty array |
| SSE | `{0,1,2,5,10,15,30s}` | `SleipnirSseClient.cs:36-45` | empty array |
| SignalR | same schedule via `WithAutomaticReconnect` | `SleipnirSignalrClient.cs:79-85` | (SignalR-managed) |
| REST | — (stateless) | — | n/a |

### Message-size caps

| Surface | Cap | Source |
|---------|-----|--------|
| REST body | 1 MB | `SleipnirEndpointExtensions.cs:41` |
| WS message | 1 MB | `SleipnirWebSocketMiddleware.cs:80` |
| SignalR | `MaximumReceiveMessageSize` (default per SignalR) | `SleipnirOptions.cs:7` |
| Batch fan-out | `MaximumBatchSize` (default unlimited) | `SleipnirOptions.cs:154` |

Compression per transport: `BEST_PRACTICES.md:91-135`. Binary per transport:
`BEST_PRACTICES.md:141-161`, `README.md:252-256`.

---

## 10. Diagnostics & troubleshooting catalog

### Errors

**`SleipnirException`** (`SleipnirCommon/Exceptions/SleipnirException.cs:8`) is the
unified exception for all transport + invocation errors; carries `SleipnirError?`
(`:13`). `SleipnirError` carries `Code`, `Message`, `RequestId`, `Category`
(`SleipnirCommon/Models/SleipnirError.cs:12-66`). See `ERROR_CATALOG.md` for the
category-aware handling (499 = client closed on REST, `:35`).

| Symptom | Cause / message | Where |
|---------|-----------------|-------|
| `Sleipnir transport 'X' is not available: the client was generated with --transport <cap>...` | `UseTransportAsync`/profile resolve hit a backend not bundled for the capability (`NotBundled`) | `SleipnirTransportRouter.cs:155-156` |
| `Sleipnir transport 'X' is not a valid profile.` | Unknown `SleipnirTransport` value | `:151` |
| `SleipnirTransportRouter: BaseUrl is required.` | Blank `BaseUrl` | `:93` |
| `NotSupportedException` on `ResumeAsync` into WS | WS-direction resume deferred — switch to `rest`/`auto` | `:296-299` |
| `NotSupportedException` "events-only transport..." on SSE `Call` | `SleipnirSseClient` carries events only | `SleipnirSseClient.cs:99, 104` |
| `"Not connected to server."` (SignalR) | Hub not connected before a call/subscribe | `SleipnirSignalrClient.cs:111, 146, 227, 269, 290` |
| `"WebSocket reconnect exhausted — connection stays offline."` | Reconnect backoff exhausted | `SleipnirWebSocketClient.cs:589-590` |
| `"SSE resume target gone (410): subscription expired."` | Durable id GC'd; pure-resume is terminal, mixed degrades once | `SleipnirSseClient.cs:444` |
| `"Malformed SSE ack block."` | First SSE block was not a valid `ack` | `SleipnirSseClient.cs:369` |
| `503` on subscribe | Durable subscription cap exceeded (`EventMaxDurableSubscriptions`) | `SleipnirOptions.cs:63` |

### Known limitations & gotchas

- **MessagePack default mismatch:** `SleipnirOptions.UseMessagePack` defaults
  `false` (server), but the C# `SleipnirSignalrClient` defaults `useMessagePack:
  true` (`SleipnirSignalrClient.cs:47`). If the server does not enable
  MessagePack, the SignalR client must be constructed with `useMessagePack: false`
  or the handshake will fail.
- **`MaximumParallelInvocationsPerClient`** must be `>0` if set, otherwise
  SignalR throws at startup (`SleipnirServiceCollectionExtension.cs:82-94`).
- **WS has no native binary frames** — JSON text only
  (`SleipnirWebSocket/README.md:184`). Use REST/SignalR for `byte[]`.
- **SSE named-params only** — positional/binary are WS/SignalR-only
  (`SleipnirTransportRouter.cs:267-272`).
- **Durable store is in-process** — no resume across a server restart
  (`README_DETAILS.md:734`).
- **Batch paths bypass user interceptors** — REST `/json/multi`, WS multi,
  JSON-RPC batch skip the interceptor pipeline (auth still enforced)
  (`SleipnirServiceCollectionExtension.cs:267-284`).
- **`WebSocketAllowedOrigins` (CSWSH protection) is planned, not implemented**
  (`docs/audits/2026-08-08-consolidation-roadmap.md:256`).
- **Python stays REST-only** (no async WS/SSE runtime in the codegen client)
  (`clients/codegen/README.md`, `sleipnir-unified-transport` memory).
- **Deprecated capability aliases** `sse` (→ `rest`) and `both` (→ `all`) kept
  for one minor version; scheduled for removal next major
  (`clients/codegen/src/emitters/ts.ts:45-48`).

### Doc-bugs to fix when convenient

- `CLAUDE.md:86` and `README_DETAILS.md:816` call `SleipnirSseClient`
  `IAsyncDisposable`; the code is `IDisposable` (`SleipnirSseClient.cs:33, 196`).

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
| `README.md` | Multi-transport thesis, package matrix, server quick start, transport table (`:225-229`), binary-per-transport (`:252-256`) |
| `README_DETAILS.md` | Transports (`:197-204`), Server-Push Events (`:484-680`), Resume Last-Event-Id (`:681-735`), REST Events SSE (`:761-808`), Known Limitations v1 (`:810-825`) |
| `PROTOCOL.md` | Wire spec: Transports (`:525-595`), Server-Push Events (`:597-855`), REST Events SSE (`:873-923`), client transport selection + capability→backend table (`:1198-1234`) |
| `JSONRPC_COMPAT.md` | JSON-RPC 2.0 adapter: enable, wire shape, endpoint, error-code map, limitations |
| `BEST_PRACTICES.md` | Host/proxy + message-size caps (`:72-87`), compression per transport (`:91-135`), binary per transport (`:141-161`), REST interplay (`:273-461`) |
| `STABILITY.md` | Stable vs experimental surface: WS/hub paths (`:45`), transport toggles (`:75-83`), client runtime stability (`:122-128`), Phase R/S status (`:203-211`) |
| `GETTING_STARTED.md` | Canonical 3-call wiring (`:76-85`) |
| `CODEGEN_REFERENCE.md` | `--transport` capability semantics, generated client wraps `SleipnirTransportRouter` |
| `ERROR_CATALOG.md` | Transport-uniform `SleipnirError.Category`, 499 = client closed |
| `CLAUDE.md` | Transport table (`:70-76`), JSON-RPC adapter summary (`:78`), client library (`:80-89`) |
| `SleipnirClient/README.md` | Full client reference: backends, router, escape hatches, events, resume limitations, binary, errors |
| `SleipnirRest/README.md` / `SleipnirWebSocket/README.md` / `SleipnirHub/README.md` | Per-transport package details |
| `docs/stories/03-the-same-contract-three-wires.md` | "Same contract, three wires" narrative |
| `docs/stories/05-realtime-push-events.md` | `[SleipnirEvent]` push story, wire frame type |

For the **TypeScript/JavaScript** client router specifically, see
`clients/ts/README.md` (router, escape hatches, events) and
`clients/codegen/README.md` (the `--transport` CLI flag).