# Chapter 9 — Eventing: a live BTC price feed (server push)

> **Goal:** the guide's first `[SleipnirEvent]`. A hosted `PriceFeedService` random-walks a BTC
> price on a timer and pushes `PriceTick`s through a hot `IObservable<T>`; the
> `PriceFeed.Ticks` event streams them to every client. The **Svelte portal** draws a live
> sparkline, the **Blazor admin** monitors a rolling tick table, and a dropped connection
> **resumes from `lastEventId`** — over WebSocket, SSE, or SignalR, all sharing one durable
> subscription store. This is the repo's first runnable event sample (`stories/05` has the
> doc, no project), and it is where "Sleipnir & REST — best friends" pays off: when the
> browser can't reach WebSocket, the feed transparently rides SSE.

Chapters 1–8 are request/response: the client asks, the server answers, the call ends. A
live price feed is the opposite — the server pushes, unprompted, ~once per second, for as
long as the client listens. Sleipnir models that as a method returning
`IObservable<T>` and decorated with `[SleipnirEvent]`; the framework turns that observable
into pushed frames on whichever transport the client is on.

## The shape: a method that returns `IObservable<T>`

An event is just a controller method with a different return contract — `IObservable<T>`
instead of `Task<T>` — and a different attribute:

```csharp
[SleipnirController("PriceFeed")]
public class PriceFeedController
{
    private readonly PriceFeedService _feed;
    public PriceFeedController(PriceFeedService feed) => _feed = feed;

    [SleipnirEvent("Ticks", Resumable = true)]
    [SleipnirDocumentation("Live price feed. Subscribe to a symbol …")]
    public IObservable<PriceTick> Ticks(string symbol) => _feed.GetStream(symbol);
}
```

Two things to notice, because both are load-bearing:

- **`IObservable<PriceTick>`, not `Task<IObservable<PriceTick>>`.** The invoker enforces this
  at registration — an event method yields the stream *immediately*; it does not asynchronously
  produce one. The framework subscribes to the returned observable once per durable
  subscription and pumps each `OnNext` as a frame.
- **`[SleipnirEvent]` is mutually exclusive with `[SleipnirMethod]`.** The method is no longer
  callable as a normal RPC call — it has no `Task<T>` result to return. Discovery tags it
  `kind: "event"` (with the element type) instead of `kind: "stream"`/`"ref"`, so the
  codegen emits a *subscribe* entry, not a *call* entry (more below).

`Resumable = true` opts in to the **durable** path: the subscription outlives the client
connection (up to `EventResumeTtl`, default 60 s) and, on reconnect, replays the missed
`eventId` tail from the framework's ring buffer before continuing live. No `Resumable` and
the subscription dies with the connection — fine for fire-and-forget, wrong for a price feed
where a dropped phone should not lose the gap.

## The one rule that makes resume work: return a *hot* singleton

`Resumable = true` only means something if the observable the method returns is the **same
long-lived instance** across calls. A *cold* observable — one that restarts per subscriber,
like a fresh `Channel` or a LINQ query — has no resume semantics: reconnecting re-runs it
from the start, the ring buffer references a dead source, and the gap is lost. The feed
therefore owns one `HotObservable<PriceTick>` **per symbol**, lazily created and reused:

```csharp
private readonly ConcurrentDictionary<string, HotObservable<PriceTick>> _streams = new(StringComparer.OrdinalIgnoreCase);

public HotObservable<PriceTick> GetStream(string symbol)
    => _streams.GetOrAdd(symbol.ToUpperInvariant(), _ => new HotObservable<PriceTick>());
```

`HotObservable<T>` is a minimal hot observable with no `System.Reactive` dependency — the
exact pattern from `SleipnirTests/Fixtures/ResumableEventFixture.cs`. It holds a list of
observers, snapshots them under a lock, and invokes `OnNext` **outside** the lock (so a
re-entrant `Subscribe`/`Dispose` inside an `OnNext` handler cannot deadlock):

```csharp
public void Push(T value)
{
    IObserver<T>[] snapshot;
    lock (_lock) snapshot = _observers.ToArray();
    foreach (var o in snapshot) o.OnNext(value);
}
```

There is deliberately **no replay buffer** inside `HotObservable<T>`. Resume/replay is the
framework's job (`SleipnirSubscriptionStore`'s ring buffer); the observable is just the live
tap. Keeping the buffer in one place — the framework, not the user's service — is what lets
the *same* store serve all three transports.

## The producer: an `IHostedService` with a timer

`PriceFeedService` is an `IHostedService` — it starts and stops with the host. A
`System.Threading.Timer` fires once per second; on each tick it random-walks every seeded
symbol's price (±0.2 %) and `Push`es a `PriceTick` to that symbol's stream:

```csharp
private void OnTick(object? state)
{
    // The admin-gated toggle: a stopped feed produces nothing — no sentinel ticks,
    // no nulls. In-flight subscriptions simply go quiet until the admin restarts it.
    if (!_control.IsRunning) return;

    foreach (var (symbol, stream) in _streams.ToArray())
    {
        if (!SeedPrices.ContainsKey(symbol)) continue;        // only seeded symbols walk
        var prev = _prices[symbol];
        var delta = (decimal)(_rng.NextDouble() * 0.004 - 0.002);
        var next = Math.Round(prev * (1m + delta), 2);
        _prices[symbol] = next;
        stream.Push(new PriceTick { Symbol = symbol, Price = next,
                                    Change = Math.Round(next - prev, 2), Time = DateTime.UtcNow });
    }
}
```

The `FeedControlService.IsRunning` flag is the same toggle the admin-only
`Portfolio.StartFeed` / `StopFeed` (chapter 8) flip. Keeping the control surface on
`Portfolio` — not on `PriceFeed` — means the feed has a single blessed *operator* surface:
the customer tier can **subscribe** (the feed is anonymous, no `[SleipnirAuthorise]`) but
cannot start or stop it. A stopped feed does not error; subscriptions stay open and resume
pushing when the admin restarts it.

### The DI registration that bit me

`PriceFeedService` is **both** an injectable dependency (the controller's ctor param) **and**
a hosted service (the timer). `AddHostedService<T>()` alone registers `T` as a hosted service
but does **not** register it as an injectable type — so `PriceFeedController(PriceFeedService
feed)` failed to resolve and the first SSE subscribe returned a generic
`500 "An internal error occurred while subscribing."`. The fix is the dual registration,
resolving the **same** singleton instance both ways:

```csharp
builder.Services.AddSingleton<PriceFeedService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PriceFeedService>());
```

One instance is now both the long-lived feed source the controller yields and the
`IHostedService` the host starts/stops. If your own event method's ctor param ever 500s on
subscribe, this is almost certainly it.

## The frame id is the framework's, not the payload's

`PriceTick` carries no id field — just `{ symbol, price, change, time }`. The `eventId` used
for `Last-Event-Id` resume is assigned by `SleipnirSubscriptionStore` (a monotonic
`Interlocked.Increment` per subscription), **not** by the payload. The wire frame is:

```json
{ "type":"event", "subscriptionId":"…", "eventId":3,
  "data":{ "symbol":"BTC", "price":60036.11, "change":-6.60, "time":"2026-08-20T17:26:04Z" } }
```

The `ack` frame (`type:"ack"`) lands **before** the first `event` frame and carries the
`subscriptionId` the client echoes back to resume. This invariant — ack-then-events — is what
lets the client capture the id for resume before any data arrives.

## Three transports, one store

The same `SleipnirSubscriptionStore` (a process-wide DI singleton) backs all three event
transports, which is why a `subscriptionId` created over one is resumable over another:

| Transport | Subscribe path | Resume path | Browser auth |
|-----------|---------------|-------------|--------------|
| **WebSocket** | `wss://…/sleipnirws` `kind:"subscribe"` frame | (intentionally deferred — needs a controller/method) | ❌ can't set `Authorization` |
| **SSE over REST** | `GET /api/sleipnir/events/{controller}/{method}?…` (fresh) | `GET /api/sleipnir/events/{subscriptionId}` + `Last-Event-Id:` header | ✅ REST header |
| **SignalR** | hub stream `IAsyncEnumerable<string>` (ack = first yielded item) | (hub-streaming) | ✅ hub auth |

This feed is **anonymous** (no `[SleipnirAuthorise]`), so the browser-WebSocket-auth
limitation from chapter 8 does not apply here — the portal can subscribe over `auto` (WS).
The moment you put `[SleipnirAuthorise]` on an event, the browser must fall back to REST + SSE
for it, exactly as it did for authed calls in chapter 8. That is "REST best friends" again:
SSE is the proxy-safe, browser-auth-friendly event path.

`Program.cs` turns the SignalR transport on for the chapter (opt-in, default `false`) so the
portal can `useTransport("signalr")` and the admin can exercise the hub path; `MessagePack`
gives the hub a binary wire:

```csharp
builder.Services.AddSleipnir(options =>
{
    options.UseSignalR = true;
    options.UseMessagePack = true;
    // …
});
```

## Discovery and the codegen: `kind: "event"`

Discovery emits the event method with `returnType.kind: "event"` and an `element` TypeRef —
the shape the codegen keys on to emit a *subscribe* entry rather than a *call* entry:

```json
{ "methodName":"Ticks",
  "returnType":{ "kind":"event", "element":{ "kind":"ref", "ref":"…PriceTick" } },
  "parameters":[{ "parameterName":"symbol", "parameterType":{ "kind":"scalar", "name":"string" } }] }
```

The TS emitter turns that into a `ticks(symbol, handlers)` method returning
`Promise<SleipnirSubscription>`; the C# emitter turns it into a `Ticks(Arg<string>)` method
returning a `Call` you feed to `Subscribe<T>`. Both are the *same* method, just shaped for
their language's subscribe API:

```ts
// generated TS (portal)
ticks(symbol: string, handlers: SubscribeHandlers<PriceTick>): Promise<SleipnirSubscription> {
  return this._subscribe<PriceTick>(
    this._build("PriceFeed", "Ticks").with({ symbol }).toRequest(), handlers);
}
```

```csharp
// generated C# (admin)
public Call Ticks(Arg<string> symbol)
    => SleipnirCall.Init("PriceFeed", "Ticks").With(symbol).ToCall();
// consumed as: await Sleipnir.Subscribe<PriceTick>(Sleipnir.PriceFeed.Ticks("BTC"));
```

`SubscribeHandlers<T>` is `{ onNext: (value: T) => void; onError?: (err: Error) => void;
onComplete?: () => void }` — `onNext` is required, the others optional. The returned
`SleipnirSubscription` carries `subscriptionId`, `lastEventId`, and an `unsubscribe()`.

## Tier 3 — the Svelte portal: a live sparkline + cross-transport resume

The portal subscribes, keeps a rolling 60-point price window, and draws an SVG sparkline.
A transport radio lets you watch the feed over `auto` (WS), `rest` (REST + SSE), or
`signalr`:

```ts
async function subscribeFeed() {
  if (feedSub) return;
  feedBusy = true; feedStatus = null;
  try {
    await client.useTransport(feedTransport);                 // pin the transport
    feedSub = await client.priceFeed.ticks("BTC", feedHandlers());
    feedSubId = feedSub.subscriptionId;
    feedLastEventId = feedSub.lastEventId ?? 0;
    feedStatus = `subscribed (${feedTransport}) · id ${feedSubId.slice(0, 8)}…`;
  } catch (e) {
    feedStatus = `subscribe failed: ${e instanceof Error ? e.message : String(e)}`;
    feedSub = null;
  } finally { feedBusy = false; }
}
```

`feedHandlers()` is the `{ onNext, onError?, onComplete? }` object the generated method
takes — `onNext` fires per tick on the client's pump, appends to the sparkline window, and
mirrors the framework's `lastEventId` for resume:

```ts
function feedHandlers() {
  return {
    onNext: (t) => {
      const price = t.price ?? 0, change = t.change ?? 0;     // wire fields are optional
      priceSeries = [...priceSeries, price].slice(-60);
      feedTicks = [...feedTicks, t].slice(-20);
      if (feedSub) feedLastEventId = feedSub.lastEventId ?? feedLastEventId;
    },
    onError: (e) => { feedStatus = `feed error: ${e.message}`; },
    onComplete: () => { feedStatus = "feed completed."; },
  };
}
```

### Cross-transport resume — and the one unsubscribe caveat

The headline demo: drop the connection, **resume over SSE from `lastEventId`**, and watch the
server replay the gap. Because `SleipnirSubscriptionStore` is process-wide, a `subscriptionId`
created over WS or SignalR is resumable over SSE. `dropAndResume` captures the id + cursor,
drops the handle, switches to REST, and calls the SSE backend's `resume`:

```ts
async function dropAndResume() {
  if (!feedSub) { resumeStatus = "Subscribe first."; return; }
  const subId = feedSub.subscriptionId;
  const cursor = feedSub.lastEventId ?? 0;
  feedSub.unsubscribe();
  feedSub = null;
  try {
    await client.useTransport("rest");
    const resumed = await client.sse!.resume(subId, cursor, feedHandlers());
    feedSub = resumed;
    // Same id → the server replayed the gap; a 410 throws before we get here.
    resumeStatus = `resumed over SSE · SAME id ${subId.slice(0, 8)}… · gap replayed from ${cursor}.`;
  } catch (e) {
    resumeStatus = `resume failed (410 = durable gone/destroyed): ${e.message}`;
  }
}
```

**The caveat to know.** A clean WS `unsubscribe()` sends a `kind:"unsubscribe"` frame which
**destroys** the durable subscription — the SSE resume then gets `410` and terminates (there
is no fresh fallback in a pure-resume call). To see a *real* gap-replay, subscribe over
`rest` (SSE) first: its unsubscribe just closes the HTTP stream, leaving the durable buffer
alive for 60 s. The chapter walks through both so the 410 is a taught outcome, not a mystery.

> Resuming **into** WebSocket is intentionally not supported — the WS resume frame would need
> to carry a controller/method, and the design punted on that. `SleipnirTransportRouter`
> throws `NotSupportedException` if you try; resume over SSE (REST) or SignalR instead. This is
> why `dropAndResume` pins `rest` before resuming.

## Tier 2 — the Blazor admin: `IObserver<T>` + a render-thread marshal

The admin subscribes to the same feed over its server-side `auto` (WS) path — the C#
`ClientWebSocket` *can* set `Authorization`, so it gets WS even authed (the admin is logged in
as `admin`). The generated event method returns a `Call`; `Subscribe<PriceTick>(call)` routes
it to the active event backend and returns a `SleipnirSubscription<PriceTick>` that is both
`IObservable<PriceTick>` and `IDisposable`:

```csharp
_subscription = await Sleipnir.Subscribe<PriceTick>(Sleipnir.PriceFeed.Ticks("BTC"));
observerToken = _subscription.Subscribe(new TickObserver(this));
```

`TickObserver` is a tiny `IObserver<PriceTick>` (no `System.Reactive` dependency) that
forwards to the page. The one Blazor-specific trap: `OnNext` fires on the **framework's pump
thread**, not the Blazor render thread, so the state mutation + `StateHasChanged` must be
marshaled onto the render thread via `InvokeAsync`:

```csharp
private void OnTick(PriceTick tick)
{
    _ = InvokeAsync(() =>
    {
        ticks.Add(tick);
        if (ticks.Count > 20) ticks.RemoveRange(0, ticks.Count - 20);   // rolling window
        StateHasChanged();
    });
}

private sealed class TickObserver : IObserver<PriceTick>
{
    private readonly Feed _page;
    public TickObserver(Feed page) => _page = page;
    public void OnNext(PriceTick value) => _page.OnTick(value);
    public void OnError(Exception error) => _page.OnMonitorError(error);
    public void OnCompleted() { /* terminal */ }
}
```

`Dispose` sends the server unsubscribe and detaches the observer. The same page also carries
the chapter-8 authed surface: `StartFeed` / `StopFeed` (admin-only) toggle the feed the
monitor is watching — start it, watch ticks arrive; stop it, watch the table go quiet; start
it again, watch it resume. The control and the monitor on one page makes the cause-and-effect
visible.

## Try it

```bash
# terminal 1 — the API (now with PriceFeed + SignalR)
dotnet run --project guide/server

# terminal 2 — the admin (Blazor Pflege-Backend): log in as admin/admin → Feed page
dotnet run --project guide/admin   # → https://localhost:5011/feed

# terminal 3 — the portal (Svelte)
cd guide/portal && npm run dev     # → http://localhost:5173  (Live feed section)
```

On the portal: the **Live feed** section's transport radio defaults to `auto` (WebSocket).
**Subscribe (BTC)** → the sparkline starts moving ~once per second. Switch the radio to
`rest` (REST + SSE) or `signalr` and subscribe again — same feed, different wire. **Drop &
resume** (subscribe over `rest` first for a real gap-replay) → the badge reads
`resumed over SSE · SAME id … · gap replayed from eventId N`. On the admin: log in as
`admin` / `admin` → **Subscribe (BTC)** on the Feed page → the rolling tick table fills;
**Start feed** / **Stop feed** toggle the producer and the table responds live.

### Verify the wire without a UI

```bash
# 1. Fresh SSE subscribe — ack frame, then ~1 event/s (feed must be running; admin starts it,
#    and FeedControlService defaults IsRunning = true so this works out of the box)
curl -sk -N --max-time 5 "https://localhost:5010/api/sleipnir/events/PriceFeed/Ticks?symbol=BTC"
# id: 0
# event: ack
# data: {"subscriptionId":"f9ac71c77c504cdab3cecd54ff82f32a"}
#
# id: 1
# event: event
# data: {"type":"event","subscriptionId":"f9ac71c7…","eventId":1,
#        "data":{"symbol":"BTC","price":60042.71,"change":42.71,"time":"2026-08-20T17:26:03Z"}}

# 2. Capture the subscriptionId + last eventId, drop the curl (Ctrl-C), wait a few seconds,
#    then resume — same id, replayedFrom = lastEventId + 1, then live ticks continue:
curl -sk -N "https://localhost:5010/api/sleipnir/events/<subscriptionId>" \
  -H "Last-Event-Id: <lastEventId>"
# id: 0
# event: ack
# data: {"subscriptionId":"<SAME id>","replayedFrom":5}
#
# id: 5
# event: event
# data: {"type":"event","subscriptionId":"<SAME id>","eventId":5,"data":{…}}
```

The `replayedFrom` field in the resume ack is the whole feature in one token: the server
acknowledged your `Last-Event-Id`, found the durable buffer, and is replaying everything
after it before going live. A `410` instead means the durable subscription was destroyed (a
clean WS unsubscribe) or expired past `EventResumeTtl` — re-subscribe fresh.

## The contract loop, one more time

The event is part of the contract, so the loop from chapter 2 applies: change
`PriceFeedController` → rebuild the server (`SLEIPNIR_REGEN_GOLDEN=1 dotnet build
guide/server` regenerates `contract.sleipnir.json` and drift-fails the build if the committed
copy diverges) → rebuild the admin (the Roslyn source generator regenerates
`SleipnirGenerated.cs` with the `Ticks` event entry + `Subscribe<T>`) → `npm run gen` in the
portal (regenerates `priceFeed.ticks`). One source of truth, three clients.

---

**Next:** [Chapter 10 — Production: interceptors, observability, binary](10-production.md).
The app runs; this last chapter shapes it for production — an interceptor pipeline that logs
every call, the opt-in `/metrics` (Prometheus) and `/observability` (JSON) endpoints, the
`Sleipnir` `ActivitySource` for distributed tracing, and `byte[]` /
`SleipnirResponse.Content` for binary payloads.