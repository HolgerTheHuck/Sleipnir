using TrameCommon.Models;
using Microsoft.AspNetCore.Http;

namespace TrameCore.Services;

/// <summary>
/// Delegate that invokes the next interceptor or the actual RPC method. Takes the
/// <see cref="TrameInvocationContext"/> (instead of the raw <see cref="TrameRequest"/>) — so
/// interceptors can read/write <see cref="TrameInvocationContext.HttpContext"/>,
/// <see cref="TrameInvocationContext.InvokeInfo"/>, <see cref="TrameInvocationContext.Response"/>
/// and <see cref="TrameInvocationContext.Activity"/>.
/// </summary>
public delegate Task<TrameResponse?> TrameInvocationDelegate(TrameInvocationContext context);

/// <summary>
/// Interface for interceptors that can intercept RPC calls before/after execution.
/// </summary>
/// <remarks>
/// <para><b>Where this runs today (1.1.x).</b> <see cref="ITrameInterceptor"/> instances are
/// invoked <b>only on the single-call path</b> (<c>ITrameCore.InvokeDi(TrameRequest)</c>). They
/// do <b>not</b> run on the per-request elements of a batch — <c>/json/multi</c>, a WebSocket
/// multi-request, or a JSON-RPC batch — and <see cref="ITrameBatchInterceptor"/> has no consumer
/// at all yet. A user interceptor registered via <c>TrameOptions.Interceptors</c> therefore sees
/// single calls but is silently bypassed on every batch call.</para>
/// <para><b>Security implication.</b> Authorization is <b>not</b> affected — it is enforced
/// structurally by the invoker's serial auth pre-pass, not by user interceptors. But any
/// <i>custom</i> logic you place behind this seam (tenant isolation, request validation, rate
/// limiting, audit logging) is bypassed on batches. Do not build a security control on this
/// seam in 1.1.x; use <c>[TrameAuthorise]</c>/policies and the framework-level gates instead.
/// Routing the batch path through the interceptor pipeline is tracked for 1.2
/// (<c>ROADMAP.md</c> R7); a startup warning is logged once when <c>TrameOptions.Interceptors</c>
/// is non-empty.</para>
/// <para>Phase 1 — see <c>docs/design/phase-1-interceptor-pipeline.md</c>. The signature changed
/// from <c>InvokeAsync(TrameRequest, TrameInvocationDelegate, CancellationToken)</c> to
/// <c>InvokeAsync(TrameInvocationContext, TrameInvocationDelegate)</c> (breaking, but
/// <c>ITrameInterceptor</c> is marked experimental in <c>STABILITY.md</c> §2).</para>
/// </remarks>
public interface ITrameInterceptor
{
    /// <summary>
    /// Intercepts an RPC call. Call <paramref name="next"/> to continue the pipeline.
    /// <see cref="TrameInvocationContext.HttpContext"/>, <see cref="TrameInvocationContext.InvokeInfo"/>
    /// and <see cref="TrameInvocationContext.Activity"/> may only be populated after resolver/span
    /// setup — before <c>next</c>, <see cref="TrameInvocationContext.InvokeInfo"/> is typically
    /// <c>null</c>; after <c>next</c>, <see cref="TrameInvocationContext.Response"/> is populated.
    /// </summary>
    /// <remarks>
    /// Runs on the single-call path only in 1.1.x — see the type-level remarks for the
    /// batch-element bypass and its security implication.
    /// </remarks>
    Task<TrameResponse?> InvokeAsync(
        TrameInvocationContext context,
        TrameInvocationDelegate next);
}

/// <summary>
/// Context information passed to interceptors — per invocation (a single call, or one element
/// in a batch). Replaces the previous vague <c>TrameInvocationContext</c> that was never used,
/// and threads <c>HttpContext</c> through (the key gap in v1.0).
/// </summary>
public sealed class TrameInvocationContext
{
    public required TrameRequest Request { get; init; }
    public HttpContext? HttpContext { get; init; }
    public string ControllerName => Request.Controller;
    public string MethodName => Request.Method;
    public string RequestId => Request.Id ?? string.Empty;

    /// <summary>
    /// Populated by the invoker after controller/method resolution (before the method call).
    /// An auth interceptor reads <see cref="InvokeInfo.AnonymousAttribute"/> /
    /// <see cref="InvokeInfo.AuthoriseAttribute"/> from it. <c>null</c> before resolution.
    /// </summary>
    public TrameInvoker.InvokeInfo? InvokeInfo { get; set; }

    /// <summary>
    /// Populated by the invoker after method execution — the resulting response.
    /// A tracing/logging interceptor reads it *after* <c>next</c>. <c>null</c> before <c>next</c>.
    /// </summary>
    public TrameResponse? Response { get; set; }

    /// <summary>
    /// The <c>TrameCall</c> span (from <c>TrameTracing.StartCall</c>), if a listener is
    /// subscribed; otherwise <c>null</c>. A telemetry interceptor uses it instead of opening
    /// its own span (no double-counting).
    /// </summary>
    public System.Diagnostics.Activity? Activity { get; set; }

    public CancellationToken CancellationToken { get; init; }
}