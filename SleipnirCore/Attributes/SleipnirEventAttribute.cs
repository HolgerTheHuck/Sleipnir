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
    /// <b>Kompositionsregel:</b> Events sind *nicht* chainbar (wie Streams). Ein Event-Stream
    /// hat keinen fertigen Response für <c>Exposes("$.id", …)</c>. Compile-Fehler im Codegen.
    /// </para>
    /// <para>
    /// <b>Transport:</b> WS-only für v1; SignalR folgt; REST: nein.
    /// <b>Auth:</b> zur Subscribe-Zeit (wie jeder Call, via Auth-Interceptor, Phase 1).
    /// <b>Gap-Semantik:</b> at-most-once-while-disconnected (v1); <c>Last-Event-Id</c>-Resume v1.x+.
    /// <b>Backpressure:</b> bounded Buffer + drop-oldest + <c>sleipnir.event.dropped</c>-Metrik.
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
    }
}