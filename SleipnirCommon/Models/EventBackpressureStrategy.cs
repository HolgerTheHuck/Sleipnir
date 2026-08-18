namespace SleipnirCommon.Models;

/// <summary>
/// Backpressure strategy for a server-push event subscription's per-subscription
/// buffer (Phase 3, Events). When the producer emits an event while the buffer is
/// full (the consumer is not draining fast enough), this decides what happens.
/// Set globally via <c>SleipnirOptions.EventBackpressureStrategy</c>, overridden
/// per event via <c>[SleipnirEvent(BackpressureStrategy = …)]</c>.
/// </summary>
public enum EventBackpressureStrategy
{
    /// <summary>
    /// Sentinel meaning "inherit the global
    /// <c>SleipnirOptions.EventBackpressureStrategy</c>". This is the default of
    /// <c>[SleipnirEvent(BackpressureStrategy = …)]</c> (i.e. the attribute does
    /// not override the strategy). At the global option level, <c>Inherit</c> is
    /// treated as <see cref="DropOldest"/> (the framework default) — set an
    /// explicit value on the option.
    /// </summary>
    Inherit,

    /// <summary>
    /// Default. Bounded buffer; when full, the <b>oldest</b> buffered event is
    /// evicted to make room for the newest. Keeps the subscription recent (the
    /// consumer sees the latest events, not the oldest backlog) and is DoS-safe:
    /// a slow consumer cannot block the producer or exhaust memory. Evictions
    /// increment the <c>sleipnir.event.dropped</c> counter. Use this for
    /// "current-state" streams (prices, presence, telemetry) where stale values
    /// are worthless.
    /// </summary>
    DropOldest,

    /// <summary>
    /// Bounded buffer; when full, the <b>newest</b> event is dropped (not
    /// enqueued) and <c>sleipnir.event.dropped</c> is incremented. Preserves the
    /// full backlog in order but loses the latest event — the consumer falls
    /// behind. Use this when the historical sequence matters more than freshness
    /// (an ordered log the consumer will catch up on).
    /// </summary>
    DropWrite,

    /// <summary>
    /// Bounded buffer; when full, the producer's <c>OnNext</c> call <b>blocks</b>
    /// until the consumer drains a slot. No event is lost and the counter is not
    /// incremented, but the producer thread is suspended — a slow consumer
    /// back-pressures the source. Risky for hot observables on a shared thread
    /// (a stalled consumer can stall the producer and, transitively, other work
    /// on that thread); opt in deliberately for lossless, cooperative flow
    /// control with a producer that tolerates blocking.
    /// </summary>
    Block,

    /// <summary>
    /// Unbounded buffer; every event is enqueued, nothing is dropped, the counter
    /// is never incremented. Memory grows without bound if the consumer is slower
    /// than the producer — there is no DoS backstop. Use only for short-lived
    /// subscriptions with a known bounded total volume, or when the consumer is
    /// guaranteed faster than the producer.
    /// </summary>
    Unbounded,
}