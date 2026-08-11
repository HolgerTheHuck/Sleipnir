using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using SleipnirCore.Tracing;

namespace SleipnirTelemetry;

/// <summary>
/// Server-seitiger Einstiegspunkt für OpenTelemetry: bootet das OTel-SDK und
/// abonniert den Sleipnir-ActivitySource. Entspricht der <c>AddSleipnir</c>-Signatur.
/// </summary>
/// <remarks>
/// Die Instrumentierung selbst (ActivitySource "Sleipnir") lebt immer-an im Motor
/// (<c>SleipnirCore.Tracing.SleipnirTracing</c>) und ist kostenneutral ohne Listener.
/// Dieses Paket bringt das SDK und die Exporter; wer sie nicht braucht, ruft
/// <c>AddSleipnirTelemetry</c> einfach nicht auf (oder nutzt eigenes OTel-Setup
/// mit <c>AddSource(SleipnirTracing.ActivitySourceName)</c>).
/// </remarks>
public static class SleipnirTelemetryServiceExtensions
{
    /// <param name="services">Die Service-Kollektion.</param>
    /// <param name="configure">Optionale Konfiguration der <see cref="SleipnirTelemetryOptions"/>.</param>
    /// <returns><paramref name="services"/> für Fluent-Chaining.</returns>
    public static IServiceCollection AddSleipnirTelemetry(
        this IServiceCollection services,
        Action<SleipnirTelemetryOptions>? configure = null)
    {
        var options = new SleipnirTelemetryOptions();
        configure?.Invoke(options);

        services.AddOpenTelemetry().WithTracing(builder =>
        {
            // Der einzige Integrationspunkt: der Sleipnir-Quellenname.
            builder
                .AddSource(SleipnirTracing.ActivitySourceName)
                .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(options.ServiceName));

            if (options.IncludeAspNetCore)
                builder.AddAspNetCoreInstrumentation();
            if (options.IncludeHttpClient)
                builder.AddHttpClientInstrumentation();

            if (options.Exporter == SleipnirExporter.Console)
            {
                builder.AddConsoleExporter();
            }
            else
            {
                builder.AddOtlpExporter(o =>
                {
                    if (!string.IsNullOrEmpty(options.OtlpEndpoint))
                        o.Endpoint = new Uri(options.OtlpEndpoint);
                });
            }
        });

        return services;
    }
}