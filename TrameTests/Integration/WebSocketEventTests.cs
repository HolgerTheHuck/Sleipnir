using FluentAssertions;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TrameTests.Fixtures;
using Xunit;

namespace TrameTests.Integration;

/// <summary>
/// Phase 3 Integration-Tests für Events/Server-Push über WebSocket. Beweist den
/// Subscribe/Unsubscribe-Dispatcher, Event-Frame-Push, complete-Frame und Auto-Cleanup.
/// Nutzt rohe ClientWebSocket (TrameWebSocketClient hat kein Subscribe in v1).
/// </summary>
public class WebSocketEventTests : IClassFixture<TransportTestFixture>
{
    private readonly TransportTestFixture _fixture;

    public WebSocketEventTests(TransportTestFixture fixture) => _fixture = _fixture = fixture;

    [Fact]
    public async Task Subscribe_ReceivesEvents_AndComplete()
    {
        // Arrange: roher WS-Client gegen /tramews.
        var wsUrl = _fixture.BaseUrl.Replace("http://", "ws://") + "tramews";
        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri(wsUrl), CancellationToken.None);

        // Act: Subscribe-Request an ObservableStrings (gibt IObservable<string> mit 3 Events).
        var subscribeReq = new
        {
            kind = "subscribe",
            controller = "TestInvoker",
            method = "ObservableStrings",
            @params = new[] { new { parameterName = "count", data = 3 } },
            id = "sub-1",
        };
        await SendAsync(client, JsonSerializer.Serialize(subscribeReq));

        // Assert: Subscribe-Response mit code 200.
        var subscribeResp = await ReceiveAsync(client);
        var subDoc = JsonDocument.Parse(subscribeResp);
        subDoc.RootElement.GetProperty("code").GetInt32().Should().Be(200);

        // Assert: 3 Event-Frames empfangen. subscriptionId aus dem ersten Event extrahieren
        // (die Subscribe-Response serialisiert data je nach Converter — robust: aus Event-Frame).
        var events = new List<JsonElement>();
        string? subscriptionId = null;
        for (int i = 0; i < 3; i++)
        {
            var eventFrame = await ReceiveAsync(client);
            var evtDoc = JsonDocument.Parse(eventFrame);
            evtDoc.RootElement.GetProperty("type").GetString().Should().Be("event");
            subscriptionId ??= evtDoc.RootElement.GetProperty("subscriptionId").GetString();
            evtDoc.RootElement.GetProperty("subscriptionId").GetString().Should().Be(subscriptionId);
            evtDoc.RootElement.GetProperty("eventId").GetInt64().Should().Be(i + 1);
            evtDoc.RootElement.GetProperty("data").GetString().Should().Be($"evt-{i}");
            events.Add(evtDoc.RootElement);
        }

        // Assert: complete-Frame.
        var completeFrame = await ReceiveAsync(client);
        var completeDoc = JsonDocument.Parse(completeFrame);
        completeDoc.RootElement.GetProperty("type").GetString().Should().Be("complete");
        completeDoc.RootElement.GetProperty("subscriptionId").GetString().Should().Be(subscriptionId);

        // Cleanup: client schließen, Auto-Cleanup auf Server-Seite.
        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Fact]
    public async Task Unsubscribe_Returns200_AndStopsEvents()
    {
        var wsUrl = _fixture.BaseUrl.Replace("http://", "ws://") + "tramews";
        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri(wsUrl), CancellationToken.None);

        // Subscribe mit 10 Events (das SimpleObservable feuert synchron — alle 10 + complete
        // kommen sofort nach der Subscribe-Response, bevor wir unsubscribed können).
        var subscribeReq = new
        {
            kind = "subscribe",
            controller = "TestInvoker",
            method = "ObservableStrings",
            @params = new[] { new { parameterName = "count", data = 10 } },
            id = "sub-2",
        };
        await SendAsync(client, JsonSerializer.Serialize(subscribeReq));
        var subscribeResp = await ReceiveAsync(client);
        JsonDocument.Parse(subscribeResp).RootElement.GetProperty("code").GetInt32().Should().Be(200);

        // Alle 10 Events + complete ablesen (SimpleObservable feuert synchron). subscriptionId
        // aus dem ersten Event extrahieren (robust gegen Converter-Serialisierung der Response).
        string? subscriptionId = null;
        for (int i = 0; i < 10; i++)
        {
            var evt = await ReceiveAsync(client);
            var evtDoc = JsonDocument.Parse(evt);
            evtDoc.RootElement.GetProperty("type").GetString().Should().Be("event");
            subscriptionId ??= evtDoc.RootElement.GetProperty("subscriptionId").GetString();
        }
        var complete = await ReceiveAsync(client);
        JsonDocument.Parse(complete).RootElement.GetProperty("type").GetString().Should().Be("complete");

        // Unsubscribe (nach complete — beweist, dass der Dispatcher Unsubscribe annimmt).
        var unsubReq = new
        {
            kind = "unsubscribe",
            subscriptionId,
            id = "unsub-1",
        };
        await SendAsync(client, JsonSerializer.Serialize(unsubReq));
        var unsubResp = await ReceiveAsync(client);
        var unsubDoc = JsonDocument.Parse(unsubResp);
        unsubDoc.RootElement.GetProperty("code").GetInt32().Should().Be(200);

        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Fact]
    public async Task Subscribe_NonObservableMethod_Returns400()
    {
        var wsUrl = _fixture.BaseUrl.Replace("http://", "ws://") + "tramews";
        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri(wsUrl), CancellationToken.None);

        // Echo ist kein IObservable<T> — Subscribe muss 400 liefern.
        var subscribeReq = new
        {
            kind = "subscribe",
            controller = "TestInvoker",
            method = "Echo",
            @params = new[] { new { parameterName = "message", data = "hi" } },
            id = "sub-3",
        };
        await SendAsync(client, JsonSerializer.Serialize(subscribeReq));
        var resp = await ReceiveAsync(client);
        var doc = JsonDocument.Parse(resp);
        doc.RootElement.GetProperty("code").GetInt32().Should().Be(400);

        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private static async Task SendAsync(WebSocket ws, string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task<string> ReceiveAsync(WebSocket ws)
    {
        var buffer = new byte[4096];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("WebSocket closed before message received.");
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }
}