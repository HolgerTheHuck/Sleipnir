using SleipnirCore.Attributes;
using SleipnirCore.Events;

namespace SleipnirTests.Fixtures;

/// <summary>Progress payload of the "history + live" fixture: the stream's sequence number travels in the event.</summary>
public sealed record RunProgressEvent(long Seq, string Step);

/// <summary>
/// The EVENTS_REFERENCE "History + live" recipe as a test fixture: one
/// <see cref="SleipnirReplayStream{T}"/> per run id, a non-resumable event with an
/// <c>afterSeq</c> cursor, and a send buffer sized to the history. Tests use unique run ids, so
/// parallel tests never share a stream.
/// </summary>
[SleipnirController("ReplayRun")]
public class ReplayRunController
{
    public const int MaxHistory = 500;

    public static readonly SleipnirReplayStreams<string, RunProgressEvent> Runs = new(MaxHistory);

    [SleipnirEvent("Progress", BufferCapacity = MaxHistory + 100)]
    public IObservable<RunProgressEvent> Progress(string runId, long afterSeq = 0)
        => Runs.Observe(runId, afterSeq);
}
