using System.Collections.Concurrent;

namespace SleipnirCore.Events;

/// <summary>
/// One <see cref="SleipnirReplayStream{T}"/> per entity key (e.g. a run id), with automatic
/// eviction of finished streams. Register it as a singleton; the producer publishes into
/// <see cref="GetOrCreate"/>, the <c>[SleipnirEvent]</c> method returns <see cref="Observe"/>:
/// <code>
/// [SleipnirEvent("Progress", BufferCapacity = RunProgress.MaxHistory + 100)]
/// public IObservable&lt;ProgressEvent&gt; Progress(string runId, long afterSeq = 0)
///     =&gt; _progress.Observe(runId, afterSeq);
/// </code>
/// </summary>
/// <remarks>
/// A completed or failed stream stays available for <see cref="CompletedRetention"/> — long enough
/// for a client that opens the page right after the job ended to still get the full history plus
/// the completion — and is then removed. Eviction runs lazily on every <see cref="GetOrCreate"/>,
/// <see cref="TryGet"/> and <see cref="Observe"/> call, so no timer is involved. Streams that never
/// complete are never evicted; call <see cref="Remove"/> for those.
/// </remarks>
/// <typeparam name="TKey">The entity key type.</typeparam>
/// <typeparam name="T">The event payload type.</typeparam>
public sealed class SleipnirReplayStreams<TKey, T> where TKey : notnull
{
    /// <summary>Default <see cref="CompletedRetention"/>: 10 minutes.</summary>
    public static readonly TimeSpan DefaultCompletedRetention = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<TKey, SleipnirReplayStream<T>> _streams;
    private readonly TimeProvider _time;

    /// <param name="maxHistory">Retained events per stream (<see cref="SleipnirReplayStream{T}.MaxHistory"/>).</param>
    /// <param name="completedRetention">How long a finished stream stays available; default 10 minutes.</param>
    /// <param name="timeProvider">Clock for the retention; default <see cref="TimeProvider.System"/>.</param>
    /// <param name="comparer">Key comparer; default the type's equality.</param>
    public SleipnirReplayStreams(
        int maxHistory = SleipnirReplayStream<T>.DefaultMaxHistory,
        TimeSpan? completedRetention = null,
        TimeProvider? timeProvider = null,
        IEqualityComparer<TKey>? comparer = null)
    {
        if (maxHistory <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxHistory), maxHistory, "maxHistory must be greater than 0.");
        var retention = completedRetention ?? DefaultCompletedRetention;
        if (retention < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(completedRetention), retention, "completedRetention must not be negative.");
        MaxHistory = maxHistory;
        CompletedRetention = retention;
        _time = timeProvider ?? TimeProvider.System;
        _streams = new ConcurrentDictionary<TKey, SleipnirReplayStream<T>>(comparer ?? EqualityComparer<TKey>.Default);
    }

    /// <summary>Retained events per stream.</summary>
    public int MaxHistory { get; }

    /// <summary>How long a completed or failed stream stays available before it is removed.</summary>
    public TimeSpan CompletedRetention { get; }

    /// <summary>Number of streams currently held (including finished ones not yet evicted).</summary>
    public int Count => _streams.Count;

    /// <summary>
    /// The stream for <paramref name="key"/>, created on first use. A finished stream whose
    /// retention has expired is replaced by a fresh one (e.g. a re-run under the same key).
    /// </summary>
    public SleipnirReplayStream<T> GetOrCreate(TKey key)
    {
        Sweep();
        return _streams.GetOrAdd(key, _ => new SleipnirReplayStream<T>(MaxHistory, _time));
    }

    /// <summary>The stream for <paramref name="key"/>, if it exists and has not been evicted.</summary>
    public bool TryGet(TKey key, out SleipnirReplayStream<T> stream)
    {
        Sweep();
        return _streams.TryGetValue(key, out stream!);
    }

    /// <summary>
    /// What a <c>[SleipnirEvent]</c> method returns: the stream for <paramref name="key"/> from the
    /// client's position (<see cref="SleipnirReplayStream{T}.From(long)"/>). An unknown or evicted
    /// key yields an empty stream that completes immediately — the client gets <c>onComplete</c>
    /// with no events. Does not create a stream, so a subscriber cannot allocate one for an
    /// arbitrary key.
    /// </summary>
    public IObservable<T> Observe(TKey key, long afterSeq = 0)
        => TryGet(key, out var stream) ? stream.From(afterSeq) : EmptyCompleted.Instance;

    /// <summary>Removes the stream for <paramref name="key"/>; current subscribers keep their subscription until it ends.</summary>
    public bool Remove(TKey key) => _streams.TryRemove(key, out _);

    private void Sweep()
    {
        var now = _time.GetUtcNow();
        foreach (var entry in _streams)
        {
            if (entry.Value.TerminatedAt is { } at && now - at >= CompletedRetention)
                _streams.TryRemove(entry);
        }
    }

    private sealed class EmptyCompleted : IObservable<T>
    {
        public static readonly EmptyCompleted Instance = new();

        public IDisposable Subscribe(IObserver<T> observer)
        {
            observer.OnCompleted();
            return Noop.Instance;
        }

        private sealed class Noop : IDisposable
        {
            public static readonly Noop Instance = new();
            public void Dispose() { }
        }
    }
}
