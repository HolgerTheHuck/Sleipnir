using System.Diagnostics;
using System.Diagnostics.Metrics;
using SleipnirCommon.Models;

namespace SleipnirCore.Tracing;

/// <summary>
/// OpenTelemetry-Metrics für Sleipnir (Phase 1). Ergänzt <see cref="SleipnirTracing"/> (Spans)
/// um die Metrics-Säule der OTel-Drei-Säulen (Traces/Metrics/Logs). Der <see cref="Meter"/>
/// heißt <see cref="MeterName"/> (=<c>"Sleipnir"</c>, derselbe Name wie der ActivitySource —
/// OTel erlaubt das; Konsumenten abonnieren beide unter <c>"Sleipnir"</c>).
/// </summary>
/// <remarks>
/// <para>
/// Wie <see cref="SleipnirTracing"/> ist diese Klasse immer instanziiert, aber kostenneutral
/// ohne abonnierten <c>MetricReader</c>: die Instrumente messen nur, wenn ein Listener
/// existiert. Der Meter wird einmal statisch erzeugt und lebt für die Prozess-Lebensdauer.
/// </para>
/// <para>
/// Semantische Konventionen folgen OTel RPC: <c>rpc.system</c>=<c>sleipnir</c>,
/// <c>rpc.service</c>, <c>rpc.method</c> als Tag-Keys. Sleipnir-spezifische Instrumente
/// tragen das <c>sleipnir.*</c>-Präfix. Siehe <c>docs/design/phase-1-interceptor-pipeline.md</c>.
/// </para>
/// </remarks>
public static class SleipnirMetrics
{
    /// <summary>Name des Meters, unter dem Sleipnir Metrics emittiert (=<see cref="SleipnirTracing.ActivitySourceName"/>).</summary>
    public const string MeterName = "Sleipnir";

    /// <summary>Die Meter-Instanz (einmalig, Prozess-Lebensdauer).</summary>
    internal static readonly Meter Meter = new(MeterName, "1.0.0");

    // ─── Live-Gauges (Verbindungen / Subscriptions — kein OTel-Äquivalent) ────
    // Werden einmalig via SetConnectionRegistry an den Meter gehängt und lesen
    // SleipnirConnectionRegistry (lock-free Interlocked) zum Scraping-Zeitpunkt.

    private static ObservableGauge<int>? _connectionsGauge;
    private static ObservableGauge<int>? _subscriptionsGauge;

    /// <summary>
    /// Hängt die <see cref="ObservableGauge{T}"/>-Instrumente
    /// <c>sleipnir.ws.connections</c> und <c>sleipnir.subscriptions.active</c> an den
    /// <see cref="Meter"/>. Einmal pro Prozess aus <c>AddSleipnir</c> gerufen; idempotent
    /// (kein Duplikat-Gauge bei mehrfacher Registrierung). Die Callbacks lesen
    /// <see cref="SleipnirConnectionRegistry.Current"/> — die prozess-global *aktuelle*
    /// Registry, nicht das hier übergebene Objekt —, sodass ein Testprozess, der mehrere
    /// Sleipnir-Hosts startet/stoppt, die Gauges nicht auf die erste Registry eingefroren
    /// bleiben. <paramref name="registry"/> dient nur als Trigger zur einmaligen Erzeugung
    /// (und wird von <c>AddSleipnir</c> ohnehin via <see cref="SleipnirConnectionRegistry.SetInstance"/>
    /// als <c>Current</c> installiert). Die Gauges liefern nur Werte, wenn ein MetricReader
    /// abonniert ist (z. B. via den Prometheus-Exporter in Sleipnir.Telemetry).
    /// </summary>
    public static void SetConnectionRegistry(SleipnirConnectionRegistry registry)
    {
        // Touch the parameter so a misused call (null) is visibly wrong at the call site;
        // the actual value read at scrape time is Current (the latest registered registry).
        _ = registry ?? throw new ArgumentNullException(nameof(registry));

        _connectionsGauge ??= Meter.CreateObservableGauge<int>(
            "sleipnir.ws.connections",
            () => SleipnirConnectionRegistry.Current?.Connections ?? 0,
            unit: "{connection}",
            description: "Active WebSocket connections.");

        _subscriptionsGauge ??= Meter.CreateObservableGauge<int>(
            "sleipnir.subscriptions.active",
            () => SleipnirConnectionRegistry.Current?.Subscriptions ?? 0,
            unit: "{subscription}",
            description: "Active event subscriptions across all WebSocket connections.");
    }

    // ─── Call-Ebene (pro RPC-Invocation) ────────────────────────────────────

    /// <summary>
    /// Call-Dauer in Millisekunden (Histogram — verteilt, für p50/p90/p99-Dashboards).
    /// Tags: rpc.system, rpc.service, rpc.method, sleipnir.error_category (None bei Erfolg).
    /// </summary>
    internal static readonly Histogram<double> CallDuration = Meter.CreateHistogram<double>(
        "sleipnir.call.duration",
        unit: "ms",
        description: "Duration of a single Sleipnir RPC call in milliseconds.");

    /// <summary>
    /// Anzahl abgeschlossener Calls (Counter — kumulativ, für QPS-Dashboards).
    /// Tags: rpc.system, rpc.service, rpc.method, sleipnir.error_category, sleipnir.success.
    /// </summary>
    internal static readonly Counter<long> CallCount = Meter.CreateCounter<long>(
        "sleipnir.call.count",
        unit: "{call}",
        description: "Total number of Sleipnir RPC calls completed (success or error).");

    /// <summary>
    /// Anzahl fehlgeschlagener Calls (Counter — Subset von CallCount mit success=false,
    /// als Convenience für Error-Rate-Dashboards ohne Filter). Tags wie CallCount.
    /// </summary>
    internal static readonly Counter<long> ErrorCount = Meter.CreateCounter<long>(
        "sleipnir.error.count",
        unit: "{call}",
        description: "Total number of failed Sleipnir RPC calls (non-2xx).");

    // ─── Batch-Ebene ────────────────────────────────────────────────────────

    /// <summary>
    /// Fan-Out eines Batches: Anzahl der Requests pro Batch (Histogram — für
    /// "wie parallel ist der Traffic"-Dashboards). Tag: sleipnir.batch.mode.
    /// </summary>
    internal static readonly Histogram<int> BatchFanOut = Meter.CreateHistogram<int>(
        "sleipnir.batch.fan_out",
        unit: "{request}",
        description: "Number of requests in a Sleipnir batch (fan-out).");

    /// <summary>
    /// Anzahl verarbeiteter Batches (Counter). Tag: sleipnir.batch.mode.
    /// </summary>
    internal static readonly Counter<long> BatchCount = Meter.CreateCounter<long>(
        "sleipnir.batch.count",
        unit: "{batch}",
        description: "Total number of Sleipnir batches processed.");

    // ─── Event-Ebene (Phase 3) ──────────────────────────────────────────────

    /// <summary>
    /// Anzahl gedroppter Events (Counter) — wenn der per-Subscription-Buffer voll ist
    /// (Backpressure, Entscheidung 7: bounded Buffer + drop-oldest). Tag: subscriptionId
    /// (ein Hash davon, um Kardinalität zu begrenzen — hier die rohe ID; ggf. später hashen).
    /// </summary>
    internal static readonly Counter<long> EventDroppedCounter = Meter.CreateCounter<long>(
        "sleipnir.event.dropped",
        unit: "{event}",
        description: "Events dropped because the per-subscription buffer was full (backpressure).");

    /// <summary>Recordet ein gedropptes Event (Backpressure-Metrik).</summary>
    public static void EventDropped(string subscriptionId)
    {
        // Bump the registry accumulator unconditionally so /observability sees drops
        // even without a subscribed MetricReader (the OTel Counter below is write-only).
        SleipnirConnectionRegistry.Current?.RecordEventDrop();
        if (!EventDroppedCounter.Enabled) return;
        EventDroppedCounter.Add(1, new TagList { RpcSystemTag, new("sleipnir.subscription_id", subscriptionId) });
    }

    // ─── Convenience: Tags als ReadOnlySpan ─────────────────────────────────

    /// <summary>Standard-Tag-Set für einen Call (rpc.system immer "sleipnir").</summary>
    internal static readonly KeyValuePair<string, object?> RpcSystemTag =
        new("rpc.system", "sleipnir");

    /// <summary>Recordet einen abgeschlossenen Call: Duration + Count + ErrorCount (falls non-2xx).</summary>
    public static void RecordCall(
        SleipnirRequest request, SleipnirResponse? response,
        double durationMs,
        SleipnirCommon.Results.SleipnirErrorCategory category = SleipnirCommon.Results.SleipnirErrorCategory.None)
    {
        var success = response?.IsSuccess == true;

        // Bump the registry accumulators unconditionally so /observability sees call/error
        // totals even without a subscribed MetricReader (the OTel Counters below are write-only).
        SleipnirConnectionRegistry.Current?.RecordCall(success);

        if (!CallDuration.Enabled && !CallCount.Enabled && !ErrorCount.Enabled)
            return;

        var tags = new TagList
        {
            RpcSystemTag,
            new("rpc.service", request.Controller),
            new("rpc.method", request.Method),
            new("sleipnir.error_category", category.ToString()),
            new("sleipnir.success", success),
        };

        CallDuration.Record(durationMs, tags);
        CallCount.Add(1, tags);
        if (!success)
            ErrorCount.Add(1, tags);
    }

    /// <summary>Recordet einen Batch: FanOut + Count.</summary>
    public static void RecordBatch(IReadOnlyList<SleipnirRequest> requests, ExecutionMode mode)
    {
        // Bump the registry accumulator unconditionally so /observability sees batch totals
        // even without a subscribed MetricReader (the OTel Counters below are write-only).
        SleipnirConnectionRegistry.Current?.RecordBatch();

        if (!BatchFanOut.Enabled && !BatchCount.Enabled)
            return;

        var tags = new TagList
        {
            RpcSystemTag,
            new("sleipnir.batch.mode", mode.ToString()),
        };

        BatchFanOut.Record(requests.Count, tags);
        BatchCount.Add(1, tags);
    }
}