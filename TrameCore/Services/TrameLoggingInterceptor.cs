using TrameCommon.Models;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace TrameCore.Services;

/// <summary>
/// Built-in logging interceptor that traces RPC call durations.
/// </summary>
public class TrameLoggingInterceptor(ILogger<TrameLoggingInterceptor> logger) : ITrameInterceptor
{
    public async Task<TrameResponse?> InvokeAsync(
        TrameInvocationContext context,
        TrameInvocationDelegate next)
    {
        var stopwatch = Stopwatch.StartNew();
        logger.LogTrace("Starting RPC call {Controller}.{Method} [{RequestId}]",
            context.ControllerName, context.MethodName, context.RequestId);

        try
        {
            var response = await next(context);
            stopwatch.Stop();
            logger.LogDebug("RPC call {Controller}.{Method} completed in {Duration}ms [{StatusCode}]",
                context.ControllerName, context.MethodName, stopwatch.ElapsedMilliseconds, response?.Code);
            return response;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            logger.LogError(ex, "RPC call {Controller}.{Method} failed after {Duration}ms",
                context.ControllerName, context.MethodName, stopwatch.ElapsedMilliseconds);
            throw;
        }
    }
}