# Sleipnir Events — User Reference

A consolidated lookup reference for **everything server-push events** in Sleipnir:
the `[SleipnirEvent]` attribute and its `IObservable<T>` contract, the four wire
frame types (`event`/`complete`/`error`/`ack`) that are byte-identical across all
three transports, the per-subscription send buffer and its backpressure
strategies, the durable subscription store and `Last-Event-Id` resume (Phase R),
the SSE-over-REST channel (Phase S), the WebSocket and SignalR event paths, the
client subscribe/resume entry points, and every event-related option on
`SleipnirOptions`.

This is a **reference**, not a tutorial. When an event stream does not behave,
look here first — the attribute contract, the frame lifecycle, the config
table, the per-transport server paths with `path:line` citations, a
diagnostics/troubleshooting catalog, and a map of where the deeper docs live.
For onboarding read `GETTING_STARTED.md`; for the wire-level spec read
`PROTOCOL.md` §"Server-Push Events"; for the client-side subscribe/resume
mechanics read `TRANSPORT_REFERENCE.md` §6. This doc consolidates those and
links back for depth.

All citations are `repo-relative/path.cs:line` against the repo root. Code-facing
text is English per `CLAUDE.md`.

## Table of contents

1. [The event model](#1-the-event-model)
2. [Authoring events — `[SleipnirEvent]`](#2-authoring-events--sleipnirevent)
3. [Wire frames — the `EventFrame` lifecycle](#3-wire-frames--the-eventframe-lifecycle)
4. [Backpressure — `EventBuffer` & strategies](#4-backpressure--eventbuffer--strategies)
5. [Durable subscriptions & resume (Phase R)](#5-durable-subscriptions--resume-phase-r)
6. [SSE-over-REST server contract (Phase S)](#6-sse-over-rest-server-contract-phase-s)
7. [WebSocket event frames](#7-websocket-event-frames)
8. [SignalR hub-streaming](#8-signalr-hub-streaming)
9. [Client subscribe & resume (cross-transport)](#9-client-subscribe--resume-cross-transport)
10. [Configuration reference (all event knobs)](#10-configuration-reference-all-event-knobs)
11. [Diagnostics & troubleshooting catalog](#11-diagnostics--troubleshooting-catalog)
12. [Phase history & limitations](#12-phase-history--limitations)
13. [How it is verified (the gates)](#13-how-it-is-verified-the-gates)
14. [Relationship to other docs](#14-relationship-to-other-docs)

---

## 1. The event model

Sleipnir distinguishes three remote surfaces on a controller method, decided by
return type and attribute:

| Surface | Attribute | Return type | Wire `kind` | Direction |
|---------|-----------|-------------|-------------|-----------|
| **Call** | `[SleipnirMethod]` | `T` / `Task<T>` / `void` / `Task` | `call` | request → response |
| **Stream** | `[SleipnirMethod]` | `IAsyncEnumerable<T>` | `stream` | request → response (array) |
| **Event** | `[SleipnirEvent]` | `IObservable<T>` | `event` | subscribe → push frames |

The load-bearing rule: **an event method must return `IObservable<T>` directly**
— not `Task<IObservable<T>>`, not `IAsyncEnumerable<T>`. This is enforced
fail-loud at registration (`SleipnirCore/Services/SleipnirInvoker.cs:226-229`):

> A method marked `[SleipnirEvent]` that does not return `IObservable<T>`
> throws `InvalidOperationException` at startup.

The inverse is also rejected: a `[SleipnirMethod]` returning `IObservable<T>`
throws (`SleipnirInvoker.cs:230-233`), and the two attributes are mutually
exclusive on one method (`SleipnirInvoker.cs:209-213`). `[SleipnirEvent]` and
`[SleipnirMethod]` share the `{Controller}_{name}` dispatch namespace and must
not collide — there is no parameter-based overload resolution
(`SleipnirInvoker.cs:235-248`).

`IAsyncEnumerable<T>` is **not** an event return type — it is the streaming-call
surface (`kind:"stream"` in Discovery, consumed into a `List<T>` JSON array).
Events are strictly `IObservable<T>` and are **not chainable**: `@alias` /
`exposes` apply to call results, not event streams (`SleipnirCore/Attributes/SleipnirEventAttribute.cs:24-28`,
`ROADMAP.md:146`).

The subscribe result the server produces is a `SleipnirSubscribeResult`
carrying `IObservable<object?>? Observable`
(`SleipnirCommon/Models/SleipnirSubscribeResult.cs:29`); the invoker converts the
method's `IObservable<T>` to `IObservable<object?>` via `TryAsObservableObject`
— covariance cast for reference types, a boxing adapter for value types
(`SleipnirInvoker.cs:430-442`).

Discovery tags event methods with `kind:"event"`
(`PROTOCOL.md:857-871`, `README_DETAILS.md:736-744`).

---

## 2. Authoring events — `[SleipnirEvent]`

**Attribute:** `SleipnirCore/Attributes/SleipnirEventAttribute.cs:50` —
`sealed class SleipnirEventAttribute : Attribute`,
`[AttributeUsage(AttributeTargets.Method)]`.

| Member | Type | Default | Line | Meaning |
|--------|------|---------|------|---------|
| `Name` (ctor arg) | `string` | required | `:54, :60` | The wire name. |
| `BufferCapacity` | `int` | `-1` | `:73` | Per-event override of the per-subscription buffer cap; `-1` inherits `SleipnirOptions.EventBufferCapacity` (fallback 100); `0` only meaningful with `Unbounded`. |
| `BackpressureStrategy` | `EventBackpressureStrategy` | `Inherit` | `:84-85` | Per-event override of the overflow strategy; `Inherit` uses the global option. |
| `Resumable` | `bool` | `false` | `:101` | Opt in to `Last-Event-Id` resume + server-side disconnect buffer (Phase R). |

The doc comment on the attribute states the contract and gives the canonical
signature (`SleipnirEventAttribute.cs:19-21`):

```csharp
[SleipnirEvent("MessageReceived")]
public IObservable<Message> Subscribe(int chatId, CancellationToken ct) => ...;
```

`CancellationToken` is injected automatically like a call method's; method
arguments bind by name like calls.

### Example — the guide's runnable event

`guide/server/Controllers/PriceFeedController.cs:36`:

```csharp
[SleipnirEvent("Ticks", Resumable = true)]
public IObservable<PriceTick> Ticks(string symbol) => _feed.GetStream(symbol);
```

The guide chapter `guide/chapters/09-events.md` builds the first live event
(BTC feed, `Resumable = true`, Svelte live chart + Blazor monitor + resume).

> **Gotcha — resumable events need a long-lived hot source.** A `Resumable`
> event **must return a long-lived hot/durable observable** (e.g. a `Subject<T>`
> or a factory backed by a long-running producer). A cold observable that
> restarts per subscribe has no resume semantics
> (`SleipnirEventAttribute.cs:39-47`, `guide/server/Controllers/PriceFeedController.cs:16-19`,
> `README_DETAILS.md:676-678`).

### Registration errors (fail-loud at startup)

- Both `[SleipnirMethod]` and `[SleipnirEvent]` on one method →
  `SleipnirInvoker.cs:209-213`.
- `[SleipnirEvent]` not returning `IObservable<T>` → `SleipnirInvoker.cs:226-229`.
- `[SleipnirMethod]` returning `IObservable<T>` → `SleipnirInvoker.cs:230-233`.
- Event + call sharing `{Controller}_{name}` → `SleipnirInvoker.cs:240-247`.

---

## 3. Wire frames — the `EventFrame` lifecycle

**Definition:** `SleipnirCore/Events/EventFrame.cs:17` — `internal static class EventFrame`.
Four variants, each producing **one JSON string** (serialized once, reused across
all transports):

| Frame | Factory | Shape | Line |
|-------|---------|-------|------|
| **event** | `Event(subscriptionId, eventId, data)` | `{ type:"event", subscriptionId, eventId, data }` | `:19-20` |
| **complete** | `Complete(subscriptionId)` | `{ type:"complete", subscriptionId }` | `:22-23` |
| **error** | `Error(subscriptionId, message)` | `{ type:"error", subscriptionId, message }` | `:25-26` |
| **ack** | `Ack(subscriptionId, replayedFrom?)` | `{ type:"ack", subscriptionId, replayedFrom? }` | `:38-39` |

The frame discriminator is `type` with values
`"event" | "complete" | "error" | "ack"` (`EventFrame.cs:8-11`). Frames are
`subscriptionId`-keyed, **not** `id`-keyed (unlike calls) — `subscriptionId` is
the per-subscription correlation handle
(`PROTOCOL.md:717-744`). The ack is the first item of a SignalR hub stream and
is delivered out-of-band on WS/SSE before any live frame.

**`replayedFrom`** is set on resume and omitted on a fresh subscribe via
`WhenWritingNull` (`EventFrame.cs:33-35`, `EventJsonOptions.cs:27`). It is the
first replayed `eventId` (null on fresh subscribe or when nothing buffered)
(`SleipnirSubscriptionStore.cs:271, :282, :336-337`).

**Shared serialization:** all frames serialize once with
`EventJsonOptions.Default` (`SleipnirCore/Events/EventJsonOptions.cs:22-28`:
camelCase + `UnsafeRelaxedJsonEscaping` + `WhenWritingNull`), and the
**same string** is sent as one WS text frame, written as an SSE block, or
yielded as a SignalR stream item (`EventFrame.cs:12-15`). This is why the TS
SignalR client parses stream items with the WS frame parser
(`SleipnirHub/Hub/SleipnirHub.cs:22-28`).

**Frame writers (the observers that call `EventFrame`):**

- *Ephemeral* — `SleipnirCore/Events/EventObserver.cs:29-51`:
  `OnNext` → `EventFrame.Event` + `Buffer.TryEnqueue` (`:31-37`);
  `OnCompleted` → `Buffer.EnqueueTerminal(EventFrame.Complete(...))` (`:47`);
  `OnError` → `Buffer.EnqueueTerminal(EventFrame.Error(...))` (`:50-51`).
- *Durable* — `EventObserver.cs:63-91`: `OnNext` →
  `state.AppendEvent(eventId, EventFrame.Event(...))` (`:74-82`);
  `OnCompleted` → `state.SetTerminal(EventFrame.Complete(...))` (`:84-85`);
  `OnError` → `state.SetTerminal(EventFrame.Error(...))` (`:87-91`).

Terminal frames (`complete`/`error`) bypass the backpressure cap via
`EnqueueTerminal` so they reach the client regardless of overflow
(`EventBuffer.cs:107-117`).

Wire-spec narrative: `PROTOCOL.md:717-744` (event/complete/error frames),
`:682-689` (subscribe-ack with `replayedFrom`).

---

## 4. Backpressure — `EventBuffer` & strategies

Each active subscription has a per-subscription **send buffer** that absorbs
the gap between a fast producer and a slow wire.

**Buffer:** `SleipnirCore/Events/EventBuffer.cs:16` — `internal sealed class EventBuffer`,
holding serialized frame strings (`:12-13`). Constructor
`EventBuffer(int capacity, EventBackpressureStrategy strategy, CancellationToken disposeToken)`
(`:28-36`); `_unbounded` when `strategy == Unbounded || capacity <= 0` (`:32`).

**`EventBackpressureStrategy` enum:** `SleipnirCommon/Models/EventBackpressureStrategy.cs:10-60`.

| Value | Line | Behavior |
|-------|------|----------|
| `Inherit` | `:20` | Sentinel; at the global-option level treated as `DropOldest`. |
| `DropOldest` | `:31` | **Default.** Evict oldest, enqueue newest, increment `sleipnir.event.dropped`. |
| `DropWrite` | `:40` | Drop newest, increment counter. |
| `Block` | `:51` | Block the producer until a slot frees; never drops. |
| `Unbounded` | `:60` | No cap, no DoS backstop. |

**Enforcement:** `EventBuffer.TryEnqueue(string frame, Action onDropped)`
(`EventBuffer.cs:48-101`): unbounded path (`:50-59`); Block awaits a `_space`
semaphore (`:61-78`); DropOldest dequeues oldest + `onDropped()` (`:85-92`);
DropWrite calls `onDropped()` and returns false (`:93-95`). Terminal frames
bypass the cap (`EnqueueTerminal`, `:107-117`).

**Where the buffer is created:**

- Ephemeral WS: `SleipnirWebSocket/SleipnirSubscriptionManager.cs:241` (the
  `EventBuffer` lives inside `EphemeralSubscriptionState`,
  `SleipnirCore/Events/EphemeralSubscriptionState.cs:23`).
- Ephemeral SSE: `SleipnirRest/Sse/SleipnirSseConnection.cs:130-131`.
- Durable send buffer (SSE/Hub): always bounded `DropOldest` between the
  unbounded tap and the slow wire — `SleipnirSseConnection.cs:124`,
  `SleipnirHub.cs:144, :176`.
- Durable replay-ring eviction uses the per-event strategy
  (`SleipnirSubscriptionStore.cs:225-229`).

**Per-event override resolution:** `SleipnirInvoker.cs:449-460` — attribute
sentinel `Inherit/-1` → global option; global `Inherit` → `DropOldest`;
`Unbounded` ignores capacity; capacity fallback 100. The resolved values ride
on `SleipnirSubscribeResult.EventBufferCapacity` / `EventBackpressureStrategy`
(`SleipnirSubscribeResult.cs:36-42`).

**Drop counting:** `EventObserver.OnDropped` (`EventObserver.cs:40-45`) calls
`SleipnirMetrics.EventDropped` + logs. The custom `EventBuffer` replaced a
`BoundedChannel(DropOldest)` whose `TryWrite` always returned true (hiding
drops) — `EventBuffer.cs:7-11`, `CHANGELOG.md:247-252`.

---

## 5. Durable subscriptions & resume (Phase R)

Opt in with `[SleipnirEvent(Resumable = true)]`. The `IObservable<T>` source is
kept subscribed across disconnects, a per-subscription **replay ring**
accumulates gap events, and on reconnect the client sends `lastEventId` for
at-least-once-within-the-replay-window replay (the client dedups by `eventId`).
Non-resumable events keep the v1 ephemeral at-most-once behavior.

### Store — `SleipnirSubscriptionStore`

`SleipnirCore/Services/SleipnirSubscriptionStore.cs:43` —
`public sealed class SleipnirSubscriptionStore : IAsyncDisposable`. Process-wide,
registered as a DI singleton (`:34-35`, `STABILITY.md:214`). Backed by
`ConcurrentDictionary<string, DurableSubscriptionState> _durable` (`:45`). The
store is shared across transports, so a durable subscription created over WS
resumes over SSE and vice-versa (`SleipnirSseConnection.cs:18-21, :92-93`).

**Per-subscription state** — `DurableSubscriptionState` (`:177`,
`IDisposable`): the source subscription (`:180`), a stable server-generated
`SubscriptionId` (`:179`), a monotonic `_eventIdCounter` (`:193`,
`NextEventId()` `:210`), a bounded replay ring
`Queue<(long EventId, string Frame)> _ring` (`:194`) with cap `_ringCap`
(`:195`), a `_liveTap` channel (`:198`), a `_terminalFrame` (`:199`), and the
`Controller`/`Method` recorded at create for reconnect auth re-check
(`:186-187`).

**Store API:**

| Method | Line | Purpose |
|--------|------|---------|
| `BeginCreate(strategy)` | `:81-92` | Create a `DurableSubscriptionState` with a server-generated `Guid N` id; returns `null` at the process-wide cap → caller returns 503. |
| `Lookup(subscriptionId)` | `:95-96` | For resume. |
| `OnAttached()` | `:102` | Bump the live-subscription gauge (symmetric with `Detach`). |
| `Detach(subscriptionId)` | `:110-116` | Complete the live tap, drop the tap ref, decrement the gauge; **source + ring persist** for resume. No-op on unknown. |
| `Destroy(subscriptionId)` | `:123-130` | Explicit unsubscribe: dispose source, discard ring, remove state, decrement gauge if a tap was attached. |
| `SweepGc()` | `:142-160` | Timer-driven (`:68-69`); evicts completed sources and detached subscriptions past the idle TTL. |
| `DisposeAsync()` | `:162-168` | Shutdown. |

### Replay ring

`DurableSubscriptionState.AppendEvent(long eventId, string frame)`
(`SleipnirSubscriptionStore.cs:218-233`): enqueue into `_ring`; on overflow
(`_ringCap > 0 && _ring.Count > _ringCap`) evict oldest and calls
`_onDrop(SubscriptionId)` (`:225-229`); forwards to the attached live tap if
any (`:230-232`).

### Replay-on-resume

`DurableSubscriptionState.Attach(long lastEventId)` (`:264-296`): snapshots
ring entries with `eid > lastEventId` into a fresh unbounded channel under the
lock (`:273-285`), sets `_liveTap` (`:286`), returns a
`Tap(SubscriptionId, Reader, ReplayedFrom)` (`:295`). `ReplayedFrom` is the
first replayed eventId (null on fresh subscribe or when nothing buffered)
(`:271, :282, :336-337`), surfaced as `replayedFrom` in the ack
(`EventFrame.cs:34-35`).

**`Tap`:** `SleipnirSubscriptionStore.cs:332-343` —
`{ SubscriptionId, ChannelReader<string> Reader, long? ReplayedFrom }`.

### Options plumbed into the store (constructor `:53-70`)

- `EventReplayBufferCapacity` → `_replayBufferCapacity` fallback 1000 (`:61`).
  Per-subscription ring cap.
- `EventResumeTtl` → `_resumeTtl` fallback 60s (`:62`); `0` disables auto-reclaim
  (`:67-69`). Idle-TTL for GC.
- `EventMaxDurableSubscriptions` → `_maxDurable` fallback 10 000 (`:63`); `0` =
  unbounded. Over-cap → `BeginCreate` returns null → 503.

### Reconnect vs. resume

- **Reconnect** = WS transport reconnection: `subscriptionId` is per-connection;
  on disconnect all subscriptions are disposed; the client re-subscribes with
  fresh ids after reconnect; gap events are lost (at-most-once-while-
  disconnected, v1 default). `SleipnirSubscriptionManager.cs:32-36`,
  `PROTOCOL.md:771-774`, `README_DETAILS.md:625-627`.
- **Resume** = `Last-Event-Id` resume (Phase R, opt-in via `Resumable = true`):
  the durable `subscriptionId` is stable across reconnects; the client sends
  `lastEventId` + `subscriptionId`; the server replays the gap from the ring
  (at-least-once within the window; client dedups by `eventId`).
  `SleipnirSubscriptionStore.cs:18-29`, `PROTOCOL.md:812-839`,
  `README_DETAILS.md:681-734`.
- The client `ResumeDecision` enum codifies the choice: `Fresh` (reconnect
  behavior), `Resume` (send id + lastEventId), `Drop` (end the subscription)
  (`SleipnirClient/Sleipnir/ResumeDecision.cs:18-28`). A `Resume` on a
  non-resumable event degrades to `Fresh` (the server does not know the id →
  fresh subscribe) (`ResumeDecision.cs:13-16`, `CHANGELOG.md:218-220`).

### Reconnect-time auth re-check

On resume, authorization is re-checked against the **original route** (the
controller/method recorded at create). A revoked role or a vanished route tears
the durable subscription down. WS: `SleipnirSubscriptionManager.cs:122-128`
(calls `AuthorizeSubscribeAsync`, on error `store.Destroy` + returns the error).
SSE: `SleipnirSseConnection.cs:157-162`. Hub: `SleipnirHub.cs:128-134`. Invoker:
`SleipnirInvoker.cs:485-506` (`BadRequest(..., NotFound)` for unknown
controller/method, `Unauthorized()`/`Forbidden()` on auth failure). Doc:
`PROTOCOL.md:841-845`, `CHANGELOG.md:224-227`.

---

## 6. SSE-over-REST server contract (Phase S)

SSE delivers the same `[SleipnirEvent]` methods over `text/event-stream` for
clients behind proxies/firewalls that block WS upgrades. It reuses the exact
Phase R resume machinery and the process-wide store → cross-transport resume.

### Connection handler

`SleipnirRest/Sse/SleipnirSseConnection.cs:45` — `internal sealed class SleipnirSseConnection`.

### Endpoint mapping — `SleipnirRest/SleipnirSseEndpointExtensions.cs:35` (`MapSleipnirSseEndpoints`, `:45-113`)

| Route | Method | Params | Line |
|-------|--------|--------|------|
| `/events/{controller}/{method}` | GET | method args as query params | `:48-76` |
| `/events/{subscriptionId}` | GET | `Last-Event-Id:` header (or `?lastEventId=` fallback) | `:79-110` |

Both routes apply a transport auth gate (401 when `RequireAuthentication` and
unauthenticated) (`:58-62`, `:87-91`). Header takes precedence over the query
fallback (`:94-96`).

### SSE block construction

`SleipnirSseConnection.WriteFrameAsync` (`:256-281`): extracts `eventId`
(→ `id:` line) and `type` (→ `event:` line) from the pre-serialized frame JSON,
then writes `data: {frame}` per line + blank line. `WriteAckAsync`
(`:242-253`): `id: 0\n event: ack\n data: {subscriptionId[, replayedFrom]}\n\n`.
Wire-mapping table: `PROTOCOL.md:904-911`.

### Ack-first rule

`StreamAsync` (`:182-212`) calls `WriteAckAsync` first (`:186`), then drains the
send buffer — the ack is written before any live frame (same invariant as the
WS race fix; SSE-specific rationale `:241`). `PROTOCOL.md:913-915`.

### Resume & 410 Gone

`PrepareResumeAsync` (`SleipnirSseConnection.cs:151-174`): `store.Lookup` →
null returns `Results.StatusCode(410)` (`:154-155`); auth re-check via
`AuthorizeSubscribeAsync` (`:157-162`); `state.Attach(lastEventId ?? 0)`
(`:164`); ack carries `ReplayedFrom` (`:167`). A 410 means the durable state
was GC'd/TTL-expired; the client falls back to a fresh subscribe
(`PROTOCOL.md:898`, `README_DETAILS.md:773-774`, `CHANGELOG.md:83-84`).

### Response headers

`WriteSseStreamAsync` (`SleipnirSseEndpointExtensions.cs:121-128`):
`text/event-stream`, `Cache-Control: no-cache`, `X-Accel-Buffering: no` (so
proxies flush per event), `StartAsync` to flush headers.

### Limitations (SSE-specific)

- **Named params only** — a GET has no body, so method args travel as query
  params; each value parsed as JSON when valid, else a string; a repeated key
  becomes a JSON array. **No type hints**: `?count=3` binds to a number and
  would 400 for a `string` parameter — use the native WebSocket wire for
  complex/typed parameters. `SleipnirSseConnection.cs:291-298` (`BuildParams`
  `:299-321`), `PROTOCOL.md:534`, `BEST_PRACTICES.md:36`, `CHANGELOG.md:49`.
- **Native `EventSource` cannot set `Authorization`** → Bearer-auth hosts need
  a fetch-based client (TS `SleipnirSseClient`); native `EventSource` works for
  cookie-auth hosts. `SleipnirSseConnection.cs:39-43`,
  `SleipnirSseEndpointExtensions.cs:30-33`, `PROTOCOL.md:921-924`.
- **WS-direction resume deferred** — resuming *into* a WS-active router is
  unsupported; the blessed cross-transport bridge is SSE/SignalR
  (`TRANSPORT_REFERENCE.md:461-465`, `:467`, `:690`, `CHANGELOG.md:53-56`).

### Gating

SSE endpoints are added only when `useSse` is true —
`SleipnirRest/SleipnirEndpointExtensions.cs:141-144`
(`if (useSse) group.MapSleipnirSseEndpoints(...)`), threaded from
`SleipnirOptions.UseSse` (`SleipnirServer/SleipnirPipelineExtensions.cs:86, :124`).

---

## 7. WebSocket event frames

**Middleware:** `SleipnirWebSocket/SleipnirWebSocketMiddleware.cs:18`. A
per-connection `SleipnirSubscriptionManager` is created at `:145`.

### Frame `kind` detection (`:212-223`)

The `kind` property is read case-insensitively from the parsed JSON root:

| `kind` | Line | Behavior |
|--------|------|----------|
| `"subscribe"` | `:225-250` | Deserialize `SleipnirRequest`, extract resume fields `lastEventId` (number) + `subscriptionId` (string) **out-of-band** from the raw frame (`:233-241`) so the `SleipnirRequest` wire model stays untouched; call `HandleSubscribeAsync(request, context, ct, lastEventId, resumeSubscriptionId, id)` (`:242-243`); enqueue the ack response if non-null (`:244-248`). |
| `"unsubscribe"` | `:252-268` | Read `subscriptionId` + `id`; 400 if `subscriptionId` missing (`:260`); call `HandleUnsubscribeAsync` (`:261`). |
| *(none)* | `:270-326` | Normal call (v1.0 behavior). |

### Resume fields (subscribe frame)

`lastEventId` (long — the last eventId the client processed) and
`subscriptionId` (the durable id to resume) (`:229-241`). Both absent → fresh
subscribe.

### Subscribe-ack shape (WS)

The ack is a real `SleipnirResponse` with `code = 200`,
`data = { subscriptionId, replayedFrom }` (`replayedFrom` omitted on fresh via
`WhenWritingNull`), `id` = request id / correlation id. Built in
`EnqueueSubscribeAckAsync` (`SleipnirSubscriptionManager.cs:168-179`), serialized
with the middleware JSON options (`SleipnirResponseJsonConverter`: explicit
nulls, fixed field order) so bytes match what the middleware would have
produced (`:160-167`). Wire example: `PROTOCOL.md:682, :689`.

### Events pushed to the client

`SleipnirSubscriptionManager` runs a per-connection send loop (`SendLoopAsync`
`:321-336`) draining a shared `Channel<string> _sendChannel` (`:70`) into
`WebSocket.SendAsync`. The ephemeral pump (`:266-277`) drains the
per-subscription `EventBuffer` into `_sendChannel`; the durable pump
(`StartDurablePump` `:213-227`) drains the `Tap.Reader` into `_sendChannel`.
Each event frame is one WS text frame (`PROTOCOL.md:720`).

### Auth gate on upgrade

`:67-71` — 401 when `RequireAuthentication` and unauthenticated, before the
socket is created.

### Unsubscribe over WS

`kind:"unsubscribe"` with `subscriptionId` (`PROTOCOL.md:753`); 404 if unknown
(`PROTOCOL.md:762`, `SleipnirSubscriptionManager.cs:305-306`).

---

## 8. SignalR hub-streaming

**Hub method:** `SleipnirHub/Hub/SleipnirHub.cs:100-104` —

```csharp
public async IAsyncEnumerable<string> SubscribeAsync(
    SleipnirRequest request, string? resumeSubscriptionId, long? lastEventId,
    [EnumeratorCancellation] CancellationToken ct)
```

Class declaration `:53-58` (`SleipnirHub : Microsoft.AspNetCore.SignalR.Hub`).
Wired only when `SleipnirOptions.UseSignalR = true` (opt-in, default false)
(`:49-51`); calls keep using `DoWork`/`DoWorkMany` (`:63-86`).

### Wire — ack-then-frames

The first yielded item is `EventFrame.Ack(subscriptionId, replayedFrom)`
(`:194`). Each subsequent item is one pre-serialized logical event frame string
(`{type,subscriptionId,eventId,data}` / `complete` / `error`) — the **same
string** WS sends as a text frame and SSE writes as a block, so the TS SignalR
client parses stream items with the WS frame parser (`:22-28`).
`PROTOCOL.md:1228` confirms the ack-then-frames order.

The hub streams **strings** (not objects) to reuse the durable string-frame
buffer and avoid double-serialization — the frames are already serialized in
the transport-agnostic core (`SleipnirHub.cs:22-28`,
`TRANSPORT_REFERENCE.md:129-132`).

### Fresh-vs-resume paths

- **Resume** (`resumeSubscriptionId` non-empty, `:118-145`): `store.Lookup` →
  null throws `HubException` ("…not found (expired or never created).
  Re-subscribe fresh.") (`:122-124`); `AuthorizeSubscribeAsync` re-checks auth
  against the original route, on error `store.Destroy` + throw `HubException`
  (`:128-134`); `state.Attach(lastEventId ?? 0)` (`:136`); bounded `DropOldest`
  send buffer (`:144`).
- **Fresh** (`:146-191`): `service.SubscribeAsync` (`:153`); resumable →
  `store.BeginCreate` + `DurableEventObserver` + `Attach(0)` (`:158-176`);
  ephemeral → `EphemeralSubscriptionState` + `EventObserver` (`:178-190`).

### Backpressure (durable)

A background `PumpDurableAsync` drains the unbounded tap into a bounded
`EventBuffer` (DropOldest) (`:196-211`, `:239-264`). A slow SignalR client
overflows the send buffer (drops oldest live frames — they remain in the replay
ring for resume), never grows the tap (`:32-38`, `:198-201`).

### Cleanup & failure

`finally` (`:213-236`): awaits the pump, disposes ephemeral (decrements gauge),
detaches durable (store owns the gauge). Pre-stream failures (auth/routing/
binding) throw `HubException` → the stream rejects on the client (mapped to
`onError`); a mid-stream source error arrives as an `{type:"error",...}`
terminal frame before the stream ends (`:43-47`).

---

## 9. Client subscribe & resume (cross-transport)

The full client-side treatment — `SubscribeAsync`/`ResumeAsync`, `ResumePolicy`,
`ResumeDecision`, the `lastEventId` cursor, the durable store shared across
transports, and the WS-direction-resume limitation — lives in
`TRANSPORT_REFERENCE.md` §6 "Events & subscriptions across transports". This
section lists the entry points only and does not re-derive them.

| Entry point | Location |
|-------------|----------|
| `ISleipnirClient.SubscribeAsync<T>(SleipnirRequest?, ResumePolicy?, CancellationToken)` | `SleipnirClient/Sleipnir/ISleipnirClient.cs:35` |
| `ISleipnirClient.ResumeAsync<T>(string subscriptionId, long lastEventId, ResumePolicy?, CancellationToken)` | `ISleipnirClient.cs:45` |
| `ResumePolicy` delegate + `ResumeDecision { Fresh, Resume, Drop }` + `SubscriptionResumeContext` record | `SleipnirClient/Sleipnir/ResumeDecision.cs:18-48` |
| `SleipnirSubscription<T> : IObservable<T>, IDisposable` (wraps `SleipnirSubject<T>`, holds `SubscriptionId`, sends unsubscribe on `Dispose`) | `SleipnirClient/Sleipnir/SleipnirSubscription.cs:18-49` |

The client tracks the highest `eventId` seen per subscription and silently
drops replayed frames with `eventId ≤ lastSeen`
(`PROTOCOL.md:830-833`, `README_DETAILS.md:715-717`). The durable store shared
across transports is `SleipnirSubscriptionStore.cs:43`
(`TRANSPORT_REFERENCE.md:448-449`). **WS-direction resume** (resuming *into* a
WS-active router) is unsupported → switch to `rest`/`auto` to resume over SSE,
or use SignalR (`TRANSPORT_REFERENCE.md:461-465`, `:467`, `:690`).

---

## 10. Configuration reference (all event knobs)

All on `SleipnirHub/Extensions/SleipnirOptions.cs`.

| Option | Type | Default (fallback) | Line | Notes |
|--------|------|--------------------|------|-------|
| `EventBufferCapacity` | `int?` | `null` → 100 | `:21` | Per-subscription send-buffer cap; ignored when `Unbounded`; per-event override via `[SleipnirEvent(BufferCapacity = …)]`. |
| `EventBackpressureStrategy` | `EventBackpressureStrategy` | `DropOldest` | `:35` | Overflow strategy; per-event override via `[SleipnirEvent(BackpressureStrategy = …)]`. |
| `EventReplayBufferCapacity` | `int?` | `null` → 1000 | `:45` | Replay-ring cap per durable subscription (evict-oldest; `0` = unbounded). |
| `EventResumeTtl` | `TimeSpan?` | `null` → 60s | `:55` | Idle-TTL for durable subscriptions; `0` = never auto-reclaim (caller accepts unbounded memory for abandoned subscriptions, `SleipnirOptions.cs:51-55`, `SleipnirSubscriptionStore.cs:67-69`). |
| `EventMaxDurableSubscriptions` | `int?` | `null` → 10 000 | `:63` | Process-wide durable cap; `0` = unbounded; over-cap → 503. |
| `UseSse` | `bool` | `true` | `:95` | Gates the SSE `/events/...` endpoints (honored by the unified `MapSleipnir` pipeline; SSE group added inside the REST group). |
| `UseRest` | `bool` | `true` | `:81` | Gates REST endpoints. |
| `UseWebSocket` | `bool` | `true` | `:106` | Gates WS transport. |
| `UseSignalR` | `bool` | `false` (opt-in) | `:69` | Gates the SignalR hub (events via hub-streaming). |

**SSE gating detail:** `UseSse` is read in
`SleipnirRest/SleipnirEndpointExtensions.cs:141-144`
(`if (useSse) group.MapSleipnirSseEndpoints(defaultBufferCapacity: sseBufferCapacity)`),
with `useSse` threaded from `SleipnirServer/SleipnirPipelineExtensions.cs:86`
(`useSse: options?.UseSse != false`) and `:124`. The SSE buffer capacity
fallback is `SleipnirOptions.EventBufferCapacity`
(`SleipnirEndpointExtensions.cs:136-143`). `STABILITY.md:203-204`,
`PROTOCOL.md:877`, `README_DETAILS.md:764`.

---

## 11. Diagnostics & troubleshooting catalog

### Error codes & messages

- **Over-cap durable subscribe (503):**
  - SSE: `SleipnirSseConnection.cs:103-106` —
    `Results.Json(new { code = 503, message = "Durable subscription cap reached — retry later." }, statusCode: 503)`.
  - WS: `SleipnirSubscriptionManager.cs:187-189` —
    `SleipnirResults.Error(SleipnirErrorCodes.ServiceUnavailable, "Durable subscription cap reached — retry later.", SleipnirErrorCategory.ResourceExhausted)`.
  - Hub: `SleipnirHub.cs:160-161` —
    `throw new HubException("Sleipnir durable subscription cap reached — retry later.")`.
  - `SleipnirErrorCodes.ServiceUnavailable` → 503 / category `Unavailable`
    (`ERROR_CATALOG.md:37, :60`).
- **410 Gone (resume on SSE, durable state GC'd/TTL-expired):**
  `SleipnirSseConnection.cs:155` — `Results.StatusCode(410)`. Client falls back to
  fresh subscribe (`PROTOCOL.md:898`, `README_DETAILS.md:773-774`,
  `CHANGELOG.md:83-84`).
- **Resume unknown/GC'd durable id (Hub):** `SleipnirHub.cs:122-124` —
  `throw new HubException("Sleipnir subscription '" + resumeSubscriptionId + "' not found (expired or never created). Re-subscribe fresh.")`.
- **Resume auth failure (role revoked during gap):** re-checked against the
  original route; 401/403 (or 404 if the route vanished) tears down the durable
  subscription. WS `SleipnirSubscriptionManager.cs:122-128`; SSE
  `SleipnirSseConnection.cs:157-162`; Hub `SleipnirHub.cs:128-134`; invoker
  `SleipnirInvoker.cs:485-506`. Doc `PROTOCOL.md:841-845`, `CHANGELOG.md:224-227`.
- **Plain call to an event method → 400:** `CHANGELOG.md:266-268` —
  "… is a server-push event; use `kind:\"subscribe\"`" (was an opaque 500 in
  1.1.0). Subscribe to a non-event method → 400 without executing
  (`CHANGELOG.md:269-271`; guard `SleipnirInvoker.cs:496-499`).
- **Subscribe to non-`IObservable<T>` return:** `SleipnirInvoker.cs:440-442` —
  `BadRequest($"Method '{request.Method}' on controller '{request.Controller}' does not return an IObservable<T> — not a subscribable event.")`.
- **WS unsubscribe missing `subscriptionId`:** `SleipnirWebSocketMiddleware.cs:260`
  — `"unsubscribe requires subscriptionId."` (400).
- **WS unsubscribe unknown id:** `SleipnirSubscriptionManager.cs:305-306` —
  `SleipnirResults.Error(SleipnirErrorCodes.NotFound, $"Subscription '{subscriptionId}' not found.", NotFound)` (404).
- **WS malformed JSON / unparseable request:** `SleipnirWebSocketMiddleware.cs:328-334`
  — 400 `"Invalid JSON in request."` (id null — uncorrelated); `:312-315` —
  400 `"Invalid request."`.
- **WS message too large (>1 MB):** `SleipnirWebSocketMiddleware.cs:80, :171-175`
  — 400 `"Message too large."`.
- **SSE malformed frame:** `SleipnirSseConnection.WriteFrameAsync` (`:262-271`) —
  `JsonDocument.Parse(frame)` wrapped in try/catch; on a malformed frame it
  falls back to `event: event` + `data:` only (no `id:` line). No exception is
  thrown — the block is still emitted.

### Known gotchas

- A `Resumable` event **must return a long-lived hot/durable observable**; a
  cold observable that restarts per subscribe has no resume semantics
  (`SleipnirEventAttribute.cs:39-47`, `README_DETAILS.md:676-678`).
- Events are **not chainable** (`@alias`/`exposes` apply to call results, not
  event streams) — compile error in codegen (`SleipnirEventAttribute.cs:24-28`,
  `ROADMAP.md:146`).
- SSE query-param binding has **no type hints** — `?count=3` binds to a number
  and 400s for a `string` parameter; complex/typed params need WS
  (`SleipnirSseConnection.cs:291-298`).
- Native `EventSource` cannot set `Authorization` — Bearer-auth hosts need a
  fetch-based client (`SleipnirSseConnection.cs:39-43`, `PROTOCOL.md:921-924`).
- **Resuming into a WS-active router is unsupported** — switch to `rest`/`auto`
  to resume over SSE, or use SignalR (`TRANSPORT_REFERENCE.md:461-465`,
  `:690`, `CHANGELOG.md:53-56`).
- The durable store is **in-process** — no restart survival
  (`SleipnirSubscriptionStore.cs:38-41`, `STABILITY.md:214`).
- The `sleipnir.event.dropped` metric was **dead code** before the custom
  `EventBuffer` (the old `BoundedChannel(DropOldest).TryWrite` always returned
  true, hiding drops) (`EventBuffer.cs:7-11`, `CHANGELOG.md:247-252`).
- Events **beyond the replay window are still lost** (counted in
  `sleipnir.event.dropped`), even for resumable subscriptions
  (`STABILITY.md:215-216`, `PROTOCOL.md:832-833`).
- A `0` `EventResumeTtl` means **never auto-reclaim** — the caller accepts
  unbounded memory for abandoned subscriptions (`SleipnirOptions.cs:51-55`,
  `SleipnirSubscriptionStore.cs:67-69`).
- `[SleipnirEvent]` and `[SleipnirMethod]` share the `{Controller}_{name}`
  dispatch namespace and must not collide (no parameter-based overload
  resolution) (`SleipnirInvoker.cs:235-248`).

---

## 12. Phase history & limitations

### Phase R — durable replay + Last-Event-Id resume (experimental)

`[SleipnirEvent(Resumable = true)]` opts an event into durable subscriptions that
survive a WS disconnect: the `IObservable<T>` source is kept subscribed across
disconnects, a per-subscription replay ring accumulates gap events, and on
reconnect the client sends `lastEventId` for at-least-once-within-the-replay-window
replay (client dedups by `eventId`). Non-resumable events keep the v1 ephemeral
at-most-once behavior. `CHANGELOG.md:205-235`, `STABILITY.md:208-217`,
`ROADMAP.md:181`.

Adds: `SleipnirSubscriptionStore` DI singleton (`CHANGELOG.md:232-234`),
`ResumeDecision`/`ResumePolicy` client hook (`CHANGELOG.md:213-220`), reconnect
auth re-check against the original route (`CHANGELOG.md:224-227`), knobs
`EventReplayBufferCapacity`/`EventResumeTtl`/`EventMaxDurableSubscriptions`
(`CHANGELOG.md:228-231`).

**Phase R limitations:**

- In-process only — no cross-restart persistence (`SleipnirSubscriptionStore.cs:38-41`,
  `STABILITY.md:214`, `README_DETAILS.md:734`, `PROTOCOL.md:854-855`).
- Exactly-once + cross-process durable remain future (`ROADMAP.md:181`).
- R1 resume re-attaches without re-auth (safe because no client sends a resume
  request until Phase R2 ships the resume hook) (`SleipnirSubscriptionStore.cs:38-41`).

### Phase S — SSE-over-REST (experimental, 1.3.1)

Same `[SleipnirEvent]` methods over `text/event-stream` for clients behind
proxies/firewalls that block WS upgrades. Reuses the exact Phase R resume
machinery and the process-wide store → cross-transport resume (a WS
subscription resumes over SSE and vice-versa). `CHANGELOG.md:70-110`,
`STABILITY.md:203-207`, `ROADMAP.md:411-416`.

**Phase S limitations:**

- SSE named-params only (`SleipnirSseConnection.cs:291-298`, `PROTOCOL.md:534`,
  `BEST_PRACTICES.md:36`, `CHANGELOG.md:49`).
- Native `EventSource` cannot set `Authorization` (`SleipnirSseConnection.cs:39-43`,
  `PROTOCOL.md:921-924`).
- WS-direction resume deferred (`TRANSPORT_REFERENCE.md:461-465`, `:690`,
  `CHANGELOG.md:53-56`).
- REST-Long-Polling and SignalR events out of scope for v1 (`STABILITY.md:207`).

---

## 13. How it is verified (the gates)

- **Unit (C#):** event-buffer/backpressure and subscription-store tests under
  `SleipnirTests/Unit/Core/`; the invoker event-contract guards
  (`SleipnirInvoker.cs:209-248` are exercised by registration tests).
- **Integration (C#):** `SleipnirTests/Integration/ResumeTests.cs`
  (cross-transport resume), `SleipnirTests/Integration/SignalRHubStreamTests.cs`
  (fresh+complete, resume-replay, auth-reject) — see also
  `TRANSPORT_REFERENCE.md:735-737`.
- **TS runtime:** `clients/ts/test/unit/` — `sse.test.ts`, `websocket.test.ts`,
  `signalr.test.ts` (event-frame parsing + resume).
- **Guide E2E:** `guide/chapters/09-events.md` + `guide/server/Controllers/PriceFeedController.cs`
  (the first runnable event sample, `Resumable = true`).

Shipped: Phase 3 events (v1.0 line), configurable backpressure + the
`EventBuffer` drop-counting fix, Phase R resume (experimental), Phase S SSE
(1.3.1), cross-transport resume + SignalR hub-streaming (v1.4.0, 2026-08-20,
PR #15 → main `b4866ef`). See the `sleipnir-cross-transport-resume` and
`sleipnir-sse-rest-events` internal notes for the release history.

---

## 14. Relationship to other docs

| Doc | Covers (event-relevant) |
|-----|--------------------------|
| `README.md` | User-facing overview: `[SleipnirEvent]` + `IObservable<T>`, `SubscribeAsync`, "Server-push events" section (`:265, :276-300`); notes WS-only in v1, not chainable. |
| `README_DETAILS.md` | Deepest user reference: Server-Push Events (`:484`), Event vs Call vs Stream table (`:506`), authoring + wire (`:514-602`), lifecycle/gap semantics (`:625-680`), Resume/Last-Event-Id (`:681-734`), Discovery (`:736-744`), REST Events SSE (`:761-806`), 1.2.0 migration (`:754-755`). |
| `PROTOCOL.md` | Wire spec: subscribe/unsubscribe/event/complete/error frames (`:609-744`), ack + `replayedFrom` (`:682-689`), Resume (Last-Event-Id) (`:812-855`), Discovery `kind:"event"` (`:857-871`), REST Events SSE endpoints + wire mapping + 410 (`:873-924`), SignalR hub-streaming (`:1228-1232`). |
| `STABILITY.md` | Stability contract: Phase 3 experimental, `[SleipnirEvent]` required marker (1.2.0), `EventBackpressureStrategy`/`EventBufferCapacity`, Phase S SSE experimental, Phase R resume experimental + in-process limitation (`:193-217`). |
| `ROADMAP.md` | Phase 3 events, gap-semantics decision (at-most-once v1 vs resume Phase R), Phase S SSE delivered, SSE-vs-stream distinction (`:146, :181, :411-416, :436-437`). |
| `CHANGELOG.md` | Phase S SSE entries (`:70-110`), Phase R resume + configurable backpressure + `sleipnir.event.dropped` fix + `[SleipnirEvent]` required-marker change (`:205-276`). |
| `TRANSPORT_REFERENCE.md` | Client-side: cross-transport subscribe/resume, `ResumePolicy`/`ResumeDecision`, durable store pointer, WS-direction resume limitation (§6, §10). |
| `CODEGEN_REFERENCE.md` | `kind:"event"` discovery surface, events-not-chainable codegen rule. |
| `ERROR_CATALOG.md` | `SleipnirErrorCodes.ServiceUnavailable` → 503 / `ResourceExhausted` (`:37, :58-60`); 499 = client closed. |
| `BEST_PRACTICES.md` | SSE-vs-WS choice for complex/typed params (`:36`); streaming-vs-events guidance (`:170`). |
| `docs/stories/05-realtime-push-events.md` | Narrative: the chat-with-instant-messages problem, `[SleipnirEvent]` as first-class surface, wire frame types. |
| `docs/design/phase-3-events.md` | Design: events vs calls architecture, server surface, wire frames, transport story. |
| `guide/chapters/09-events.md` | Guide chapter 9: first `[SleipnirEvent]` (live BTC feed), `Resumable = true`, Svelte live chart + Blazor monitor + resume. |
| `guide/server/Controllers/PriceFeedController.cs` | The guide's runnable event controller. |

> **Note:** `samples/01-notification-chat` is a multi-transport sample
> (REST/WS/SignalR) but **does not exercise events** — its controllers use
> `[SleipnirMethod]` only. For a runnable event, use the guide's
> `PriceFeedController`.