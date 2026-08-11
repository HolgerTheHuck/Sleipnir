using MessagePack;

namespace SleipnirCommon.Models;

/// <summary>
/// Batch request container for multiple RPC calls.
/// </summary>
[MessagePackObject]
public class SleipnirMultiRequest
{
    [Key(0)]
    public List<SleipnirRequest>? Requests { get; set; }

    [Key(1)]
    public ExecutionMode Mode { get; set; } = ExecutionMode.Serial;
}