# Story 03 — The Same Contract, Three Wires

> **The contract is the C# classes. The transport is a deployment detail. One domain, three
> transports — REST, WebSocket, SignalR — expose the same controllers simultaneously. Identical
> call, identical result, three wires.**

Sleipnir is code-first: the C# classes decorated with `[SleipnirController]`/`[SleipnirMethod]` *are*
the contract. The transport is not part of the contract — it's how you deliver a call. This story
runs all three transports against the **same** controllers in one server, and shows the
**same** call coming back over REST, WebSocket, and SignalR.

## Run it (F5 → DevUI)

1. Open **`Story03.sln`** in Visual Studio (or `dotnet build && dotnet run --project Story03.csproj`).
2. Press **F5**. The browser opens at **`http://localhost:5003/Sleipnir`** — the DevUI (REST wire).
3. One controller, `Greeter`, with `Greet`, `Add`, `Echo`. All three wires are live on the same
   server: REST (`/api/sleipnir/json`), WebSocket (`/sleipnirws`), SignalR (`/sleipnirhub`, MessagePack).

Call `Greeter.Greet("World")` in the DevUI:

```
POST http://localhost:5003/api/sleipnir/json
{
  "controller": "Greeter",
  "method": "Greet",
  "params": [{ "parameterName": "name", "data": "World" }],
  "id": "q1"
}
```

## Three wires, one call, one result

All three clients implement `ISleipnirClient`. The call is identical; only the constructor differs.
This project references `SleipnirClient` so the snippets compile as-is.

```csharp
using SleipnirClient.Sleipnir;
using SleipnirCommon.Models;

var request = SleipnirCall.Init("Greeter", "Greet").With("World").ToRequest();

// 1) REST — POST /api/sleipnir/json
await using var rest = new SleipnirRestJsonClient("http://localhost:5003");
var r1 = (await rest.Call<Greeting>(request))!;   // { message: "Hello, World!", count: 1 }

// 2) WebSocket — ws://localhost:5003/sleipnirws
await using var ws = new SleipnirWebSocketClient("http://localhost:5003");
var r2 = (await ws.Call<Greeting>(request))!;     // { message: "Hello, World!", count: 2 }

// 3) SignalR — /sleipnirhub (MessagePack binary)
await using var hub = new SleipnirSignalrClient("http://localhost:5003");
var r3 = (await hub.Call<Greeting>(request))!;   // { message: "Hello, World!", count: 3 }

// Same contract, same result shape — the wire is a deployment detail.
Console.WriteLine(r1.Message == r2.Message && r2.Message == r3.Message); // True
```

(The `count` increments because the controller keeps a static counter — proof the three calls
hit the same server-side controller, not three copies.)

### Polymorphic — choose the wire at the call site

```csharp
ISleipnirClient client = useRest ? new SleipnirRestJsonClient(baseUrl) : new SleipnirWebSocketClient(baseUrl);
var result = await client.Call<Greeting>(request);   // identical regardless of `client`
```

## Why three wires?

| Wire      | When                                                      | Wire format            |
|-----------|-----------------------------------------------------------|------------------------|
| REST      | request/response, caching, proxies, curl-friendly        | HTTP/1.1 + JSON        |
| WebSocket | persistent low-latency, push, many calls over one socket | RFC 6455 + JSON text   |
| SignalR   | .NET-to-.NET, binary, auto-reconnect, backpressure       | WebSocket + MessagePack |

The domain does not know which wire called it. A controller written once is reachable over all
three; you choose per client based on the client's constraints, not the server's.

## Files

- `Program.cs` — F5 wiring with `UseSignalR = true` (third wire opt-in).
- `Domain.cs` — the `Greeter` controller (one call + a 2-element batch path).
- This story is new — the narrative lives here.