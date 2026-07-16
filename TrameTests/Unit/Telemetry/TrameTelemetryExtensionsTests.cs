using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TrameCore.Tracing;
using TrameTelemetry;
using Xunit;

namespace TrameTests.Unit.Telemetry;

/// <summary>
/// Tests für <see cref="TrameTelemetryServiceExtensions.AddTrameTelemetry"/> — das
/// optionale OTel-SDK-Bootstrap. Verifiziert, dass der Trame-ActivitySource abonniert
/// wird, die IncludeAspNetCore/IncludeHttpClient-Gates die Subscription nicht abwürgen
/// und der OTLP-Pfad den Provider ohne Wurf baut. Die OTel-SDK-Subscription ist
/// prozess-global und würde den <c>NoListener</c>-Test sowie die <c>probe != null</c>-
/// Assertions der Tracing-Tests verfälschen, liefen sie parallel. Daher teilen sich
/// Tracing- und Telemetry-Tests die Collection „trame-tracing“ (serialisiert nur
/// diese untereinander); der Rest der Assembly parallelisiert normal weiter.
/// </summary>
[Collection("trame-tracing")]
public class TrameTelemetryExtensionsTests
{
    [Fact]
    public async Task AddTrameTelemetry_Console_SubscribesTrameSource()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTrameTelemetry(o => o.Exporter = TrameExporter.Console);

        var sp = services.BuildServiceProvider();
        // AddOpenTelemetry() registriert IHostedService(s) — ohne Start läuft das SDK nicht.
        sp.GetServices<IHostedService>().Should().NotBeEmpty();
        await StartHostedServicesAsync(sp);
        try
        {
            // SDK läuft nun und hat „Trame“ abonniert → ein Activity gleicher Quelle ist nicht null.
            var probeSource = new ActivitySource(TrameTracing.ActivitySourceName);
            using var probe = probeSource.StartActivity("telemetry-probe");
            probe.Should().NotBeNull();
        }
        finally
        {
            await StopAndDisposeAsync(sp);
        }
    }

    [Fact]
    public async Task AddTrameTelemetry_GatesOff_StillSubscribesTrameSource()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTrameTelemetry(o =>
        {
            o.Exporter = TrameExporter.Console;
            o.IncludeAspNetCore = false;
            o.IncludeHttpClient = false;
        });

        var sp = services.BuildServiceProvider();
        await StartHostedServicesAsync(sp);
        try
        {
            // Die beiden Instrumentierungen sind ausgeblendet — der Trame-Source bleibt
            // abonniert (AddSource ist unabhängig von den Instrumentierungs-Gates).
            var probeSource = new ActivitySource(TrameTracing.ActivitySourceName);
            using var probe = probeSource.StartActivity("telemetry-probe-gated");
            probe.Should().NotBeNull();
        }
        finally
        {
            await StopAndDisposeAsync(sp);
        }
    }

    [Fact]
    public async Task AddTrameTelemetry_OtlpWithEndpoint_BuildsProviderWithoutThrow()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTrameTelemetry(o =>
        {
            o.Exporter = TrameExporter.Otlp;
            o.OtlpEndpoint = "http://localhost:4317";
        });

        var sp = services.BuildServiceProvider();
        sp.GetServices<IHostedService>().Should().NotBeEmpty();
        // StartAsync baut den TracerProvider mit OTLP-Exporter + Endpoint. Kein Activity
        // wird emittiert → beim Stop entsteht kein Export (kein Netzwerkzugriff, kein Wurf).
        await StartHostedServicesAsync(sp);
        await StopAndDisposeAsync(sp);
    }

    private static async Task StartHostedServicesAsync(IServiceProvider sp)
    {
        foreach (var hs in sp.GetServices<IHostedService>())
            await hs.StartAsync(default);
    }

    private static async Task StopAndDisposeAsync(IServiceProvider sp)
    {
        foreach (var hs in sp.GetServices<IHostedService>())
            await hs.StopAsync(default);
        if (sp is IAsyncDisposable ad)
            await ad.DisposeAsync();
        else if (sp is IDisposable d)
            d.Dispose();
    }
}