using FluentAssertions;
using SleipnirCommon.Models;
using Xunit;

namespace SleipnirTests.Unit.EventBuffer;

/// <summary>
/// Unit tests for the per-subscription <see cref="SleipnirCore.Events.EventBuffer"/> backpressure
/// buffer. Covers all four <see cref="EventBackpressureStrategy"/> modes and the drop-counting
/// fix (the old BoundedChannel(DropOldest) path could not detect saturation — TryWrite returned
/// true unconditionally, so sleipnir.event.dropped was dead code). The buffer is
/// transport-agnostic (SleipnirCore.Events); the WebSocket and SSE transports drain it into
/// their respective sinks.
/// </summary>
public class EventBufferTests
{
    private static SleipnirCore.Events.EventBuffer NewBuffer(
        int capacity, EventBackpressureStrategy strategy, CancellationToken ct = default)
        => new(capacity, strategy, ct);

    [Fact]
    public async Task DropOldest_EvictsOldest_AndCountsDrops()
    {
        using var cts = new CancellationTokenSource();
        var buf = NewBuffer(3, EventBackpressureStrategy.DropOldest, cts.Token);
        int drops = 0;
        for (int i = 0; i < 5; i++)
            buf.TryEnqueue($"e{i}", () => drops++);

        drops.Should().Be(2);                       // e0, e1 evicted
        buf.Complete();
        var read = new List<string>();
        await foreach (var f in buf.ReadAllAsync(cts.Token)) read.Add(f);
        read.Should().Equal(["e2", "e3", "e4"]);    // the three newest survived
    }

    [Fact]
    public async Task DropWrite_DropsNewest_AndCountsDrops()
    {
        using var cts = new CancellationTokenSource();
        var buf = NewBuffer(3, EventBackpressureStrategy.DropWrite, cts.Token);
        int drops = 0;
        var accepted = new List<bool>();
        for (int i = 0; i < 5; i++)
            accepted.Add(buf.TryEnqueue($"e{i}", () => drops++));

        accepted.Should().Equal([true, true, true, false, false]); // e3, e4 rejected
        drops.Should().Be(2);
        buf.Complete();
        var read = new List<string>();
        await foreach (var f in buf.ReadAllAsync(cts.Token)) read.Add(f);
        read.Should().Equal(["e0", "e1", "e2"]);    // the backlog in order, newest lost
    }

    [Fact]
    public async Task Unbounded_NeverDrops_AndIgnoresCapacity()
    {
        using var cts = new CancellationTokenSource();
        var buf = NewBuffer(0, EventBackpressureStrategy.Unbounded, cts.Token);
        int drops = 0;
        for (int i = 0; i < 50; i++)
            buf.TryEnqueue($"e{i}", () => drops++).Should().BeTrue();

        drops.Should().Be(0);
        buf.Complete();
        var read = new List<string>();
        await foreach (var f in buf.ReadAllAsync(cts.Token)) read.Add(f);
        read.Should().HaveCount(50);
    }

    [Fact]
    public async Task Block_BlocksProducerUntilReaderDrains_AndNeverDrops()
    {
        using var cts = new CancellationTokenSource();
        var buf = NewBuffer(2, EventBackpressureStrategy.Block, cts.Token);
        int drops = 0;

        buf.TryEnqueue("e0", () => drops++).Should().BeTrue();
        buf.TryEnqueue("e1", () => drops++).Should().BeTrue();   // buffer full now

        // Third enqueue must block (no free slot).
        var enqueueTask = Task.Run(() => buf.TryEnqueue("e2", () => drops++));
        await Task.Delay(60);
        enqueueTask.IsCompleted.Should().BeFalse("producer should be blocked on a full Block buffer");

        // Start a reader; draining one slot unblocks the producer.
        var readTask = Task.Run(async () =>
        {
            var items = new List<string>();
            await foreach (var f in buf.ReadAllAsync(cts.Token))
            {
                items.Add(f);
                if (items.Count == 1) { await Task.Delay(20); buf.Complete(); }
            }
            return items;
        });

        (await enqueueTask).Should().BeTrue("the blocked enqueue completes once a slot frees");
        drops.Should().Be(0, "Block never drops");
        var items = await readTask;
        items.Should().StartWith(["e0", "e1", "e2"]);
    }

    [Fact]
    public async Task TerminalFrame_IsForcedThrough_EvenWhenFull()
    {
        using var cts = new CancellationTokenSource();
        var buf = NewBuffer(1, EventBackpressureStrategy.DropWrite, cts.Token);
        int drops = 0;

        buf.TryEnqueue("e0", () => drops++).Should().BeTrue();
        buf.TryEnqueue("e1", () => drops++).Should().BeFalse();  // dropped (newest)
        drops.Should().Be(1);

        buf.EnqueueTerminal("T");                                // must reach the client despite full buffer
        var read = new List<string>();
        await foreach (var f in buf.ReadAllAsync(cts.Token)) read.Add(f);
        read.Should().Equal(["e0", "T"]);                        // e1 dropped, terminal forced through
    }

    [Fact]
    public async Task Complete_WakesABlockedReader_WithEmptyBuffer()
    {
        using var cts = new CancellationTokenSource();
        var buf = NewBuffer(3, EventBackpressureStrategy.DropOldest, cts.Token);

        var readTask = Task.Run(async () =>
        {
            var items = new List<string>();
            await foreach (var f in buf.ReadAllAsync(cts.Token)) items.Add(f);
            return items;
        });
        await Task.Delay(60);
        readTask.IsCompleted.Should().BeFalse("reader should be blocked on an empty buffer");

        buf.Complete();
        var items = await readTask;
        items.Should().BeEmpty();
    }

    [Fact]
    public async Task DisposeToken_UnblocksBlockedProducer_WithoutCountingDrop()
    {
        using var cts = new CancellationTokenSource();
        var buf = NewBuffer(1, EventBackpressureStrategy.Block, cts.Token);
        int drops = 0;
        buf.TryEnqueue("e0", () => drops++).Should().BeTrue();

        var enqueueTask = Task.Run(() => buf.TryEnqueue("e1", () => drops++));
        await Task.Delay(60);
        enqueueTask.IsCompleted.Should().BeFalse();

        cts.Cancel();                                             // dispose → unblock
        (await enqueueTask).Should().BeFalse("dispose aborts the blocked enqueue");
        drops.Should().Be(0, "abort-on-dispose is not a drop");
    }
}