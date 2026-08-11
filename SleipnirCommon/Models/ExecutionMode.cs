namespace SleipnirCommon.Models;

/// <summary>
/// Controls execution strategy for multi-request batches.
/// </summary>
public enum ExecutionMode
{
    /// <summary>
    /// Execute all requests concurrently.
    /// </summary>
    Parallel,

    /// <summary>
    /// Execute requests sequentially, enabling dependency chaining.
    /// </summary>
    Serial
}