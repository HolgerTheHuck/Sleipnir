using TrameCommon.Models;
using Microsoft.AspNetCore.Http;

namespace TrameCore.Services;

/// <summary>
/// Delegate that invokes the next interceptor or the actual RPC method.
/// </summary>
public delegate Task<TrameResponse?> TrameInvocationDelegate(TrameRequest request, CancellationToken ct);

/// <summary>
/// Interface for interceptors that can intercept RPC calls before/after execution.
/// Use for logging, tracing, caching, validation, metrics, etc.
/// </summary>
public interface ITrameInterceptor
{
    /// <summary>
    /// Intercepts an RPC call. Call <paramref name="next"/> to continue the pipeline.
    /// </summary>
    Task<TrameResponse?> InvokeAsync(
        TrameRequest request,
        TrameInvocationDelegate next,
        CancellationToken ct);
}

/// <summary>
/// Context information passed to interceptors for additional metadata.
/// </summary>
public class TrameInvocationContext
{
    public required TrameRequest Request { get; init; }
    public HttpContext? HttpContext { get; init; }
    public string ControllerName => Request.Controller;
    public string MethodName => Request.Method;
    public string RequestId => Request.Id ?? string.Empty;
}