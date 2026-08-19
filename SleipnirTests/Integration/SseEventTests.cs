using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using SleipnirTests.Fixtures;
using Xunit;

namespace SleipnirTests.Integration;

/// <summary>
/// Integration tests for the SSE (Server-Sent Events) REST event transport — the REST-only
/// sibling of <see cref="WebSocketEventTests"/>. Proves the two <c>GET /events/…</c> endpoints
/// against the in-process Kestrel host (<see cref="TransportTestFixture"/>): fresh subscribe +
/// ack-before-first-event + events + complete, durable resume with <c>Last-Event-Id</c> gap
/// replay + live continuation, 410 on an unknown/ephemeral subscriptionId, the per-method
/// <c>[SleipnirAuthorise]</c> gate (401 without Bearer, stream with), and the non-resumable
/// (ephemeral) path. Reads the <c>text/event-stream</c> body line-by-line with
/// <c>HttpCompletionOption.ResponseHeadersRead</c> (the stream is open-ended).
/// </summary>
public class SseEventTests : IClassFixture<TransportTestFixture>
{
    private readonly TransportTestFixture _fixture;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public SseEventTests(TransportTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task FreshSubscribe_ReceivesEventsAndComplete_AckFirst()
    {
        // Cold non-resumable event: ?count=3 emits evt-0, evt-1, evt-2 then completes.
        using var http = new HttpClient { BaseAddress = new Uri(_fixture.BaseUrl) };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var resp = await http.GetAsync(
            "api/sleipnir/events/TestInvoker/ObservableStrings?count=3",
            HttpCompletionOption.ResponseHeadersRead, cts.Token);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");

        using var body = await resp.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(body);

        // Block 1: the ack (id:0, event:ack) — must precede any event frame (race-fix invariant).
        var ack = await ReadBlockAsync(reader, cts.Token);
        ack.Should().NotBeNull();
        ack!.Event.Should().Be("ack");
        ack.Id.Should().Be(0);
        var subscriptionId = (JsonSerializer.Deserialize<JsonElement>(ack.Data)
            .GetProperty("subscriptionId").GetString())!;
        subscriptionId.Should().NotBeNullOrEmpty();
        // Fresh non-resumable subscribe → no replayedFrom in the ack.
        JsonSerializer.Deserialize<JsonElement>(ack.Data).TryGetProperty("replayedFrom", out _).Should().BeFalse();

        // Blocks 2-4: three event frames, monotonic eventIds 1..3, shared subscriptionId.
        for (int i = 1; i <= 3; i++)
        {
            var block = await ReadBlockAsync(reader, cts.Token);
            block.Should().NotBeNull();
            block!.Event.Should().Be("event");
            block.Id.Should().Be(i);
            var frame = JsonSerializer.Deserialize<JsonElement>(block.Data);
            frame.GetProperty("type").GetString().Should().Be("event");
            frame.GetProperty("subscriptionId").GetString().Should().Be(subscriptionId);
            frame.GetProperty("eventId").GetInt64().Should().Be(i);
            frame.GetProperty("data").GetString().Should().Be($"evt-{i - 1}");
        }

        // Block 5: complete (no id: line — the frame carries no eventId).
        var complete = await ReadBlockAsync(reader, cts.Token);
        complete.Should().NotBeNull();
        complete!.Event.Should().Be("complete");
        complete.Id.Should().BeNull();
        var doneFrame = JsonSerializer.Deserialize<JsonElement>(complete.Data);
        doneFrame.GetProperty("type").GetString().Should().Be("complete");
        doneFrame.GetProperty("subscriptionId").GetString().Should().Be(subscriptionId);

        // After complete the server closes the response → the reader hits EOF.
        (await ReadBlockAsync(reader, cts.Token)).Should().BeNull();
    }

    [Fact]
    public async Task Resume_ReplaysGapAndContinuesLive()
    {
        using var http = new HttpClient { BaseAddress = new Uri(_fixture.BaseUrl) };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        // 1. Fresh durable subscribe → ack (no replayedFrom). Capture the subscriptionId, then
        //    drop the connection so the server Detaches (source + ring persist for resume).
        string subscriptionId;
        using (var fresh = await http.GetAsync(
            "api/sleipnir/events/SseEvent/Tick", HttpCompletionOption.ResponseHeadersRead, cts.Token))
        {
            fresh.StatusCode.Should().Be(HttpStatusCode.OK);
            using var body = await fresh.Content.ReadAsStreamAsync(cts.Token);
            using var reader = new StreamReader(body);
            var ack = await ReadBlockAsync(reader, cts.Token);
            subscriptionId = JsonSerializer.Deserialize<JsonElement>(ack!.Data)
                .GetProperty("subscriptionId").GetString()!;
        }
        // Give the server a moment to observe the disconnect and Detach the tap.
        await Task.Delay(150, cts.Token);

        // 2. Produce events during the gap — no client attached → they land in the replay ring only.
        SseEventController.Stream.Push("gap-0");
        SseEventController.Stream.Push("gap-1");

        // 3. Resume with Last-Event-Id: 0 → the ring snapshot replays both gap events (eventIds 1,2).
        using var req = new HttpRequestMessage(HttpMethod.Get, $"api/sleipnir/events/{subscriptionId}");
        req.Headers.Add("Last-Event-Id", "0");
        using var resume = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        resume.StatusCode.Should().Be(HttpStatusCode.OK);
        using var rbody = await resume.Content.ReadAsStreamAsync(cts.Token);
        using var rreader = new StreamReader(rbody);

        var ack2 = await ReadBlockAsync(rreader, cts.Token);
        ack2!.Event.Should().Be("ack");
        var ack2Data = JsonSerializer.Deserialize<JsonElement>(ack2.Data);
        ack2Data.GetProperty("subscriptionId").GetString().Should().Be(subscriptionId);
        ack2Data.GetProperty("replayedFrom").GetInt64().Should().Be(1);   // first replayed eventId

        // Two replayed gap events in order.
        var gap0 = await ReadBlockAsync(rreader, cts.Token);
        gap0!.Event.Should().Be("event");
        gap0.Id.Should().Be(1);
        JsonSerializer.Deserialize<JsonElement>(gap0.Data).GetProperty("data").GetString().Should().Be("gap-0");
        var gap1 = await ReadBlockAsync(rreader, cts.Token);
        gap1!.Event.Should().Be("event");
        gap1.Id.Should().Be(2);
        JsonSerializer.Deserialize<JsonElement>(gap1.Data).GetProperty("data").GetString().Should().Be("gap-1");

        // 4. Push a live event now that the tap is attached → it flows live (eventId 3).
        SseEventController.Stream.Push("live-0");
        var live = await ReadBlockAsync(rreader, cts.Token);
        live!.Event.Should().Be("event");
        live.Id.Should().Be(3);
        JsonSerializer.Deserialize<JsonElement>(live.Data).GetProperty("data").GetString().Should().Be("live-0");

        // 5. Complete the source → terminal frame → stream closes.
        SseEventController.Stream.Complete();
        var term = await ReadBlockAsync(rreader, cts.Token);
        term!.Event.Should().Be("complete");
        (await ReadBlockAsync(rreader, cts.Token)).Should().BeNull();
    }

    [Fact]
    public async Task Resume_UnknownSubscriptionId_Returns410()
    {
        using var http = new HttpClient { BaseAddress = new Uri(_fixture.BaseUrl) };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // A durable id that was never created (or has been GC'd) → Lookup returns null → 410 Gone.
        using var resp = await http.GetAsync(
            "api/sleipnir/events/neverExisted000", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        resp.StatusCode.Should().Be(HttpStatusCode.Gone);
        // A 410 has no body/content-type — certainly not an event stream.
        var mediaType = resp.Content.Headers.ContentType?.MediaType;
        mediaType.Should().NotBe("text/event-stream");
    }

    [Fact]
    public async Task NonResumable_EventSubscription_CannotResume_Returns410()
    {
        // A non-resumable event creates an EPHEMERAL subscription (a random Guid, not in the durable
        // store). Resuming by that id → Lookup returns null → 410. Proves non-resumable events have
        // no resume surface over SSE (same as over WebSocket).
        using var http = new HttpClient { BaseAddress = new Uri(_fixture.BaseUrl) };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        using var fresh = await http.GetAsync(
            "api/sleipnir/events/TestInvoker/ObservableStrings?count=1",
            HttpCompletionOption.ResponseHeadersRead, cts.Token);
        await using var body = await fresh.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(body);
        var ack = await ReadBlockAsync(reader, cts.Token);
        var subscriptionId = JsonSerializer.Deserialize<JsonElement>(ack!.Data)
            .GetProperty("subscriptionId").GetString()!;

        // Drain the cold stream to completion so the ephemeral subscription is disposed.
        await ReadBlockAsync(reader, cts.Token); // event
        await ReadBlockAsync(reader, cts.Token); // complete

        // Resume by the ephemeral id → 410 (not durable).
        using var resp = await http.GetAsync(
            $"api/sleipnir/events/{subscriptionId}", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        resp.StatusCode.Should().Be(HttpStatusCode.Gone);
    }

    [Fact]
    public async Task AuthedEvent_WithoutBearer_Returns401_NotStream()
    {
        using var http = new HttpClient { BaseAddress = new Uri(_fixture.BaseUrl) };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // [SleipnirAuthorise(Role="Admin")] — no Bearer → SubscribeAsync → Unauthorized (401),
        // returned as a normal HTTP error (no text/event-stream).
        using var resp = await http.GetAsync(
            "api/sleipnir/events/AuthedResumableEvent/SecureTick",
            HttpCompletionOption.ResponseHeadersRead, cts.Token);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var ct = resp.Content.Headers.ContentType?.MediaType;
        ct.Should().NotBe("text/event-stream");
    }

    [Fact]
    public async Task AuthedEvent_WithBearer_StreamsAck()
    {
        using var http = new HttpClient { BaseAddress = new Uri(_fixture.BaseUrl) };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var req = new HttpRequestMessage(HttpMethod.Get, "api/sleipnir/events/AuthedResumableEvent/SecureTick");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAuthHandler.ValidToken);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        using var body = await resp.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(body);
        var ack = await ReadBlockAsync(reader, cts.Token);
        ack!.Event.Should().Be("ack");
        JsonSerializer.Deserialize<JsonElement>(ack.Data).GetProperty("subscriptionId").GetString().Should().NotBeNullOrEmpty();
    }

    // ─── SSE block reader ───────────────────────────────────────────────────────
    // A block is a sequence of `field: value` lines terminated by a blank line. SSE id:/event:/
    // data: lines map to (Id, Event, Data). data: lines are concatenated with '\n' (our frames are
    // single-line JSON, so there is exactly one). EOF (the server closed the stream) → null.

    private sealed record SseBlock(long? Id, string Event, string Data);

    private static async Task<SseBlock?> ReadBlockAsync(StreamReader reader, CancellationToken ct)
    {
        long? id = null;
        var evt = string.Empty;
        var data = new StringWriter();
        bool any = false;
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (line.Length == 0)
            {
                if (!any) continue;          // tolerate leading blank lines
                return new SseBlock(id, evt, data.ToString());
            }
            any = true;
            if (line.StartsWith("id:", StringComparison.Ordinal))
                id = long.TryParse(line.AsSpan(3).Trim(), out var n) ? n : id;
            else if (line.StartsWith("event:", StringComparison.Ordinal))
                evt = ValueAfterColon(line, 6);
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.ToString().Length > 0) data.WriteLine();
                data.Write(ValueAfterColon(line, 5));
            }
            // Other fields (e.g. retry:, comments) are ignored.
        }
        return any ? new SseBlock(id, evt, data.ToString()) : null;
    }

    private static string ValueAfterColon(string line, int colonIndex)
    {
        // SSE: the value is everything after the colon; one leading space is stripped by convention.
        if (colonIndex + 1 < line.Length && line[colonIndex + 1] == ' ')
            return line[(colonIndex + 2)..];
        return line[(colonIndex + 1)..];
    }
}