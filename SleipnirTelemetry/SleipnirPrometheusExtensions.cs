using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using SleipnirCore.Services;
using SleipnirCore.Tracing;

namespace SleipnirTelemetry;

/// <summary>
/// Opt-in Prometheus scrape endpoint for Sleipnir metrics. Adds a pull-model
/// <c>GET /metrics</c> endpoint (Prometheus text exposition) alongside the push-model
/// OTLP exporter wired by <see cref="SleipnirTelemetryServiceExtensions.AddSleipnirTelemetry"/>.
/// </summary>
/// <remarks>
/// <para>
/// Two calls, mirroring the ASP.NET Core <c>Add</c>/<c>Use</c> split:
/// <list type="bullet">
/// <item><c>AddSleipnirPrometheusMetrics()</c> — subscribes the Sleipnir <see cref="Meter"/>
/// ("Sleipnir") and attaches the Prometheus exporter to the OTel metrics pipeline.</item>
/// <item><c>UseSleipnirPrometheusScrapingEndpoint(path, requireAuth)</c> — mounts the
/// scraping middleware at <paramref name="path"/> (default <c>/api/sleipnir/metrics</c>),
/// gated by the same <c>RequireAuthentication</c> rule as <c>/discovery</c> when
/// <paramref name="requireAuth"/> is <c>true</c> (default).</item>
/// </list>
/// </para>
/// <para>
/// The auth gate reads <see cref="ISleipnirCore.RequireAuthentication"/> from request-scoped
/// DI (<see cref="ISleipnirCore"/> lives in SleipnirCore, which this package already references),
/// so no SleipnirHub/SleipnirOptions dependency is needed. Authentication must have populated
/// <c>HttpContext.User</c> upstream (reverse proxy / token middleware).
/// </para>
/// <para>
/// <b>Heimdall.</b> The Prometheus-text <c>/metrics</c> interface is the durable contract —
/// any scraper (Prometheus, Grafana Agent, VictoriaMetrics, or Heimdall, Holger's upcoming
/// embedded OTel stack) reads it. This OTel exporter is the interim producer; Heimdall can
/// later replace it without changing consumers.
/// </para>
/// </remarks>
public static class SleipnirPrometheusExtensions
{
    /// <summary>
    /// Subscribes the Sleipnir <see cref="Meter"/> and attaches the Prometheus exporter,
    /// so <see cref="SleipnirMetrics"/>' instruments (call/batch/event counters+histograms
    /// and the <c>sleipnir.ws.connections</c>/<c>sleipnir.subscriptions.active</c> gauges)
    /// are exposed at the scrape endpoint. Call once in <c>ConfigureServices</c> before
    /// <c>UseSleipnirPrometheusScrapingEndpoint</c>.
    /// </summary>
    public static IServiceCollection AddSleipnirPrometheusMetrics(this IServiceCollection services)
    {
        services.AddOpenTelemetry().WithMetrics(builder =>
        {
            builder
                .AddMeter(SleipnirMetrics.MeterName)
                .AddPrometheusExporter();
        });
        return services;
    }

    /// <summary>
    /// Mounts the Prometheus scraping endpoint at <paramref name="path"/> (default
    /// <c>/api/sleipnir/metrics</c>). When <paramref name="requireAuth"/> is <c>true</c>
    /// (default) and <see cref="ISleipnirCore.RequireAuthentication"/> is on, an
    /// unauthenticated scraper receives <c>401</c> — the same gate <c>/discovery</c> uses.
    /// Call once in the pipeline (<c>Configure</c>) after <c>AddSleipnirPrometheusMetrics</c>.
    /// </summary>
    public static IApplicationBuilder UseSleipnirPrometheusScrapingEndpoint(
        this IApplicationBuilder app,
        string path = "/api/sleipnir/metrics",
        bool requireAuth = true)
    {
        if (requireAuth)
        {
            // Auth gate runs before the terminal scraping middleware: it short-circuits to
            // 401 for the scrape path when RequireAuthentication is on and the caller is
            // unauthenticated. Resolves ISleipnirCore per-request (scoped) so no static
            // capture of host options is needed (and no SleipnirHub dependency).
            app.Use((context, next) =>
            {
                if (context.Request.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
                {
                    var core = context.RequestServices.GetService<ISleipnirCore>();
                    if (core?.RequireAuthentication == true
                        && !(context.User?.Identity?.IsAuthenticated ?? false))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return Task.CompletedTask;
                    }
                }
                return next();
            });
        }

        return app.UseOpenTelemetryPrometheusScrapingEndpoint(path);
    }
}