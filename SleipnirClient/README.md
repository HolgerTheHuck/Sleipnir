# Sleipnir.Client

C# client for [Sleipnir](../README.md) — the code-first, multi-transport framework for
command-oriented web APIs on .NET 8+. Four backends share one `ISleipnirClient`
surface (REST, WebSocket, SSE, SignalR), and `SleipnirTransportRouter` bundles them
into one unified client that selects the transport at runtime.

- **Backends:** `SleipnirRestJsonClient` (`HttpClient`, `IDisposable`, calls only),
  `SleipnirWebSocketClient` (persistent `ClientWebSocket`, auto-reconnect, `IAsyncDisposable`, calls + events),
  `SleipnirSseClient` (events over `text/event-stream`, `Last-Event-Id` resume, `IAsyncDisposable`),
  `SleipnirSignalrClient` (SignalR hub, MessagePack, `IAsyncDisposable`; calls via `DoWork`/`DoWorkMany`, events via hub-streaming).
- **Unified router:** `SleipnirTransportRouter` bundles the backends for a capability
  (`rest|ws|all|signalr`); `auto` (default) probes WebSocket and falls back to REST+SSE.
- **API:** fluent `SleipnirCall` builder **and** raw `SleipnirRequest`.
- **Events:** `SubscribeAsync<T>` / `ResumeAsync<T>` on `ISleipnirClient` — cross-transport
  durable subscriptions (resume by id + `lastEventId` across SSE/SignalR; resuming into
  WebSocket throws — a WS resume frame needs the original controller/method).
- **Batching + dependency chaining** in a single roundtrip (resolved server-side).
- **Binary:** `byte[]` results via `CallBinary`; `byte[]` parameters via
  `request.BinaryData` (no `WithBinary` on the builder yet — see Known limitations).
- **Cancellation** via `CancellationToken` on every call.

The wire format is specified in [PROTOCOL.md](../PROTOCOL.md). Runnable examples
live in [`samples/csharp/`](../samples/csharp).

## Install

```xml
<PackageReference Include="Sleipnir.Client" Version="1.4.3" />
```

The package targets `net8.0` (runs on .NET 8+). It brings `MessagePack` and the
SignalR client transitively for the SignalR transport.

## Quick start

```csharp
using SleipnirClient.Sleipnir;
using SleipnirCommon.Exceptions;
using SleipnirCommon.Models;
using SleipnirCommon.Results;
using System.Text.Json;

// REST (HttpClient-based, IDisposable)
using var rest = new SleipnirRestJsonClient("https://localhost:5001");

// Fluent builder → request
var req = SleipnirCall.Init("Customer", "GetById")
    .Param("id", 42)
    .ToRequest();

// Typed, throws on non-2xx
var customer = await rest.Call<Customer>(req);

// Raw response (does not throw on logical non-2xx)
var raw = await rest.Call(req);
if (raw is { IsSuccess: false })
    Console.Error.WriteLine(raw.Error?.Message);
```

## The fluent builder

`SleipnirCall` produces a `SleipnirRequest`. Parameters are matched **by name** on the
server (`.Param("name", value)`); `.With(args)` / `.Add(arg)` bind positionally as a
fallback. JSON serialization of parameter values is the builder's job — you pass
live objects.

```csharp
var create = SleipnirCall.Init("Customer", "Create")
    .Param("name", "Alice")
    .Param("email", "alice@x.com")
    .Named("step1")           // Request id (default: "Controller.Method")
    .Exposes("$", "newId")    // Result path → alias (result-relative, no $.data)
    .ToRequest();

int newId = await rest.Call<int>(create);
```

`Exposes(jsonPath, alias)` — the JSON path is **result-relative**: `$` is the whole
serialized result (e.g. an `int` or a `Customer`), `$.Id` a property, `$[0].Id` the
first list element, and `$[*].Id` **every** element. There is no `$.data` envelope
level. A multi-match path (wildcard `$[*]`, recursive `$..`) collects all matches into
a JSON array, injected as a single list-typed parameter — see List fan-out below.

`WithAlias("@alias")` — consumes a previously exposed value. The server resolves
the `@alias` placeholder before invoking the method. Unresolved aliases fail the
call (no implicit fallback in v1).

## REST client

```csharp
using var rest = new SleipnirRestJsonClient(
    "https://localhost:5001",
    httpClient: null,            // null => own HttpClient (PooledConnectionLifetime 2 min)
    apiPath: "api/sleipnir",       // default
    httpClientTimeout: TimeSpan.FromSeconds(30));

// Your own HttpClient, e.g. for auth headers / HttpClientFactory:
var http = new HttpClient { BaseAddress = new Uri("https://localhost:5001") };
http.DefaultRequestHeaders.Authorization = new("Bearer", token);
using var rest2 = new SleipnirRestJsonClient("https://localhost:5001", http);
```

Methods (from `ISleipnirClient`):
- `Call(SleipnirRequest)` → `SleipnirResponse?` (raw; non-2xx returned, not thrown).
- `Call<T>(SleipnirRequest?)` → `T?` (deserializes `data`; throws `SleipnirException` on non-2xx).
- `Call(SleipnirMultiRequest?)` → `IEnumerable<SleipnirResponse?>?` (batch).
- `CallBinary(SleipnirRequest?)` → `byte[]?` (the `content` field; throws on non-2xx).

`SleipnirRestJsonClient` is `IDisposable` — dispose it (and the owned `HttpClient`)
when done.

## WebSocket client

```csharp
await using var ws = new SleipnirWebSocketClient(
    "https://localhost:5001",   // Base URL; scheme is mapped (https→wss, http→ws)
    wsPath: "sleipnirws",          // default
    callTimeout: TimeSpan.FromSeconds(10),
    autoReconnect: true);       // default; an empty reconnectDelays disables it

await ws.ConnectAsync();        // called implicitly by Call() (concurrent-safe)
var customer = await ws.Call<Customer>(req);
// await using disposes asynchronously at end of block (IAsyncDisposable);
// an early ws.DisposeAsync() rejects all pending calls (terminal, no reconnect).
```

Correlation: each response is matched by `id` (single) or the first request's `id`
(batch). Responses with no matching pending call are logged and dropped (no
misdelivery). Auto-reconnect mirrors SignalR's backoff (2,2,5,5,10,10,30,30s,
1,1,5 min); in-flight calls are rejected on drop, new calls during reconnect wait
on the same in-flight connect.

Auth: the C# WS client has no `bearer` parameter — set headers on a `ClientWebSocket`
you inject via the `webSocket`/`socketFactory` ctor argument (e.g.
`webSocket.Options.SetRequestHeader("Authorization", $"Bearer {token}")`).

`SleipnirWebSocketClient` is `IAsyncDisposable` — `await using var ws = ...` is the
preferred pattern.

## SignalR client

```csharp
await using var signalr = new SleipnirSignalrClient(
    "https://localhost:5001",
    bearer: "eyJ...",            // JWT; AccessTokenProvider resolved lazily per call
    hubPath: "sleipnirhub",            // default
    useMessagePack: true);      // default; native bin instead of base64

var customer = await signalr.Call<Customer>(req);
```

SignalR carries `byte[]` natively (MessagePack `bin`) — no base64. Auto-reconnect
uses SignalR's built-in mechanism with the same backoff schedule. `SleipnirSignalrClient`
is `IAsyncDisposable`.

## Transport router

`SleipnirTransportRouter` bundles the backends for a capability and selects the transport
at runtime — the generated typed client wraps one. You can also use it directly:

```csharp
await using var router = new SleipnirTransportRouter(
    new SleipnirRouterOptions
    {
        BaseUrl = "https://localhost:5001",
        Capability = SleipnirBundleCapability.All,   // rest | ws | all | signalr
    });

// auto (default) probes WebSocket once on first use, falls back to REST+SSE on failure.
var customer = await router.Call<Customer>(req);

// Switch explicitly at runtime:
await router.UseTransportAsync(SleipnirTransport.Ws);

// Escape hatches reach the raw bundled backend (null when not bundled by the capability):
SleipnirRestJsonClient? rest = router.Rest;
SleipnirWebSocketClient? ws = router.Ws;
SleipnirSseClient? sse = router.Sse;
SleipnirSignalrClient? signalr = router.Signalr;
```

`auto` probes WebSocket once on first use (default 1500 ms, set via `ProbeTimeout`) and
reuses the probe socket as the live connection; on failure it falls back to REST (calls)
+ SSE (events). `ws` has no fallback — a probe failure surfaces the error. The `rest` /
`ws` / `sse` / `signalr` escape hatches are `null` when the capability did not bundle that
backend. `SetBearer` fans the token out to every bundled backend that accepts one.

## Events

Event methods return `IObservable<T>`. Subscribe through any backend that supports
events, or through the router:

```csharp
var sub = await router.SubscribeAsync<Message>(
    SleipnirCall.Init("Chat", "OnMessage").ToRequest());

// SleipnirSubscription<T> is IObservable<T> + IDisposable.
var subscription = sub.Subscribe(msg => Console.WriteLine(msg.Text));
// dispose sub to unsubscribe on the server.
```

- **WebSocket / SignalR** — the request is passed straight through; the server streams
  matching events.
- **SSE** — the router unpacks the request into `GET /events/{controller}/{method}`
  (then `/events/{subscriptionId}` on resume).
- **Durable, cross-transport resume** — subscriptions live in a process-wide store shared
  across transports. `ResumeAsync<T>(subscriptionId, lastEventId, ...)` replays the buffer
  tail then goes live over whatever transport is active. Resuming *into* WebSocket throws
  `NotSupportedException` (a WS resume frame would need the original controller/method) —
  switch to the `Rest` profile via `UseTransportAsync(SleipnirTransport.Rest)` to resume
  over SSE.

## Batches and dependency chaining

Batches use `SleipnirMultiRequest` — there is no `SleipnirCall.batch` helper in C# (unlike
the TS client). Build the list and pick an `ExecutionMode`:

```csharp
var batch = new SleipnirMultiRequest
{
    Requests = new()
    {
        SleipnirCall.Init("Customer", "Create")
            .Param("name", "Alice")
            .Named("step1")
            .Exposes("$", "newId")        // forward the new id
            .ToRequest(),
        SleipnirCall.Init("Customer", "GetById")
            .WithAlias("@newId")          // server replaces @newId before the call
            .Named("step2")
            .ToRequest(),
    },
    Mode = ExecutionMode.Serial,         // Serial => @alias resolution between calls
};

var responses = (await rest.Call(batch))!.ToList();
var newId = responses[0]!.Data?.Deserialize<int>();
var loaded = responses[1]!.Data?.Deserialize<Customer>();
```

- `ExecutionMode.Parallel` — `Task.WhenAll`, no alias resolution.
- `ExecutionMode.Serial` — sequential, resolves `@alias` against prior responses.
- If any request carries `dependencyMapping`, the server auto-switches to topological
  batch execution regardless of `Mode`.

## List fan-out (multi-match `@alias`)

A wildcard or recursive JsonPath (`$[*].Id`, `$..Id`) matches multiple nodes. Sleipnir
collects **all** matches into a JSON array and injects it as one list-typed parameter
value — so `Search → GetByIds` runs in a single roundtrip, no client-side glue:

```csharp
// Search returns List<Customer>; $[*].Id collects every id → array.
// GetByIds(int[] ids) receives the array as @customerIds.
var batch = new SleipnirMultiRequest
{
    Requests = new()
    {
        SleipnirCall.Init("Customer", "Search")
            .With("France")
            .Named("search")
            .Exposes("$[*].Id", "customerIds")   // alle Ids als Array
            .ToRequest(),
        SleipnirCall.Init("Customer", "GetByIds")
            .WithAlias("@customerIds")            // the parameter is named customerIds
            .Named("load")
            .ToRequest(),
    },
    Mode = ExecutionMode.Serial,
};

var responses = (await rest.Call(batch))!.ToList();
var loaded = responses[1]!.Data?.Deserialize<List<Customer>>();
```

This is one value per alias — an array, not N separate calls. The consuming parameter
must be list-typed (`List<T>`, `T[]`, `IEnumerable<T>`); a scalar parameter won't
deserialize from an array. The `WithAlias("@customerIds")` derives the parameter name
`customerIds`, so the target method must have a parameter of that exact name.

## Binary

`byte[]` results come back in the response `content` field:

```csharp
var dl = SleipnirCall.Init("Document", "Download").Param("id", 7).ToRequest();
byte[]? bytes = await rest.CallBinary(dl);   // null for empty content
```

`byte[]` **parameters** are fed from `SleipnirRequest.BinaryData` (injected into the
first `byte[]` parameter server-side). The fluent builder has no `WithBinary` yet —
set it directly:

```csharp
var up = SleipnirCall.Init("Document", "Upload").Param("name", "x.bin").ToRequest();
up.BinaryData = File.ReadAllBytes("x.bin");
await rest.Call(up);
```

Wire encoding depends on the transport: base64 over REST and WebSocket (JSON),
native MessagePack `bin` over SignalR. See [Binary](../README.md#binary) and
[PROTOCOL.md](../PROTOCOL.md). Limits: REST 1 MB body, WebSocket 1 MB/message
(hardcoded).

## Errors

`Call<T>` / `CallBinary` throw `SleipnirException` on non-2xx; `Call(SleipnirRequest)`
returns the `SleipnirResponse` with `.Error` populated (non-throwing) so you can
inspect status yourself:

```csharp
try { await rest.Call<Customer>(req); }
catch (SleipnirException ex)
{
    Console.Error.WriteLine($"{ex.Error?.Code} {ex.Error?.Message} (request {ex.Error?.RequestId})");
}
```

Transport-level failures (connection, parse) also surface as `SleipnirException`. A
`CancellationToken` canceling mid-call propagates as `OperationCanceledException`
(not wrapped).

## Cancellation

Every call takes a `CancellationToken`:

```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
var customer = await rest.Call<Customer>(req, cts.Token);
```

The WebSocket client additionally links its `callTimeout` with the caller's token.

## Known limitations

- **No `WithBinary` on `SleipnirCall`.** Set `request.BinaryData` directly for binary
  uploads (tracked in [ROADMAP.md](../ROADMAP.md)).
- **`byte[]` parameters bind first-match-only.** A method with more than one
  `byte[]` parameter receives `binaryData` in only the first.
- **Batch `id` collisions.** Concurrent batches whose first request shares the same
  `id` (default `Controller.Method`) collide on correlation. Set explicit, unique
  `id`s via `.Named(...)` for concurrent batches (same constraint as the TS client).
- **WebSocket auth has no ctor convenience.** Inject a pre-configured
  `ClientWebSocket` (or `socketFactory`) with the header set.
- **`IAsyncEnumerable<T>` call results are materialized.** A method returning
  `IAsyncEnumerable<T>` is consumed server-side into a JSON array; the client receives the
  array, not a live stream. (Event *subscriptions* are separate and DO stream — over
  WebSocket, SSE, or SignalR hub-streaming `SubscribeAsync`; see Events above.)

## License

MIT.