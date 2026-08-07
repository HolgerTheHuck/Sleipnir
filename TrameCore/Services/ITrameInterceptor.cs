using TrameCommon.Models;
using Microsoft.AspNetCore.Http;

namespace TrameCore.Services;

/// <summary>
/// Delegate that invokes the next interceptor or the actual RPC method. Nimmt den
/// <see cref="TrameInvocationContext"/> (statt der rohen <see cref="TrameRequest"/>) — so
/// können Interceptors <see cref="TrameInvocationContext.HttpContext"/>,
/// <see cref="TrameInvocationContext.InvokeInfo"/>, <see cref="TrameInvocationContext.Response"/>
/// und <see cref="TrameInvocationContext.Activity"/> lesen/schreiben.
/// </summary>
public delegate Task<TrameResponse?> TrameInvocationDelegate(TrameInvocationContext context);

/// <summary>
/// Interface for interceptors that can intercept RPC calls before/after execution.
/// Use for logging, tracing, caching, validation, metrics, auth, rate-limiting, etc.
/// </summary>
/// <remarks>
/// Phase 1 — siehe <c>docs/design/phase-1-interceptor-pipeline.md</c>. Die Signatur wurde
/// von <c>InvokeAsync(TrameRequest, TrameInvocationDelegate, CancellationToken)</c> auf
/// <c>InvokeAsync(TrameInvocationContext, TrameInvocationDelegate)</c> umgestellt (breaking,
/// aber <c>ITrameInterceptor</c> ist in <c>STABILITY.md</c> §2 als experimental markiert).
/// </remarks>
public interface ITrameInterceptor
{
    /// <summary>
    /// Intercepts an RPC call. Call <paramref name="next"/> to continue the pipeline.
    /// <see cref="TrameInvocationContext.HttpContext"/>, <see cref="TrameInvocationContext.InvokeInfo"/>
    /// und <see cref="TrameInvocationContext.Activity"/> sind u. U. erst nach Resolver-/Span-
    /// Eröffnung belegt — vor <c>next</c> ist <see cref="TrameInvocationContext.InvokeInfo"/>
    /// typischerweise <c>null</c>, danach <see cref="TrameInvocationContext.Response"/> belegt.
    /// </summary>
    Task<TrameResponse?> InvokeAsync(
        TrameInvocationContext context,
        TrameInvocationDelegate next);
}

/// <summary>
/// Context information passed to interceptors — pro-Invocation (Single-Call oder pro
/// Element im Batch). Ersetzt das vage bisherige <c>TrameInvocationContext</c>, das nirgends
/// verwendet wurde, und reicht <c>HttpContext</c> durch (die Schlüssellücke in v1.0).
/// </summary>
public sealed class TrameInvocationContext
{
    public required TrameRequest Request { get; init; }
    public HttpContext? HttpContext { get; init; }
    public string ControllerName => Request.Controller;
    public string MethodName => Request.Method;
    public string RequestId => Request.Id ?? string.Empty;

    /// <summary>
    /// Wird vom Invoker nach Controller/Method-Resolve belegt (vor dem Methoden-Aufruf).
    /// Ein Auth-Interceptor liest <see cref="InvokeInfo.AnonymousAttribute"/> /
    /// <see cref="InvokeInfo.AuthoriseAttribute"/> daraus. Vor dem Resolve <c>null</c>.
    /// </summary>
    public TrameInvoker.InvokeInfo? InvokeInfo { get; set; }

    /// <summary>
    /// Wird vom Invoker nach der Method-Execution belegt — die resultierende Response.
    /// Ein Tracing/Logging-Interceptor liest sie *nach* <c>next</c>. Vor <c>next</c> <c>null</c>.
    /// </summary>
    public TrameResponse? Response { get; set; }

    /// <summary>
    /// Der <c>TrameCall</c>-Span (aus <c>TrameTracing.StartCall</c>), falls ein Listener
    /// abonniert hat; sonst <c>null</c>. Ein Telemetry-Interceptor nutzt ihn statt einen
    /// eigenen Span aufzumachen (kein Double-Count).
    /// </summary>
    public System.Diagnostics.Activity? Activity { get; set; }

    public CancellationToken CancellationToken { get; init; }
}