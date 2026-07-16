# Story 03 — The Same Contract, Three Wires

> **The contract is the C# classes. The transport is a deployment detail. One domain, three
> transports — REST, WebSocket, SignalR — expose the same controllers simultaneously. Identical
> call, identical result, three wires.**

## The problem this story is NOT about

Stories 01 and 02 were about *dependency graphs* and *command fan-out* — the things Trame does
that plain REST cannot. This story is about something simpler and more fundamental: the wire is
not part of the contract.

In most RPC frameworks the transport is load-bearing: you design for gRPC, or for REST, or for
SignalR, and switching is a rewrite. In Trame the contract is the C# classes decorated with
`[TrameController]` / `[TrameMethod]`. The transport is how a call gets *delivered* — and a single
server can expose the same controllers over REST, WebSocket, and SignalR at the same time.

## The domain (deliberately tiny)

One controller, three methods — small enough that the point is the transport, not the domain:

```csharp
[TrameController("Greeter")]
public class GreeterController
{
    [TrameMethod("Greet")]
    public Greeting Greet(string name) => new() { Message = $"Hello, {name}!", Count = ++_n };

    [TrameMethod("Add")]
    public int Add(int a, int b) => a + b;

    [TrameMethod("Echo")]
    public int Echo(int value) => value;
}
```

Nothing here knows which wire called it. The controller has no `[TrameController(Transport=…)]`
because there is no such knob. The transport is configured in `Program.cs`, not in the domain.

## Three wires, one server

```csharp
builder.Services.AddTrame(new TrameOptions
{
    UseSignalR = true,     // REST + WebSocket are always on; SignalR is the opt-in third wire
    UseMessagePack = true, // SignalR binary
});
app.UseTrameTransports();   // WebSocket (default) + controller registration
app.MapTrame();             // REST endpoints + DevUI + SignalR hub (UseSignalR=true)
```

All three wires are live against the **same** controllers:

| Wire      | Endpoint                  | Wire format            | Client                          |
|-----------|---------------------------|------------------------|---------------------------------|
| REST      | `POST /api/trame/json`    | HTTP/1.1 + JSON        | `TrameRestJsonClient`           |
| WebSocket | `ws://host/tramews`       | RFC 6455 + JSON text   | `TrameWebSocketClient`          |
| SignalR   | `/tramehub`               | WebSocket + MessagePack | `TrameSignalrClient`             |

## The same call over three clients

All three implement `ITrameClient`. The call is identical; only the constructor differs:

```csharp
var request = TrameCall.Init("Greeter", "Greet").With("World").ToRequest();

await using var rest = new TrameRestJsonClient("http://localhost:5003");
var r1 = (await rest.Call<Greeting>(request))!;   // { message: "Hello, World!", count: 1 }

await using var ws = new TrameWebSocketClient("http://localhost:5003");
var r2 = (await ws.Call<Greeting>(request))!;     // { message: "Hello, World!", count: 2 }

await using var hub = new TrameSignalrClient("http://localhost:5003");
var r3 = (await hub.Call<Greeting>(request))!;   // { message: "Hello, World!", count: 3 }
```

The `count` increments because the controller keeps a static counter — proof the three calls hit
the same server-side controller, not three copies.

### Polymorphic — choose the wire at the call site

```csharp
ITrameClient client = useRest ? new TrameRestJsonClient(baseUrl) : new TrameWebSocketClient(baseUrl);
var result = await client.Call<Greeting>(request);   // identical regardless of `client`
```

## Why three wires?

| Wire      | When it wins                                            |
|-----------|---------------------------------------------------------|
| REST      | request/response, caching, proxies, curl-friendly, ops |
| WebSocket | persistent low-latency, push, many calls over one socket |
| SignalR   | .NET-to-.NET, binary (MessagePack), auto-reconnect, backpressure |

The domain does not know which wire called it. A controller written once is reachable over all
three; you choose per client based on the *client's* constraints (browser? .NET? latency? binary?),
not the server's. The server carries all three because `MapTrame` maps them together.

## Try it

**Standalone solution — open in Visual Studio, press F5:**

```
stories/03-same-contract-three-wires/Story03.sln
```

Boots the server with all three wires live (port 5003); the browser lands in the DevUI at `/Trame`
(the REST wire). The three client snippets above are in the story README and compile against the
project's `TrameClient` reference. Source: `stories/03-same-contract-three-wires/Program.cs`
+ `Domain.cs`.

Next story: **North-bound Security** — Trame was a trusted south-bound caller; now untrusted
clients drive it, and every request must be authenticated, authorized, rate-limited, and
size-capped before a controller runs.