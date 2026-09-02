using Heimdall.Blazor;
using Heimdall.Prometheus;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace SleipnirTelemetryHeimdall;

/// <summary>
/// Endpoint mapping for the built-in Heimdall telemetry surface. Maps the Blazor dashboard
/// and the Prometheus HTTP API under a shared prefix (default "/otel").
/// </summary>
/// <remarks>
/// Call after <c>app.UseRouting()</c> (or the Minimal-API equivalent) and, so the dashboard's
/// static assets load, after <c>app.UseStaticFiles()</c>. The services must already be
/// registered via <see cref="SleipnirHeimdallServiceExtensions.AddSleipnirHeimdallTelemetry"/>.
/// </remarks>
public static class SleipnirHeimdallEndpointExtensions
{
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="prefix">URL prefix shared by the dashboard and the Prometheus API. Default "/otel".</param>
    /// <returns>The dashboard endpoint convention builder; the Prometheus API is also mapped.</returns>
    /// <remarks>
    /// The endpoints are <b>unauthenticated</b> by default. Protect them in production — e.g.
    /// chain <c>.RequireAuthorization(...)</c> on the returned builder, gate the prefix with an
    /// authorization middleware, or front the host with a reverse proxy. The JSON observability
    /// snapshot at <c>/api/sleipnir/observability</c> (gated by
    /// <c>SleipnirOptions.EnableObservability</c>) is independent and stays governed by its own
    /// auth flag.
    /// </remarks>
    public static IEndpointConventionBuilder MapSleipnirHeimdall(
        this IEndpointRouteBuilder endpoints,
        string prefix = "/otel")
    {
        // Both route groups live under the same prefix: the dashboard at {prefix} and the
        // PromQL HTTP API at {prefix}/api/v1/*. Mapping is additive — the Prometheus API is
        // Grafana-compatible and is the single Prometheus surface when Heimdall is in use.
        endpoints.MapHeimdallPrometheus(prefix);
        return endpoints.MapHeimdallDashboard(prefix);
    }
}