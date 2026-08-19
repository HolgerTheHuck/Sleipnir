namespace SleipnirClient.Sleipnir;

/// <summary>
/// Reconnect decision for a single event subscription (Phase R, resume). Consulted by
/// <see cref="SleipnirWebSocketClient"/> on auto-reconnect, per subscription, before
/// re-subscribing.
/// </summary>
/// <remarks>
/// <b>Fresh</b> (the default) re-subscribes with a fresh subscription — today's behavior,
/// a new <c>subscriptionId</c>, the <c>eventId</c> counter restarts at 1, and events
/// produced during the disconnect are lost. <b>Resume</b> sends the durable
/// <c>subscriptionId</c> + <c>lastEventId</c> so the server replays the gap from its
/// disconnect buffer (at-least-once within the replay-buffer window; the client dedups
/// by <c>eventId</c>). A Resume on a non-resumable event degrades to Fresh (the server
/// does not know the id → fresh subscribe). <b>Drop</b> ends the subscription without
/// re-subscribing — the consumer's <c>IObservable&lt;T&gt;</c> completes.
/// </remarks>
public enum ResumeDecision
{
    /// <summary>Re-subscribe with a fresh subscription (new subscriptionId; events in the gap are lost).</summary>
    Fresh,

    /// <summary>Resume the durable subscription: send lastEventId + subscriptionId for server-side gap replay.</summary>
    Resume,

    /// <summary>Do not re-subscribe; the subscription ends (the consumer's IObservable completes).</summary>
    Drop,
}

/// <summary>
/// Context passed to a <see cref="ResumePolicy"/> on reconnect: the controller/method,
/// the (durable) <c>subscriptionId</c> of the dropped subscription, and the last
/// <c>eventId</c> the client processed (<c>null</c> when no event was received yet).
/// </summary>
public readonly record struct SubscriptionResumeContext(
    string Controller,
    string Method,
    string SubscriptionId,
    long? LastEventId);

/// <summary>
/// Per-subscription reconnect policy. Returns a <see cref="ResumeDecision"/> for the given
/// context, or <c>null</c> to abstain (the next policy in the fallback chain is consulted;
/// a fully-null chain means <see cref="ResumeDecision.Fresh"/>). Wired via the
/// <c>SleipnirWebSocketClient</c> constructor (<c>resumePolicy</c>) and overridable per
/// <c>SubscribeAsync</c> call.
/// </summary>
public delegate ResumeDecision? ResumePolicy(SubscriptionResumeContext context);