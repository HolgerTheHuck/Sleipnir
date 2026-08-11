using SleipnirCommon.Models;
using Microsoft.AspNetCore.Http;

namespace SleipnirCore.Services;

/// <summary>
/// Delegate that invokes the next interceptor or the actual RPC method. Takes the
/// <see cref="SleipnirInvocationContext"/> (instead of the raw <see cref="SleipnirRequest"/>) — so
/// interceptors can read/write <see cref="SleipnirInvocationContext.HttpContext"/>,
/// <see cref="SleipnirInvocationContext.InvokeInfo"/>, <see cref="SleipnirInvocationContext.Response"/>
/// and <see cref="SleipnirInvocationContext.Activity"/>.
/// </summary>
public delegate Task<SleipnirResponse?> SleipnirInvocationDelegate(SleipnirInvocationContext context);

/// <summary>
/// Interface for interceptors that can intercept RPC calls before/after execution.
/// </summary>
/// <remarks>
/// <para><b>Where this runs today (1.1.x).</b> <see cref="ISleipnirInterceptor"/> instances are
/// invoked <b>only on the single-call path</b> (<c>ISleipnirCore.InvokeDi(SleipnirRequest)</c>). They
/// do <b>not</b> run on the per-request elements of a batch — <c>/json/multi</c>, a WebSocket
/// multi-request, or a JSON-RPC batch — and <see cref="ISleipnirBatchInterceptor"/> has no consumer
/// at all yet. A user interceptor registered via <c>SleipnirOptions.Interceptors</c> therefore sees
/// single calls but is silently bypassed on every batch call.</para>
/// <para><b>Security implication.</b> Authorization is <b>not</b> affected — it is enforced
/// structurally by the invoker's serial auth pre-pass, not by user interceptors. But any
/// <i>custom</i> logic you place behind this seam (tenant isolation, request validation, rate
/// limiting, audit logging) is bypassed on batches. Do not build a security control on this
/// seam in 1.1.x; use <c>[SleipnirAuthorise]</c>/policies and the framework-level gates instead.
/// Routing the batch path through the interceptor pipeline is tracked for 1.2
/// (<c>ROADMAP.md</c> R7); a startup warning is logged once when <c>SleipnirOptions.Interceptors</c>
/// is non-empty.</para>
/// <para>Phase 1 — see <c>docs/design/phase-1-interceptor-pipeline.md</c>. The signature changed
/// from <c>InvokeAsync(SleipnirRequest, SleipnirInvocationDelegate, CancellationToken)</c> to
/// <c>InvokeAsync(SleipnirInvocationContext, SleipnirInvocationDelegate)</c> (breaking, but
/// <c>ISleipnirInterceptor</c> is marked experimental in <c>STABILITY.md</c> §2).</para>
/// </remarks>
public interface ISleipnirInterceptor
{
    /// <summary>
    /// Intercepts an RPC call. Call <paramref name="next"/> to continue the pipeline.
    /// <see cref="SleipnirInvocationContext.HttpContext"/>, <see cref="SleipnirInvocationContext.InvokeInfo"/>
    /// and <see cref="SleipnirInvocationContext.Activity"/> may only be populated after resolver/span
    /// setup — before <c>next</c>, <see cref="SleipnirInvocationContext.InvokeInfo"/> is typically
    /// <c>null</c>; after <c>next</c>, <see cref="SleipnirInvocationContext.Response"/> is populated.
    /// </summary>
    /// <remarks>
    /// Runs on the single-call path only in 1.1.x — see the type-level remarks for the
    /// batch-element bypass and its security implication.
    /// </remarks>
    Task<SleipnirResponse?> InvokeAsync(
        SleipnirInvocationContext context,
        SleipnirInvocationDelegate next);
}

/// <summary>
/// Context information passed to interceptors — per invocation (a single call, or one element
/// in a batch). Replaces the previous vague <c>SleipnirInvocationContext</c> that was never used,
/// and threads <c>HttpContext</c> through (the key gap in v1.0).
/// </summary>
public sealed class SleipnirInvocationContext
{
    public required SleipnirRequest Request { get; init; }
    public HttpContext? HttpContext { get; init; }
    public string ControllerName => Request.Controller;
    public string MethodName => Request.Method;
    public string RequestId => Request.Id ?? string.Empty;

    /// <summary>
    /// Populated by the invoker after controller/method resolution (before the method call).
    /// An auth interceptor reads <see cref="InvokeInfo.AnonymousAttribute"/> /
    /// <see cref="InvokeInfo.AuthoriseAttribute"/> from it. <c>null</c> before resolution.
    /// </summary>
    public SleipnirInvoker.InvokeInfo? InvokeInfo { get; set; }

    /// <summary>
    /// Populated by the invoker after method execution — the resulting response.
    /// A tracing/logging interceptor reads it *after* <c>next</c>. <c>null</c> before <c>next</c>.
    /// </summary>
    public SleipnirResponse? Response { get; set; }

    /// <summary>
    /// The <c>SleipnirCall</c> span (from <c>SleipnirTracing.StartCall</c>), if a listener is
    /// subscribed; otherwise <c>null</c>. A telemetry interceptor uses it instead of opening
    /// its own span (no double-counting).
    /// </summary>
    public System.Diagnostics.Activity? Activity { get; set; }

    public CancellationToken CancellationToken { get; init; }
}