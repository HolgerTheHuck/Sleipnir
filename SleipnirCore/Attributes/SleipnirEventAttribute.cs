namespace SleipnirCore.Attributes
{
    /// <summary>
    /// Markiert eine Methode als server-pushed Event-Subscription (Phase 3, Events).
    /// Analog zu <see cref="SleipnirMethodAttribute"/>, aber die Methode gibt ein
    /// <c>IObservable&lt;T&gt;</c> zurück und wird über einen Subscribe/Unsubscribe-Dispatcher
    /// angesprochen (WS/SignalR-Only, kein REST). Die Parameter sind First-Class in der
    /// Subscription (z. B. <c>chatId</c>), der Server pusht jedes <c>T</c> als Event-Frame.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Phase 3 — siehe <c>docs/design/phase-3-events.md</c>. Experimental bis die Pipeline
    /// gelandet ist (siehe <c>STABILITY.md</c> §2). Die Subscribe-Methode:
    /// </para>
    /// <code>
    /// [SleipnirController("Chat")]
    /// public class ChatController
    /// {
    ///     [SleipnirEvent("MessageReceived")]
    ///     public IObservable&lt;Message&gt; Subscribe(int chatId, CancellationToken ct) { ... }
    /// }
    /// </code>
    /// <para>
    /// <b>Kompositionsregel:</b> Events sind *nicht* chainbar (Streams dagegen schon — sie
    /// werden zu fertigen JSON-Arrays materialisiert und laufen durch <c>ExecuteAuthorized</c>).
    /// Ein Event-Stream hat keinen fertigen Response für <c>Exposes("$.id", …)</c>. Compile-Fehler
    /// im Codegen.
    /// </para>
    /// <para>
    /// <b>Transport:</b> WS-only für v1; SignalR folgt; REST: nein.
    /// <b>Auth:</b> zur Subscribe-Zeit (wie jeder Call, via Auth-Interceptor, Phase 1).
    /// <b>Gap-Semantik:</b> at-most-once-while-disconnected (v1); <c>Last-Event-Id</c>-Resume v1.x+.
    /// <b>Backpressure:</b> pro-Subscription bounded Buffer mit wählbarer Strategie
    /// (<c>EventBackpressureStrategy</c>: DropOldest/DropWrite/Block/Unbounded), per-Event
    /// über <see cref="BufferCapacity"/>/<see cref="BackpressureStrategy"/> überschreibbar;
    /// <c>sleipnir.event.dropped</c> zählt verlorene Events.
    /// </para>
    /// <para>
    /// <b>Resume:</b> <see cref="Resumable"/> (default <c>false</c>) opts in to
    /// <c>Last-Event-Id</c> resume + a server-side disconnect buffer (Phase R). A
    /// <c>Resumable</c> method must return a <b>long-lived hot/durable observable</b>
    /// (e.g. <c>Subject&lt;T&gt;</c> or a factory backed by a long-running producer) —
    /// the server keeps <i>one</i> subscription alive across the disconnect and buffers
    /// events; a cold observable that restarts per subscribe has no resume semantics.
    /// Replay is <c>at-least-once</c> within the replay-buffer window; the client dedups
    /// by <c>eventId</c>. See <c>STABILITY.md</c> §2.
    /// </para>
    /// </remarks>
    [System.AttributeUsage(System.AttributeTargets.Method)]
    public class SleipnirEventAttribute : System.Attribute
    {
        private readonly string _name;

        public SleipnirEventAttribute(string name)
        {
            _name = name;
        }

        /// <summary>Der Event-Name auf dem Wire (analog zu <see cref="SleipnirMethodAttribute.Name"/>).</summary>
        public string Name => _name;

        /// <summary>
        /// Optional per-event override of the per-subscription buffer capacity.
        /// <c>-1</c> (the default) inherits <c>SleipnirOptions.EventBufferCapacity</c>
        /// (fallback 100). A non-negative value caps the per-subscription buffer at
        /// that many events; combined with <see cref="BackpressureStrategy"/> it
        /// governs what happens on overflow. <c>0</c> is only meaningful with
        /// <see cref="SleipnirCommon.Models.EventBackpressureStrategy.Unbounded"/>
        /// (otherwise the framework treats a non-Unbounded <c>0</c> as the 100
        /// fallback). Ignored when <see cref="BackpressureStrategy"/> is
        /// <see cref="SleipnirCommon.Models.EventBackpressureStrategy.Unbounded"/>.
        /// </summary>
        public int BufferCapacity { get; set; } = -1;

        /// <summary>
        /// Optional per-event override of the overflow strategy
        /// (<see cref="SleipnirCommon.Models.EventBackpressureStrategy"/>).
        /// <see cref="SleipnirCommon.Models.EventBackpressureStrategy.Inherit"/>
        /// (the default) uses the global
        /// <c>SleipnirOptions.EventBackpressureStrategy</c>. Set to
        /// <see cref="SleipnirCommon.Models.EventBackpressureStrategy.Unbounded"/>
        /// to disable the cap for this event (ignores <see cref="BufferCapacity"/>).
        /// </summary>
        public SleipnirCommon.Models.EventBackpressureStrategy BackpressureStrategy { get; set; }
            = SleipnirCommon.Models.EventBackpressureStrategy.Inherit;

        /// <summary>
        /// Opt-in for <c>Last-Event-Id</c> resume + a server-side disconnect buffer
        /// (Phase R, experimental — see <c>STABILITY.md</c> §2). Default <c>false</c>
        /// (non-breaking: today's at-most-once-while-disconnected behavior). When <c>true</c>,
        /// the server keeps <i>one</i> <c>IObservable</c> subscription alive across
        /// WebSocket disconnects, buffers events produced while no client is attached, and
        /// replays from the client-supplied <c>lastEventId</c> on reconnect (at-least-once
        /// within the replay-buffer window; the client dedups by <c>eventId</c>).
        /// <b>Contract:</b> the method must return a long-lived hot/durable observable
        /// (e.g. <c>Subject&lt;T&gt;</c>) — a cold observable that restarts per subscribe
        /// has no resume semantics. The <c>subscriptionId</c> is stable (durable) across
        /// reconnects for resumable events. Reclaim/GC: idle-TTL, explicit unsubscribe, or
        /// source <c>complete</c>/<c>error</c> (<c>SleipnirOptions.EventResumeTtl</c>).
        /// </summary>
        public bool Resumable { get; set; }
    }
}