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

        public static SleipnirSubscribeResult Ok(IObservable<object?> observable) => new() { Observable = observable };
        public static SleipnirSubscribeResult Fail(SleipnirResponse error) => new() { Error = error };
    }
}