# Trame.Client

C# client for [Trame](../README.md) — the code-first, multi-transport framework for
command-oriented web APIs on .NET 8+. Three transports share one `ITrameClient`
surface, so the same call runs over REST, WebSocket, or SignalR.

- **Transports:** `TrameRestJsonClient` (`HttpClient`, `IDisposable`),
  `TrameWebSocketClient` (persistent `ClientWebSocket`, auto-reconnect, `IAsyncDisposable`),
  `TrameSignalrClient` (SignalR hub, MessagePack, `IAsyncDisposable`).
- **API:** fluent `TrameCall` builder **and** raw `TrameRequest`.
- **Batching + dependency chaining** in a single roundtrip (resolved server-side).
- **Binary:** `byte[]` results via `CallBinary`; `byte[]` parameters via
  `request.BinaryData` (no `WithBinary` on the builder yet — see Known limitations).
- **Cancellation** via `CancellationToken` on every call.

The wire format is specified in [PROTOCOL.md](../PROTOCOL.md). Runnable examples
live in [`samples/csharp/`](../samples/csharp).

## Install

```xml
<PackageReference Include="Trame.Client" Version="1.0.0" />
```

The package targets `net8.0` (runs on .NET 8+). It brings `MessagePack` and the
SignalR client transitively for the SignalR transport.

## Quick start

```csharp
using TrameClient.Trame;
using TrameCommon.Exceptions;
using TrameCommon.Models;
using TrameCommon.Results;
using System.Text.Json;

// REST (HttpClient-basiert, IDisposable)
using var rest = new TrameRestJsonClient("https://localhost:5001");

// Fluent Builder → Request
var req = TrameCall.Init("Customer", "GetById")
    .Param("id", 42)
    .ToRequest();

// Getypt & werfend bei Nicht-2xx
var customer = await rest.Call<Customer>(req);

// Rohe Response (wirft nicht bei logischem Nicht-2xx)
var raw = await rest.Call(req);
if (raw is { IsSuccess: false })
    Console.Error.WriteLine(raw.Error?.Message);
```

## The fluent builder

`TrameCall` produces a `TrameRequest`. Parameters are matched **by name** on the
server (`.Param("name", value)`); `.With(args)` / `.Add(arg)` bind positionally as a
fallback. JSON serialization of parameter values is the builder's job — you pass
live objects.

```csharp
var create = TrameCall.Init("Customer", "Create")
    .Param("name", "Alice")
    .Param("email", "alice@x.com")
    .Named("step1")           // Request-Id (Default: "Controller.Method")
    .Exposes("$", "newId")    // Ergebnis-Pfad → Alias (ergebnisrelativ, kein $.data)
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
using var rest = new TrameRestJsonClient(
    "https://localhost:5001",
    httpClient: null,            // null => eigener HttpClient (PooledConnectionLifetime 2 min)
    apiPath: "api/trame",       // Default
    httpClientTimeout: TimeSpan.FromSeconds(30));

// Eigener HttpClient z. B. für Auth-Header / HttpClientFactory:
var http = new HttpClient { BaseAddress = new Uri("https://localhost:5001") };
http.DefaultRequestHeaders.Authorization = new("Bearer", token);
using var rest2 = new TrameRestJsonClient("https://localhost:5001", http);
```

Methods (from `ITrameClient`):
- `Call(TrameRequest)` → `TrameResponse?` (raw; non-2xx returned, not thrown).
- `Call<T>(TrameRequest?)` → `T?` (deserializes `data`; throws `TrameException` on non-2xx).
- `Call(TrameMultiRequest?)` → `IEnumerable<TrameResponse?>?` (batch).
- `CallBinary(TrameRequest?)` → `byte[]?` (the `content` field; throws on non-2xx).

`TrameRestJsonClient` is `IDisposable` — dispose it (and the owned `HttpClient`)
when done.

## WebSocket client

```csharp
await using var ws = new TrameWebSocketClient(
    "https://localhost:5001",   // Basis-URL; Schema wird gemappt (https→wss, http→ws)
    wsPath: "tramews",          // Default
    callTimeout: TimeSpan.FromSeconds(10),
    autoReconnect: true);       // Default; leeres reconnectDelays schaltet es aus

await ws.ConnectAsync();        // wird von Call() implizit aufgerufen (concurrent-safe)
var customer = await ws.Call<Customer>(req);
// await using entsorgt am Blockende asynchron (IAsyncDisposable);
// ein vorzeitiges ws.DisposeAsync() lehnt alle pending Calls ab (terminal, kein Reconnect).
```

Correlation: each response is matched by `id` (single) or the first request's `id`
(batch). Responses with no matching pending call are logged and dropped (no
misdelivery). Auto-reconnect mirrors SignalR's backoff (2,2,5,5,10,10,30,30s,
1,1,5 min); in-flight calls are rejected on drop, new calls during reconnect wait
on the same in-flight connect.

Auth: the C# WS client has no `bearer` parameter — set headers on a `ClientWebSocket`
you inject via the `webSocket`/`socketFactory` ctor argument (e.g.
`webSocket.Options.SetRequestHeader("Authorization", $"Bearer {token}")`).

`TrameWebSocketClient` is `IAsyncDisposable` — `await using var ws = ...` is the
preferred pattern.

## SignalR client

```csharp
await using var signalr = new TrameSignalrClient(
    "https://localhost:5001",
    bearer: "eyJ...",            // JWT; AccessTokenProvider lazy pro Call
    hubPath: "tramehub",            // Default
    useMessagePack: true);      // Default; native bin statt Base64

var customer = await signalr.Call<Customer>(req);
```

SignalR carries `byte[]` natively (MessagePack `bin`) — no base64. Auto-reconnect
uses SignalR's built-in mechanism with the same backoff schedule. `TrameSignalrClient`
is `IAsyncDisposable`.

## Batches and dependency chaining

Batches use `TrameMultiRequest` — there is no `TrameCall.batch` helper in C# (unlike
the TS client). Build the list and pick an `ExecutionMode`:

```csharp
var batch = new TrameMultiRequest
{
    Requests = new()
    {
        TrameCall.Init("Customer", "Create")
            .Param("name", "Alice")
            .Named("step1")
            .Exposes("$", "newId")        // neue Id weitergeben
            .ToRequest(),
        TrameCall.Init("Customer", "GetById")
            .WithAlias("@newId")          // Server ersetzt @newId vor dem Call
            .Named("step2")
            .ToRequest(),
    },
    Mode = ExecutionMode.Serial,         // Serial => @alias-Auflösung zwischen Calls
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

A wildcard or recursive JsonPath (`$[*].Id`, `$..Id`) matches multiple nodes. Trame
collects **all** matches into a JSON array and injects it as one list-typed parameter
value — so `Search → GetByIds` runs in a single roundtrip, no client-side glue:

```csharp
// Search gibt List<Customer>; $[*].Id sammelt jede Id → Array.
// GetByIds(int[] ids) empfängt das Array als @customerIds.
var batch = new TrameMultiRequest
{
    Requests = new()
    {
        TrameCall.Init("Customer", "Search")
            .With("France")
            .Named("search")
            .Exposes("$[*].Id", "customerIds")   // alle Ids als Array
            .ToRequest(),
        TrameCall.Init("Customer", "GetByIds")
            .WithAlias("@customerIds")            // Parameter heißt customerIds
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
var dl = TrameCall.Init("Document", "Download").Param("id", 7).ToRequest();
byte[]? bytes = await rest.CallBinary(dl);   // null bei leerem Content
```

`byte[]` **parameters** are fed from `TrameRequest.BinaryData` (injected into the
first `byte[]` parameter server-side). The fluent builder has no `WithBinary` yet —
set it directly:

```csharp
var up = TrameCall.Init("Document", "Upload").Param("name", "x.bin").ToRequest();
up.BinaryData = File.ReadAllBytes("x.bin");
await rest.Call(up);
```

Wire encoding depends on the transport: base64 over REST and WebSocket (JSON),
native MessagePack `bin` over SignalR. See [Binary](../README.md#binary) and
[PROTOCOL.md](../PROTOCOL.md). Limits: REST 1 MB body, WebSocket 1 MB/message
(hardcoded).

## Errors

`Call<T>` / `CallBinary` throw `TrameException` on non-2xx; `Call(TrameRequest)`
returns the `TrameResponse` with `.Error` populated (non-throwing) so you can
inspect status yourself:

```csharp
try { await rest.Call<Customer>(req); }
catch (TrameException ex)
{
    Console.Error.WriteLine($"{ex.Error?.Code} {ex.Error?.Message} (request {ex.Error?.RequestId})");
}
```

Transport-level failures (connection, parse) also surface as `TrameException`. A
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

- **No `WithBinary` on `TrameCall`.** Set `request.BinaryData` directly for binary
  uploads (tracked in [ROADMAP.md](../ROADMAP.md)).
- **`byte[]` parameters bind first-match-only.** A method with more than one
  `byte[]` parameter receives `binaryData` in only the first.
- **Batch `id` collisions.** Concurrent batches whose first request shares the same
  `id` (default `Controller.Method`) collide on correlation. Set explicit, unique
  `id`s via `.Named(...)` for concurrent batches (same constraint as the TS client).
- **WebSocket auth has no ctor convenience.** Inject a pre-configured
  `ClientWebSocket` (or `socketFactory`) with the header set.
- **No SignalR streaming surface.** `IAsyncEnumerable<T>` results are materialized
  server-side into a JSON array; the client receives the array, not a live stream.

## License

MIT.