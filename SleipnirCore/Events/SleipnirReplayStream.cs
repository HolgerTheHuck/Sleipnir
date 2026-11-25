namespace SleipnirCore.Events;

/// <summary>
/// A hot, per-entity event stream with history — the building block for "history + live"
/// subscriptions such as a job's progress (<c>Runs.Progress(runId)</c>): a client that subscribes
/// late, or re-subscribes after a reconnect, first receives what already happened, then the live
/// events, and finally the completion — without gaps and without duplicates.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sequence numbers.</b> Every published event gets a per-stream sequence number
/// (1, 2, 3, … — gap-free, never reused). Unlike the framework's per-subscription
/// <c>eventId</c>, it is stable across subscriptions, so a client can resume with
/// <see cref="From(long)"/> (<c>afterSeq</c> = the last sequence number it processed) and
/// drop anything at or below it. Put it into the payload with
/// <see cref="Publish(Func{long, T})"/>: <c>stream.Publish(seq =&gt; new Progress(seq, …))</c>.
/// </para>
/// <para>
/// <b>Ordering guarantee.</b> Publishing, subscribing and delivery share one lock: a subscriber
/// receives the retained history and is then attached atomically, so no event is missed or
/// delivered twice at the handover, and every subscriber sees events in sequence order.
/// Observers are called on the publishing thread while the lock is held (the same model as an
/// Rx <c>Subject</c>); Sleipnir's own subscription observers only enqueue into the send buffer,
/// so this is cheap. Do not combine this type with <c>EventBackpressureStrategy.Block</c> — a
/// slow client would then hold the lock and stall the producer.
/// </para>
/// <para>
/// <b>Send-buffer sizing.</b> The history is replayed synchronously into the subscription's send
/// buffer (<c>SleipnirOptions.EventBufferCapacity</c>, default 100, <c>DropOldest</c>). If the
/// history can be longer than the buffer, the oldest replayed events are dropped. Size the
/// event's buffer to the history: <c>[SleipnirEvent("Progress", BufferCapacity = MaxHistory + 100)]</c>
/// with a <c>const</c> shared with the <see cref="SleipnirReplayStream{T}(int)"/> constructor.
/// </para>
/// <para>
/// <b>Termination.</b> After <see cref="Complete"/> or <see cref="Fail"/> the history is kept:
/// a later subscriber still receives it, followed by the terminal signal. Publishing after
/// termination throws. Use <see cref="SleipnirReplayStreams{TKey, T}"/> to keep one stream per
/// entity and evict finished ones after a retention period.
/// </para>
/// <para>
/// Use it with a non-resumable <c>[SleipnirEvent]</c>: the sequence number plus
/// <see cref="From(long)"/> already cover reconnects on every transport. Experimental, like the
/// rest of the event surface (<c>STABILITY.md</c> §2). Recipe: <c>EVENTS_REFERENCE.md</c>
/// §"History + live".
/// </para>
/// </remarks>
/// <typeparam name="T">The event payload type.</typeparam>
public sealed class SleipnirReplayStream<T> : IObservable<T>
{
    /// <summary>Default number of retained events (<see cref="MaxHistory"/>).</summary>
    public const int DefaultMaxHistory = 1000;

    private readonly object _lock = new();
    private readonly Queue<(long Seq, T Value)> _history = new();
    private readonly List<IObserver<T>> _observers = new();
    private readonly TimeProvider _time;
    private long _lastSeq;
    private bool _completed;
    private Exception? _error;
    private DateTimeOffset? _terminatedAt;

    /// <summary>Creates a stream that retains up to <paramref name="maxHistory"/> events.</summary>
    /// <param name="maxHistory">Retained events; the oldest are evicted beyond this (must be &gt; 0).</param>
    public SleipnirReplayStream(int maxHistory = DefaultMaxHistory)
        : this(maxHistory, TimeProvider.System)
    {
    }

    internal SleipnirReplayStream(int maxHistory, TimeProvider time)
    {
        if (maxHistory <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxHistory), maxHistory, "maxHistory must be greater than 0.");
        MaxHistory = maxHistory;
        _time = time;
    }

    /// <summary>Maximum number of retained events.</summary>
    public int MaxHistory { get; }

    /// <summary>Sequence number of the last published event; <c>0</c> before the first one.</summary>
    public long LastSeq { get { lock (_lock) return _lastSeq; } }

    /// <summary>
    /// Sequence number of the oldest retained event; <c>0</c> while the history is empty. A client
    /// whose <c>afterSeq</c> is below <c>FirstRetainedSeq - 1</c> has missed evicted events.
    /// </summary>
    public long FirstRetainedSeq { get { lock (_lock) return _history.Count == 0 ? 0 : _history.Peek().Seq; } }

    /// <summary><c>true</c> once <see cref="Complete"/> or <see cref="Fail"/> was called.</summary>
    public bool IsTerminated { get { lock (_lock) return _terminatedAt.HasValue; } }

    internal DateTimeOffset? TerminatedAt { get { lock (_lock) return _terminatedAt; } }

    /// <summary>Publishes an event to the history and to all subscribers.</summary>
    /// <returns>The event's sequence number.</returns>
    /// <exception cref="InvalidOperationException">The stream is already terminated.</exception>
    public long Publish(T value) => Publish(_ => value);

    /// <summary>
    /// Publishes an event built from its sequence number — the way to carry the number in the
    /// payload so clients can resume and de-duplicate: <c>Publish(seq =&gt; new Progress(seq, step))</c>.
    /// The factory runs under the stream's lock; keep it cheap and side-effect free.
    /// </summary>
    /// <returns>The event's sequence number.</returns>
    /// <exception cref="InvalidOperationException">The stream is already terminated.</exception>
    public long Publish(Func<long, T> create)
    {
        if (create is null) throw new ArgumentNullException(nameof(create));
        lock (_lock)
        {
            if (_terminatedAt.HasValue)
                throw new InvalidOperationException("Cannot publish to a completed or failed SleipnirReplayStream.");
            var seq = _lastSeq + 1;
            var value = create(seq);
            _lastSeq = seq;
            _history.Enqueue((seq, value));
            if (_history.Count > MaxHistory) _history.Dequeue();
            foreach (var observer in _observers.ToArray())
                Deliver(observer, o => o.OnNext(value));
            return seq;
        }
    }

    /// <summary>Completes the stream: subscribers get <c>OnCompleted</c>; the history stays for late subscribers. Idempotent.</summary>
    public void Complete() => Terminate(null);

    /// <summary>Fails the stream: subscribers get <c>OnError</c>; the history stays for late subscribers. Ignored once terminated.</summary>
    public void Fail(Exception error) => Terminate(error ?? throw new ArgumentNullException(nameof(error)));

    /// <summary>
    /// The stream from a client's position: retained events with a sequence number greater than
    /// <paramref name="afterSeq"/>, then the live events, then the terminal signal. <c>0</c> (the
    /// default) = everything retained. Return it from the <c>[SleipnirEvent]</c> method with the
    /// client's <c>afterSeq</c> parameter.
    /// </summary>
    public IObservable<T> From(long afterSeq) => new FromObservable(this, afterSeq);

    /// <summary>Subscribes from the beginning of the retained history (<see cref="From(long)"/> with <c>0</c>).</summary>
    public IDisposable Subscribe(IObserver<T> observer) => Attach(observer, 0);

    private IDisposable Attach(IObserver<T> observer, long afterSeq)
    {
        if (observer is null) throw new ArgumentNullException(nameof(observer));
        lock (_lock)
        {
            foreach (var (seq, value) in _history)
            {
                if (seq <= afterSeq) continue;
                if (!Deliver(observer, o => o.OnNext(value)))
                    return NoopDisposable.Instance;
            }

            if (_terminatedAt.HasValue)
            {
                var error = _error;
                Deliver(observer, o => { if (error is null) o.OnCompleted(); else o.OnError(error); });
                return NoopDisposable.Instance;
            }

            _observers.Add(observer);
            return new Subscription(this, observer);
        }
    }

    private void Terminate(Exception? error)
    {
        lock (_lock)
        {
            if (_terminatedAt.HasValue) return;
            _terminatedAt = _time.GetUtcNow();
            _completed = error is null;
            _error = error;
            var observers = _observers.ToArray();
            _observers.Clear();
            foreach (var observer in observers)
                Deliver(observer, o => { if (_completed) o.OnCompleted(); else o.OnError(error!); });
        }
    }

    /// <summary>
    /// Calls an observer; a throwing observer is detached so it can neither break the producer
    /// nor the other subscribers. Returns <c>false</c> when it threw.
    /// </summary>
    private bool Deliver(IObserver<T> observer, Action<IObserver<T>> call)
    {
        try
        {
            call(observer);
            return true;
        }
        catch
        {
            _observers.Remove(observer);
            return false;
        }
    }

    private void Detach(IObserver<T> observer)
    {
        lock (_lock) _observers.Remove(observer);
    }

    private sealed class FromObservable : IObservable<T>
    {
        private readonly SleipnirReplayStream<T> _stream;
        private readonly long _afterSeq;

        public FromObservable(SleipnirReplayStream<T> stream, long afterSeq)
        {
            _stream = stream;
            _afterSeq = afterSeq;
        }

        public IDisposable Subscribe(IObserver<T> observer) => _stream.Attach(observer, _afterSeq);
    }

    private sealed class Subscription : IDisposable
    {
        private SleipnirReplayStream<T>? _stream;
        private readonly IObserver<T> _observer;

        public Subscription(SleipnirReplayStream<T> stream, IObserver<T> observer)
        {
            _stream = stream;
            _observer = observer;
        }

        public void Dispose() => Interlocked.Exchange(ref _stream, null)?.Detach(_observer);
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }
}
