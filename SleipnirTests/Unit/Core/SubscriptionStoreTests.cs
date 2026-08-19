using FluentAssertions;
using SleipnirCommon.Models;
using SleipnirCore.Services;
using SleipnirCore.Tracing;
using System.Text.Json;
using Xunit;

namespace SleipnirTests.Unit.Core;

/// <summary>
/// Unit tests for the Phase R durable subscription store
/// (<see cref="SleipnirSubscriptionStore"/> + <see cref="DurableSubscriptionState"/>):
/// attach/resume replay, the dedup boundary (eventId &gt; lastEventId), ring-buffer
/// overflow → drop count, the process-wide cap reject, source-complete GC, idle-TTL
/// eviction, and the live-subscription gauge accounting.
/// </summary>
/// <remarks>
/// The drop-count test sets the process-wide <see cref="SleipnirConnectionRegistry"/>
/// singleton (<c>SleipnirMetrics.EventDropped</c> bumps <c>Current</c>), so this class
/// joins the <c>sleipnir-tracing</c> collection — serialized with the other process-global
/// telemetry tests to avoid racing the static instance.
/// </remarks>
[Collection("sleipnir-tracing")]
public class SubscriptionStoreTests
{
    /// <summary>Builds a frame string carrying the given eventId (the durable observer format).</summary>
    private static string Frame(long eventId) =>
        JsonSerializer.Serialize(new { type = "event", subscriptionId = "s", eventId, data = (string?)null });

    /// <summary>Reads up to <paramref name="expected"/> eventIds from a tap, with a timeout.</summary>
    private static async Task<List<long>> ReadEventIdsAsync(Tap tap, int expected, int timeoutMs = 3000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var ids = new List<long>();
        try
        {
            while (ids.Count < expected)
            {
                if (!await tap.Reader.WaitToReadAsync(cts.Token))
                    break; // channel completed (terminal/detach) — stop early
                while (ids.Count < expected && tap.Reader.TryRead(out var frame))
                {
                    using var doc = JsonDocument.Parse(frame);
                    if (doc.RootElement.TryGetProperty("eventId", out var eid))
                        ids.Add(eid.GetInt64());
                }
            }
        }
        catch (OperationCanceledException) { /* timeout — return whatever arrived */ }
        return ids;
    }

    [Fact]
    public async Task Attach_Replays_All_Buffered_Events_When_LastEventId_Zero()
    {
        var store = new SleipnirSubscriptionStore(new SleipnirConnectionRegistry(), replayBufferCapacity: 100,
            resumeTtl: TimeSpan.FromSeconds(60), maxDurable: 100, logger: null);
        await using (store)
        {
            var state = store.BeginCreate(EventBackpressureStrategy.DropOldest)!;
            state.AppendEvent(1, Frame(1));
            state.AppendEvent(2, Frame(2));
            state.AppendEvent(3, Frame(3));

            var tap = state.Attach(lastEventId: 0);
            tap.ReplayedFrom.Should().Be(1);
            var ids = await ReadEventIdsAsync(tap, expected: 3);
            ids.Should().Equal([1L, 2L, 3L]);
        }
    }

    [Fact]
    public async Task Attach_Replays_Only_Events_After_LastEventId()
    {
        var store = new SleipnirSubscriptionStore(new SleipnirConnectionRegistry(), 100,
            TimeSpan.FromSeconds(60), 100, null);
        await using (store)
        {
            var state = store.BeginCreate(EventBackpressureStrategy.DropOldest)!;
            for (long i = 1; i <= 5; i++)
                state.AppendEvent(i, Frame(i));

            // Client already processed eventId 3 → replay 4 and 5 only.
            var tap = state.Attach(lastEventId: 3);
            tap.ReplayedFrom.Should().Be(4);
            var ids = await ReadEventIdsAsync(tap, expected: 2);
            ids.Should().Equal([4L, 5L]);
        }
    }

    [Fact]
    public async Task Attach_With_LastEventId_At_Max_Replays_Nothing()
    {
        var store = new SleipnirSubscriptionStore(new SleipnirConnectionRegistry(), 100,
            TimeSpan.FromSeconds(60), 100, null);
        await using (store)
        {
            var state = store.BeginCreate(EventBackpressureStrategy.DropOldest)!;
            state.AppendEvent(1, Frame(1));
            state.AppendEvent(2, Frame(2));

            var tap = state.Attach(lastEventId: 2);
            tap.ReplayedFrom.Should().BeNull();
            var ids = await ReadEventIdsAsync(tap, expected: 2, timeoutMs: 500);
            ids.Should().BeEmpty("the client is already up to date — nothing to replay");
        }
    }

    [Fact]
    public async Task RingBuffer_Overflow_Evicts_Oldest_And_Counts_Drops()
    {
        // The store's OnDropped routes through SleipnirMetrics.EventDropped → Current registry.
        var registry = new SleipnirConnectionRegistry();
        SleipnirConnectionRegistry.SetInstance(registry);
        SleipnirMetrics.SetConnectionRegistry(registry);

        var store = new SleipnirSubscriptionStore(registry, replayBufferCapacity: 3,
            resumeTtl: TimeSpan.FromSeconds(60), maxDurable: 100, logger: null);
        await using (store)
        {
            var state = store.BeginCreate(EventBackpressureStrategy.DropOldest)!;
            for (long i = 1; i <= 5; i++)
                state.AppendEvent(i, Frame(i));

            // 5 events into a cap-3 ring → 2 evicted (oldest: 1 and 2) → 2 drops counted.
            registry.EventDroppedTotal.Should().Be(2);

            // The surviving window is the last 3 events (3, 4, 5).
            var tap = state.Attach(lastEventId: 0);
            var ids = await ReadEventIdsAsync(tap, expected: 3);
            ids.Should().Equal([3L, 4L, 5L]);
        }
    }

    [Fact]
    public void BeginCreate_Rejects_Over_The_Process_Wide_Cap()
    {
        var store = new SleipnirSubscriptionStore(new SleipnirConnectionRegistry(), 100,
            TimeSpan.FromSeconds(60), maxDurable: 1, logger: null);

        var first = store.BeginCreate(EventBackpressureStrategy.DropOldest);
        var second = store.BeginCreate(EventBackpressureStrategy.DropOldest);

        first.Should().NotBeNull();
        second.Should().BeNull("the process-wide durable cap (1) is reached");
    }

    [Fact]
    public async Task Completed_Source_Marks_State_For_Gc_And_Replays_Terminal()
    {
        var store = new SleipnirSubscriptionStore(new SleipnirConnectionRegistry(), 100,
            TimeSpan.FromSeconds(60), 100, null);
        await using (store)
        {
            var state = store.BeginCreate(EventBackpressureStrategy.DropOldest)!;
            state.AppendEvent(1, Frame(1));
            state.SetTerminal(JsonSerializer.Serialize(new { type = "complete", subscriptionId = "s" }));

            state.Completed.Should().BeTrue();

            var tap = state.Attach(lastEventId: 0);
            // The replayed event plus the terminal frame complete the channel.
            using var cts = new CancellationTokenSource(2000);
            var frames = new List<string>();
            await foreach (var f in tap.Reader.ReadAllAsync(cts.Token))
                frames.Add(f);
            frames.Should().HaveCount(2);
            frames[0].Should().Contain("\"eventId\":1");
            frames[1].Should().Contain("\"type\":\"complete\"");
        }
    }

    [Fact]
    public async Task Ttl_Eviction_Removes_Detached_State()
    {
        var store = new SleipnirSubscriptionStore(new SleipnirConnectionRegistry(), 100,
            resumeTtl: TimeSpan.FromMilliseconds(50), maxDurable: 100, logger: null);
        var state = store.BeginCreate(EventBackpressureStrategy.DropOldest)!;
        var id = state.SubscriptionId;

        store.Lookup(id).Should().NotBeNull();
        store.OnAttached();
        var tap = state.Attach(0);
        store.Detach(id); // no tap → idle TTL countdown begins

        // Poll for up to 2s — the GC timer (period = TTL) evicts the detached state.
        var evicted = false;
        for (int i = 0; i < 40; i++)
        {
            await Task.Delay(50);
            if (store.Lookup(id) is null) { evicted = true; break; }
        }
        evicted.Should().BeTrue("a detached durable subscription is reclaimed after the idle TTL");

        await store.DisposeAsync();
    }

    [Fact]
    public void Gauge_Accounting_OnAttached_Detach_Destroy()
    {
        var registry = new SleipnirConnectionRegistry();
        var store = new SleipnirSubscriptionStore(registry, 100, TimeSpan.FromSeconds(60), 100, null);

        // Create + attach (fresh subscribe) → gauge +1. Attach installs the live tap.
        var state = store.BeginCreate(EventBackpressureStrategy.DropOldest)!;
        state.Attach(0);
        store.OnAttached();
        registry.Subscriptions.Should().Be(1);

        // Detach (disconnect) → gauge -1; source + buffer persist, tap gone.
        store.Detach(state.SubscriptionId).Should().BeTrue();
        registry.Subscriptions.Should().Be(0);

        // Re-attach (resume) installs a fresh tap → gauge +1 again.
        state.Attach(0);
        store.OnAttached();
        registry.Subscriptions.Should().Be(1);

        // Destroy (explicit unsubscribe) with a tap still attached → gauge -1.
        store.Destroy(state.SubscriptionId).Should().BeTrue();
        registry.Subscriptions.Should().Be(0);

        // Destroy is idempotent-ish: a second destroy on the (now removed) id is a no-op.
        store.Destroy(state.SubscriptionId).Should().BeFalse();
    }

    [Fact]
    public void Detach_On_Unknown_Id_Is_NoOp()
    {
        var store = new SleipnirSubscriptionStore(new SleipnirConnectionRegistry(), 100,
            TimeSpan.FromSeconds(60), 100, null);
        store.Detach("does-not-exist").Should().BeFalse("an unknown id (e.g. already GC'd) is a no-op");
    }
}