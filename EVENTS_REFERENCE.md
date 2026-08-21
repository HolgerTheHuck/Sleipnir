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
table, the per-transport server paths, a diagnostics/troubleshooting catalog,
and a map of where the deeper docs live. For onboarding read
`GETTING_STARTED.md`; for the wire-level spec read `PROTOCOL.md` §"Server-Push
Events"; for the client-side subscribe/resume mechanics read
`TRANSPORT_REFERENCE.md` §6. This doc consolidates those and links back for
depth.

**Citations are durable, not line-pinned.** A code citation points at a file
plus the fully-qualified symbol that owns the claim (`SleipnirInvoker.Register()`,
`DurableSubscriptionState.AppendEvent()`), or — for a string, comment, or
attribute-decoration — a file plus a short verbatim quote
(`SleipnirSubscriptionStore.cs` "A durable subscription outlives a single
WebSocket connection"). A markdown citation points at the enclosing heading
(`PROTOCOL.md` §"Resume (Last-Event-Id) — resumable events"). Line numbers are
deliberately omitted: they drift on every edit and would force a re-check of
this doc whenever the surrounding code is touched. Code-facing text is English
per `CLAUDE.md`.

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
fail-loud at registration (`SleipnirCore/Services/SleipnirInvoker.cs` →
`SleipnirInvoker.Register()`, "marked [SleipnirEvent] but does not return
IObservable<T>"):

> A method marked `[SleipnirEvent]` that does not return `IObservable<T>`
> throws `InvalidOperationException` at startup.

The inverse is also rejected: a `[SleipnirMethod]` returning `IObservable<T>`
throws (`SleipnirInvoker.Register()`, "returns IObservable<T> but is marked
[SleipnirMethod]"), and the two attributes are mutually exclusive on one method
(`SleipnirInvoker.Register()`, "decorated with both [SleipnirMethod] and
[SleipnirEvent]"). `[SleipnirEvent]` and `[SleipnirMethod]` share the
`{Controller}_{name}` dispatch namespace and must not collide — there is no
parameter-based overload resolution (`SleipnirInvoker.Register()`, "does not
resolve overloads by parameters").

`IAsyncEnumerable<T>` is **not** an event return type — it is the streaming-call
surface (`kind:"stream"` in Discovery, consumed into a `List<T>` JSON array).
Events are strictly `IObservable<T>` and are **not chainable**: `@alias` /
`exposes` apply to call results, not event streams (`SleipnirCore/Attributes/SleipnirEventAttribute.cs`
→ `SleipnirEventAttribute`, "nicht chainable"; `ROADMAP.md` §"Phase 3 —
Echtzeit-Kohärenz").

The subscribe result the server produces is a `SleipnirSubscribeResult`
carrying `IObservable<object?>? Observable` (`SleipnirCommon/Models/SleipnirSubscribeResult.cs`
→ `SleipnirSubscribeResult.Observable`); the invoker converts the method's
`IObservable<T>` to `IObservable<object?>` via `TryAsObservableObject` —
covariance cast for reference types, a boxing adapter for value types
(`SleipnirCore/Services/SleipnirInvoker.cs` → `SleipnirInvoker.TryAsObservableObject()`).

Discovery tags event methods with `kind:"event"`
(`PROTOCOL.md` §"Discovery", `README_DETAILS.md` §"Discovery").

---

## 2. Authoring events — `[SleipnirEvent]`

**Attribute:** `SleipnirCore/Attributes/SleipnirEventAttribute.cs` →
`SleipnirEventAttribute` — `sealed class SleipnirEventAttribute : Attribute`,
`[AttributeUsage(AttributeTargets.Method)]`.

| Member | Type | Default | Where | Meaning |
|--------|------|---------|-------|---------|
| `Name` (ctor arg) | `string` | required | `SleipnirEventAttribute.Name` | The wire name. |
| `BufferCapacity` | `int` | `-1` | `SleipnirEventAttribute.BufferCapacity` | Per-event override of the per-subscription buffer cap; `-1` inherits `SleipnirOptions.EventBufferCapacity` (fallback 100); `0` only meaningful with `Unbounded`. |
| `BackpressureStrategy` | `EventBackpressureStrategy` | `Inherit` | `SleipnirEventAttribute.BackpressureStrategy` | Per-event override of the overflow strategy; `Inherit` uses the global option. |
| `Resumable` | `bool` | `false` | `SleipnirEventAttribute.Resumable` | Opt in to `Last-Event-Id` resume + server-side disconnect buffer (Phase R). |

The doc comment on the attribute states the contract and gives the canonical
signature (`SleipnirEventAttribute` class doc, "[SleipnirEvent(\"MessageReceived\")]"):

```csharp
[SleipnirEvent("MessageReceived")]
public IObservable<Message> Subscribe(int chatId, CancellationToken ct) => ...;
```

`CancellationToken` is injected automatically like a call method's; method
arguments bind by name like calls.

### Example — the guide's runnable event

`guide/server/Controllers/PriceFeedController.cs` → `PriceFeedController.Ticks`:

```csharp
[SleipnirEvent("Ticks", Resumable = true)]
public IObservable<PriceTick> Ticks(string symbol) => _feed.GetStream(symbol);
```

The guide chapter `guide/chapters/09-events.md` builds the first live event
(BTC feed, `Resumable = true`, Svelte live chart + Blazor monitor + resume).

> **Gotcha — resumable events need a long-lived hot source.** A `Resumable`
> event **must return a long-lived hot/durable observable** (e.g. a `Subject<T>`
> or a factory backed by a long-running producer). A cold observable that
> restarts per subscribe has no resume semantics (`SleipnirEventAttribute`
> class doc, "long-lived hot/durable observable"; `PriceFeedController` class
> doc, "Resumable = true opts in to the durable path"; `README_DETAILS.md`
> §"Cold vs. hot observables").

### Registration errors (fail-loud at startup)

- Both `[SleipnirMethod]` and `[SleipnirEvent]` on one method →
  `SleipnirInvoker.Register()`, "decorated with both [SleipnirMethod] and [SleipnirEvent]".
- `[SleipnirEvent]` not returning `IObservable<T>` → `SleipnirInvoker.Register()`,
  "marked [SleipnirEvent] but does not return IObservable<T>".
- `[SleipnirMethod]` returning `IObservable<T>` → `SleipnirInvoker.Register()`,
  "returns IObservable<T> but is marked [SleipnirMethod]".
- Event + call sharing `{Controller}_{name}` → `SleipnirInvoker.Register()`,
  "does not resolve overloads by parameters".

---

## 3. Wire frames — the `EventFrame` lifecycle

**Definition:** `SleipnirCore/Events/EventFrame.cs` → `EventFrame` —
`internal static class EventFrame`. Four variants, each producing **one JSON
string** (serialized once, reused across all transports):

| Frame | Factory | Shape | Where |
|-------|---------|-------|-------|
| **event** | `Event(subscriptionId, eventId, data)` | `{ type:"event", subscriptionId, eventId, data }` | `EventFrame.Event()` |
| **complete** | `Complete(subscriptionId)` | `{ type:"complete", subscriptionId }` | `EventFrame.Complete()` |
| **error** | `Error(subscriptionId, message)` | `{ type:"error", subscriptionId, message }` | `EventFrame.Error()` |
| **ack** | `Ack(subscriptionId, replayedFrom?)` | `{ type:"ack", subscriptionId, replayedFrom? }` | `EventFrame.Ack()` |

The frame discriminator is `type` with values
`"event" | "complete" | "error" | "ack"` (`EventFrame` class doc,
"{type:\"event\",subscriptionId,eventId,data}"). Frames are
`subscriptionId`-keyed, **not** `id`-keyed (unlike calls) — `subscriptionId` is
the per-subscription correlation handle (`PROTOCOL.md` §"Event frames
(server → client)" and §"Completion and error frames (server → client)"). The
ack is the first item of a SignalR hub stream and is delivered out-of-band on
WS/SSE before any live frame.

**`replayedFrom`** is set on resume and omitted on a fresh subscribe via
`WhenWritingNull` (`EventFrame.Ack()` doc, "replayedFrom is omitted on a fresh
subscribe"; `SleipnirCore/Events/EventJsonOptions.cs` → `EventJsonOptions.Default`).
It is the first replayed `eventId` (null on fresh subscribe or when nothing
buffered) (`SleipnirCore/Services/SleipnirSubscriptionStore.cs` →
`DurableSubscriptionState.Attach()` and `Tap.ReplayedFrom`).

**Shared serialization:** all frames serialize once with
`EventJsonOptions.Default` (`SleipnirCore/Events/EventJsonOptions.cs` →
`EventJsonOptions.Default`: camelCase + `UnsafeRelaxedJsonEscaping` +
`WhenWritingNull`), and the **same string** is sent as one WS text frame,
written as an SSE block, or yielded as a SignalR stream item (`EventFrame`
class doc, "bytes are identical regardless of transport"). This is why the TS
SignalR client parses stream items with the WS frame parser (`SleipnirHub/Hub/SleipnirHub.cs`
→ `SleipnirHub` class doc, "the SAME string the WebSocket sends").

**Frame writers (the observers that call `EventFrame`):**

- *Ephemeral* — `SleipnirCore/Events/EventObserver.cs` → `EventObserver<T>`:
  `OnNext` → `EventFrame.Event` + `Buffer.TryEnqueue` (`EventObserver<T>.OnNext()`);
  `OnCompleted` → `Buffer.EnqueueTerminal(EventFrame.Complete(...))`
  (`EventObserver<T>.OnCompleted()`);
  `OnError` → `Buffer.EnqueueTerminal(EventFrame.Error(...))`
  (`EventObserver<T>.OnError()`).
- *Durable* — `EventObserver.cs` → `DurableEventObserver<T>`: `OnNext` →
  `state.AppendEvent(eventId, EventFrame.Event(...))`
  (`DurableEventObserver<T>.OnNext()`);
  `OnCompleted` → `state.SetTerminal(EventFrame.Complete(...))`
  (`DurableEventObserver<T>.OnCompleted()`);
  `OnError` → `state.SetTerminal(EventFrame.Error(...))`
  (`DurableEventObserver<T>.OnError()`).

Terminal frames (`complete`/`error`) bypass the backpressure cap via
`EnqueueTerminal` so they reach the client regardless of overflow
(`SleipnirCore/Events/EventBuffer.cs` → `EventBuffer.EnqueueTerminal()`).

Wire-spec narrative: `PROTOCOL.md` §"Event frames (server → client)" and
§"Completion and error frames (server → client)" (event/complete/error frames),
§"Subscribe response (server → client)" (subscribe-ack with `replayedFrom`).

---

## 4. Backpressure — `EventBuffer` & strategies

Each active subscription has a per-subscription **send buffer** that absorbs
the gap between a fast producer and a slow wire.

**Buffer:** `SleipnirCore/Events/EventBuffer.cs` → `EventBuffer` —
`internal sealed class EventBuffer`, holding serialized frame strings
(`EventBuffer` class doc, "holds serialized frame strings"). Constructor
`EventBuffer(int capacity, EventBackpressureStrategy strategy, CancellationToken disposeToken)`
(`EventBuffer()` ctor); `_unbounded` when `strategy == Unbounded || capacity <= 0`
(`EventBuffer` ctor body, "Unbounded || capacity <= 0").

**`EventBackpressureStrategy` enum:** `SleipnirCommon/Models/EventBackpressureStrategy.cs`
→ `EventBackpressureStrategy`.

| Value | Where | Behavior |
|-------|-------|----------|
| `Inherit` | `EventBackpressureStrategy.Inherit` | Sentinel; at the global-option level treated as `DropOldest`. |
| `DropOldest` | `EventBackpressureStrategy.DropOldest` | **Default.** Evict oldest, enqueue newest, increment `sleipnir.event.dropped`. |
| `DropWrite` | `EventBackpressureStrategy.DropWrite` | Drop newest, increment counter. |
| `Block` | `EventBackpressureStrategy.Block` | Block the producer until a slot frees; never drops. |
| `Unbounded` | `EventBackpressureStrategy.Unbounded` | No cap, no DoS backstop. |

**Enforcement:** `EventBuffer.TryEnqueue(string frame, Action onDropped)`
(`SleipnirCore/Events/EventBuffer.cs` → `EventBuffer.TryEnqueue()`): unbounded
path; Block awaits a `_space` semaphore; DropOldest dequeues oldest +
`onDropped()`; DropWrite calls `onDropped()` and returns false. Terminal frames
bypass the cap (`EventBuffer.EnqueueTerminal()`).

**Where the buffer is created:**

- Ephemeral WS: `SleipnirWebSocket/SleipnirSubscriptionManager.cs` →
  `SleipnirSubscriptionManager.CreateEphemeralAsync()` (the `EventBuffer` lives
  inside `EphemeralSubscriptionState`, `SleipnirCore/Events/EphemeralSubscriptionState.cs`
  → `EphemeralSubscriptionState.Buffer`).
- Ephemeral SSE: `SleipnirRest/Sse/SleipnirSseConnection.cs` →
  `SleipnirSseConnection.PrepareFreshAsync()` (ephemeral branch).
- Durable send buffer (SSE/Hub): always bounded `DropOldest` between the
  unbounded tap and the slow wire — `SleipnirSseConnection.PrepareFreshAsync()`
  (durable send buffer), `SleipnirHub/Hub/SleipnirHub.cs` →
  `SleipnirHub.SubscribeAsync()` (durable send buffer in fresh + resume paths).
- Durable replay-ring eviction uses the per-event strategy
  (`DurableSubscriptionState.AppendEvent()`, evict-oldest + `_onDrop`).

**Per-event override resolution:** `SleipnirCore/Services/SleipnirInvoker.cs` →
`SleipnirInvoker.SubscribeAsync()` (backpressure resolution) — attribute
sentinel `Inherit/-1` → global option; global `Inherit` → `DropOldest`;
`Unbounded` ignores capacity; capacity fallback 100. The resolved values ride
on `SleipnirSubscribeResult.EventBufferCapacity` /
`SleipnirSubscribeResult.EventBackpressureStrategy`
(`SleipnirCommon/Models/SleipnirSubscribeResult.cs`).

**Drop counting:** `EventObserver.OnDropped` (`SleipnirCore/Events/EventObserver.cs`
→ `EventObserver<T>.OnDropped()`) calls `SleipnirMetrics.EventDropped` + logs.
The custom `EventBuffer` replaced a `BoundedChannel(DropOldest)` whose
`TryWrite` always returned true (hiding drops) — `EventBuffer` class doc,
"TryWrite always returned true"; `CHANGELOG.md` §"Fixed — Events:
`sleipnir.event.dropped` metric was dead code".

---

## 5. Durable subscriptions & resume (Phase R)

Opt in with `[SleipnirEvent(Resumable = true)]`. The `IObservable<T>` source is
kept subscribed across disconnects, a per-subscription **replay ring**
accumulates gap events, and on reconnect the client sends `lastEventId` for
at-least-once-within-the-replay-window replay (the client dedups by `eventId`).
Non-resumable events keep the v1 ephemeral at-most-once behavior.

### Store — `SleipnirSubscriptionStore`

`SleipnirCore/Services/SleipnirSubscriptionStore.cs` →
`SleipnirSubscriptionStore` —
`public sealed class SleipnirSubscriptionStore : IAsyncDisposable`.
Process-wide, registered as a DI singleton (`SleipnirSubscriptionStore` class
doc, "Registered once as a DI singleton"; `STABILITY.md` §"2. Experimental
surface"). Backed by `ConcurrentDictionary<string, DurableSubscriptionState>
_durable` (`SleipnirSubscriptionStore._durable`). The store is shared across
transports, so a durable subscription created over WS resumes over SSE and
vice-versa (`SleipnirRest/Sse/SleipnirSseConnection.cs` → `SleipnirSseConnection`
class doc, "a durable subscription created over WebSocket can be resumed over
SSE and vice-versa").

**Per-subscription state** — `DurableSubscriptionState`
(`SleipnirSubscriptionStore.cs` → `DurableSubscriptionState`, `IDisposable`):
the source subscription (`DurableSubscriptionState.SourceSubscription`), a
stable server-generated `SubscriptionId` (`DurableSubscriptionState.SubscriptionId`),
a monotonic `_eventIdCounter` (`DurableSubscriptionState._eventIdCounter`,
`DurableSubscriptionState.NextEventId()`), a bounded replay ring
`Queue<(long EventId, string Frame)> _ring` (`DurableSubscriptionState._ring`)
with cap `_ringCap` (`DurableSubscriptionState._ringCap`), a `_liveTap` channel
(`DurableSubscriptionState._liveTap`), a `_terminalFrame`
(`DurableSubscriptionState._terminalFrame`), and the `Controller`/`Method`
recorded at create for reconnect auth re-check
(`DurableSubscriptionState.Controller` / `DurableSubscriptionState.Method`).

**Store API:**

| Method | Where | Purpose |
|--------|-------|---------|
| `BeginCreate(strategy)` | `SleipnirSubscriptionStore.BeginCreate()` | Create a `DurableSubscriptionState` with a server-generated `Guid N` id; returns `null` at the process-wide cap → caller returns 503. |
| `Lookup(subscriptionId)` | `SleipnirSubscriptionStore.Lookup()` | For resume. |
| `OnAttached()` | `SleipnirSubscriptionStore.OnAttached()` | Bump the live-subscription gauge (symmetric with `Detach`). |
| `Detach(subscriptionId)` | `SleipnirSubscriptionStore.Detach()` | Complete the live tap, drop the tap ref, decrement the gauge; **source + ring persist** for resume. No-op on unknown. |
| `Destroy(subscriptionId)` | `SleipnirSubscriptionStore.Destroy()` | Explicit unsubscribe: dispose source, discard ring, remove state, decrement gauge if a tap was attached. |
| `SweepGc()` | `SleipnirSubscriptionStore.SweepGc()` | Timer-driven (GC timer in `SleipnirSubscriptionStore()` ctor); evicts completed sources and detached subscriptions past the idle TTL. |
| `DisposeAsync()` | `SleipnirSubscriptionStore.DisposeAsync()` | Shutdown. |

### Replay ring

`DurableSubscriptionState.AppendEvent(long eventId, string frame)`
(`SleipnirCore/Services/SleipnirSubscriptionStore.cs` →
`DurableSubscriptionState.AppendEvent()`): enqueue into `_ring`; on overflow
(`_ringCap > 0 && _ring.Count > _ringCap`) evict oldest and calls
`_onDrop(SubscriptionId)`; forwards to the attached live tap if any.

### Replay-on-resume

`DurableSubscriptionState.Attach(long lastEventId)`
(`DurableSubscriptionState.Attach()`): snapshots ring entries with
`eid > lastEventId` into a fresh unbounded channel under the lock, sets
`_liveTap`, returns a `Tap(SubscriptionId, Reader, ReplayedFrom)`.
`ReplayedFrom` is the first replayed eventId (null on fresh subscribe or when
nothing buffered) (`DurableSubscriptionState.Attach()` and `Tap.ReplayedFrom`),
surfaced as `replayedFrom` in the ack (`SleipnirCore/Events/EventFrame.cs` →
`EventFrame.Ack()` doc).

**`Tap`:** `SleipnirSubscriptionStore.cs` → `Tap` —
`{ SubscriptionId, ChannelReader<string> Reader, long? ReplayedFrom }`.

### Options plumbed into the store (constructor)

- `EventReplayBufferCapacity` → `_replayBufferCapacity` fallback 1000
  (`SleipnirSubscriptionStore()` ctor). Per-subscription ring cap.
- `EventResumeTtl` → `_resumeTtl` fallback 60s (`SleipnirSubscriptionStore()`
  ctor); `0` disables auto-reclaim (GC timer in the same ctor). Idle-TTL for GC.
- `EventMaxDurableSubscriptions` → `_maxDurable` fallback 10 000
  (`SleipnirSubscriptionStore()` ctor); `0` = unbounded. Over-cap → `BeginCreate`
  returns null → 503.

### Reconnect vs. resume

- **Reconnect** = WS transport reconnection: `subscriptionId` is per-connection;
  on disconnect all subscriptions are disposed; the client re-subscribes with
  fresh ids after reconnect; gap events are lost (at-most-once-while-
  disconnected, v1 default). `SleipnirWebSocket/SleipnirSubscriptionManager.cs`
  → `SleipnirSubscriptionManager` class doc, "Disconnect werden alle
  Subscriptions disposed"; `PROTOCOL.md` §"Subscription lifecycle & delivery
  semantics"; `README_DETAILS.md` §"Subscription lifecycle & delivery
  semantics".
- **Resume** = `Last-Event-Id` resume (Phase R, opt-in via `Resumable = true`):
  the durable `subscriptionId` is stable across reconnects; the client sends
  `lastEventId` + `subscriptionId`; the server replays the gap from the ring
  (at-least-once within the window; client dedups by `eventId`).
  `SleipnirSubscriptionStore` class doc, "A durable subscription outlives a
  single WebSocket connection"; `PROTOCOL.md` §"Resume (Last-Event-Id) —
  resumable events"; `README_DETAILS.md` §"Resume (Last-Event-Id) — resumable
  events".
- The client `ResumeDecision` enum codifies the choice: `Fresh` (reconnect
  behavior), `Resume` (send id + lastEventId), `Drop` (end the subscription)
  (`SleipnirClient/Sleipnir/ResumeDecision.cs` → `ResumeDecision`). A `Resume`
  on a non-resumable event degrades to `Fresh` (the server does not know the id
  → fresh subscribe) (`ResumeDecision` enum doc, "degrades to Fresh";
  `CHANGELOG.md` §"Added — Events: Last-Event-Id resume + server disconnect
  buffer (Phase R, experimental)").

### Reconnect-time auth re-check

On resume, authorization is re-checked against the **original route** (the
controller/method recorded at create). A revoked role or a vanished route tears
the durable subscription down. WS: `SleipnirWebSocket/SleipnirSubscriptionManager.cs`
→ `SleipnirSubscriptionManager.HandleSubscribeAsync()` (calls
`AuthorizeSubscribeAsync`, on error `store.Destroy` + returns the error). SSE:
`SleipnirRest/Sse/SleipnirSseConnection.cs` →
`SleipnirSseConnection.PrepareResumeAsync()`. Hub: `SleipnirHub/Hub/SleipnirHub.cs`
→ `SleipnirHub.SubscribeAsync()` (resume branch). Invoker:
`SleipnirCore/Services/SleipnirInvoker.cs` →
`SleipnirInvoker.AuthorizeSubscribeAsync()` (`BadRequest(..., NotFound)` for
unknown controller/method, `Unauthorized()`/`Forbidden()` on auth failure).
Doc: `PROTOCOL.md` §"Resume (Last-Event-Id) — resumable events"; `CHANGELOG.md`
§"Added — Events: Last-Event-Id resume + server disconnect buffer (Phase R,
experimental)".

---

## 6. SSE-over-REST server contract (Phase S)

SSE delivers the same `[SleipnirEvent]` methods over `text/event-stream` for
clients behind proxies/firewalls that block WS upgrades. It reuses the exact
Phase R resume machinery and the process-wide store → cross-transport resume.

### Connection handler

`SleipnirRest/Sse/SleipnirSseConnection.cs` → `SleipnirSseConnection` —
`internal sealed class SleipnirSseConnection`.

### Endpoint mapping — `SleipnirRest/SleipnirSseEndpointExtensions.cs` → `SleipnirSseEndpointExtensions.MapSleipnirSseEndpoints()`

| Route | Method | Params | Where |
|-------|--------|--------|-------|
| `/events/{controller}/{method}` | GET | method args as query params | `MapSleipnirSseEndpoints()` (fresh route) |
| `/events/{subscriptionId}` | GET | `Last-Event-Id:` header (or `?lastEventId=` fallback) | `MapSleipnirSseEndpoints()` (resume route) |

Both routes apply a transport auth gate (401 when `RequireAuthentication` and
unauthenticated). Header takes precedence over the query fallback (both in
`MapSleipnirSseEndpoints()`).

### SSE block construction

`SleipnirSseConnection.WriteFrameAsync`: extracts `eventId` (→ `id:` line)
and `type` (→ `event:` line) from the pre-serialized frame JSON, then writes
`data: {frame}` per line + blank line. `SleipnirSseConnection.WriteAckAsync`:
`id: 0\n event: ack\n data: {subscriptionId[, replayedFrom]}\n\n`. Wire-mapping
table: `PROTOCOL.md` §"REST Events (SSE)".

### Ack-first rule

`SleipnirSseConnection.StreamAsync` calls `WriteAckAsync` first, then drains
the send buffer — the ack is written before any live frame (same invariant as
the WS race fix; SSE-specific rationale in `SleipnirSseConnection.WriteAckAsync()`
doc). `PROTOCOL.md` §"REST Events (SSE)".

### Resume & 410 Gone

`SleipnirSseConnection.PrepareResumeAsync`: `store.Lookup` → null returns
`Results.StatusCode(410)`; auth re-check via `AuthorizeSubscribeAsync`;
`state.Attach(lastEventId ?? 0)`; ack carries `ReplayedFrom`. A 410 means the
durable state was GC'd/TTL-expired; the client falls back to a fresh subscribe
(`PROTOCOL.md` §"REST Events (SSE)"; `README_DETAILS.md` §"REST Events (SSE)";
`CHANGELOG.md` §"Added — Events over REST: SSE").

### Response headers

`SleipnirSseEndpointExtensions.WriteSseStreamAsync`: `text/event-stream`,
`Cache-Control: no-cache`, `X-Accel-Buffering: no` (so proxies flush per event),
`StartAsync` to flush headers.

### Limitations (SSE-specific)

- **Named params only** — a GET has no body, so method args travel as query
  params; each value parsed as JSON when valid, else a string; a repeated key
  becomes a JSON array. **No type hints**: `?count=3` binds to a number and
  would 400 for a `string` parameter — use the native WebSocket wire for
  complex/typed parameters. `SleipnirSseConnection.BuildParams()` (remarks doc),
  `PROTOCOL.md` §"REST (HTTP/1.1 + JSON)", `CHANGELOG.md` §"Added — Events
  over REST: SSE".
- **Native `EventSource` cannot set `Authorization`** → Bearer-auth hosts need
  a fetch-based client (TS `SleipnirSseClient`); native `EventSource` works for
  cookie-auth hosts. `SleipnirSseConnection` class doc, "EventSource cannot set
  a Bearer header"; `SleipnirSseEndpointExtensions` class doc, "EventSource
  cannot set a Bearer header"; `PROTOCOL.md` §"REST Events (SSE)".
- **WS-direction resume deferred** — resuming *into* a WS-active router is
  unsupported; the blessed cross-transport bridge is SSE/SignalR
  (`TRANSPORT_REFERENCE.md` §"Intentionally deferred: WS-direction resume" and
  §"Errors"; `CHANGELOG.md` §"Changed — Unified transport").

### Gating

SSE endpoints are added only when `useSse` is true —
`SleipnirRest/SleipnirEndpointExtensions.cs` →
`SleipnirEndpointExtensions.MapSleipnirEndpoints()` (`if (useSse)
group.MapSleipnirSseEndpoints(...)`), threaded from `SleipnirOptions.UseSse`
(`SleipnirServer/SleipnirPipelineExtensions.cs` →
`SleipnirPipelineExtensions.MapSleipnir()` for the `useSse` threading and
`SleipnirPipelineExtensions.LogTransportIntrospection()` for the introspection
log).

---

## 7. WebSocket event frames

**Middleware:** `SleipnirWebSocket/SleipnirWebSocketMiddleware.cs` →
`SleipnirWebSocketMiddleware`. A per-connection `SleipnirSubscriptionManager`
is created in `SleipnirWebSocketMiddleware.HandleConnectionAsync()`.

### Frame `kind` detection

The `kind` property is read case-insensitively from the parsed JSON root in
`SleipnirWebSocketMiddleware.ProcessMessageAsync()`:

| `kind` | Where | Behavior |
|--------|-------|----------|
| `"subscribe"` | `SleipnirWebSocketMiddleware.ProcessMessageAsync()` (subscribe branch) | Deserialize `SleipnirRequest`, extract resume fields `lastEventId` (number) + `subscriptionId` (string) **out-of-band** from the raw frame so the `SleipnirRequest` wire model stays untouched; call `HandleSubscribeAsync(request, context, ct, lastEventId, resumeSubscriptionId, id)`; enqueue the ack response if non-null. |
| `"unsubscribe"` | `SleipnirWebSocketMiddleware.ProcessMessageAsync()` (unsubscribe branch) | Read `subscriptionId` + `id`; 400 if `subscriptionId` missing; call `HandleUnsubscribeAsync`. |
| *(none)* | `SleipnirWebSocketMiddleware.ProcessMessageAsync()` (call branch) | Normal call (v1.0 behavior). |

### Resume fields (subscribe frame)

`lastEventId` (long — the last eventId the client processed) and
`subscriptionId` (the durable id to resume), extracted out-of-band in
`SleipnirWebSocketMiddleware.ProcessMessageAsync()`. Both absent → fresh
subscribe.

### Subscribe-ack shape (WS)

The ack is a real `SleipnirResponse` with `code = 200`,
`data = { subscriptionId, replayedFrom }` (`replayedFrom` omitted on fresh via
`WhenWritingNull`), `id` = request id / correlation id. Built in
`SleipnirSubscriptionManager.EnqueueSubscribeAckAsync()`, serialized with the
middleware JSON options (`SleipnirResponseJsonConverter`: explicit nulls, fixed
field order) so bytes match what the middleware would have produced
(`EnqueueSubscribeAckAsync()` doc, "bytes are identical to what the middleware
would have produced"). Wire example: `PROTOCOL.md` §"Subscribe response
(server → client)".

### Events pushed to the client

`SleipnirSubscriptionManager` runs a per-connection send loop
(`SleipnirSubscriptionManager.SendLoopAsync()`) draining a shared
`Channel<string> _sendChannel` (`SleipnirSubscriptionManager._sendChannel`) into
`WebSocket.SendAsync`. The ephemeral pump (in
`SleipnirSubscriptionManager.CreateEphemeralAsync()`) drains the
per-subscription `EventBuffer` into `_sendChannel`; the durable pump
(`SleipnirSubscriptionManager.StartDurablePump()`) drains the `Tap.Reader` into
`_sendChannel`. Each event frame is one WS text frame (`PROTOCOL.md` §"Event
frames (server → client)").

### Auth gate on upgrade

`SleipnirWebSocketMiddleware.InvokeAsync()` — 401 when
`RequireAuthentication` and unauthenticated, before the socket is created.

### Unsubscribe over WS

`kind:"unsubscribe"` with `subscriptionId` (`PROTOCOL.md` §"Unsubscribe request
/ response"); 404 if unknown (`PROTOCOL.md` §"Unsubscribe request / response";
`SleipnirSubscriptionManager.HandleUnsubscribeAsync()`).

---

## 8. SignalR hub-streaming

**Hub method:** `SleipnirHub/Hub/SleipnirHub.cs` → `SleipnirHub.SubscribeAsync()` —

```csharp
public async IAsyncEnumerable<string> SubscribeAsync(
    SleipnirRequest request, string? resumeSubscriptionId, long? lastEventId,
    [EnumeratorCancellation] CancellationToken ct)
```

Class declaration `SleipnirHub` (`SleipnirHub : Microsoft.AspNetCore.SignalR.Hub`).
Wired only when `SleipnirOptions.UseSignalR = true` (opt-in, default false)
(`SleipnirHub` class doc, "Only wired when SleipnirOptions.UseSignalR = true");
calls keep using `SleipnirHub.DoWork()` / `SleipnirHub.DoWorkMany()`.

### Wire — ack-then-frames

The first yielded item is `EventFrame.Ack(subscriptionId, replayedFrom)`
(in `SleipnirHub.SubscribeAsync()`). Each subsequent item is one pre-serialized
logical event frame string (`{type,subscriptionId,eventId,data}` / `complete` /
`error`) — the **same string** WS sends as a text frame and SSE writes as a
block, so the TS SignalR client parses stream items with the WS frame parser
(`SleipnirHub` class doc, "the SAME string the WebSocket sends"). `PROTOCOL.md`
§"Transport selection (client)" confirms the ack-then-frames order.

The hub streams **strings** (not objects) to reuse the durable string-frame
buffer and avoid double-serialization — the frames are already serialized in
the transport-agnostic core (`SleipnirHub` class doc, "the SAME string the
WebSocket sends"; `TRANSPORT_REFERENCE.md` §"SignalR — `SleipnirHub`").

### Fresh-vs-resume paths

- **Resume** (`resumeSubscriptionId` non-empty, `SleipnirHub.SubscribeAsync()`
  resume branch): `store.Lookup` → null throws `HubException` ("…not found
  (expired or never created). Re-subscribe fresh."); `AuthorizeSubscribeAsync`
  re-checks auth against the original route, on error `store.Destroy` + throw
  `HubException`; `state.Attach(lastEventId ?? 0)`; bounded `DropOldest` send
  buffer.
- **Fresh** (`SleipnirHub.SubscribeAsync()` fresh branch): `service.SubscribeAsync`;
  resumable → `store.BeginCreate` + `DurableEventObserver` + `Attach(0)`;
  ephemeral → `EphemeralSubscriptionState` + `EventObserver`.

### Backpressure (durable)

A background `SleipnirHub.PumpDurableAsync()` drains the unbounded tap into a
bounded `EventBuffer` (DropOldest). A slow SignalR client overflows the send
buffer (drops oldest live frames — they remain in the replay ring for resume),
never grows the tap (`SleipnirHub` class doc, "never grows the tap").

### Cleanup & failure

`finally` (in `SleipnirHub.SubscribeAsync()`): awaits the pump, disposes
ephemeral (decrements gauge), detaches durable (store owns the gauge).
Pre-stream failures (auth/routing/binding) throw `HubException` → the stream
rejects on the client (mapped to `onError`); a mid-stream source error arrives
as an `{type:"error",...}` terminal frame before the stream ends (`SleipnirHub`
class doc, "a mid-stream source error arrives as an {type:\"error\",...}
frame").

---

## 9. Client subscribe & resume (cross-transport)

The full client-side treatment — `SubscribeAsync`/`ResumeAsync`, `ResumePolicy`,
`ResumeDecision`, the `lastEventId` cursor, the durable store shared across
transports, and the WS-direction-resume limitation — lives in
`TRANSPORT_REFERENCE.md` §6 "Events & subscriptions across transports". This
section lists the entry points only and does not re-derive them.

| Entry point | Location |
|-------------|----------|
| `ISleipnirClient.SubscribeAsync<T>(SleipnirRequest?, ResumePolicy?, CancellationToken)` | `SleipnirClient/Sleipnir/ISleipnirClient.cs` → `ISleipnirClient.SubscribeAsync<T>()` |
| `ISleipnirClient.ResumeAsync<T>(string subscriptionId, long lastEventId, ResumePolicy?, CancellationToken)` | `ISleipnirClient.ResumeAsync<T>()` |
| `ResumePolicy` delegate + `ResumeDecision { Fresh, Resume, Drop }` + `SubscriptionResumeContext` record | `SleipnirClient/Sleipnir/ResumeDecision.cs` → `ResumeDecision` / `ResumePolicy` / `SubscriptionResumeContext` |
| `SleipnirSubscription<T> : IObservable<T>, IDisposable` (wraps `SleipnirSubject<T>`, holds `SubscriptionId`, sends unsubscribe on `Dispose`) | `SleipnirClient/Sleipnir/SleipnirSubscription.cs` → `SleipnirSubscription<T>` |

The client tracks the highest `eventId` seen per subscription and silently
drops replayed frames with `eventId ≤ lastSeen` (`PROTOCOL.md` §"Resume
(Last-Event-Id) — resumable events"; `README_DETAILS.md` §"Resume (Last-Event-Id)
— resumable events"). The durable store shared across transports is
`SleipnirCore/Services/SleipnirSubscriptionStore.cs` → `SleipnirSubscriptionStore`
(`TRANSPORT_REFERENCE.md` §"Cross-transport resume"). **WS-direction resume**
(resuming *into* a WS-active router) is unsupported → switch to `rest`/`auto`
to resume over SSE, or use SignalR (`TRANSPORT_REFERENCE.md` §"Intentionally
deferred: WS-direction resume" and §"Errors").

---

## 10. Configuration reference (all event knobs)

All on `SleipnirHub/Extensions/SleipnirOptions.cs` → `SleipnirOptions`.

| Option | Type | Default (fallback) | Where | Notes |
|--------|------|--------------------|-------|-------|
| `EventBufferCapacity` | `int?` | `null` → 100 | `SleipnirOptions.EventBufferCapacity` | Per-subscription send-buffer cap; ignored when `Unbounded`; per-event override via `[SleipnirEvent(BufferCapacity = …)]`. |
| `EventBackpressureStrategy` | `EventBackpressureStrategy` | `DropOldest` | `SleipnirOptions.EventBackpressureStrategy` | Overflow strategy; per-event override via `[SleipnirEvent(BackpressureStrategy = …)]`. |
| `EventReplayBufferCapacity` | `int?` | `null` → 1000 | `SleipnirOptions.EventReplayBufferCapacity` | Replay-ring cap per durable subscription (evict-oldest; `0` = unbounded). |
| `EventResumeTtl` | `TimeSpan?` | `null` → 60s | `SleipnirOptions.EventResumeTtl` | Idle-TTL for durable subscriptions; `0` = never auto-reclaim (caller accepts unbounded memory for abandoned subscriptions, `SleipnirOptions.EventResumeTtl` doc "0 = never auto-reclaim"; `SleipnirSubscriptionStore()` ctor GC timer). |
| `EventMaxDurableSubscriptions` | `int?` | `null` → 10 000 | `SleipnirOptions.EventMaxDurableSubscriptions` | Process-wide durable cap; `0` = unbounded; over-cap → 503. |
| `UseSse` | `bool` | `true` | `SleipnirOptions.UseSse` | Gates the SSE `/events/...` endpoints (honored by the unified `MapSleipnir` pipeline; SSE group added inside the REST group). |
| `UseRest` | `bool` | `true` | `SleipnirOptions.UseRest` | Gates REST endpoints. |
| `UseWebSocket` | `bool` | `true` | `SleipnirOptions.UseWebSocket` | Gates WS transport. |
| `UseSignalR` | `bool` | `false` (opt-in) | `SleipnirOptions.UseSignalR` | Gates the SignalR hub (events via hub-streaming). |

**SSE gating detail:** `UseSse` is read in
`SleipnirRest/SleipnirEndpointExtensions.cs` →
`SleipnirEndpointExtensions.MapSleipnirEndpoints()` (`if (useSse)
group.MapSleipnirSseEndpoints(defaultBufferCapacity: sseBufferCapacity)`), with
`useSse` threaded from `SleipnirServer/SleipnirPipelineExtensions.cs` →
`SleipnirPipelineExtensions.MapSleipnir()` (`useSse: options?.UseSse != false`)
and surfaced in `SleipnirPipelineExtensions.LogTransportIntrospection()`. The
SSE buffer capacity fallback is `SleipnirOptions.EventBufferCapacity`
(`MapSleipnirEndpoints()` SSE-gating block). `STABILITY.md` §"2. Experimental
surface"; `PROTOCOL.md` §"REST Events (SSE)"; `README_DETAILS.md` §"REST
Events (SSE)".

---

## 11. Diagnostics & troubleshooting catalog

### Error codes & messages

- **Over-cap durable subscribe (503):**
  - SSE: `SleipnirSseConnection.PrepareFreshAsync()` —
    `Results.Json(new { code = 503, message = "Durable subscription cap reached — retry later." }, statusCode: 503)`.
  - WS: `SleipnirSubscriptionManager.CreateDurableAsync()` —
    `SleipnirResults.Error(SleipnirErrorCodes.ServiceUnavailable, "Durable subscription cap reached — retry later.", SleipnirErrorCategory.ResourceExhausted)`.
  - Hub: `SleipnirHub.SubscribeAsync()` (fresh branch) —
    `throw new HubException("Sleipnir durable subscription cap reached — retry later.")`.
  - `SleipnirErrorCodes.ServiceUnavailable` → 503 / category `Unavailable`
    (`ERROR_CATALOG.md` §"1. Numeric codes (`SleipnirErrorCodes`)" and §"2.
    Semantic categories (`SleipnirErrorCategory`)").
- **410 Gone (resume on SSE, durable state GC'd/TTL-expired):**
  `SleipnirSseConnection.PrepareResumeAsync()` — `Results.StatusCode(410)`.
  Client falls back to fresh subscribe (`PROTOCOL.md` §"REST Events (SSE)";
  `README_DETAILS.md` §"REST Events (SSE)"; `CHANGELOG.md` §"Added — Events
  over REST: SSE").
- **Resume unknown/GC'd durable id (Hub):** `SleipnirHub.SubscribeAsync()`
  (resume branch) — `throw new HubException("Sleipnir subscription '" +
  resumeSubscriptionId + "' not found (expired or never created). Re-subscribe
  fresh.")`.
- **Resume auth failure (role revoked during gap):** re-checked against the
  original route; 401/403 (or 404 if the route vanished) tears down the durable
  subscription. WS `SleipnirSubscriptionManager.HandleSubscribeAsync()`; SSE
  `SleipnirSseConnection.PrepareResumeAsync()`; Hub
  `SleipnirHub.SubscribeAsync()` (resume branch); invoker
  `SleipnirInvoker.AuthorizeSubscribeAsync()`. Doc `PROTOCOL.md` §"Resume
  (Last-Event-Id) — resumable events"; `CHANGELOG.md` §"Added — Events:
  Last-Event-Id resume + server disconnect buffer (Phase R, experimental)".
- **Plain call to an event method → 400:** `CHANGELOG.md` §"Changed — Events:
  `[SleipnirEvent]` is now the required marker" — "… is a server-push event;
  use `kind:\"subscribe\"`" (was an opaque 500 in 1.1.0). Subscribe to a
  non-event method → 400 without executing (same CHANGELOG section; guard
  `SleipnirInvoker.AuthorizeSubscribeAsync()`, "is not an event").
- **Subscribe to non-`IObservable<T>` return:** `SleipnirInvoker.SubscribeAsync()`
  — `BadRequest($"Method '{request.Method}' on controller '{request.Controller}'
  does not return an IObservable<T> — not a subscribable event.")`.
- **WS unsubscribe missing `subscriptionId`:**
  `SleipnirWebSocketMiddleware.ProcessMessageAsync()` (unsubscribe branch) —
  `"unsubscribe requires subscriptionId."` (400).
- **WS unsubscribe unknown id:** `SleipnirSubscriptionManager.HandleUnsubscribeAsync()`
  — `SleipnirResults.Error(SleipnirErrorCodes.NotFound, $"Subscription
  '{subscriptionId}' not found.", NotFound)` (404).
- **WS malformed JSON / unparseable request:**
  `SleipnirWebSocketMiddleware.ProcessMessageAsync()` — 400 `"Invalid JSON in
  request."` (id null — uncorrelated, `JsonException` catch); and 400 `"Invalid
  request."` (invalid request branch).
- **WS message too large (>1 MB):** `SleipnirWebSocketMiddleware` ("MaxMessageSize"
  const) / `SleipnirWebSocketMiddleware.HandleConnectionAsync()` (size guard) —
  400 `"Message too large."`.
- **SSE malformed frame:** `SleipnirSseConnection.WriteFrameAsync()` —
  `JsonDocument.Parse(frame)` wrapped in try/catch; on a malformed frame it
  falls back to `event: event` + `data:` only (no `id:` line). No exception is
  thrown — the block is still emitted.

### Known gotchas

- A `Resumable` event **must return a long-lived hot/durable observable**; a
  cold observable that restarts per subscribe has no resume semantics
  (`SleipnirEventAttribute` class doc, "long-lived hot/durable observable";
  `README_DETAILS.md` §"Cold vs. hot observables").
- Events are **not chainable** (`@alias`/`exposes` apply to call results, not
  event streams) — compile error in codegen (`SleipnirEventAttribute` class doc,
  "nicht chainable"; `ROADMAP.md` §"Phase 3 — Echtzeit-Kohärenz").
- SSE query-param binding has **no type hints** — `?count=3` binds to a number
  and 400s for a `string` parameter; complex/typed params need WS
  (`SleipnirSseConnection.BuildParams()` remarks doc).
- Native `EventSource` cannot set `Authorization` — Bearer-auth hosts need a
  fetch-based client (`SleipnirSseConnection` class doc, "EventSource cannot set
  a Bearer header"; `PROTOCOL.md` §"REST Events (SSE)").
- **Resuming into a WS-active router is unsupported** — switch to `rest`/`auto`
  to resume over SSE, or use SignalR (`TRANSPORT_REFERENCE.md` §"Intentionally
  deferred: WS-direction resume" and §"Errors"; `CHANGELOG.md` §"Changed —
  Unified transport").
- The durable store is **in-process** — no restart survival
  (`SleipnirSubscriptionStore` class doc, R1 scope note; `STABILITY.md` §"2.
  Experimental surface").
- The `sleipnir.event.dropped` metric was **dead code** before the custom
  `EventBuffer` (the old `BoundedChannel(DropOldest).TryWrite` always returned
  true, hiding drops) (`EventBuffer` class doc, "TryWrite always returned true";
  `CHANGELOG.md` §"Fixed — Events: `sleipnir.event.dropped` metric was dead
  code").
- Events **beyond the replay window are still lost** (counted in
  `sleipnir.event.dropped`), even for resumable subscriptions (`STABILITY.md`
  §"2. Experimental surface"; `PROTOCOL.md` §"Resume (Last-Event-Id) —
  resumable events").
- A `0` `EventResumeTtl` means **never auto-reclaim** — the caller accepts
  unbounded memory for abandoned subscriptions (`SleipnirOptions.EventResumeTtl`
  doc, "0 = never auto-reclaim"; `SleipnirSubscriptionStore()` ctor GC timer).
- `[SleipnirEvent]` and `[SleipnirMethod]` share the `{Controller}_{name}`
  dispatch namespace and must not collide (no parameter-based overload
  resolution) (`SleipnirInvoker.Register()`, "does not resolve overloads by
  parameters").

---

## 12. Phase history & limitations

### Phase R — durable replay + Last-Event-Id resume (experimental)

`[SleipnirEvent(Resumable = true)]` opts an event into durable subscriptions that
survive a WS disconnect: the `IObservable<T>` source is kept subscribed across
disconnects, a per-subscription replay ring accumulates gap events, and on
reconnect the client sends `lastEventId` for at-least-once-within-the-replay-window
replay (client dedups by `eventId`). Non-resumable events keep the v1 ephemeral
at-most-once behavior. `CHANGELOG.md` §"Added — Events: Last-Event-Id resume +
server disconnect buffer (Phase R, experimental)"; `STABILITY.md` §"2.
Experimental surface"; `ROADMAP.md` §"Offene Design-Entscheidungen".

Adds: `SleipnirSubscriptionStore` DI singleton, `ResumeDecision`/`ResumePolicy`
client hook, reconnect auth re-check against the original route, knobs
`EventReplayBufferCapacity`/`EventResumeTtl`/`EventMaxDurableSubscriptions`
(all in `CHANGELOG.md` §"Added — Events: Last-Event-Id resume + server
disconnect buffer (Phase R, experimental)").

**Phase R limitations:**

- In-process only — no cross-restart persistence (`SleipnirSubscriptionStore`
  class doc, R1 scope note; `STABILITY.md` §"2. Experimental surface";
  `README_DETAILS.md` §"Resume (Last-Event-Id) — resumable events"; `PROTOCOL.md`
  §"Resume (Last-Event-Id) — resumable events").
- Exactly-once + cross-process durable remain future (`ROADMAP.md` §"Offene
  Design-Entscheidungen").
- R1 resume re-attaches without re-auth (safe because no client sends a resume
  request until Phase R2 ships the resume hook) (`SleipnirSubscriptionStore`
  class doc, R1 scope note).

### Phase S — SSE-over-REST (experimental, 1.3.1)

Same `[SleipnirEvent]` methods over `text/event-stream` for clients behind
proxies/firewalls that block WS upgrades. Reuses the exact Phase R resume
machinery and the process-wide store → cross-transport resume (a WS
subscription resumes over SSE and vice-versa). `CHANGELOG.md` §"Added — Events
over REST: SSE"; `STABILITY.md` §"2. Experimental surface"; `ROADMAP.md`
§"Later (v1.x+, unsorted)".

**Phase S limitations:**

- SSE named-params only (`SleipnirSseConnection.BuildParams()` remarks doc;
  `PROTOCOL.md` §"REST (HTTP/1.1 + JSON)"; `CHANGELOG.md` §"Added — Events over
  REST: SSE").
- Native `EventSource` cannot set `Authorization` (`SleipnirSseConnection` class
  doc, "EventSource cannot set a Bearer header"; `PROTOCOL.md` §"REST Events
  (SSE)").
- WS-direction resume deferred (`TRANSPORT_REFERENCE.md` §"Intentionally
  deferred: WS-direction resume" and §"Errors"; `CHANGELOG.md` §"Changed —
  Unified transport").
- REST-Long-Polling and SignalR events out of scope for v1 (`STABILITY.md`
  §"2. Experimental surface").

---

## 13. How it is verified (the gates)

- **Unit (C#):** event-buffer/backpressure and subscription-store tests under
  `SleipnirTests/Unit/Core/`; the invoker event-contract guards
  (`SleipnirInvoker.Register()` — both-attributes / not-IObservable /
  call-IObservable / name-collision throws — are exercised by registration
  tests).
- **Integration (C#):** `SleipnirTests/Integration/ResumeTests.cs`
  (cross-transport resume), `SleipnirTests/Integration/SignalRHubStreamTests.cs`
  (fresh+complete, resume-replay, auth-reject) — see also
  `TRANSPORT_REFERENCE.md` §"How it is verified (the gates)".
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
| `README.md` | User-facing overview: `[SleipnirEvent]` + `IObservable<T>`, `SubscribeAsync`, "Server-push events" section (§"Features at a glance", §"Server-push events"); notes WS-only in v1, not chainable. |
| `README_DETAILS.md` | Deepest user reference: Server-Push Events (§"Server-Push Events (IObservable<T>)"), Event vs Call vs Stream table (§"Events vs. calls vs. streams"), authoring + wire (§"Server-Push Events (IObservable<T>)"), lifecycle/gap semantics (§"Server-Push Events (IObservable<T>)"), Resume/Last-Event-Id (§"Resume (Last-Event-Id) — resumable events"), Discovery (§"Discovery"), REST Events SSE (§"REST Events (SSE)"), 1.2.0 migration (§"Migration from 1.1.x"). |
| `PROTOCOL.md` | Wire spec: subscribe/unsubscribe/event/complete/error frames (§"Server-Push Events (Phase 3, experimental)"), ack + `replayedFrom` (§"Subscribe response (server → client)"), Resume (Last-Event-Id) (§"Resume (Last-Event-Id) — resumable events"), Discovery `kind:"event"` (§"Discovery"), REST Events SSE endpoints + wire mapping + 410 (§"REST Events (SSE)"), SignalR hub-streaming (§"Transport selection (client)"). |
| `STABILITY.md` | Stability contract: Phase 3 experimental, `[SleipnirEvent]` required marker (1.2.0), `EventBackpressureStrategy`/`EventBufferCapacity`, Phase S SSE experimental, Phase R resume experimental + in-process limitation (§"2. Experimental surface"). |
| `ROADMAP.md` | Phase 3 events, gap-semantics decision (at-most-once v1 vs resume Phase R), Phase S SSE delivered, SSE-vs-stream distinction (§"Phase 3 — Echtzeit-Kohärenz", §"Offene Design-Entscheidungen", §"Later (v1.x+, unsorted)"). |
| `CHANGELOG.md` | Phase S SSE entries (§"Added — Events over REST: SSE"), Phase R resume + configurable backpressure + `sleipnir.event.dropped` fix + `[SleipnirEvent]` required-marker change (§"[1.2.0]"). |
| `TRANSPORT_REFERENCE.md` | Client-side: cross-transport subscribe/resume, `ResumePolicy`/`ResumeDecision`, durable store pointer, WS-direction resume limitation (§6, §10). |
| `CODEGEN_REFERENCE.md` | `kind:"event"` discovery surface, events-not-chainable codegen rule. |
| `ERROR_CATALOG.md` | `SleipnirErrorCodes.ServiceUnavailable` → 503 / `ResourceExhausted` (§"1. Numeric codes (`SleipnirErrorCodes`)", §"2. Semantic categories (`SleipnirErrorCategory`)"); 499 = client closed. |
| `BEST_PRACTICES.md` | Streaming-vs-events guidance (§"2.2 `IAsyncEnumerable<T>` is materialized on the wire"). |
| `docs/stories/05-realtime-push-events.md` | Narrative: the chat-with-instant-messages problem, `[SleipnirEvent]` as first-class surface, wire frame types. |
| `docs/design/phase-3-events.md` | Design: events vs calls architecture, server surface, wire frames, transport story. |
| `guide/chapters/09-events.md` | Guide chapter 9: first `[SleipnirEvent]` (live BTC feed), `Resumable = true`, Svelte live chart + Blazor monitor + resume. |
| `guide/server/Controllers/PriceFeedController.cs` | The guide's runnable event controller. |

> **Note:** `samples/01-notification-chat` is a multi-transport sample
> (REST/WS/SignalR) but **does not exercise events** — its controllers use
> `[SleipnirMethod]` only. For a runnable event, use the guide's
> `PriceFeedController`.