using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using SleipnirCore.Services;
using SleipnirCore.Tracing;

namespace SleipnirRest;

/// <summary>
/// Maps the SSE (Server-Sent Events) REST event endpoints. Adds the two <c>GET</c> routes to the
/// REST endpoint group built by <see cref="SleipnirEndpointExtensions.MapSleipnirEndpoints"/> —
/// call this on that group so the routes share the prefix, rate-limiting, and metadata.
/// <para>
/// <b>Routes</b> (both under <c>{prefix}/events</c>):
/// <list type="bullet">
/// <item><c>GET /events/{controller}/{method}</c> — fresh subscribe. Method arguments travel as
/// query params (GET has no body); each value is parsed as JSON when valid, else a string.</item>
/// <item><c>GET /events/{subscriptionId}</c> — resume. <c>Last-Event-Id:</c> request header (and
/// <c>?lastEventId=</c> fallback) selects the gap replay start.</item>
/// </list>
/// </para>
/// <para>
/// The response is written directly to <c>HttpContext.Response.Body</c> (not via <c>Results.Stream</c>,
/// whose overloads on this target are file-download-oriented): set <c>text/event-stream</c> +
/// <c>no-cache</c> + <c>X-Accel-Buffering: no</c>, <c>StartAsync</c> to flush the headers, then drain
/// the <see cref="SleipnirSseConnection"/>. Error results from <c>Prepare*</c> (auth/routing/binding,
/// 410 on a GC'd durable id) are executed as normal HTTP responses via <c>IResult.ExecuteAsync</c>.
/// </para>
/// <para>
/// <b>Auth.</b> The transport <see cref="ISleipnirCore.RequireAuthentication"/> gate (401) runs first;
/// per-method <c>[SleipnirAuthorise]</c> runs in <c>SubscribeAsync</c> / <c>AuthorizeSubscribeAsync</c>.
/// <c>EventSource</c> cannot set a Bearer header — the supported TS client is fetch-based; native
/// <c>EventSource</c> works for cookie-auth hosts.
/// </para>
/// </summary>
public static class SleipnirSseEndpointExtensions
{
    /// <summary>
    /// Adds the fresh-subscribe and resume SSE endpoints to the given endpoint group (the group
    /// built by <see cref="SleipnirEndpointExtensions.MapSleipnirEndpoints"/>, so the prefix +
    /// rate-limiting are inherited). No-op-safe: services resolve from DI per request.
    /// </summary>
    /// <param name="endpoints">The prefixed REST endpoint group.</param>
    /// <param name="defaultBufferCapacity">Fallback per-subscription send-buffer capacity (from
    /// <c>SleipnirOptions.EventBufferCapacity</c>, 100 when unset).</param>
    public static IEndpointRouteBuilder MapSleipnirSseEndpoints(this IEndpointRouteBuilder endpoints, int defaultBufferCapacity)
    {
        // Fresh subscribe: GET /events/{controller}/{method}?…  (method args as query params).
        endpoints.MapGet("/events/{controller}/{method}", async (
            string controller,
            string method,
            ISleipnirCore sleipnirService,
            SleipnirSubscriptionStore store,
            SleipnirConnectionRegistry registry,
            ILoggerFactory? loggerFactory,
            HttpContext httpContext) =>
        {
            // Transport auth gate — mirrors /discovery + /observability (north-bound default-deny).
            if (sleipnirService.RequireAuthentication && !(httpContext.User?.Identity?.IsAuthenticated ?? false))
            {
                httpContext.Response.StatusCode = 401;
                return;
            }

            var conn = new Sse.SleipnirSseConnection(
                httpContext, sleipnirService, store, registry, defaultBufferCapacity,
                loggerFactory?.CreateLogger<Sse.SleipnirSseConnection>());

            var ct = httpContext.RequestAborted;
            var error = await conn.PrepareFreshAsync(controller, method, httpContext.Request.Query, ct);
            if (error is not null)
            {
                await error.ExecuteAsync(httpContext);
                return;
            }
            await WriteSseStreamAsync(httpContext, conn, ct);
        });

        // Resume: GET /events/{subscriptionId}  with Last-Event-Id: header (and ?lastEventId= fallback).
        endpoints.MapGet("/events/{subscriptionId}", async (
            string subscriptionId,
            ISleipnirCore sleipnirService,
            SleipnirSubscriptionStore store,
            SleipnirConnectionRegistry registry,
            ILoggerFactory? loggerFactory,
            HttpContext httpContext) =>
        {
            if (sleipnirService.RequireAuthentication && !(httpContext.User?.Identity?.IsAuthenticated ?? false))
            {
                httpContext.Response.StatusCode = 401;
                return;
            }

            // Last-Event-Id header (the native EventSource reconnect path) takes precedence; the
            // ?lastEventId= query is the fallback for fetch-based clients that set the id manually.
            long? lastEventId = TryParseLastEventId(httpContext.Request.Headers["Last-Event-Id"])
                ?? TryParseLastEventId(httpContext.Request.Query["lastEventId"]);

            var conn = new Sse.SleipnirSseConnection(
                httpContext, sleipnirService, store, registry, defaultBufferCapacity,
                loggerFactory?.CreateLogger<Sse.SleipnirSseConnection>());

            var ct = httpContext.RequestAborted;
            var error = await conn.PrepareResumeAsync(subscriptionId, lastEventId, ct);
            if (error is not null)
            {
                await error.ExecuteAsync(httpContext);
                return;
            }
            await WriteSseStreamAsync(httpContext, conn, ct);
        });

        return endpoints;
    }

    /// <summary>
    /// Writes the SSE response: content-type + no-cache + <c>X-Accel-Buffering: no</c> (so proxies
    /// flush each event instead of buffering — essential for server-push through the corporate
    /// proxies SSE exists to serve), starts the response to flush headers, then drains the
    /// connection. The connection owns per-event flushing inside <c>StreamAsync</c>.
    /// </summary>
    private static async Task WriteSseStreamAsync(HttpContext ctx, Sse.SleipnirSseConnection conn, CancellationToken ct)
    {
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers["Cache-Control"] = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";
        await ctx.Response.StartAsync(ct);
        await conn.StreamAsync(ctx.Response.Body, ct);
    }

    private static long? TryParseLastEventId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return long.TryParse(value.Trim(), out var n) ? n : null;
    }
}