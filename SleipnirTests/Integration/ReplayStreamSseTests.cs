using System.Net;
using System.Text.Json;
using FluentAssertions;
using SleipnirTests.Fixtures;
using Xunit;

namespace SleipnirTests.Integration;

/// <summary>
/// The "history + live" recipe end-to-end over SSE (<see cref="ReplayRunController"/>): a client
/// that subscribes while the run is in progress gets the history, then live events, then
/// <c>complete</c>; a re-subscribe with <c>afterSeq</c> gets only what it is missing; a client that
/// arrives after the run ended still gets everything; an unknown run completes immediately.
/// </summary>
public class ReplayStreamSseTests : IClassFixture<TransportTestFixture>
{
    private readonly TransportTestFixture _fixture;

    public ReplayStreamSseTests(TransportTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task LateSubscriber_GetsHistoryThenLiveThenComplete()
    {
        var runId = Guid.NewGuid().ToString("N");
        var stream = ReplayRunController.Runs.GetOrCreate(runId);
        stream.Publish(seq => new RunProgressEvent(seq, "step-1"));
        stream.Publish(seq => new RunProgressEvent(seq, "step-2"));

        using var http = new HttpClient { BaseAddress = new Uri(_fixture.BaseUrl) };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var resp = await http.GetAsync($"api/sleipnir/events/ReplayRun/Progress?runId={runId}",
            HttpCompletionOption.ResponseHeadersRead, cts.Token);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(cts.Token));

        (await ReadBlockAsync(reader, cts.Token))!.Event.Should().Be("ack");
        (await ReadEventAsync(reader, cts.Token)).Should().Be((1L, "step-1"));
        (await ReadEventAsync(reader, cts.Token)).Should().Be((2L, "step-2"));

        stream.Publish(seq => new RunProgressEvent(seq, "step-3"));
        (await ReadEventAsync(reader, cts.Token)).Should().Be((3L, "step-3"));

        stream.Complete();
        (await ReadBlockAsync(reader, cts.Token))!.Event.Should().Be("complete");
        (await ReadBlockAsync(reader, cts.Token)).Should().BeNull("the server closes the stream after complete");
    }

    [Fact]
    public async Task Resubscribe_WithAfterSeq_GetsOnlyTheMissingEvents()
    {
        var runId = Guid.NewGuid().ToString("N");
        var stream = ReplayRunController.Runs.GetOrCreate(runId);
        for (var i = 1; i <= 4; i++) stream.Publish(seq => new RunProgressEvent(seq, $"step-{seq}"));
        stream.Complete();

        using var http = new HttpClient { BaseAddress = new Uri(_fixture.BaseUrl) };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var resp = await http.GetAsync($"api/sleipnir/events/ReplayRun/Progress?runId={runId}&afterSeq=2",
            HttpCompletionOption.ResponseHeadersRead, cts.Token);
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(cts.Token));

        (await ReadBlockAsync(reader, cts.Token))!.Event.Should().Be("ack");
        (await ReadEventAsync(reader, cts.Token)).Should().Be((3L, "step-3"));
        (await ReadEventAsync(reader, cts.Token)).Should().Be((4L, "step-4"));
        (await ReadBlockAsync(reader, cts.Token))!.Event.Should().Be("complete",
            "a client arriving after the run ended still gets the history and the completion");
    }

    [Fact]
    public async Task HistoryLongerThanTheDefaultBuffer_IsReplayedCompletely()
    {
        // 300 events > the default send buffer (100): the recipe's BufferCapacity sizing keeps
        // the replay from dropping the oldest events.
        var runId = Guid.NewGuid().ToString("N");
        var stream = ReplayRunController.Runs.GetOrCreate(runId);
        for (var i = 0; i < 300; i++) stream.Publish(seq => new RunProgressEvent(seq, "s"));
        stream.Complete();

        using var http = new HttpClient { BaseAddress = new Uri(_fixture.BaseUrl) };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var resp = await http.GetAsync($"api/sleipnir/events/ReplayRun/Progress?runId={runId}",
            HttpCompletionOption.ResponseHeadersRead, cts.Token);
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(cts.Token));

        (await ReadBlockAsync(reader, cts.Token))!.Event.Should().Be("ack");
        var seqs = new List<long>();
        SseBlock? block;
        while ((block = await ReadBlockAsync(reader, cts.Token)) is { Event: "event" })
            seqs.Add(Payload(block).GetProperty("seq").GetInt64());

        seqs.Should().Equal(Enumerable.Range(1, 300).Select(i => (long)i));
        block!.Event.Should().Be("complete");
    }

    [Fact]
    public async Task UnknownRun_CompletesImmediately()
    {
        using var http = new HttpClient { BaseAddress = new Uri(_fixture.BaseUrl) };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var resp = await http.GetAsync($"api/sleipnir/events/ReplayRun/Progress?runId={Guid.NewGuid():N}",
            HttpCompletionOption.ResponseHeadersRead, cts.Token);
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(cts.Token));

        (await ReadBlockAsync(reader, cts.Token))!.Event.Should().Be("ack");
        (await ReadBlockAsync(reader, cts.Token))!.Event.Should().Be("complete");
    }

    private static JsonElement Payload(SseBlock block)
        => JsonSerializer.Deserialize<JsonElement>(block.Data).GetProperty("data");

    private static async Task<(long Seq, string Step)> ReadEventAsync(StreamReader reader, CancellationToken ct)
    {
        var block = await ReadBlockAsync(reader, ct);
        block.Should().NotBeNull();
        block!.Event.Should().Be("event");
        var data = Payload(block);
        return (data.GetProperty("seq").GetInt64(), data.GetProperty("step").GetString()!);
    }

    // SSE block reader — same parsing as SseEventTests (id:/event:/data: lines, blank line ends a block).
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
                if (!any) continue;
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
        }
        return any ? new SseBlock(id, evt, data.ToString()) : null;
    }

    private static string ValueAfterColon(string line, int colonIndex)
        => colonIndex + 1 < line.Length && line[colonIndex + 1] == ' ' ? line[(colonIndex + 2)..] : line[(colonIndex + 1)..];
}
