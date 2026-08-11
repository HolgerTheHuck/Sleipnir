using SleipnirCommon.Models;
using Microsoft.AspNetCore.Http;

namespace SleipnirCore.Services;

/// <summary>
/// Interceptor that wraps a *whole* batch (not per element). Use for batch metrics
/// (<c>sleipnir.batch.*</c>), batch logging, batch rate limiting. See
/// <see cref="ISleipnirInterceptor"/> for per-element interceptors.
/// </summary>
/// <remarks>
/// <para><b>Where this runs today (1.1.x).</b> <see cref="ISleipnirBatchInterceptor"/> has <b>no
/// consumer yet</b> — the batch path (<c>InvokeDi(IEnumerable&lt;SleipnirRequest&gt;)</c>) goes
/// straight into parallel/serial/topological execution without a batch-level interceptor
/// pipeline, so instances registered via <c>SleipnirOptions.BatchInterceptors</c> are never
/// invoked. Do not rely on this seam in 1.1.x; wiring a consumer (or removing the interface)
/// is tracked for 1.2 (<c>ROADMAP.md</c> R7).</para>
/// <para>Phase 1 — see <c>docs/design/phase-1-interceptor-pipeline.md</c>. Experimental until the
/// pipeline lands (see <c>STABILITY.md</c> §2).</para>
/// </remarks>
public interface ISleipnirBatchInterceptor
{
    /// <summary>
    /// Intercepts a batch invocation. Call <paramref name="next"/> to continue the pipeline
    /// (which performs the batch execution). The <paramref name="context"/> carries the batch
    /// context (requests, mode, HttpContext, and the resulting responses after <c>next</c>).
    /// </summary>
    Task<IEnumerable<SleipnirResponse?>> InvokeAsync(
        SleipnirBatchInvocationContext context,
        Func<SleipnirBatchInvocationContext, Task<IEnumerable<SleipnirResponse?>>> next,
        CancellationToken ct);
}

/// <summary>
/// Context for <see cref="ISleipnirBatchInterceptor"/> — batch level (one batch = N requests).
/// </summary>
public sealed class SleipnirBatchInvocationContext
{
    public required IReadOnlyList<SleipnirRequest> Requests { get; init; }
    public required ExecutionMode Mode { get; init; }
    public HttpContext? HttpContext { get; init; }
    /// <summary>Populated after <c>next</c> — the resulting responses of the batch.</summary>
    public IReadOnlyList<SleipnirResponse?>? Responses { get; set; }
    /// <summary>The <c>SleipnirBatch</c> span (consolidated from <c>SleipnirTracing</c>), if active.</summary>
    public System.Diagnostics.Activity? Activity { get; set; }
    public CancellationToken CancellationToken { get; init; }
}