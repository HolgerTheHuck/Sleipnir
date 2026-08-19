using System;

namespace SleipnirCommon.Models
{
    /// <summary>
    /// Ergebnis eines Subscribe-Aufrufs (Phase 3, Events). Entweder ein Fehler
    /// (<see cref="Error"/> != null — Auth/Routing/Binding) oder das Observable
    /// (<see cref="Observable"/> != null — Erfolg, der Aufrufer subscribt darauf).
    /// </summary>
    /// <remarks>
    /// Phase 3 — siehe <c>docs/design/phase-3-events.md</c>. Das Observable trägt
    /// <c>object?</c>-Elemente (jedes wird als JSON serialisiert); der konkrete
    /// Element-Typ steht in der Discovery (<c>kind:"event"</c> + <c>Element</c>).
    /// <see cref="EventBufferCapacity"/>/<see cref="EventBackpressureStrategy"/>
    /// sind die pro-Subscription aufgelösten Backpressure-Parameter (Per-Event-
    /// Override ?? globale Option ?? Default); der SubscriptionManager legt den
    /// Buffer damit an.
    /// </remarks>
    public sealed class SleipnirSubscribeResult
    {
        /// <summary>Fehler-Response bei Auth/Routing/Binding-Fehler; null bei Erfolg.</summary>
        public SleipnirResponse? Error { get; set; }

        /// <summary>
        /// Das IObservable bei Erfolg (der Server subscribt darauf und pusht jedes
        /// Element als Event-Frame); null bei Fehler. Elemente sind <c>object?</c>
        /// (werden als JSON serialisiert).
        /// </summary>
        public IObservable<object?>? Observable { get; set; }

        /// <summary>
        /// Aufgelöste pro-Subscription Buffer-Kapazität (0 bei Strategie
        /// <see cref="EventBackpressureStrategy.Unbounded"/>). Nur bei Erfolg
        /// (<see cref="Observable"/> != null) belegt.
        /// </summary>
        public int EventBufferCapacity { get; set; }

        /// <summary>
        /// Aufgelöste Überschuss-Strategie für den pro-Subscription Buffer.
        /// Nur bei Erfolg (<see cref="Observable"/> != null) belegt.
        /// </summary>
        public EventBackpressureStrategy EventBackpressureStrategy { get; set; }

        /// <summary>
        /// Whether the event is marked for <c>Last-Event-Id</c> resume
        /// (<c>[SleipnirEvent(Resumable = true)]</c>, Phase R). Only set on success.
        /// When <c>true</c>, the subscription manager does NOT create an ephemeral
        /// per-connection subscription; it delegates to the <c>SleipnirSubscriptionStore</c>
        /// (durable subscriptionId, server-side disconnect buffer, replay from
        /// <c>lastEventId</c>). Default <c>false</c> → today's ephemeral path.
        /// </summary>
        public bool Resumable { get; set; }

        /// <summary>
        /// Resolved replay-buffer capacity (Phase R) — how many events the server-side
        /// disconnect buffer retains per durable subscription (evict-oldest on overflow;
        /// <c>0</c> = unbounded). Only relevant when <see cref="Resumable"/>. From
        /// <c>SleipnirOptions.EventReplayBufferCapacity</c> (fallback 1000).
        /// </summary>
        public int ReplayBufferCapacity { get; set; }

        public static SleipnirSubscribeResult Ok(
            IObservable<object?> observable,
            int eventBufferCapacity,
            EventBackpressureStrategy eventBackpressureStrategy,
            bool resumable = false,
            int replayBufferCapacity = 0) => new()
            {
                Observable = observable,
                EventBufferCapacity = eventBufferCapacity,
                EventBackpressureStrategy = eventBackpressureStrategy,
                Resumable = resumable,
                ReplayBufferCapacity = replayBufferCapacity,
            };

        public static SleipnirSubscribeResult Fail(SleipnirResponse error) => new() { Error = error };
    }
}