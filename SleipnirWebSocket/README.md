# Sleipnir.WebSocket

The WebSocket transport for [Sleipnir](../README.md) — the code-first, multi-transport
RPC framework for .NET 8. It uses plain **standard WebSockets (RFC 6455)** with
**JSON text frames**. This makes it especially easy to integrate from other languages
(Java, JavaScript, Python, Go, Rust, …) and it has less overhead than SignalR.

## Endpoint

```
ws://<host>/sleipnirws      (HTTP)
wss://<host>/sleipnirws     (HTTPS)
```

In the .NET app the endpoint is registered with

```csharp
app.UseWebSockets();
app.UseSleipnirWebSocket("/sleipnirws");
```

## Protocol

Every message is a JSON object representing either a single request or a multi-request.

### Single request

```json
{
  "controller": "Customer",
  "method": "GetCustomerById",
  "params": [
    { "parameterName": "id", "data": 1 }
  ],
  "id": "my-request-id"
}
```

Response:

```json
{
  "id": "my-request-id",
  "code": 200,
  "data": { "id": 1, "name": "Max" },
  "exposedDependencies": null
}
```

### Multi-request

```json
{
  "mode": 0,
  "requests": [
    {
      "controller": "Customer",
      "method": "AddCustomer",
      "params": [
        { "parameterName": "name", "data": "Max" }
      ],
      "id": "create",
      "dependencyMapping": { "newId": "$" }
    },
    {
      "controller": "Customer",
      "method": "GetCustomerById",
      "params": [
        { "parameterName": "id", "data": "@newId" }
      ],
      "id": "read"
    }
  ]
}
```

`mode`: `0` = parallel, `1` = serial.

With `mode: 1` the requests run sequentially and aliases (`@alias`) are resolved
from prior responses.

Response:

```json
[
  { "id": "create", "code": 200, "data": 1, "exposedDependencies": { "newId": "1" } },
  { "id": "read", "code": 200, "data": "..." }
]
```

## Java example

```java
import java.net.URI;
import org.java_websocket.client.WebSocketClient;
import org.java_websocket.handshake.ServerHandshake;

public class SleipnirWebSocketClient extends WebSocketClient {
    public SleipnirWebSocketClient(URI serverUri) {
        super(serverUri);
    }

    @Override
    public void onOpen(ServerHandshake handshake) {
        String request = "{\"controller\":\"Customer\",\"method\":\"GetAllCustomers\",\"id\":\"1\"}";
        send(request);
    }

    @Override
    public void onMessage(String message) {
        System.out.println("Response: " + message);
    }

    @Override
    public void onClose(int code, String reason, boolean remote) { }

    @Override
    public void onError(Exception ex) { ex.printStackTrace(); }
}
```

## JavaScript example

```javascript
const ws = new WebSocket('ws://localhost:5000/sleipnirws');

ws.onopen = () => {
    ws.send(JSON.stringify({
        controller: 'Customer',
        method: 'GetAllCustomers',
        id: '1'
    }));
};

ws.onmessage = (event) => {
    console.log('Response:', JSON.parse(event.data));
};
```

## Python example

```python
import asyncio
import websockets
import json

async def call():
    uri = "ws://localhost:5000/sleipnirws"
    async with websockets.connect(uri) as ws:
        request = {
            "controller": "Customer",
            "method": "GetAllCustomers",
            "id": "1"
        }
        await ws.send(json.dumps(request))
        response = await ws.recv()
        print(response)

asyncio.run(call())
```

## .NET client

```csharp
await using var client = new SleipnirWebSocketClient("https://localhost:5001");
await client.ConnectAsync();

var response = await client.Call(new SleipnirRequest
{
    Controller = "Customer",
    Method = "GetAllCustomers"
});
```

## Advantages over SignalR

- No proprietary protocol.
- Lower overhead (no hub negotiation, no MessagePack required).
- Simpler implementation in non-.NET languages.
- Direct control over the connection lifecycle.

## Limitations

- JSON only (no MessagePack) for now.
- No automatic reconnect (must be implemented client-side).
- No central authentication handshake; a token can be passed, for example, in the
  query string.

## Install

```xml
<PackageReference Include="Sleipnir.WebSocket" Version="1.0.0" />
```

Targets `net8.0`. Depends on `Sleipnir.Core` (→ `Sleipnir.Common`). The full field-by-field
wire contract is in [PROTOCOL.md](../PROTOCOL.md); the .NET client is in
`Sleipnir.Client`. See the [root README](../README.md).