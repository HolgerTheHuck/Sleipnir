using FluentAssertions;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SleipnirTests.Fixtures;
using Xunit;

namespace SleipnirTests.Integration;

/// <summary>
/// R6 regression: frame integrity under concurrent server sends on one WebSocket connection.
///
/// Hotfix 1.1.1 routed every server-side send through a single bounded channel with one
/// <c>SendLoopAsync</c> reader (see <c>SleipnirSubscriptionManager</c>). Before that, two threads
/// could call <c>WebSocket.SendAsync</c> concurrently on the same socket — the middleware
/// thread (call responses) and the per-subscription pump task (event/complete frames) — and an
/// interleaved send corrupts a frame or throws. This test reproduces that concurrency on ONE
/// connection: an active subscription whose pump task pushes event frames over a ~160 ms window
/// while the middleware thread pushes 50 call responses. It asserts that EVERY received frame is
/// a complete, parseable JSON document, that each of the 50 calls correlates to its own echo, and
/// that the subscription still delivers all its events plus the terminal complete frame.
/// </summary>
public class WebSocketConcurrentSendTests : IClassFixture<TransportTestFixture>
{
    private readonly TransportTestFixture _fixture;

    public WebSocketConcurrentSendTests(TransportTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ConcurrentCalls_WithActiveSubscription_EveryFrameIsIntact()
    {
        var wsUrl = _fixture.BaseUrl.Replace("http://", "ws://") + "sleipnirws";
        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri(wsUrl), CancellationToken.None);

        // Subscribe to an observable that fires events on a background task over time (count=20,
        // delayMs=8 → ~160 ms of event frames pushed by the pump task). The synchronous
        // ObservableStrings drains before any call traffic starts, so it cannot exercise the
        // concurrent-send path; the over-time variant keeps the pump task busy while the calls run.
        var subscribeReq = new
        {
            kind = "subscribe",
            controller = "TestInvoker",
            method = "ObservableStringsOverTime",
            @params = new[]
            {
                new { parameterName = "count", data = 20 },
                new { parameterName = "delayMs", data = 8 },
            },
            id = "sub",
        };
        await SendAsync(client, JsonSerializer.Serialize(subscribeReq));

        // Fire 50 echo calls. EchoAsync awaits Task.Delay(10) each, so the middleware thread is
        // busy pushing call responses for ~500 ms — overlapping the 160 ms event window, so the
        // two threads enqueue onto the shared send channel concurrently (the race the 1.1.1
        // single-sender channel serializes). Sends are serialized on the raw client (one
        // ClientWebSocket is not thread-safe for concurrent sends); the concurrency under test is
        // server-side, not client-side.
        const int n = 50;
        for (var i = 0; i < n; i++)
        {
            var callReq = new
            {
                controller = "TestInvoker",
                method = "EchoAsync",
                @params = new[] { new { parameterName = "message", data = $"msg{i}" } },
                id = $"c{i}",
            };
            await SendAsync(client, JsonSerializer.Serialize(callReq));
        }

        // Read frames until the subscription completes AND all 50 call responses are in. Every
        // frame must parse as a complete JSON document — a pre-1.1.1 interleaved send would
        // surface as a JsonException here (a partial/corrupted frame) or a structurally-wrong
        // frame (wrong id / wrong type).
        var callResponses = new Dictionary<string, string>();
        var eventIds = new List<long>();
        string? subscriptionId = null;
        bool subscribeResponseSeen = false;
        bool completeReceived = false;
        var parseFailures = 0;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while ((!completeReceived || callResponses.Count < n) && DateTime.UtcNow < deadline)
        {
            var msg = await ReceiveAsync(client, TimeSpan.FromSeconds(10));
            JsonDocument doc;
            try { doc = JsonDocument.Parse(msg); }
            catch (JsonException) { parseFailures++; continue; }
            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    // A non-object root is a corrupted frame (e.g. two frames concatenated or a
                    // split frame) — the single-sender channel must prevent this.
                    parseFailures++;
                    continue;
                }

                if (root.TryGetProperty("type", out var typeProp))
                {
                    var type = typeProp.GetString();
                    if (type == "event")
                    {
                        subscriptionId ??= root.GetProperty("subscriptionId").GetString();
                        root.GetProperty("subscriptionId").GetString().Should().Be(subscriptionId,
                            "an event frame must carry the subscription id intact");
                        eventIds.Add(root.GetProperty("eventId").GetInt64());
                    }
                    else if (type == "complete")
                    {
                        completeReceived = true;
                    }
                }
                else if (root.TryGetProperty("code", out _))
                {
                    var id = root.GetProperty("id").GetString();
                    if (id == "sub")
                    {
                        root.GetProperty("code").GetInt32().Should().Be(200, "subscribe must succeed");
                        subscribeResponseSeen = true;
                        subscriptionId ??= root.GetProperty("data").GetProperty("subscriptionId").GetString();
                    }
                    else
                    {
                        callResponses[id!] = msg;
                    }
                }
            }
        }

        // Frame integrity #0 — no frame was unparseable or structurally corrupt. This is the
        // direct assertion of the single-sender channel: concurrent sends must not interleave.
        parseFailures.Should().Be(0, "every frame must be a complete JSON document (no interleaved sends)");
        subscribeResponseSeen.Should().BeTrue();
        completeReceived.Should().BeTrue();

        // Frame integrity #1 — call responses: exactly 50, each correlating to its OWN echo.
        callResponses.Should().HaveCount(n, "all 50 call responses must arrive");
        for (var i = 0; i < n; i++)
        {
            callResponses.Should().ContainKey($"c{i}", "call c{i}'s response must not be lost or mis-correlated");
            using var respDoc = JsonDocument.Parse(callResponses[$"c{i}"]);
            respDoc.RootElement.GetProperty("code").GetInt32().Should().Be(200);
            respDoc.RootElement.GetProperty("data").GetRawText().Should().Contain($"msg{i}",
                "call c{i} must receive its own echo, not another call's or an event's payload");
        }

        // Frame integrity #2 — event path: all 20 events arrived, in order, intact.
        eventIds.Should().HaveCount(20, "all 20 event frames must survive the concurrent call traffic");
        eventIds.Should().BeInAscendingOrder("event ids must arrive in publication order (no reordering)");

        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private static async Task SendAsync(WebSocket ws, string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task<string> ReceiveAsync(WebSocket ws, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var buffer = new byte[4096];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("WebSocket closed before message received.");
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }
}