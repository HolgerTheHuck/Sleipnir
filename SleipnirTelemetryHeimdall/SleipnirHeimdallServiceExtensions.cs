using Heimdall;
using Heimdall.Blazor;
using Heimdall.Blazor.Alerts;
using Heimdall.Prometheus;
using Heimdall.Sdk;
using Heimdall.Storage.SQLite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using SleipnirCore.Tracing;

namespace SleipnirTelemetryHeimdall;

/// <summary>
/// Server-side entry point for the built-in Heimdall telemetry backend. Boots the OTel SDK
/// with an in-process Heimdall exporter pointed at an embedded SQLite sink, subscribes the
/// Sleipnir ActivitySource + Meter (and optionally <c>ILogger</c>), and registers the Heimdall
/// dashboard + Prometheus DI services. The actual endpoints are mapped separately via
/// <see cref="SleipnirHeimdallEndpointExtensions.MapSleipnirHeimdall"/>.
/// </summary>
/// <remarks>
/// The instrumentation itself (ActivitySource "Sleipnir") lives always-on in the engine
/// (<c>SleipnirCore.Tracing.SleipnirTracing</c>) and is cost-neutral without a listener. This
/// package subscribes it to a Heimdall sink and is the turn-key alternative to
/// <c>Sleipnir.Telemetry</c>'s OTLP/Console + Prometheus-scrape producers: Heimdall becomes the
/// single Prometheus surface (its PromQL engine under <c>{prefix}/api/v1/*</c> replaces the
/// <c>/api/sleipnir/metrics</c> scrape). The <c>/api/sleipnir/observability</c> JSON snapshot
/// endpoint is unaffected (different purpose — runtime connection counts, not OTel signals).
/// <para>
/// <b>SleipnirServer</b> and <b>SleipnirCore</b> do not reference this package or Heimdall;
/// consumers opt in by referencing <c>Sleipnir.Telemetry.Heimdall</c> and calling this method —
/// the same backend-agnostic doctrine as <c>Sleipnir.Telemetry</c>.
/// </para>
/// </remarks>
public static class SleipnirHeimdallServiceExtensions
{
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional configuration of <see cref="SleipnirHeimdallOptions"/>.</param>
    /// <returns><paramref name="services"/> for fluent chaining.</returns>
    public static IServiceCollection AddSleipnirHeimdallTelemetry(
        this IServiceCollection services,
        Action<SleipnirHeimdallOptions>? configure = null)
    {
        var options = new SleipnirHeimdallOptions();
        configure?.Invoke(options);

        // One shared sink instance backs the OTel exporter (IHeimdallSink), the dashboard
        // (IHeimdallQuery), and the Prometheus engine (IHeimdallMetricSource + IHeimdallQuery
        // — the query enables RED-metric derivation from Sleipnir server spans).
        // SQLiteTelemetrySink implements all three and is IDisposable; registering it as a
        // singleton lets the host dispose it on shutdown.
        var sink = new SQLiteTelemetrySink(new SQLiteTelemetryOptions
        {
            DataPath = options.DataPath,
            RetentionDays = options.RetentionDays,
        });

        services.AddSingleton<IHeimdallSink>(sink);
        services.AddSingleton<IHeimdallQuery>(sink);
        services.AddSingleton<IHeimdallMetricSource>(sink);
        services.AddSingleton(sink);

        var exporterOptions = new HeimdallExporterOptions
        {
            Sink = sink,
            ServiceName = options.ServiceName,
            ServiceVersion = options.ServiceVersion,
        };

        // Traces: the single integration touchpoint — subscribe the Sleipnir ActivitySource.
        services.AddOpenTelemetry().WithTracing(builder =>
        {
            builder
                .AddSource(SleipnirTracing.ActivitySourceName)
                .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(options.ServiceName, options.ServiceVersion));

            if (options.IncludeAspNetCore)
                builder.AddAspNetCoreInstrumentation();
            if (options.IncludeHttpClient)
                builder.AddHttpClientInstrumentation();

            builder.UseHeimdallExporter(exporterOptions);
        });

        // Metrics: subscribe the Sleipnir Meter (counters/histogram/gauges) so the instruments
        // no longer write into the void. Heimdall's PromQL engine is the pull surface (under
        // {prefix}/api/v1/*); do NOT also wire Sleipnir.Telemetry's /api/sleipnir/metrics scrape
        // — Heimdall replaces that producer.
        services.AddOpenTelemetry().WithMetrics(builder =>
        {
            builder
                .AddMeter(SleipnirMetrics.MeterName)
                .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(options.ServiceName, options.ServiceVersion));

            builder.UseHeimdallExporter(exporterOptions);
        });

        if (options.IncludeLogs)
        {
            // Attach the Heimdall log exporter to the OTel logger provider...
            services.AddOpenTelemetry().WithLogging(builder => builder.UseHeimdallExporter(exporterOptions));
            // ...and bridge ILogger output into that provider. AddLogging merges with the
            // host's logging configuration, so Sleipnir's built-in logging interceptor and
            // any ILogger<T> usage flow into Heimdall. If logs do not appear in the dashboard
            // after startup, additionally call builder.Logging.AddOpenTelemetry(o =>
            // { o.IncludeFormattedMessage = true; o.IncludeScopes = true; }) on the host's
            // WebApplicationBuilder — see the package README.
            services.AddLogging(logging => logging.AddOpenTelemetry(o =>
            {
                o.IncludeFormattedMessage = true;
                o.IncludeScopes = true;
            }));
        }

        // Dashboard (Blazor SSR) + Prometheus (PromQL engine + RED-from-spans) DI services.
        // The same sink is passed as the read/query side; AddHeimdallPrometheus takes the sink
        // as IHeimdallMetricSource and the query enables RED-metric derivation.
        services.AddHeimdallDashboard(sink).AddHeimdallPrometheus(sink, sink);

        // Alert subsystem: Heimdall's AddHeimdallDashboard registers default file-based stores
        // (TryAddSingleton, Heimdall's own Options dirs) since 1.3.1, so registration is no
        // longer mandatory to keep routing alive. We still register explicitly because an
        // explicit AddHeimdallAlerting wins over the TryAdd defaults — this keeps the rule/state
        // files co-located with the SQLite db instead of Heimdall's default working-directory
        // paths, pins the notification language to English, and gates the evaluator on
        // EnableAlerting (the evaluator itself — rules → channels — stays opt-in).
        var dataDir = Path.GetDirectoryName(Path.GetFullPath(options.DataPath)) ?? ".";
        services.AddHeimdallAlerting(sink, new HeimdallAlertingOptions
        {
            Enabled = options.EnableAlerting,
            RulesDir = options.AlertingRulesDir ?? Path.Combine(dataDir, "alerts", "rules"),
            StateDir = options.AlertingStateDir ?? Path.Combine(dataDir, "alerts"),
            Language = "en",
        });

        return services;
    }
}