using SleipnirCommon.Models;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace SleipnirCore.Services;

/// <summary>
/// Built-in logging interceptor that traces RPC call durations.
/// </summary>
public class SleipnirLoggingInterceptor(ILogger<SleipnirLoggingInterceptor> logger) : ISleipnirInterceptor
{
    public async Task<SleipnirResponse?> InvokeAsync(
        SleipnirInvocationContext context,
        SleipnirInvocationDelegate next)
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