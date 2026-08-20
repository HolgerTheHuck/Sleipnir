using SleipnirCore.Attributes;

namespace SleipnirTests.Fixtures;

/// <summary>
/// Dedicated <c>[SleipnirEvent(Resumable = true)]</c> hot streams for the SignalR hub-streaming
/// integration tests (<c>SignalRHubStreamTests</c>). Lives apart from
/// <see cref="ResumableEventController"/>/<see cref="E2EResumeEventController"/>/
/// <see cref="AuthedResumableEventController"/> (used by the WebSocket/SSE resume tests) so the
/// hub-stream tests can run in parallel with those classes without cross-broadcasting pushed
/// values into each other's durable observers — <see cref="HotObservable{T}"/> fans a
/// <c>Push</c> out to every currently-subscribed observer, and a durable observer stays
/// subscribed to the singleton for the durable's lifetime. The hub-stream tests run sequentially
/// within their class, so they share these streams safely.
/// </summary>
[SleipnirController("HubStreamEvent")]
public class HubStreamEventController
{
    public static readonly HotObservable<string> Stream = new();

    [SleipnirEvent("Tick", Resumable = true)]
    public IObservable<string> Tick() => Stream;
}

/// <summary>
/// Dedicated stream for the hub-stream <b>resume</b> test. It is NOT completed during the test
/// (the stream is canceled, not source-completed), so it stays reusable and never poisons
/// <see cref="HubStreamEventController.Stream"/> (which the complete-frame test completes).
/// </summary>
[SleipnirController("HubStreamResumeEvent")]
public class HubStreamResumeEventController
{
    public static readonly HotObservable<string> Stream = new();

    [SleipnirEvent("Tick", Resumable = true)]
    public IObservable<string> Tick() => Stream;
}

/// <summary>
/// Auth-protected resumable event for the SignalR hub-stream auth-reject test. Gated by
/// <c>[SleipnirAuthorise(Role = "Admin")]</c>; a fresh subscribe succeeds only with an
/// authenticated Admin principal, an unauthenticated stream rejects with 401 (thrown as a
/// <c>HubException</c> before the first item). Own hot stream — no interference with
/// <see cref="AuthedResumableEventController"/>.
/// </summary>
[SleipnirController("HubStreamAuthedEvent")]
public class HubStreamAuthedEventController
{
    public static readonly HotObservable<string> Stream = new();

    [SleipnirEvent("SecureTick", Resumable = true)]
    [SleipnirAuthorise(Role = "Admin")]
    public IObservable<string> SecureTick() => Stream;
}