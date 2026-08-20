using Microsoft.Extensions.Logging;
using SleipnirCommon.Models;
using SleipnirCore.Services;
using System.Diagnostics;

namespace Sleipnir.Guide.Api.Interceptors;

// Chapter 10 — a custom interceptor. Sleipnir's interceptor pipeline wraps every single RPC
// invocation; a custom interceptor can read the request + HttpContext before the method runs,
// call `next` to continue the pipeline, and inspect the response after. This one propagates a
// correlation id (from the X-Correlation-Id request header, or a fresh one if absent) onto the
// HTTP response and logs every call with that id, the controller.method, the status code, and
// the duration — the shape a real audit/request-logging interceptor takes.
//
// It is registered AFTER AddSleipnir (see Program.cs), so DI appends it after the built-in
// interceptors (Auth → Telemetry → Logging). The pipeline runs interceptors in REVERSE
// registration order — last-registered runs first — so this interceptor is OUTERMOST: it wraps
// Auth and Logging, seeing unauthorized calls too. That is the right place for a
// request-level/correlation concern (a method-level concern would register inner instead).
//
// IMPORTANT CAVEAT (1.1.x): user interceptors run on the SINGLE-CALL path only — POST /json
// (single), a WebSocket single-frame request. They do NOT run on the per-element invocations of
// a batch (/json/multi, a WS multi-request, a JSON-RPC batch); the batch path bypasses the
// interceptor pipeline (routed through it in 1.2, ROADMAP R7). Authorization is unaffected — it
// is enforced structurally by the invoker's serial auth pre-pass, not by user interceptors —
// so this is a logging/observability seam, NOT a security seam. Do not build an auth/rate-limit
// control on this; use [SleipnirAuthorise]/policies and the framework-level gates instead.
public sealed class CorrelationIdInterceptor(ILogger<CorrelationIdInterceptor> logger) : ISleipnirInterceptor
{
    public async Task<SleipnirResponse?> InvokeAsync(
        SleipnirInvocationContext context,
        SleipnirInvocationDelegate next)
    {
        // HttpContext is non-null on the REST/WebSocket single-call path; null on in-memory.
        var http = context.HttpContext;
        var incoming = http?.Request.Headers["X-Correlation-Id"].ToString();
        var correlationId = !string.IsNullOrWhiteSpace(incoming)
            ? incoming!
            : Guid.NewGuid().ToString("N")[..12];

        // Echo the correlation id back on the HTTP response so the caller can pair request and
        // response logs. Safe to set a header before next() — the response body is not started.
        if (http is not null && !http.Response.HasStarted)
            http.Response.Headers["X-Correlation-Id"] = correlationId;

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var response = await next(context);
            stopwatch.Stop();
            logger.LogInformation(
                "RPC {Controller}.{Method} [{CorrelationId}] -> {Code} in {Duration}ms",
                context.ControllerName, context.MethodName, correlationId, response?.Code, stopwatch.ElapsedMilliseconds);
            return response;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            logger.LogError(ex,
                "RPC {Controller}.{Method} [{CorrelationId}] threw after {Duration}ms",
                context.ControllerName, context.MethodName, correlationId, stopwatch.ElapsedMilliseconds);
            throw;
        }
    }
}