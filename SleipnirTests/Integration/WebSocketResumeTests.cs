using FluentAssertions;
using SleipnirTests.Fixtures;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace SleipnirTests.Integration;

/// <summary>
/// Phase R1 integration test: proves the server-side resume path end-to-end over a real
/// Kestrel + <c>ClientWebSocket</c>. A durable (<c>[SleipnirEvent(Resumable = true)]</c>)
/// subscription survives a WebSocket disconnect — the source stays subscribed and events
/// produced during the gap accumulate in the replay ring buffer; on reconnect the client
/// sends <c>lastEventId</c> + the durable <c>subscriptionId</c> and the server replays the
/// gap (at-least-once within the buffer window). Uses a raw <c>ClientWebSocket</c> because
/// the SleipnirClient resume hook lands in Phase R2.
/// </summary>
public class WebSocketResumeTests : IClassFixture<TransportTestFixture>
{
    private readonly TransportTestFixture _fixture;

    public WebSocketResumeTests(TransportTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Resume_Replays_Events_Produced_During_The_Disconnect_Gap()
    {
        var wsUrl = _fixture.BaseUrl.Replace("http://", "ws://") + "sleipnirws";

        string durableId;
        using (var client1 = new ClientWebSocket())
        {
            await client1.ConnectAsync(new Uri(wsUrl), CancellationToken.None);

            // ── 1. Fresh subscribe to the resumable hot stream ────────────────────
            await SendAsync(client1, JsonSerializer.Serialize(new
            {
                kind = "subscribe",
                controller = "ResumableEvent",
                method = "Tick",
                id = "sub-1",
            }));

            // ── 2. Read the subscribe response FIRST (confirms the server has subscribed
            //    the durable observer + attached the live tap), THEN push events. Pushing
            //    before the server subscribed would broadcast to zero observers → lost. ─
            var (subscriptionId, _, bufferedLive) = await ReadResponseAsync(client1);
            subscriptionId.Should().NotBeNullOrEmpty();
            durableId = subscriptionId;
            bufferedLive.Should().BeEmpty("no events are produced before the first Push");

            ResumableEventController.Stream.Push("a");
            ResumableEventController.Stream.Push("b");
            var liveEvents = bufferedLive.Concat(await ReadEventFramesAsync(client1, expectedCount: 2)).ToList();
            liveEvents.Should().HaveCount(2);
            liveEvents.Select(e => e.eventId).Should().Equal([1L, 2L]);
            liveEvents.Select(e => e.data).Should().Equal(["a", "b"]);

            // ── 3. Force-disconnect (the durable source + ring buffer persist) ────
            await client1.CloseAsync(WebSocketCloseStatus.NormalClosure, "gap", CancellationToken.None);
        }

        // Give the server a moment to detach the tap (the source subscription stays alive).
        await Task.Delay(250);

        // ── 4. Produce events during the gap — they accumulate in the ring buffer ─
        ResumableEventController.Stream.Push("c");
        ResumableEventController.Stream.Push("d");
        ResumableEventController.Stream.Push("e");

        // ── 5. Reconnect and resume from lastEventId = 2 ──────────────────────────
        using var client2 = new ClientWebSocket();
        await client2.ConnectAsync(new Uri(wsUrl), CancellationToken.None);
        await SendAsync(client2, JsonSerializer.Serialize(new
        {
            kind = "subscribe",
            controller = "ResumableEvent",
            method = "Tick",
            subscriptionId = durableId,
            lastEventId = 2,
            id = "sub-resume",
        }));

        // Replayed frames may race the response (the replay pump enqueues them around the
        // response enqueue) — ReadResponseAsync buffers any that arrive first.
        var (resumedId, replayedFrom, bufferedReplay) = await ReadResponseAsync(client2);
        resumedId.Should().Be(durableId, "the durable subscriptionId is stable across reconnects");
        replayedFrom.Should().Be(3, "the first replayed event is the one after lastEventId=2");

        var replayed = bufferedReplay.Concat(await ReadEventFramesAsync(client2, expectedCount: 3 - bufferedReplay.Count)).ToList();
        replayed.Should().HaveCount(3);
        replayed.Select(e => e.eventId).Should().Equal([3L, 4L, 5L]);
        replayed.Select(e => e.data).Should().Equal(["c", "d", "e"]);

        // Cleanup: explicit unsubscribe destroys the durable state (no TTL linger).
        await SendAsync(client2, JsonSerializer.Serialize(new
        {
            kind = "unsubscribe",
            subscriptionId = durableId,
            id = "unsub-1",
        }));
        _ = await ReceiveAsync(client2);
        await client2.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Fact]
    public async Task Resume_On_NonResumable_Event_Degrades_To_Fresh()
    {
        var wsUrl = _fixture.BaseUrl.Replace("http://", "ws://") + "sleipnirws";
        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri(wsUrl), CancellationToken.None);

        // A resume frame for a non-resumable event (ObservableStrings) carries a fake
        // subscriptionId the store never knew → Lookup fails → fresh subscribe (cold
        // observable re-invoked, eventIds restart at 1, no replay).
        await SendAsync(client, JsonSerializer.Serialize(new
        {
            kind = "subscribe",
            controller = "TestInvoker",
            method = "ObservableStrings",
            subscriptionId = "never-existed",
            lastEventId = 99,
            @params = new[] { new { parameterName = "count", data = 3 } },
            id = "sub-fresh",
        }));

        var (subId, replayedFrom, buffered) = await ReadResponseAsync(client);
        subId.Should().NotBeNullOrEmpty();
        replayedFrom.Should().BeNull("a fresh subscribe has nothing to replay");

        var events = buffered.Concat(await ReadEventFramesAsync(client, expectedCount: 3 - buffered.Count)).ToList();
        events.Should().HaveCount(3);
        events.Select(e => e.eventId).Should().Equal([1L, 2L, 3L], "fresh eventIds restart at 1");

        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private record EventFrame(long eventId, string data);

    /// <summary>
    /// Reads until the subscribe response arrives, BUFFERING any event frames that raced
    /// ahead of it (the durable replay pump may enqueue replayed frames before the response).
    /// Returns the response fields plus the buffered events so the caller can fold them in.
    /// </summary>
    private static async Task<(string subscriptionId, long? replayedFrom, List<EventFrame> buffered)>
        ReadResponseAsync(WebSocket ws, int timeoutMs = 5000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var buffered = new List<EventFrame>();
        while (true)
        {
            var msg = await ReceiveAsync(ws, cts.Token);
            var doc = JsonDocument.Parse(msg);
            if (doc.RootElement.TryGetProperty("code", out _))
            {
                doc.RootElement.GetProperty("code").GetInt32().Should().Be(200);
                var data = doc.RootElement.GetProperty("data");
                var subId = data.GetProperty("subscriptionId").GetString()!;
                long? replayedFrom = null;
                if (data.TryGetProperty("replayedFrom", out var rf) && rf.ValueKind == JsonValueKind.Number)
                    replayedFrom = rf.GetInt64();
                return (subId, replayedFrom, buffered);
            }
            if (doc.RootElement.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "event")
            {
                buffered.Add(new EventFrame(
                    doc.RootElement.GetProperty("eventId").GetInt64(),
                    doc.RootElement.GetProperty("data").GetString()!));
            }
        }
    }

    private static async Task<List<EventFrame>> ReadEventFramesAsync(WebSocket ws, int expectedCount, int timeoutMs = 5000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var frames = new List<EventFrame>();
        while (frames.Count < expectedCount)
        {
            string msg;
            try { msg = await ReceiveAsync(ws, cts.Token); }
            catch (OperationCanceledException) { break; }

            var doc = JsonDocument.Parse(msg);
            if (!doc.RootElement.TryGetProperty("type", out var typeProp) || typeProp.GetString() != "event")
                continue;
            frames.Add(new EventFrame(
                doc.RootElement.GetProperty("eventId").GetInt64(),
                doc.RootElement.GetProperty("data").GetString()!));
        }
        return frames;
    }

    private static async Task SendAsync(WebSocket ws, string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task<string> ReceiveAsync(WebSocket ws, CancellationToken ct = default)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("WebSocket closed before message received.");
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }
}