using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using TrameCore.Tracing;

namespace TrameTelemetry;

/// <summary>
/// Server-seitiger Einstiegspunkt für OpenTelemetry: bootet das OTel-SDK und
/// abonniert den Trame-ActivitySource. Entspricht der <c>AddTrame</c>-Signatur.
/// </summary>
/// <remarks>
/// Die Instrumentierung selbst (ActivitySource "Trame") lebt immer-an im Motor
/// (<c>TrameCore.Tracing.TrameTracing</c>) und ist kostenneutral ohne Listener.
/// Dieses Paket bringt das SDK und die Exporter; wer sie nicht braucht, ruft
/// <c>AddTrameTelemetry</c> einfach nicht auf (oder nutzt eigenes OTel-Setup
/// mit <c>AddSource(TrameTracing.ActivitySourceName)</c>).
/// </remarks>
public static class TrameTelemetryServiceExtensions
{
    /// <param name="services">Die Service-Kollektion.</param>
    /// <param name="configure">Optionale Konfiguration der <see cref="TrameTelemetryOptions"/>.</param>
    /// <returns><paramref name="services"/> für Fluent-Chaining.</returns>
    public static IServiceCollection AddTrameTelemetry(
        this IServiceCollection services,
        Action<TrameTelemetryOptions>? configure = null)
    {
        var options = new TrameTelemetryOptions();
        configure?.Invoke(options);

        services.AddOpenTelemetry().WithTracing(builder =>
        {
            // Der einzige Integrationspunkt: der Trame-Quellenname.
            builder
                .AddSource(TrameTracing.ActivitySourceName)
                .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(options.ServiceName));

            if (options.IncludeAspNetCore)
                builder.AddAspNetCoreInstrumentation();
            if (options.IncludeHttpClient)
                builder.AddHttpClientInstrumentation();

            if (options.Exporter == TrameExporter.Console)
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