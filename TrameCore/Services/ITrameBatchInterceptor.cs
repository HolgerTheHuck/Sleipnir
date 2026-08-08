using TrameCommon.Models;
using Microsoft.AspNetCore.Http;

namespace TrameCore.Services;

/// <summary>
/// Interceptor that wraps a *whole* batch (not per element). Use for batch metrics
/// (<c>trame.batch.*</c>), batch logging, batch rate limiting. See
/// <see cref="ITrameInterceptor"/> for per-element interceptors.
/// </summary>
/// <remarks>
/// <para><b>Where this runs today (1.1.x).</b> <see cref="ITrameBatchInterceptor"/> has <b>no
/// consumer yet</b> — the batch path (<c>InvokeDi(IEnumerable&lt;TrameRequest&gt;)</c>) goes
/// straight into parallel/serial/topological execution without a batch-level interceptor
/// pipeline, so instances registered via <c>TrameOptions.BatchInterceptors</c> are never
/// invoked. Do not rely on this seam in 1.1.x; wiring a consumer (or removing the interface)
/// is tracked for 1.2 (<c>ROADMAP.md</c> R7).</para>
/// <para>Phase 1 — see <c>docs/design/phase-1-interceptor-pipeline.md</c>. Experimental until the
/// pipeline lands (see <c>STABILITY.md</c> §2).</para>
/// </remarks>
public interface ITrameBatchInterceptor
{
    /// <summary>
    /// Intercepts a batch invocation. Call <paramref name="next"/> to continue the pipeline
    /// (which performs the batch execution). The <paramref name="context"/> carries the batch
    /// context (requests, mode, HttpContext, and the resulting responses after <c>next</c>).
    /// </summary>
    Task<IEnumerable<TrameResponse?>> InvokeAsync(
        TrameBatchInvocationContext context,
        Func<TrameBatchInvocationContext, Task<IEnumerable<TrameResponse?>>> next,
        CancellationToken ct);
}

/// <summary>
/// Context for <see cref="ITrameBatchInterceptor"/> — batch level (one batch = N requests).
/// </summary>
public sealed class TrameBatchInvocationContext
{
    public required IReadOnlyList<TrameRequest> Requests { get; init; }
    public required ExecutionMode Mode { get; init; }
    public HttpContext? HttpContext { get; init; }
    /// <summary>Populated after <c>next</c> — the resulting responses of the batch.</summary>
    public IReadOnlyList<TrameResponse?>? Responses { get; set; }
    /// <summary>The <c>TrameBatch</c> span (consolidated from <c>TrameTracing</c>), if active.</summary>
    public System.Diagnostics.Activity? Activity { get; set; }
    public CancellationToken CancellationToken { get; init; }
}