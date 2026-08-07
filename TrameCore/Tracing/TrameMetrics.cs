using System.Diagnostics;
using System.Diagnostics.Metrics;
using TrameCommon.Models;

namespace TrameCore.Tracing;

/// <summary>
/// OpenTelemetry-Metrics für Trame (Phase 1). Ergänzt <see cref="TrameTracing"/> (Spans)
/// um die Metrics-Säule der OTel-Drei-Säulen (Traces/Metrics/Logs). Der <see cref="Meter"/>
/// heißt <see cref="MeterName"/> (=<c>"Trame"</c>, derselbe Name wie der ActivitySource —
/// OTel erlaubt das; Konsumenten abonnieren beide unter <c>"Trame"</c>).
/// </summary>
/// <remarks>
/// <para>
/// Wie <see cref="TrameTracing"/> ist diese Klasse immer instanziiert, aber kostenneutral
/// ohne abonnierten <c>MetricReader</c>: die Instrumente messen nur, wenn ein Listener
/// existiert. Der Meter wird einmal statisch erzeugt und lebt für die Prozess-Lebensdauer.
/// </para>
/// <para>
/// Semantische Konventionen folgen OTel RPC: <c>rpc.system</c>=<c>trame</c>,
/// <c>rpc.service</c>, <c>rpc.method</c> als Tag-Keys. Trame-spezifische Instrumente
/// tragen das <c>trame.*</c>-Präfix. Siehe <c>docs/design/phase-1-interceptor-pipeline.md</c>.
/// </para>
/// </remarks>
public static class TrameMetrics
{
    /// <summary>Name des Meters, unter dem Trame Metrics emittiert (=<see cref="TrameTracing.ActivitySourceName"/>).</summary>
    public const string MeterName = "Trame";

    /// <summary>Die Meter-Instanz (einmalig, Prozess-Lebensdauer).</summary>
    internal static readonly Meter Meter = new(MeterName, "1.0.0");

    // ─── Call-Ebene (pro RPC-Invocation) ────────────────────────────────────

    /// <summary>
    /// Call-Dauer in Millisekunden (Histogram — verteilt, für p50/p90/p99-Dashboards).
    /// Tags: rpc.system, rpc.service, rpc.method, trame.error_category (None bei Erfolg).
    /// </summary>
    internal static readonly Histogram<double> CallDuration = Meter.CreateHistogram<double>(
        "trame.call.duration",
        unit: "ms",
        description: "Duration of a single Trame RPC call in milliseconds.");

    /// <summary>
    /// Anzahl abgeschlossener Calls (Counter — kumulativ, für QPS-Dashboards).
    /// Tags: rpc.system, rpc.service, rpc.method, trame.error_category, trame.success.
    /// </summary>
    internal static readonly Counter<long> CallCount = Meter.CreateCounter<long>(
        "trame.call.count",
        unit: "{call}",
        description: "Total number of Trame RPC calls completed (success or error).");

    /// <summary>
    /// Anzahl fehlgeschlagener Calls (Counter — Subset von CallCount mit success=false,
    /// als Convenience für Error-Rate-Dashboards ohne Filter). Tags wie CallCount.
    /// </summary>
    internal static readonly Counter<long> ErrorCount = Meter.CreateCounter<long>(
        "trame.error.count",
        unit: "{call}",
        description: "Total number of failed Trame RPC calls (non-2xx).");

    // ─── Batch-Ebene ────────────────────────────────────────────────────────

    /// <summary>
    /// Fan-Out eines Batches: Anzahl der Requests pro Batch (Histogram — für
    /// "wie parallel ist der Traffic"-Dashboards). Tag: trame.batch.mode.
    /// </summary>
    internal static readonly Histogram<int> BatchFanOut = Meter.CreateHistogram<int>(
        "trame.batch.fan_out",
        unit: "{request}",
        description: "Number of requests in a Trame batch (fan-out).");

    /// <summary>
    /// Anzahl verarbeiteter Batches (Counter). Tag: trame.batch.mode.
    /// </summary>
    internal static readonly Counter<long> BatchCount = Meter.CreateCounter<long>(
        "trame.batch.count",
        unit: "{batch}",
        description: "Total number of Trame batches processed.");

    // ─── Event-Ebene (Phase 3) ──────────────────────────────────────────────

    /// <summary>
    /// Anzahl gedroppter Events (Counter) — wenn der per-Subscription-Buffer voll ist
    /// (Backpressure, Entscheidung 7: bounded Buffer + drop-oldest). Tag: subscriptionId
    /// (ein Hash davon, um Kardinalität zu begrenzen — hier die rohe ID; ggf. später hashen).
    /// </summary>
    internal static readonly Counter<long> EventDroppedCounter = Meter.CreateCounter<long>(
        "trame.event.dropped",
        unit: "{event}",
        description: "Events dropped because the per-subscription buffer was full (backpressure).");

    /// <summary>Recordet ein gedropptes Event (Backpressure-Metrik).</summary>
    public static void EventDropped(string subscriptionId)
    {
        if (!EventDroppedCounter.Enabled) return;
        EventDroppedCounter.Add(1, new TagList { RpcSystemTag, new("trame.subscription_id", subscriptionId) });
    }

    // ─── Convenience: Tags als ReadOnlySpan ─────────────────────────────────

    /// <summary>Standard-Tag-Set für einen Call (rpc.system immer "trame").</summary>
    internal static readonly KeyValuePair<string, object?> RpcSystemTag =
        new("rpc.system", "trame");

    /// <summary>Recordet einen abgeschlossenen Call: Duration + Count + ErrorCount (falls non-2xx).</summary>
    public static void RecordCall(
        TrameRequest request, TrameResponse? response,
        double durationMs,
        TrameCommon.Results.TrameErrorCategory category = TrameCommon.Results.TrameErrorCategory.None)
    {
        if (!CallDuration.Enabled && !CallCount.Enabled && !ErrorCount.Enabled)
            return;

        var success = response?.IsSuccess == true;
        var tags = new TagList
        {
            RpcSystemTag,
            new("rpc.service", request.Controller),
            new("rpc.method", request.Method),
            new("trame.error_category", category.ToString()),
            new("trame.success", success),
        };

        CallDuration.Record(durationMs, tags);
        CallCount.Add(1, tags);
        if (!success)
            ErrorCount.Add(1, tags);
    }

    /// <summary>Recordet einen Batch: FanOut + Count.</summary>
    public static void RecordBatch(IReadOnlyList<TrameRequest> requests, ExecutionMode mode)
    {
        if (!BatchFanOut.Enabled && !BatchCount.Enabled)
            return;

        var tags = new TagList
        {
            RpcSystemTag,
            new("trame.batch.mode", mode.ToString()),
        };

        BatchFanOut.Record(requests.Count, tags);
        BatchCount.Add(1, tags);
    }
}