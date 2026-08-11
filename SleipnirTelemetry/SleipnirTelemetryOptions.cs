namespace SleipnirTelemetry;

/// <summary>Wohin Sleipnir die Spans exportiert (via <c>AddSleipnirTelemetry</c>).</summary>
public enum SleipnirExporter
{
    /// <summary>Ausgabe auf der Konsole (für Entwicklung/Diagnose).</summary>
    Console,

    /// <summary>Export via OTLP (z. B. an einen lokalen Collector oder Jaeger/Tempo).</summary>
    Otlp,
}

/// <summary>
/// Optionen für <see cref="SleipnirTelemetryServiceExtensions.AddSleipnirTelemetry"/>.
/// Bewusst minimal: wer ResourceBuilder, Sampler oder eigene Exporter braucht,
/// ruft direkt <c>AddOpenTelemetry()</c> auf und abonniert den Quellennamen
/// <c>SleipnirCore.Tracing.SleipnirTracing.ActivitySourceName</c>.
/// </summary>
public sealed class SleipnirTelemetryOptions
{
    /// <summary>Service-Name für die OTel-Resource (Default "Sleipnir").</summary>
    public string ServiceName { get; set; } = "Sleipnir";

    /// <summary>Export-Ziel. Default OTLP; Console für lokale Diagnose.</summary>
    public SleipnirExporter Exporter { get; set; } = SleipnirExporter.Otlp;

    /// <summary>
    /// OTLP-Endpoint (z. B. "http://localhost:4317"). <c>null</c> überlässt dem
    /// SDK-Default bzw. der Umgebungsvariable <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>.
    /// </summary>
    public string? OtlpEndpoint { get; set; }

    /// <summary>AspNetCore-Instrumentierung (HTTP-Inbound) zuschalten. Default an.</summary>
    public bool IncludeAspNetCore { get; set; } = true;

    /// <summary>HttpClient-Instrumentierung (HTTP-Outbound) zuschalten. Default an.</summary>
    public bool IncludeHttpClient { get; set; } = true;
}