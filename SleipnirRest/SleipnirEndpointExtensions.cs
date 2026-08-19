using SleipnirCore.Services;
using SleipnirCore.Tracing;
using SleipnirCore.Model.Messages.Mex;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using SleipnirRest.JsonRpc;

namespace SleipnirRest
{
    public static class SleipnirEndpointExtensions
    {
        /// <summary>
        /// Fügt die Sleipnir-REST-Endpunkte (Minimal-API) zur Anwendung hinzu.
        /// Das ist die kanonische REST-Implementierung für v1.0 — früher gab es
        /// zusätzlich einen MVC-Controller mit denselben Routen, der bei
        /// <c>AddControllers()</c> zu AmbiguousMatchException führte; er ist
        /// zugunsten dieses Pfads entfallen.
        /// </summary>
        /// <param name="endpoints">Der IEndpointRouteBuilder der Anwendung.</param>
        /// <param name="prefix">Das URL-Präfix für die Sleipnir-Endpunkte.</param>
        /// <param name="enableRateLimiting">
        /// Wenn <c>true</c>, wird die Policy <c>sleipnir</c> auf alle Endpunkte
        /// angewandt (<c>RequireRateLimiting</c>). Nur aktivieren, wenn der Host
        /// die Policy registriert hat (AddSleipnir mit RateLimitPermitLimit &gt; 0).
        /// </param>
        /// <param name="enableJsonRpcCompat">
        /// Wenn <c>true</c>, wird der JSON-RPC-2.0-Kompatibilitäts-Endpoint
        /// <c>POST {prefix}/jsonrpc</c> registriert (Opt-in, Default <c>false</c>).
        /// Siehe <c>JSONRPC_COMPAT.md</c>.
        /// </param>
        /// <returns>Der IEndpointRouteBuilder für weitere Konfigurationen.</returns>
        public static IEndpointRouteBuilder MapSleipnirEndpoints(this IEndpointRouteBuilder endpoints,
            string prefix = "/api/sleipnir", bool enableRateLimiting = false, bool enableJsonRpcCompat = false,
            bool enableObservability = false, bool signalREnabled = false)
        {
            // Erstellt eine Gruppe für alle Routen — sauber und konfliktfrei.
            var group = endpoints.MapGroup(prefix);

            // 1 MB max Request-Body — entspricht dem früheren MVC-RequestSizeLimit.
            group.WithMetadata(new RequestSizeLimitAttribute(1_048_576));

            // Per-Endpoint Rate Limiting nur auf ausdrücklichen Wunsch, damit Hosts
            // ohne registrierte "sleipnir"-Policy keinen Runtime-Fehler produzieren.
            if (enableRateLimiting)
            {
                group.RequireRateLimiting("sleipnir");
            }

            group.MapPost("/json", async (
                SleipnirRequest request, ISleipnirCore sleipnirService, HttpContext httpContext, CancellationToken ct) =>
            {
                try
                {
                    var result = await sleipnirService.InvokeDi(request, httpContext, ct);
                    return Results.Ok(result);
                }
                catch (OperationCanceledException)
                {
                    return Results.Json(new { title = "Request cancelled.", status = 499 },
                        statusCode: 499);
                }
            });

            group.MapPost("/json/multi", async (
                SleipnirMultiRequest? request, ISleipnirCore sleipnirService, HttpContext httpContext, CancellationToken ct) =>
            {
                if (request?.Requests is null || request.Requests.Count == 0)
                    return Results.BadRequest("Request is empty.");
                // Batch-Cap-Gate (North-Bound-Härtung): früher 400 statt Fan-Out-DoS.
                // Quelle ist ISleipnirCore (SleipnirOptions → Invoker → Interface → Transporte).
                if (sleipnirService.MaximumBatchSize > 0 && request.Requests.Count > sleipnirService.MaximumBatchSize)
                    return Results.BadRequest($"Batch exceeds MaximumBatchSize ({sleipnirService.MaximumBatchSize}).");
                try
                {
                    var result = await sleipnirService.InvokeDi(request.Requests, httpContext, request.Mode, ct);
                    return Results.Ok(result);
                }
                catch (OperationCanceledException)
                {
                    return Results.Json(new { title = "Request cancelled.", status = 499 },
                        statusCode: 499);
                }
            });

            group.MapGet("/discovery", (ISleipnirCore sleipnirService, HttpContext httpContext) =>
            {
                // North-Bound: Discovery ist ein Angriffsflächen-Orakel — hinter Auth
                // legen, wenn RequireAuthentication an (Security-Audit F7.3). Die
                // per-Method-Auth der Controller erledigt der Invoker; dieser Gate
                // schützt nur die Framework-Discovery selbst.
                if (sleipnirService.RequireAuthentication && !(httpContext.User?.Identity?.IsAuthenticated ?? false))
                    return Results.Unauthorized();
                // Serialize with the deterministic discovery options so the wire contract is
                // independent of host JSON configuration (docs/discovery-schema.md §11 no-drift gate).
                var allControllers = sleipnirService.GetDiscoveryInfo();
                var json = JsonSerializer.Serialize(allControllers, DiscoverySerialization.Options);
                return Results.Content(json, "application/json");
            });

            // Observability snapshot endpoint (Opt-in via SleipnirOptions.EnableObservability).
            // Returns a small JSON document with live transport/runtime state for the Developer
            // UI Observability panel. RequireAuth-gated like /discovery — the same transport-level
            // gate protects the framework surface; per-method auth is the invoker's job.
            if (enableObservability)
            {
                group.MapGet("/observability", (
                    ISleipnirCore sleipnirService,
                    SleipnirConnectionRegistry registry,
                    HttpContext httpContext) =>
                {
                    if (sleipnirService.RequireAuthentication && !(httpContext.User?.Identity?.IsAuthenticated ?? false))
                        return Results.Unauthorized();

                    var snap = registry.GetSnapshot();
                    // Transport flags: REST is on (this endpoint lives in the REST group), WebSocket
                    // is the default-on primary channel in v1 (Task #11 will add a toggle), SignalR is
                    // the only optional transport — its state is threaded in from SleipnirOptions.
                    var payload = new
                    {
                        transports = new { rest = true, webSocket = true, signalR = signalREnabled },
                        activeConnections = snap.ActiveConnections,
                        activeSubscriptions = snap.ActiveSubscriptions,
                        eventDroppedTotal = snap.EventDroppedTotal,
                        callCount = snap.CallCount,
                        errorCount = snap.ErrorCount,
                        batchCount = snap.BatchCount,
                        uptimeMs = (long)(DateTimeOffset.UtcNow - registry.StartedAtUtc).TotalMilliseconds,
                    };
                    return Results.Json(payload);
                });
            }

            // JSON-RPC 2.0 Kompatibilitäts-Endpoint (Opt-in). Liest den Body roh
            // (Object = Einzel-Request, Array = Batch), delegiert an den Dispatcher,
            // der JSON-RPC → Sleipnir übersetzt und ISleipnirCore.InvokeDi ruft. Antwort
            // immer als 200er Hülle mit JSON-RPC-Envelope im Body (envelope-at-200);
            // 204 ausschließlich, wenn jeder Request eine Notification war.
            if (enableJsonRpcCompat)
            {
                group.MapPost("/jsonrpc", async (
                    ISleipnirCore sleipnirService, HttpContext httpContext, CancellationToken ct) =>
                {
                    httpContext.Request.EnableBuffering();
                    var (status, body) = await JsonRpcDispatcher.DispatchAsync(
                        sleipnirService, httpContext, httpContext.Request.Body, ct);
                    if (body is null)
                        return Results.StatusCode(status);
                    return Results.Content(body.ToJsonString(), "application/json; charset=utf-8", statusCode: status);
                });
            }

            return group;
        }
    }
}
