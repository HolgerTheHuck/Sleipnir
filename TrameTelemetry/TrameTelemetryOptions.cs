namespace TrameTelemetry;

/// <summary>Wohin Trame die Spans exportiert (via <c>AddTrameTelemetry</c>).</summary>
public enum TrameExporter
{
    /// <summary>Ausgabe auf der Konsole (für Entwicklung/Diagnose).</summary>
    Console,

    /// <summary>Export via OTLP (z. B. an einen lokalen Collector oder Jaeger/Tempo).</summary>
    Otlp,
}

/// <summary>
/// Optionen für <see cref="TrameTelemetryServiceExtensions.AddTrameTelemetry"/>.
/// Bewusst minimal: wer ResourceBuilder, Sampler oder eigene Exporter braucht,
/// ruft direkt <c>AddOpenTelemetry()</c> auf und abonniert den Quellennamen
/// <c>TrameCore.Tracing.TrameTracing.ActivitySourceName</c>.
/// </summary>
public sealed class TrameTelemetryOptions
{
    /// <summary>Service-Name für die OTel-Resource (Default "Trame").</summary>
    public string ServiceName { get; set; } = "Trame";

    /// <summary>Export-Ziel. Default OTLP; Console für lokale Diagnose.</summary>
    public TrameExporter Exporter { get; set; } = TrameExporter.Otlp;

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