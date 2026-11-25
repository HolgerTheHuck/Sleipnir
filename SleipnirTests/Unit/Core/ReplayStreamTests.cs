using FluentAssertions;
using SleipnirCore.Events;
using Xunit;

namespace SleipnirTests.Unit.Core;

/// <summary>
/// <see cref="SleipnirReplayStream{T}"/> / <see cref="SleipnirReplayStreams{TKey, T}"/>: history
/// then live without gaps or duplicates, sequence numbers, the <c>afterSeq</c> cursor, termination
/// for late subscribers, history eviction, observer isolation, and the registry's retention.
/// </summary>
public class ReplayStreamTests
{
    private sealed class Recorder<T> : IObserver<T>
    {
        public readonly List<T> Values = new();
        public bool Completed;
        public Exception? Error;
        public Action<T>? OnValue;
        public void OnNext(T value) { lock (Values) Values.Add(value); OnValue?.Invoke(value); }
        public void OnCompleted() => Completed = true;
        public void OnError(Exception error) => Error = error;
    }

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void LateSubscriber_GetsHistoryThenLiveThenCompletion()
    {
        var stream = new SleipnirReplayStream<string>();
        stream.Publish("a");
        stream.Publish("b");

        var late = new Recorder<string>();
        using var _ = stream.Subscribe(late);
        late.Values.Should().Equal("a", "b");

        stream.Publish("c");
        stream.Complete();

        late.Values.Should().Equal("a", "b", "c");
        late.Completed.Should().BeTrue();
    }

    [Fact]
    public void Publish_AssignsGapFreeSequenceNumbers_IntoThePayload()
    {
        var stream = new SleipnirReplayStream<(long Seq, string Step)>();

        stream.Publish(seq => (seq, "x")).Should().Be(1);
        stream.Publish(seq => (seq, "y")).Should().Be(2);

        var r = new Recorder<(long Seq, string Step)>();
        stream.Subscribe(r);
        r.Values.Select(v => v.Seq).Should().Equal(1, 2);
        stream.LastSeq.Should().Be(2);
    }

    [Fact]
    public void From_SkipsWhatTheClientAlreadyHas()
    {
        var stream = new SleipnirReplayStream<long>();
        for (var i = 0; i < 5; i++) stream.Publish(seq => seq);

        var r = new Recorder<long>();
        stream.From(3).Subscribe(r);
        stream.Publish(seq => seq);

        r.Values.Should().Equal(4, 5, 6);
    }

    [Fact]
    public void AfterTermination_LateSubscriberGetsHistoryAndTerminal_PublishThrows()
    {
        var stream = new SleipnirReplayStream<string>();
        stream.Publish("a");
        stream.Fail(new InvalidOperationException("boom"));

        var r = new Recorder<string>();
        stream.Subscribe(r);

        r.Values.Should().Equal("a");
        r.Error.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be("boom");
        stream.IsTerminated.Should().BeTrue();
        stream.Invoking(s => s.Publish("late")).Should().Throw<InvalidOperationException>();
        stream.Invoking(s => s.Complete()).Should().NotThrow("terminating twice is a no-op");
    }

    [Fact]
    public void History_IsCappedAtMaxHistory_OldestEvicted()
    {
        var stream = new SleipnirReplayStream<long>(maxHistory: 3);
        for (var i = 0; i < 5; i++) stream.Publish(seq => seq);

        var r = new Recorder<long>();
        stream.Subscribe(r);

        r.Values.Should().Equal(3, 4, 5);
        stream.FirstRetainedSeq.Should().Be(3);
    }

    [Fact]
    public void Dispose_StopsDelivery()
    {
        var stream = new SleipnirReplayStream<string>();
        var r = new Recorder<string>();
        var sub = stream.Subscribe(r);

        stream.Publish("a");
        sub.Dispose();
        stream.Publish("b");

        r.Values.Should().Equal("a");
    }

    [Fact]
    public void ThrowingObserver_IsDetached_WithoutBreakingProducerOrOthers()
    {
        var stream = new SleipnirReplayStream<string>();
        var bad = new Recorder<string> { OnValue = _ => throw new InvalidOperationException() };
        var good = new Recorder<string>();
        stream.Subscribe(bad);
        stream.Subscribe(good);

        stream.Invoking(s => s.Publish("a")).Should().NotThrow();
        stream.Publish("b");

        good.Values.Should().Equal("a", "b");
        bad.Values.Should().Equal("a");
    }

    [Fact]
    public async Task ConcurrentPublishAndSubscribe_EverySubscriberSeesAContiguousOrderedTail()
    {
        var stream = new SleipnirReplayStream<long>(maxHistory: 10_000);
        const int total = 5_000;
        var recorders = new List<Recorder<long>>();

        var producer = Task.Run(() => { for (var i = 0; i < total; i++) stream.Publish(seq => seq); stream.Complete(); });
        var subscribers = Task.Run(() =>
        {
            for (var i = 0; i < 50; i++)
            {
                var r = new Recorder<long>();
                lock (recorders) recorders.Add(r);
                stream.Subscribe(r);
            }
        });
        await Task.WhenAll(producer, subscribers);

        foreach (var r in recorders)
        {
            r.Completed.Should().BeTrue();
            r.Values.Should().Equal(Enumerable.Range(1, total).Select(i => (long)i),
                "history + live hand over without a gap or duplicate, in sequence order");
        }
    }

    [Fact]
    public void Registry_Observe_UnknownKeyCompletesEmpty_AndDoesNotCreate()
    {
        var runs = new SleipnirReplayStreams<string, int>();
        var r = new Recorder<int>();

        runs.Observe("nope").Subscribe(r);

        r.Values.Should().BeEmpty();
        r.Completed.Should().BeTrue();
        runs.Count.Should().Be(0);
    }

    [Fact]
    public void Registry_EvictsFinishedStreamsAfterRetention_KeepsRunningOnes()
    {
        var time = new ManualTime();
        var runs = new SleipnirReplayStreams<string, int>(completedRetention: TimeSpan.FromMinutes(5), timeProvider: time);
        runs.GetOrCreate("done").Publish(1);
        runs.GetOrCreate("done").Complete();
        runs.GetOrCreate("running").Publish(1);

        time.Now += TimeSpan.FromMinutes(4);
        runs.TryGet("done", out var stillThere).Should().BeTrue("within the retention a late subscriber still gets the history");
        var r = new Recorder<int>();
        stillThere.Subscribe(r);
        r.Values.Should().Equal(1);
        r.Completed.Should().BeTrue();

        time.Now += TimeSpan.FromMinutes(2);
        runs.TryGet("done", out _).Should().BeFalse();
        runs.TryGet("running", out _).Should().BeTrue("streams that never complete are not evicted");
        runs.GetOrCreate("done").LastSeq.Should().Be(0, "a re-run under an evicted key starts a fresh stream");
    }
}
