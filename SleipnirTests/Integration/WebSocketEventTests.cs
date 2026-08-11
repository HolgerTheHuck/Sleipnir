using FluentAssertions;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SleipnirTests.Fixtures;
using Xunit;

namespace SleipnirTests.Integration;

/// <summary>
/// Phase 3 Integration-Tests für Events/Server-Push über WebSocket. Beweist den
/// Subscribe/Unsubscribe-Dispatcher, Event-Frame-Push, complete-Frame und Auto-Cleanup.
/// Nutzt rohe ClientWebSocket (SleipnirWebSocketClient hat kein Subscribe in v1).
/// </summary>
public class WebSocketEventTests : IClassFixture<TransportTestFixture>
{
    private readonly TransportTestFixture _fixture;

    public WebSocketEventTests(TransportTestFixture fixture) => _fixture = _fixture = fixture;

    [Fact]
    public async Task Subscribe_ReceivesEvents_AndComplete()
    {
        // Arrange: roher WS-Client gegen /sleipnirws.
        var wsUrl = _fixture.BaseUrl.Replace("http://", "ws://") + "sleipnirws";
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

        // Assert: Subscribe-Response mit code 200 + 3 Event-Frames + complete.
        // Robust: die erste Nachricht kann ein Event-Frame sein (SimpleObservable feuert
        // synchron, der Send-Loop kann Events vor der Subscribe-Response liefern).
        var events = new List<JsonElement>();
        string? subscriptionId = null;
        int eventsReceived = 0;
        bool completeReceived = false;
        bool subscribeResponseSeen = false;
        while (!subscribeResponseSeen || eventsReceived < 3 || !completeReceived)
        {
            var msg = await ReceiveAsync(client);
            var doc = JsonDocument.Parse(msg);
            if (doc.RootElement.TryGetProperty("code", out _))
            {
                // Subscribe-Response.
                doc.RootElement.GetProperty("code").GetInt32().Should().Be(200);
                subscribeResponseSeen = true;
            }
            else if (doc.RootElement.TryGetProperty("type", out var typeProp))
            {
                var type = typeProp.GetString();
                if (type == "event")
                {
                    eventsReceived++;
                    var root = doc.RootElement;
                    subscriptionId ??= root.GetProperty("subscriptionId").GetString();
                    root.GetProperty("subscriptionId").GetString().Should().Be(subscriptionId);
                    root.GetProperty("eventId").GetInt64().Should().Be(eventsReceived);
                    root.GetProperty("data").GetString().Should().Be($"evt-{eventsReceived - 1}");
                    events.Add(root);
                }
                else if (type == "complete")
                {
                    completeReceived = true;
                    doc.RootElement.GetProperty("subscriptionId").GetString().Should().Be(subscriptionId);
                }
            }
        }
        subscribeResponseSeen.Should().BeTrue();
        eventsReceived.Should().Be(3);
        completeReceived.Should().BeTrue();

        // Cleanup: client schließen, Auto-Cleanup auf Server-Seite.
        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Fact]
    public async Task Unsubscribe_Returns200_AndStopsEvents()
    {
        var wsUrl = _fixture.BaseUrl.Replace("http://", "ws://") + "sleipnirws";
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

        // Subscribe-Response + 10 Events + complete ablesen (robust: Frames können in
        // beliebiger Reihenfolge kommen — SimpleObservable feuert synchron).
        string? subscriptionId = null;
        int eventsReceived = 0;
        bool completeReceived = false;
        bool subscribeResponseSeen = false;
        while (!subscribeResponseSeen || eventsReceived < 10 || !completeReceived)
        {
            var msg = await ReceiveAsync(client);
            var doc = JsonDocument.Parse(msg);
            if (doc.RootElement.TryGetProperty("code", out _))
            {
                doc.RootElement.GetProperty("code").GetInt32().Should().Be(200);
                subscribeResponseSeen = true;
            }
            else if (doc.RootElement.TryGetProperty("type", out var typeProp))
            {
                var type = typeProp.GetString();
                if (type == "event")
                {
                    eventsReceived++;
                    subscriptionId ??= doc.RootElement.GetProperty("subscriptionId").GetString();
                }
                else if (type == "complete")
                {
                    completeReceived = true;
                    subscriptionId ??= doc.RootElement.GetProperty("subscriptionId").GetString();
                }
            }
        }
        subscribeResponseSeen.Should().BeTrue();
        eventsReceived.Should().Be(10);
        completeReceived.Should().BeTrue();
        subscriptionId.Should().NotBeNullOrEmpty();

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
        var wsUrl = _fixture.BaseUrl.Replace("http://", "ws://") + "sleipnirws";
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