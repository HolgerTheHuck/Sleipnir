# Story 05 — Realtime Push Events

> **A chat where new messages appear instantly on every connected client — no polling, no
> client-side orchestration, no separate SignalR hub. Sleipnir's `[SleipnirEvent]` turns a controller
> method into a server-pushed subscription, on the same WebSocket that carries the calls.**

## The problem

Stories 01–04 covered request/response: a client calls, the server answers. But the
notification-chat domain that Sleipnir's samples are built around has a second shape —
**server→client push**. A new chat message, a notification arriving, a participant typing: the
server knows first, and every connected client needs to see it *immediately*, without polling.

Before 1.1.0, Sleipnir had no answer for this. You'd fall back to raw SignalR (`IHubContext`,
manual hub methods, a separate contract outside `[SleipnirController]`) — splitting your codebase
into "Sleipnir for calls" and "SignalR for push", with separate typing, separate auth, separate
observability. Or you'd poll `GetMessages` every few seconds — latency, wasted requests.

## The solution: `[SleipnirEvent]` — push as a first-class surface

1.1.0 adds **server-pushed events** alongside calls. A controller method marked with
`[SleipnirEvent]` returns an `IObservable<T>`; the server subscribes and pushes every `T` as an
event frame over the WebSocket. The client subscribes and gets an `IObservable<T>` back —
typed, on the same connection, with the same auth and discovery.

```csharp
[SleipnirController("Chat")]
public class ChatController(IChatService service)
{
    [SleipnirMethod("SendMessage")]
    public Task<Message> SendMessage(int chatId, string sender, string text, CancellationToken ct)
        => service.SendAsync(chatId, sender, text, ct);

    // NEW: a subscription, not a call. Returns IObservable<Message>; the server
    // pushes every new message for this chatId to every subscribed client.
    [SleipnirEvent("MessageReceived")]
    public IObservable<Message> Subscribe(int chatId, CancellationToken ct)
        => service.SubscribeMessages(chatId, ct);
}
```

The client subscribes — and gets a typed `IObservable<Message>`:

```csharp
await using var ws = new SleipnirWebSocketClient("https://localhost:5001");
await ws.ConnectAsync();

var subscription = await ws.SubscribeAsync<Message>("Chat", "MessageReceived", args: [42]);
subscription.Subscribe(message =>
{
    Console.WriteLine($"New message from {message.Sender}: {message.Text}");
});
// Every new message in chat 42 is pushed — no polling, no client-side loop.
```

## The wire: a separate frame type, not a response

Subscribe/Unsubscribe are requests (`kind:"subscribe"` / `kind:"unsubscribe"`); the server
responds with a `subscriptionId`. Events are a **separate frame type** — not a `SleipnirResponse`:

```json
// Client → Server: subscribe
{"kind":"subscribe","controller":"Chat","method":"MessageReceived","params":[{"parameterName":"chatId","data":42}],"id":"sub-1"}

// Server → Client: subscribe response (carries subscriptionId)
{"code":200,"data":{"subscriptionId":"a1b2c3..."},"id":"sub-1"}

// Server → Client: event frame (pushed, no request)
{"type":"event","subscriptionId":"a1b2c3...","eventId":1,"data":{"id":99,"sender":"Alice","text":"Hi!"}}

// Server → Client: complete (IObservable.OnCompleted)
{"type":"complete","subscriptionId":"a1b2c3..."}

// Client → Server: unsubscribe
{"kind":"unsubscribe","subscriptionId":"a1b2c3...","id":"unsub-1"}
```

Calls (without `kind`) are unchanged — 1.0.0 clients keep working.

## Lifecycle: subscribe, push, reconnect, unsubscribe

- **Subscribe** → server returns `subscriptionId`; client holds it.
- **Push** → every `IObservable.OnNext` becomes an event frame; `OnCompleted` → `complete`
  frame; `OnError` → `error` frame.
- **Reconnect** → the client's auto-reconnect re-subscribes automatically with the same
  parameters (new `subscriptionId`, since the connection is new). Events during the disconnect
  gap are lost — **at-most-once-while-disconnected** (documented; `Last-Event-Id`-resume is
  v1.x+).
- **Unsubscribe** → client sends `{kind:"unsubscribe",subscriptionId}`; server disposes the
  `IObservable` subscription.
- **Disconnect** → server auto-cleans all subscriptions for that connection.

## Backpressure: bounded buffer + drop-oldest

If the client is slow (bad connection), the server buffers per subscription (bounded, default
100). When full, the oldest event is dropped and `sleipnir.event.dropped` is incremented —
deterministic, DoS-safe. No blocking, no disconnect.

## Auth, discovery, codegen — same as calls

- **Auth**: the subscribe request runs through the same `[SleipnirAuthorise]` / `RequireAuthentication`
  / `[SleipnirAnonymous]` path as any call (Phase 1 interceptor pipeline). Auth at subscribe time;
  re-check on reconnect is v1.x+.
- **Discovery**: `IObservable<T>` is declared as `kind:"event"` (analog to `kind:"stream"` for
  `IAsyncEnumerable<T>`). The element type is in `ReturnType.Element`.
- **Codegen**: the C# emitter recognizes `kind:"event"` and emits `SubscribeAsync<T>` instead of
  a `Call` — typed subscriptions from discovery.

## What's not in 1.1.0

- **SignalR events** — WS-only in v1; SignalR follows.
- **REST events** — no long-polling; REST clients poll or use WS.
- **`Last-Event-Id` resume** — gap-accepting (at-most-once) in v1; resume is v1.x+.
- **Bidirectional streaming** (client→server push) — separate model, not in 1.1.0.

## Try it

The `samples/01-notification-chat` sample demonstrates the full Sleipnir surface — calls, batches,
chaining, and (with 1.1.0) events. See `samples/01-notification-chat/server/Controllers/` for
the controllers and `samples/01-notification-chat/web/` for the Svelte SPA client.

Full design: [`docs/design/phase-3-events.md`](../design/phase-3-events.md) (8 decisions).
Stability: [`STABILITY.md`](../../STABILITY.md) §2 (experimental).