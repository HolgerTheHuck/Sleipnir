using SleipnirCore.Attributes;

namespace SleipnirTests.Fixtures;

/// <summary>
/// Dedicated <c>[SleipnirEvent(Resumable = true)]</c> hot stream for the SSE integration tests
/// (<see cref="SleipnirTests.Integration.SseEventTests"/>). Lives apart from
/// <see cref="ResumableEventController.Stream"/> / <see cref="E2EResumeEventController.Stream"/>
/// so the SSE and WebSocket resume suites can run in parallel without cross-broadcasting pushed
/// values into each other's durable observers — <see cref="HotObservable{T}"/> fans a
/// <c>Push</c> out to every currently-subscribed observer, and a durable observer stays
/// subscribed to the singleton for the durable's lifetime. The SSE tests run sequentially within
/// their class, so they share this one stream safely.
/// </summary>
[SleipnirController("SseEvent")]
public class SseEventController
{
    public static readonly HotObservable<string> Stream = new();

    [SleipnirEvent("Tick", Resumable = true)]
    public IObservable<string> Tick() => Stream;
}