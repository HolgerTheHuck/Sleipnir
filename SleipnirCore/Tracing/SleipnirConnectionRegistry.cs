using System.Threading;

namespace SleipnirCore.Tracing;

/// <summary>
/// Process-wide, lock-free registry of live Sleipnir transport state — active
/// WebSocket connections, active event subscriptions, and cumulative counters
/// (calls, errors, batches, dropped events). Backs both the
/// <c>sleipnir.ws.connections</c> / <c>sleipnir.subscriptions.active</c>
/// <see cref="System.Diagnostics.Metrics.ObservableGauge{T}"/> instruments on
/// <see cref="SleipnirMetrics"/> (read by the Prometheus exporter at scrape time)
/// and the JSON <c>/observability</c> snapshot (read directly, without an OTel
/// <c>MetricReader</c>).
/// </summary>
/// <remarks>
/// <para>
/// The OTel <c>Counter</c>/<c>Histogram</c> instruments on <see cref="SleipnirMetrics"/>
/// are write-only — the .NET Metrics API offers no way to read an accumulated value
/// back out. To keep the JSON <c>/observability</c> endpoint free of the OTel SDK and
/// readable without a subscribed reader, this registry holds parallel
/// <see cref="Interlocked"/> accumulators that <see cref="SleipnirMetrics"/> bumps
/// alongside the OTel instruments (localized double-bookkeeping). The gauges read the
/// live connection/subscription counts (no OTel equivalent exists for those).
/// </para>
/// <para>
/// Registered once as a DI singleton in <c>AddSleipnir</c>; the process-wide
/// <see cref="Instance"/> is set eagerly at registration so the static
/// <see cref="SleipnirMetrics"/> callbacks can read it without DI. Thread-safe via
/// <see cref="Interlocked"/>; no locks.
/// </para>
/// </remarks>
public sealed class SleipnirConnectionRegistry
{
    private int _connections;
    private int _subscriptions;
    private long _eventDroppedTotal;
    private long _callCount;
    private long _errorCount;
    private long _batchCount;

    /// <summary>
    /// When this registry was created (during <c>AddSleipnir</c>, i.e. host
    /// service-registration time). The <c>/observability</c> endpoint derives uptime from
    /// this — close enough to process start for an ops display.
    /// </summary>
    public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;

    private static SleipnirConnectionRegistry? _instance;

    /// <summary>
    /// The process-wide singleton, set once during <c>AddSleipnir</c> registration so
    /// the static <see cref="SleipnirMetrics"/> gauge callbacks can read live values
    /// without a DI scope. Throws if accessed before registration (programming error).
    /// </summary>
    public static SleipnirConnectionRegistry Instance =>
        _instance ?? throw new InvalidOperationException(
            "SleipnirConnectionRegistry is not registered. Call AddSleipnir first.");

    /// <summary>
    /// The currently registered instance, or <c>null</c> before registration. Used by
    /// <see cref="SleipnirMetrics"/> to bump accumulators unconditionally (even without
    /// an OTel reader) without throwing when the registry is absent (e.g. unit tests
    /// that exercise the invoker without the full host).
    /// </summary>
    internal static SleipnirConnectionRegistry? Current => _instance;

    /// <summary>Set the process-wide singleton (called once from <c>AddSleipnir</c>).</summary>
    public static void SetInstance(SleipnirConnectionRegistry registry)
        => _instance = registry;

    // ─── Live gauges (no OTel equivalent) ──────────────────────────────────────

    /// <summary>Active WebSocket connections (incremented at upgrade, decremented on close).</summary>
    public int Connections => Interlocked.CompareExchange(ref _connections, 0, 0);

    /// <summary>Active event subscriptions across all WebSocket connections.</summary>
    public int Subscriptions => Interlocked.CompareExchange(ref _subscriptions, 0, 0);

    // ─── Cumulative counters (parallel to the OTel Counter instruments) ────────

    /// <summary>Total events dropped due to per-subscription buffer backpressure.</summary>
    public long EventDroppedTotal => Interlocked.Read(ref _eventDroppedTotal);

    /// <summary>Total Sleipnir RPC calls completed (success or error).</summary>
    public long CallCount => Interlocked.Read(ref _callCount);

    /// <summary>Total failed Sleipnir RPC calls (non-2xx).</summary>
    public long ErrorCount => Interlocked.Read(ref _errorCount);

    /// <summary>Total Sleipnir batches processed.</summary>
    public long BatchCount => Interlocked.Read(ref _batchCount);

    // ─── Mutators (called from the transports + SleipnirMetrics) ────────────────

    /// <summary>Call once per accepted WebSocket connection (after the auth gate).</summary>
    public void IncConnection() => Interlocked.Increment(ref _connections);

    /// <summary>Call once per closed WebSocket connection (in the connection finally block).</summary>
    public void DecConnection() => Interlocked.Decrement(ref _connections);

    /// <summary>Call once per successfully added event subscription.</summary>
    public void IncSubscription() => Interlocked.Increment(ref _subscriptions);

    /// <summary>Call exactly once per ended event subscription (unsubscribe or disconnect).</summary>
    public void DecSubscription() => Interlocked.Decrement(ref _subscriptions);

    /// <summary>Record a dropped event (called from the per-subscription backpressure callback).</summary>
    public void RecordEventDrop() => Interlocked.Increment(ref _eventDroppedTotal);

    /// <summary>Record a completed call (bumps <see cref="CallCount"/>; bumps <see cref="ErrorCount"/> when <paramref name="success"/> is false).</summary>
    public void RecordCall(bool success)
    {
        Interlocked.Increment(ref _callCount);
        if (!success) Interlocked.Increment(ref _errorCount);
    }

    /// <summary>Record a processed batch.</summary>
    public void RecordBatch() => Interlocked.Increment(ref _batchCount);

    /// <summary>A point-in-time snapshot of all registry counters (for the JSON /observability endpoint).</summary>
    public ObservabilitySnapshot GetSnapshot() => new()
    {
        ActiveConnections = Connections,
        ActiveSubscriptions = Subscriptions,
        EventDroppedTotal = EventDroppedTotal,
        CallCount = CallCount,
        ErrorCount = ErrorCount,
        BatchCount = BatchCount,
    };
}

/// <summary>
/// Point-in-time transport/runtime snapshot returned by the JSON
/// <c>GET /api/sleipnir/observability</c> endpoint (opt-in via
/// <c>SleipnirOptions.EnableObservability</c>, RequireAuth-gated). The DevUI
/// Observability panel polls this.
/// </summary>
public sealed class ObservabilitySnapshot
{
    /// <summary>Active WebSocket connections.</summary>
    public int ActiveConnections { get; set; }

    /// <summary>Active event subscriptions across all WebSocket connections.</summary>
    public int ActiveSubscriptions { get; set; }

    /// <summary>Cumulative events dropped due to backpressure.</summary>
    public long EventDroppedTotal { get; set; }

    /// <summary>Cumulative completed RPC calls.</summary>
    public long CallCount { get; set; }

    /// <summary>Cumulative failed RPC calls.</summary>
    public long ErrorCount { get; set; }

    /// <summary>Cumulative batches processed.</summary>
    public long BatchCount { get; set; }
}