using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using SleipnirCommon.Models;
using SleipnirTests.Fixtures;
using System.Text.Json;
using Xunit;

namespace SleipnirTests.Integration;

/// <summary>
/// Phase 3a integration test: proves the SignalR hub-streaming <c>SubscribeAsync</c> method
/// against a real Kestrel host + <c>HubConnection</c>. The hub bridges the transport-agnostic
/// <c>IObservable&lt;T&gt;</c> event pipeline to a SignalR <c>IAsyncEnumerable&lt;string&gt;</c>
/// stream, reusing the same durable subscription store + event buffer as WebSocket/SSE — so a
/// durable subscription survives a stream cancel and resumes from <c>lastEventId</c>.
/// <para>
/// Uses a raw <c>HubConnection</c> with <c>StreamAsync&lt;string&gt;</c> (the C#
/// <c>SleipnirSignalrClient</c> gains streaming in Phase 3b/4c). Each stream item is one
/// pre-serialized event frame string; the first item is the <c>ack</c> (the subscriptionId
/// lives there — SignalR has no separate response channel for the ack).
/// </para>
/// </summary>
public class SignalRHubStreamTests : IClassFixture<TransportTestFixture>
{
    private readonly TransportTestFixture _fixture;

    public SignalRHubStreamTests(TransportTestFixture fixture) => _fixture = fixture;

    private HubConnection CreateHub(string? bearer = null)
    {
        var hubUrl = _fixture.BaseUrl.TrimEnd('/') + "/sleipnirhub";
        var conn = new HubConnectionBuilder()
            .WithUrl(hubUrl, o =>
            {
                if (!string.IsNullOrEmpty(bearer))
                    o.AccessTokenProvider = () => Task.FromResult<string?>(bearer);
            })
            // JSON protocol (default) — the server accepts JSON + MessagePack; JSON keeps the
            // test free of the MessagePack resolver wiring, and the frame strings are JSON either way.
            .Build();
        return conn;
    }

    private static IAsyncEnumerator<string> OpenStream(
        HubConnection conn, SleipnirRequest req, string? resumeId, long? lastEventId)
    {
        // The params overload (3 individual args) — unambiguous. The (object?[], CancellationToken)
        // overload collides with params and the compiler can send a 2-arg shape (array + token) that
        // fails to bind server-side. Cancellation is via enumerator DisposeAsync (sends a stream
        // Cancel to the server), not a token.
        var stream = conn.StreamAsync<string>("SubscribeAsync", req, resumeId, lastEventId);
        return stream.GetAsyncEnumerator();
    }

    private sealed record Frame(string Type, string? SubscriptionId, long? ReplayedFrom, long EventId, string? Data, string? Message)
    {
        public static Frame Parse(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            string? subId = root.TryGetProperty("subscriptionId", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            long? replayedFrom = root.TryGetProperty("replayedFrom", out var rf) && rf.ValueKind == JsonValueKind.Number ? rf.GetInt64() : null;
            long eventId = root.TryGetProperty("eventId", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt64() : 0;
            string? data = root.TryGetProperty("data", out var d) ? (d.ValueKind == JsonValueKind.String ? d.GetString() : d.GetRawText()) : null;
            string? message = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            return new Frame(type, subId, replayedFrom, eventId, data, message);
        }
    }

    [Fact]
    public async Task Fresh_Subscribe_Delivers_Ack_Events_And_Complete()
    {
        await using var conn = CreateHub();
        await conn.StartAsync();

        var req = new SleipnirRequest { Controller = "HubStreamEvent", Method = "Tick", Id = "hub-fresh" };
        await using var e = OpenStream(conn, req, null, null);

        // 1. Ack first (ack-before-first-frame); no replayedFrom on a fresh durable subscribe.
        (await e.MoveNextAsync()).Should().BeTrue();
        var ack = Frame.Parse(e.Current);
        ack.Type.Should().Be("ack");
        ack.SubscriptionId.Should().NotBeNullOrEmpty();
        ack.ReplayedFrom.Should().BeNull();

        // 2. Push two events AFTER the ack (the durable observer is subscribed by then).
        HubStreamEventController.Stream.Push("a");
        HubStreamEventController.Stream.Push("b");

        (await e.MoveNextAsync()).Should().BeTrue();
        var ev1 = Frame.Parse(e.Current);
        ev1.Type.Should().Be("event");
        ev1.EventId.Should().Be(1);
        ev1.Data.Should().Be("a");

        (await e.MoveNextAsync()).Should().BeTrue();
        var ev2 = Frame.Parse(e.Current);
        ev2.Type.Should().Be("event");
        ev2.EventId.Should().Be(2);
        ev2.Data.Should().Be("b");

        // 3. Complete the source → a terminal complete frame, then the stream ends.
        HubStreamEventController.Stream.Complete();
        (await e.MoveNextAsync()).Should().BeTrue();
        var done = Frame.Parse(e.Current);
        done.Type.Should().Be("complete");
        (await e.MoveNextAsync()).Should().BeFalse("the stream ends after the terminal frame");
    }

    [Fact]
    public async Task Resume_Replays_Events_Produced_During_The_Disconnect_Gap()
    {
        await using var conn = CreateHub();
        await conn.StartAsync();

        string subId;
        long lastEventId;

        // ── Stream 1: fresh subscribe, read the ack + the first event ────────────────
        var req1 = new SleipnirRequest { Controller = "HubStreamResumeEvent", Method = "Tick", Id = "hub-1" };
        var e1 = OpenStream(conn, req1, null, null);
        try
        {
            (await e1.MoveNextAsync()).Should().BeTrue();
            var ack = Frame.Parse(e1.Current);
            ack.Type.Should().Be("ack");
            subId = ack.SubscriptionId!;
            HubStreamResumeEventController.Stream.Push("a");
            (await e1.MoveNextAsync()).Should().BeTrue();
            var ev1 = Frame.Parse(e1.Current);
            ev1.Type.Should().Be("event");
            ev1.EventId.Should().Be(1);
            ev1.Data.Should().Be("a");
            lastEventId = ev1.EventId;
        }
        finally
        {
            // Dispose stream 1 → sends a stream Cancel to the server → the hub `finally` runs →
            // Detach (the durable source + replay ring persist; only this tap is detached).
            await e1.DisposeAsync();
        }

        // Give the server a moment to detach the tap (the source subscription stays alive).
        await Task.Delay(300);

        // ── Gap: produce an event into the durable replay ring (no attached tap) ─────
        HubStreamResumeEventController.Stream.Push("b");

        // ── Stream 2: resume from lastEventId = 1 ───────────────────────────────────
        var resumeReq = new SleipnirRequest { Controller = "HubStreamResumeEvent", Method = "Tick", Id = "hub-2" };
        await using var e2 = OpenStream(conn, resumeReq, subId, lastEventId);

        (await e2.MoveNextAsync()).Should().BeTrue();
        var ack2 = Frame.Parse(e2.Current);
        ack2.Type.Should().Be("ack");
        ack2.SubscriptionId.Should().Be(subId, "the durable subscriptionId is stable across reconnects");
        ack2.ReplayedFrom.Should().Be(2, "the first replayed event is the one after lastEventId=1");

        (await e2.MoveNextAsync()).Should().BeTrue();
        var ev2 = Frame.Parse(e2.Current);
        ev2.Type.Should().Be("event");
        ev2.EventId.Should().Be(2);
        ev2.Data.Should().Be("b");

        // A live event after the replayed gap is delivered with the next monotonic eventId.
        HubStreamResumeEventController.Stream.Push("c");
        (await e2.MoveNextAsync()).Should().BeTrue();
        var ev3 = Frame.Parse(e2.Current);
        ev3.Type.Should().Be("event");
        ev3.EventId.Should().Be(3);
        ev3.Data.Should().Be("c");
    }

    [Fact]
    public async Task Authed_Subscribe_Without_Bearer_Rejects_The_Stream()
    {
        await using var conn = CreateHub(); // no bearer → anonymous
        await conn.StartAsync();

        var req = new SleipnirRequest { Controller = "HubStreamAuthedEvent", Method = "SecureTick", Id = "hub-auth" };
        var e = OpenStream(conn, req, null, null);
        await using var _ = e;

        // The server runs [SleipnirAuthorise(Role="Admin")] in SubscribeAsync → 401 → throws
        // HubException BEFORE yielding any item; the first MoveNextAsync surfaces it. The SignalR
        // streaming-error wrapper prepends "An error occurred on the server while streaming
        // results. HubException: ", so match on the embedded 401 message with a wildcard.
        var act = async () => await e.MoveNextAsync();
        await act.Should().ThrowAsync<HubException>().WithMessage("*Unauthorized*");
    }
}