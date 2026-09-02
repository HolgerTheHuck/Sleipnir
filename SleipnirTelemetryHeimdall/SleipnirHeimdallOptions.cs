namespace SleipnirTelemetryHeimdall;

/// <summary>
/// Options for <see cref="SleipnirHeimdallServiceExtensions.AddSleipnirHeimdallTelemetry"/>.
/// Deliberately minimal: the single integration touchpoint is the source/meter name
/// <c>"Sleipnir"</c> (<c>SleipnirCore.Tracing.SleipnirTracing.ActivitySourceName</c> /
/// <c>SleipnirCore.Tracing.SleipnirMetrics.MeterName</c>). Consumers who need full control
/// (custom resource attributes, sampling, runtime instrumentation, a different Heimdall
/// storage backend) can skip this package and wire
/// <c>AddOpenTelemetry().WithTracing(b =&gt; b.AddSource("Sleipnir"))</c> plus Heimdall's
/// own <c>UseHeimdallExporter</c> directly.
/// </summary>
public sealed class SleipnirHeimdallOptions
{
    /// <summary><c>service.name</c> on the OTel resource emitted to Heimdall. Default "Sleipnir".</summary>
    public string ServiceName { get; set; } = "Sleipnir";

    /// <summary><c>service.version</c> on the OTel resource (optional).</summary>
    public string? ServiceVersion { get; set; }

    /// <summary>
    /// SQLite database path for the embedded Heimdall backend
    /// (forwarded to <c>SQLiteTelemetryOptions.DataPath</c>). Default "heimdall-otel.db".
    /// </summary>
    public string DataPath { get; set; } = "heimdall-otel.db";

    /// <summary>
    /// Retention window in days (forwarded to <c>SQLiteTelemetryOptions.RetentionDays</c>;
    /// per-signal overrides remain at their defaults). Default 7.
    /// </summary>
    public int RetentionDays { get; set; } = 7;

    /// <summary>Add ASP.NET Core inbound HTTP instrumentation to the trace pipeline. Default on.</summary>
    public bool IncludeAspNetCore { get; set; } = true;

    /// <summary>Add HttpClient outbound instrumentation to the trace pipeline. Default on.</summary>
    public bool IncludeHttpClient { get; set; } = true;

    /// <summary>
    /// Bridge <c>ILogger</c> output into Heimdall via the OTel logs pipeline. Default on.
    /// Attaches the Heimdall log exporter to the OTel logger provider and bridges
    /// <c>ILogger</c> into that provider, so the host's normal <c>ILogger&lt;T&gt;</c> usage
    /// (including Sleipnir's built-in logging interceptor) flows into Heimdall.
    /// </summary>
    public bool IncludeLogs { get; set; } = true;

    /// <summary>
    /// Enable the periodic alert evaluator (rules → channels). The alert rule/state stores and
    /// the <c>/otel/alerts</c> UI are registered unconditionally (the dashboard maps them and
    /// they are required for routing to come up at all); this flag additionally starts the
    /// evaluator. Default false (stores/UI only, no evaluation).
    /// </summary>
    public bool EnableAlerting { get; set; }

    /// <summary>
    /// Directory for alert rule JSON files. Empty = derived next to <see cref="DataPath"/>
    /// (<c>&lt;DataPath-dir&gt;/alerts/rules</c>). Default empty (derived).
    /// </summary>
    public string? AlertingRulesDir { get; set; }

    /// <summary>
    /// Directory for the alert state store (<c>alertstate.json</c>). Empty = derived next to
    /// <see cref="DataPath"/> (<c>&lt;DataPath-dir&gt;/alerts</c>). Default empty (derived).
    /// </summary>
    public string? AlertingStateDir { get; set; }
}