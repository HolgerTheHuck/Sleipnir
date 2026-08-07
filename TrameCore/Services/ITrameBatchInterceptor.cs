using TrameCommon.Models;
using Microsoft.AspNetCore.Http;

namespace TrameCore.Services;

/// <summary>
/// Interceptor, der um einen *ganzen* Batch (nicht pro Element) läuft. Use für Batch-Metrics
/// (<c>trame.batch.*</c>), Batch-Logging, Batch-Rate-Limiting. Siehe
/// <see cref="ITrameInterceptor"/> für pro-Element-Interceptors.
/// </summary>
/// <remarks>
/// Phase 1 — siehe <c>docs/design/phase-1-interceptor-pipeline.md</c>. Experimental bis die
/// Pipeline gelandet ist (siehe <c>STABILITY.md</c> §2).
/// </remarks>
public interface ITrameBatchInterceptor
{
    /// <summary>
    /// Intercepts a batch invocation. Call <paramref name="next"/> to continue the pipeline
    /// (die die Batch-Ausführung übernimmt). Der <paramref name="context"/> trägt den Batch-
    /// Kontext (Requests, Mode, HttpContext, ggf. die resultierenden Responses nach <c>next</c>).
    /// </summary>
    Task<IEnumerable<TrameResponse?>> InvokeAsync(
        TrameBatchInvocationContext context,
        Func<TrameBatchInvocationContext, Task<IEnumerable<TrameResponse?>>> next,
        CancellationToken ct);
}

/// <summary>
/// Context für <see cref="ITrameBatchInterceptor"/> — Batch-Ebene (ein Batch = N Requests).
/// </summary>
public sealed class TrameBatchInvocationContext
{
    public required IReadOnlyList<TrameRequest> Requests { get; init; }
    public required ExecutionMode Mode { get; init; }
    public HttpContext? HttpContext { get; init; }
    /// <summary>Wird nach <c>next</c> belegt — die resultierenden Responses des Batch.</summary>
    public IReadOnlyList<TrameResponse?>? Responses { get; set; }
    /// <summary>Der <c>TrameBatch</c>-Span (konsolidiert aus <c>TrameTracing</c>), falls aktiv.</summary>
    public System.Diagnostics.Activity? Activity { get; set; }
    public CancellationToken CancellationToken { get; init; }
}