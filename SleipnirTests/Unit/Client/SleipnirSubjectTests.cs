using FluentAssertions;
using SleipnirClient.Sleipnir;
using Xunit;

namespace SleipnirTests.Unit.Client;

/// <summary>
/// The subject behind every C# <c>SleipnirSubscription</c>: events (and a terminal signal) that
/// arrive before the first observer — a hot source pushing right behind the subscribe ack, before
/// SubscribeAsync has returned — are held for that first observer and replayed in order.
/// </summary>
public class SleipnirSubjectTests
{
    private sealed class Recorder<T> : IObserver<T>
    {
        public readonly List<T> Values = new();
        public bool Completed;
        public Exception? Error;
        public void OnNext(T value) { lock (Values) Values.Add(value); }
        public void OnCompleted() => Completed = true;
        public void OnError(Exception error) => Error = error;
    }

    [Fact]
    public void EventsBeforeTheFirstObserver_AreReplayedToIt_ThenTheSubjectIsHot()
    {
        var subject = new SleipnirSubject<int>();
        subject.OnNext(1);
        subject.OnNext(2);

        var first = new Recorder<int>();
        subject.Subscribe(first);
        subject.OnNext(3);
        var second = new Recorder<int>();
        subject.Subscribe(second);
        subject.OnNext(4);

        first.Values.Should().Equal(1, 2, 3, 4);
        second.Values.Should().Equal(4);
    }

    [Fact]
    public void HeldEvents_AreBounded_OldestDropped()
    {
        var subject = new SleipnirSubject<int>();
        for (var i = 0; i < SleipnirSubject<int>.MaxHeldForFirstObserver + 5; i++)
            subject.OnNext(i);

        var observer = new Recorder<int>();
        subject.Subscribe(observer);

        observer.Values.Should().HaveCount(SleipnirSubject<int>.MaxHeldForFirstObserver);
        observer.Values[0].Should().Be(5);
    }

    [Fact]
    public void ATerminalSignalBeforeTheFirstObserver_IsReplayedAfterTheHeldEvents()
    {
        var completed = new SleipnirSubject<string>();
        completed.OnNext("a");
        completed.OnCompleted();
        var c = new Recorder<string>();
        completed.Subscribe(c);
        c.Values.Should().Equal("a");
        c.Completed.Should().BeTrue();

        var failed = new SleipnirSubject<string>();
        failed.OnNext("b");
        failed.OnError(new InvalidOperationException("boom"));
        var f = new Recorder<string>();
        failed.Subscribe(f);
        f.Values.Should().Equal("b");
        f.Error.Should().BeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task SubscribingWhileEventsFlow_KeepsOrder_NoGapNoReordering()
    {
        // The reader thread pushes while the caller attaches its observer: whatever the observer
        // receives must be a consecutive, ordered run (the held replay runs under the lock, so no
        // concurrent OnNext overtakes it).
        var subject = new SleipnirSubject<int>();
        var producer = Task.Run(() =>
        {
            for (var i = 0; i < 20_000; i++) subject.OnNext(i);
        });
        await Task.Delay(1);
        var observer = new Recorder<int>();
        subject.Subscribe(observer);
        await producer;

        List<int> values;
        lock (observer.Values) values = observer.Values.ToList();
        values.Should().NotBeEmpty();
        values.Last().Should().Be(19_999);
        values.Zip(values.Skip(1), (a, b) => b - a).Should().OnlyContain(d => d == 1);
    }
}
