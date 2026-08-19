using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using SleipnirClient.Sleipnir;
using SleipnirHub.Extensions;
using SleipnirRest;
using SleipnirTests.Fixtures;
using SleipnirWebSocket;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace SleipnirTests.Integration;

/// <summary>
/// Phase R3 end-to-end resume tests: prove the full client + server resume stack cooperates over a
/// real in-process Kestrel host. Each test builds its own host so it can tune the resume knobs
/// (replay-buffer cap, resume TTL) and the auth surface for the case under test.
/// <list type="bullet">
/// <item><b>Gap replay + dedup through the real <see cref="SleipnirWebSocketClient"/></b> — the
///   R2 resume hook (Resume policy) drives the R1 server replay path; events produced during a
///   forced disconnect are replayed and the consumer's <c>eventId</c> cursor prevents duplicates.</item>
/// <item><b>Over-cap gap → overflow events lost, in-window events replayed</b> — a tiny replay ring
///   evicts the oldest gap events; only the tail is replayed on resume.</item>
/// <item><b>TTL expiry → degrade to fresh</b> — after the idle TTL the durable state is GC'd; a
///   resume falls through to a fresh subscribe (new id, eventIds restart at 1, no replay).</item>
/// <item><b>Auth-revoke on resume → 401 + teardown</b> (Phase R3a) — a resume re-runs the same
///   authorization a fresh subscribe runs, against the ORIGINAL route recorded at create time. A
///   resume arriving without credentials (role revoked / token dropped during the gap) is rejected
///   with 401 and the durable subscription is torn down (a later authed resume degrades to fresh
///   because the state no longer exists).</item>
/// </list>
/// The non-resumable degrade case is covered by <see cref="WebSocketResumeTests"/>; the ring-overflow
/// drop-counter accounting (<c>sleipnir_event_dropped_total</c>) is covered by
/// <see cref="SleipnirTests.Unit.Core.SubscriptionStoreTests"/> — the process-global
/// <c>SleipnirConnectionRegistry</c> counter is not asserted here because parallel integration hosts
/// overwrite the static <c>Current</c> singleton, making an absolute-count read flaky.
/// </summary>
public class ResumeTests
{
    /// <summary>Builds and starts a real in-process Kestrel host with the given options + test auth scheme.</summary>
    private static async Task<WebApplication> BuildHostAsync(SleipnirOptions options)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Services.AddSleipnir(options);

        // Test-only auth: Bearer "valid-token" → authenticated Admin principal.
        builder.Services.AddAuthentication("Test")
            .AddScheme<TestAuthOptions, TestAuthHandler>("Test", _ => { });
        builder.Services.AddAuthorization();

        var app = builder.Build();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseSleipnir();
        app.MapSleipnirEndpoints("/api/sleipnir");
        app.UseWebSockets();
        app.UseSleipnirWebSocket("/sleipnirws");

        await app.StartAsync();
        return app;
    }

    private static string WsUrl(WebApplication app)
        => app.Urls.First().TrimEnd('/').Replace("http://", "ws://") + "/sleipnirws";

    private static string HttpUrl(WebApplication app)
        => app.Urls.First().TrimEnd('/') + "/";

    // ─── Test 1: real client gap replay + dedup ───────────────────────────────

    [Fact]
    public async Task GapReplay_RealClient_ResumesAndDedups()
    {
        await using var host = await BuildHostAsync(new SleipnirOptions { EnableDetailedErrors = true });
        var wsUrl = WsUrl(host);

        // socketFactory captures every socket the client creates so the test can force a transport
        // drop by aborting the current one (the client then reconnects through the same factory).
        var sockets = new List<ClientWebSocket>();
        Func<ClientWebSocket> factory = () =>
        {
            var ws = new ClientWebSocket();
            lock (sockets) sockets.Add(ws);
            return ws;
        };

        await using var client = new SleipnirWebSocketClient(HttpUrl(host),
            autoReconnect: true,
            reconnectDelays: new[] { TimeSpan.FromMilliseconds(40) },
            socketFactory: factory,
            resumePolicy: _ => ResumeDecision.Resume);
        await client.ConnectAsync();

        var sub = await client.SubscribeAsync<string>("E2EResumeEvent", "Tick");
        var obs = new CountingObserver<string>();
        sub.Subscribe(obs);

        // Process one event so the client's per-subscription LastEventId cursor becomes 1.
        E2EResumeEventController.Stream.Push("a");
        await WaitUntilAsync(() => obs.NextCount >= 1, 3000);
        obs.Values.Should().Equal(["a"]);

        // Force a transport drop → the client reconnects in the background; give the server a moment
        // to detach the live tap (the durable source + ring buffer persist).
        sockets[0].Abort();
        await WaitForStateAsync(client, SleipnirConnectionState.Reconnecting, 3000);
        await Task.Delay(300);

        // Produce events during the gap — they accumulate in the replay ring buffer (no live tap).
        E2EResumeEventController.Stream.Push("b");
        E2EResumeEventController.Stream.Push("c");
        E2EResumeEventController.Stream.Push("d");

        // Reconnect → ResubscribeAllAsync (Resume policy) sends the durable id + lastEventId=1 →
        // the server replays the gap (eventIds 2,3,4). The client cursor (1) admits them; "a" is
        // not re-delivered (the server only replays eventId > lastEventId).
        await WaitForStateAsync(client, SleipnirConnectionState.Connected, 5000);
        await WaitUntilAsync(() => obs.NextCount >= 4, 5000);

        obs.Values.Should().Equal(["a", "b", "c", "d"], "the gap is replayed with no duplicates");

        sub.Dispose();
    }

    // ─── Test 2: over-cap gap → overflow lost, in-window replayed ─────────────

    [Fact]
    public async Task OverCapGap_OverflowEventsLost_InWindowReplayed()
    {
        await using var host = await BuildHostAsync(new SleipnirOptions
        {
            EnableDetailedErrors = true,
            EventReplayBufferCapacity = 2,
        });
        var wsUrl = WsUrl(host);

        string durableId;
        using (var ws1 = new ClientWebSocket())
        {
            await ws1.ConnectAsync(new Uri(wsUrl), CancellationToken.None);
            await SendAsync(ws1, JsonSerializer.Serialize(new
            {
                kind = "subscribe",
                controller = "E2EResumeEvent",
                method = "Tick",
                id = "sub-1",
            }));

            var (code, subId, _, buffered) = await ReadResponseAsync(ws1);
            code.Should().Be(200, "fresh subscribe succeeds");
            durableId = subId;
            buffered.Should().BeEmpty("no events before the first Push");

            // Two live events → ring holds [1,2] (delivery does not drain the ring).
            E2EResumeEventController.Stream.Push("a");
            E2EResumeEventController.Stream.Push("b");
            var live = buffered.Concat(await ReadEventFramesAsync(ws1, 2 - buffered.Count)).ToList();
            live.Select(e => e.data).Should().Equal(["a", "b"]);

            await ws1.CloseAsync(WebSocketCloseStatus.NormalClosure, "gap", CancellationToken.None);
        }

        await Task.Delay(300); // let the server detach the tap (source + ring persist)

        // Three gap events into a cap-2 ring: [1,2] → +3 evicts 1 → [2,3] → +4 evicts 2 → [3,4] →
        // +5 evicts 3 → [4,5]. eventIds 1,2,3 are evicted; the replay can only yield 4,5.
        E2EResumeEventController.Stream.Push("c");
        E2EResumeEventController.Stream.Push("d");
        E2EResumeEventController.Stream.Push("e");

        using var ws2 = new ClientWebSocket();
        await ws2.ConnectAsync(new Uri(wsUrl), CancellationToken.None);
        await SendAsync(ws2, JsonSerializer.Serialize(new
        {
            kind = "subscribe",
            controller = "E2EResumeEvent",
            method = "Tick",
            subscriptionId = durableId,
            lastEventId = 2,
            id = "sub-resume",
        }));

        var (resumeCode, resumedId, replayedFrom, bufferedReplay) = await ReadResponseAsync(ws2);
        resumeCode.Should().Be(200, "resume succeeds within the buffer window");
        resumedId.Should().Be(durableId, "the durable subscriptionId is stable across reconnects");
        replayedFrom.Should().Be(4, "the oldest surviving ring entry is eventId 4 (1,2,3 were evicted)");

        var replayed = bufferedReplay.Concat(await ReadEventFramesAsync(ws2, 2 - bufferedReplay.Count)).ToList();
        replayed.Select(e => e.eventId).Should().Equal([4L, 5L], "only the in-window tail is replayed");
        replayed.Select(e => e.data).Should().Equal(["d", "e"], "overflow event 'c' (eventId 3) was lost");

        await ws2.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    // ─── Test 3: TTL expiry → degrade to fresh ────────────────────────────────

    [Fact]
    public async Task TtlExpiry_DegradesToFresh()
    {
        await using var host = await BuildHostAsync(new SleipnirOptions
        {
            EnableDetailedErrors = true,
            EventResumeTtl = TimeSpan.FromMilliseconds(300),
        });
        var wsUrl = WsUrl(host);

        string durableId;
        using (var ws1 = new ClientWebSocket())
        {
            await ws1.ConnectAsync(new Uri(wsUrl), CancellationToken.None);
            await SendAsync(ws1, JsonSerializer.Serialize(new
            {
                kind = "subscribe",
                controller = "E2EResumeEvent",
                method = "Tick",
                id = "sub-1",
            }));
            var (_, subId, _, _) = await ReadResponseAsync(ws1);
            durableId = subId;

            E2EResumeEventController.Stream.Push("a");
            var live = await ReadEventFramesAsync(ws1, 1);
            live.Select(e => e.data).Should().Equal(["a"]);

            await ws1.CloseAsync(WebSocketCloseStatus.NormalClosure, "gap", CancellationToken.None);
        }

        // Wait past the idle TTL + the GC sweep interval so the detached durable state is evicted.
        await Task.Delay(1100);

        using var ws2 = new ClientWebSocket();
        await ws2.ConnectAsync(new Uri(wsUrl), CancellationToken.None);
        await SendAsync(ws2, JsonSerializer.Serialize(new
        {
            kind = "subscribe",
            controller = "E2EResumeEvent",
            method = "Tick",
            subscriptionId = durableId,
            lastEventId = 1,
            id = "sub-resume",
        }));

        // Lookup fails (TTL-evicted) → fresh subscribe: new id, nothing to replay, eventIds restart.
        var (_, freshId, replayedFrom, _) = await ReadResponseAsync(ws2);
        freshId.Should().NotBe(durableId, "the TTL-expired durable id is gone — a fresh id is minted");
        replayedFrom.Should().BeNull("a fresh subscribe has nothing to replay");

        E2EResumeEventController.Stream.Push("x");
        E2EResumeEventController.Stream.Push("y");
        var fresh = await ReadEventFramesAsync(ws2, 2);
        fresh.Select(e => e.eventId).Should().Equal([1L, 2L], "fresh eventIds restart at 1");
        fresh.Select(e => e.data).Should().Equal(["x", "y"]);

        await ws2.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    // ─── Test 4: auth-revoke on resume → 401 + teardown (R3a) ─────────────────

    [Fact]
    public async Task AuthRevoke_OnResume_RejectedAndTornDown()
    {
        await using var host = await BuildHostAsync(new SleipnirOptions { EnableDetailedErrors = true });
        var wsUrl = WsUrl(host);

        // 1. Fresh subscribe WITH credentials → the [SleipnirAuthorise(Role="Admin")] event admits
        //    the authenticated Admin principal; a durable subscription is created.
        string durableId;
        using (var ws1 = new ClientWebSocket())
        {
            ws1.Options.SetRequestHeader(HeaderNames.Authorization, "Bearer " + TestAuthHandler.ValidToken);
            await ws1.ConnectAsync(new Uri(wsUrl), CancellationToken.None);
            await SendAsync(ws1, JsonSerializer.Serialize(new
            {
                kind = "subscribe",
                controller = "AuthedResumableEvent",
                method = "SecureTick",
                id = "sub-1",
            }));
            var (_, subId, _, _) = await ReadResponseAsync(ws1);
            subId.Should().NotBeNullOrEmpty("an authed fresh subscribe succeeds");
            durableId = subId;

            AuthedResumableEventController.Stream.Push("a");
            var live = await ReadEventFramesAsync(ws1, 1);
            live.Select(e => e.data).Should().Equal(["a"]);

            await ws1.CloseAsync(WebSocketCloseStatus.NormalClosure, "gap", CancellationToken.None);
        }

        await Task.Delay(300); // let the server detach (durable source + ring persist)

        // Produce an event during the gap — it lands in the durable ring buffer.
        AuthedResumableEventController.Stream.Push("b");

        // 2. Resume WITHOUT credentials (role revoked / token dropped during the gap) → the R3a
        //    reconnect auth re-check re-runs CheckAuthorisation against the ORIGINAL route → 401,
        //    and the durable subscription is torn down.
        using (var ws2 = new ClientWebSocket())
        {
            await ws2.ConnectAsync(new Uri(wsUrl), CancellationToken.None);
            await SendAsync(ws2, JsonSerializer.Serialize(new
            {
                kind = "subscribe",
                controller = "AuthedResumableEvent",
                method = "SecureTick",
                subscriptionId = durableId,
                lastEventId = 1,
                id = "sub-resume-revoked",
            }));
            var (code, _, _, _) = await ReadResponseAsync(ws2);
            code.Should().Be(401, "a resume after a revoked role must not silently re-attach");
        }

        // 3. A later authed resume degrades to FRESH (new id, no replay of the gap event "b") —
        //    proof the 401 path actually destroyed the durable state (otherwise this would resume
        //    and replay "b").
        using (var ws3 = new ClientWebSocket())
        {
            ws3.Options.SetRequestHeader(HeaderNames.Authorization, "Bearer " + TestAuthHandler.ValidToken);
            await ws3.ConnectAsync(new Uri(wsUrl), CancellationToken.None);
            await SendAsync(ws3, JsonSerializer.Serialize(new
            {
                kind = "subscribe",
                controller = "AuthedResumableEvent",
                method = "SecureTick",
                subscriptionId = durableId,
                lastEventId = 1,
                id = "sub-resume-after-teardown",
            }));
            var (_, freshId, replayedFrom, _) = await ReadResponseAsync(ws3);
            freshId.Should().NotBe(durableId, "the torn-down durable id is gone — fresh id minted");
            replayedFrom.Should().BeNull("the gap event was not replayed — the state was destroyed");

            AuthedResumableEventController.Stream.Push("c");
            var fresh = await ReadEventFramesAsync(ws3, 1);
            fresh.Select(e => e.eventId).Should().Equal([1L], "fresh eventIds restart at 1");
            fresh.Select(e => e.data).Should().Equal(["c"]);

            await ws3.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        }
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private sealed class CountingObserver<T> : IObserver<T>
    {
        public int NextCount;
        public int CompletedCount;
        public readonly List<T> Values = new();
        public void OnNext(T value) { NextCount++; Values.Add(value); }
        public void OnCompleted() => Interlocked.Increment(ref CompletedCount);
        public void OnError(Exception error) { }
    }

    private static async Task WaitForStateAsync(SleipnirWebSocketClient client, SleipnirConnectionState expected, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (client.State == expected) return;
            await Task.Delay(20);
        }
        client.State.Should().Be(expected, $"client should reach {expected} (was {client.State})");
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(20);
        }
        predicate().Should().BeTrue("condition should hold within the timeout");
    }

    private record EventFrame(long eventId, string data);

    /// <summary>
    /// Reads until the subscribe response arrives, BUFFERING any event frames that raced ahead of
    /// it. The manager now enqueues the subscribe-ack before starting the durable pump, so the ack
    /// is guaranteed to precede the replayed-gap / live frames and the buffer stays empty in
    /// practice — it is kept as a defensive fallback. Returns the response <c>code</c>, the
    /// <c>subscriptionId</c> + <c>replayedFrom</c> (null when the response is not a 200 or the field
    /// is absent), and the (normally empty) buffered events.
    /// </summary>
    private static async Task<(int code, string? subscriptionId, long? replayedFrom, List<EventFrame> buffered)>
        ReadResponseAsync(WebSocket ws, int timeoutMs = 5000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var buffered = new List<EventFrame>();
        while (true)
        {
            var msg = await ReceiveAsync(ws, cts.Token);
            using var doc = JsonDocument.Parse(msg);
            if (doc.RootElement.TryGetProperty("code", out var codeProp))
            {
                var code = codeProp.GetInt32();
                string? subId = null;
                long? replayedFrom = null;
                if (code == 200 && doc.RootElement.TryGetProperty("data", out var data))
                {
                    if (data.TryGetProperty("subscriptionId", out var sid) && sid.ValueKind == JsonValueKind.String)
                        subId = sid.GetString();
                    if (data.TryGetProperty("replayedFrom", out var rf) && rf.ValueKind == JsonValueKind.Number)
                        replayedFrom = rf.GetInt64();
                }
                return (code, subId, replayedFrom, buffered);
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

            using var doc = JsonDocument.Parse(msg);
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