using MessagePack;

namespace TrameCommon.Models;

/// <summary>
/// Batch request container for multiple RPC calls.
/// </summary>
[MessagePackObject]
public class TrameMultiRequest
{
    [Key(0)]
    public List<TrameRequest>? Requests { get; set; }

    [Key(1)]
    public ExecutionMode Mode { get; set; } = ExecutionMode.Serial;
}