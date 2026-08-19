using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SleipnirCommon.Models;
using SleipnirCore.Tracing;

namespace SleipnirCore.Services;

/// <summary>
/// Process-wide store of <b>durable</b> event subscriptions for
/// <c>[SleipnirEvent(Resumable = true)]</c> events (Phase R — Last-Event-Id resume +
/// server-side disconnect buffer). Sibling of <see cref="SleipnirConnectionRegistry"/>,
/// but holds real per-subscription state (the live <c>IObservable</c> source subscription,
/// a stable monotonic <c>eventId</c> counter, and a bounded replay ring buffer), not just
/// counts.
/// </summary>
/// <remarks>
/// <para>
/// A durable subscription outlives a single WebSocket connection. The <c>IObservable</c>
/// source is subscribed <b>once</b> (on first subscribe) and kept alive across disconnects;
/// the <see cref="DurableEventObserver"/> (in SleipnirWebSocket, which owns JSON
/// serialization) appends every event to the ring buffer and, when a client <see cref="Tap"/>
/// is attached, also forwards it live. On disconnect the tap is detached (completed) but the
/// source + ring buffer persist — events produced while no client is attached accumulate in
/// the ring buffer (up to its cap, evict-oldest → <c>sleipnir.event.dropped</c>). On reconnect
/// the client sends <c>lastEventId</c>; <see cref="ResumeAsync"/> snapshots ring entries with
/// <c>eventId &gt; lastEventId</c> into a fresh tap and continues live — at-least-once within
/// the replay window (the client dedups by <c>eventId</c>).
/// </para>
/// <para>
/// Reclaim: a durable subscription is evicted after the idle <see cref="SleipnirOptions"/>,
/// <c>EventResumeTtl</c> with no attached tap, or on explicit unsubscribe, or when the source
/// completes/errors. A process-wide cap (<c>EventMaxDurableSubscriptions</c>) rejects
/// over-cap create attempts (DoS backstop). Registered once as a DI singleton in
/// <c>AddSleipnir</c>. See <c>docs/design/phase-3-events.md</c> + <c>STABILITY.md</c> §2.
/// </para>
/// <para>
/// <b>R1 scope note:</b> reconnect-time authorization re-check (design decision 3, v1.x+) is
/// wired in Phase R3 — R1's resume path re-attaches without re-auth, which is safe because
/// no client sends a resume request until Phase R2 ships the resume hook.
/// </para>
/// </remarks>
public sealed class SleipnirSubscriptionStore : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, DurableSubscriptionState> _durable = new();
    private readonly int _replayBufferCapacity;     // 0 = unbounded
    private readonly TimeSpan _resumeTtl;
    private readonly int _maxDurable;               // 0 = unbounded
    private readonly SleipnirConnectionRegistry _connectionRegistry;
    private readonly ILogger? _logger;
    private readonly Timer? _gcTimer;

    public SleipnirSubscriptionStore(
        SleipnirConnectionRegistry connectionRegistry,
        int? replayBufferCapacity,
        TimeSpan? resumeTtl,
        int? maxDurable,
        ILogger? logger)
    {
        _connectionRegistry = connectionRegistry;
        _replayBufferCapacity = replayBufferCapacity is { } rc && rc > 0 ? rc : 1000;
        _resumeTtl = resumeTtl ?? TimeSpan.FromSeconds(60);
        _maxDurable = maxDurable ?? 10_000;
        _logger = logger;
        // Lazy GC sweep — a dedicated timer keeps abandoned durable subscriptions from
        // pinning memory after a client vanishes without unsubscribing. No-op when the
        // TTL is zero (never auto-reclaim — caller accepts the memory risk).
        if (_resumeTtl > TimeSpan.Zero)
            _gcTimer = new Timer(_ => SweepGc(), null, _resumeTtl, _resumeTtl);
    }

    /// <summary>
    /// Begins a fresh durable subscription: registers a new <see cref="DurableSubscriptionState"/>
    /// (with a server-generated stable id) and returns it. The caller (SleipnirSubscriptionManager)
    /// subscribes its <see cref="DurableEventObserver"/> to the source, sets
    /// <see cref="DurableSubscriptionState.SourceSubscription"/>, then calls
    /// <see cref="DurableSubscriptionState.Attach"/> to obtain the live <see cref="Tap"/>.
    /// Returns <c>null</c> when the process-wide durable cap is reached (over-cap → caller
    /// returns a 503).
    /// </summary>
    public DurableSubscriptionState? BeginCreate(EventBackpressureStrategy strategy)
    {
        if (_maxDurable > 0 && _durable.Count >= _maxDurable)
        {
            _logger?.LogWarning("Durable subscription cap reached ({Cap}); subscribe rejected.", _maxDurable);
            return null;
        }
        var id = Guid.NewGuid().ToString("N");
        var state = new DurableSubscriptionState(id, _replayBufferCapacity, strategy, OnDropped);
        _durable[id] = state;
        return state;
    }

    /// <summary>Looks up an existing durable subscription by its stable id (for resume).</summary>
    public DurableSubscriptionState? Lookup(string subscriptionId)
        => _durable.TryGetValue(subscriptionId, out var state) ? state : null;

    /// <summary>
    /// Called by the per-connection manager when a client re-attaches (create or resume):
    /// bump the live-subscription gauge. Symmetric with <see cref="Detach"/>.
    /// </summary>
    public void OnAttached() => _connectionRegistry.IncSubscription();

    /// <summary>
    /// Detaches the current client tap from a durable subscription (on WebSocket disconnect):
    /// completes the live channel, drops the tap reference, and decrements the live-subscription
    /// gauge. The source subscription + ring buffer <b>persist</b> for resume; the idle TTL
    /// countdown (re)starts. No-op (returns false) when the id is unknown — e.g. already GC'd.
    /// </summary>
    public bool Detach(string subscriptionId)
    {
        if (!_durable.TryGetValue(subscriptionId, out var state)) return false;
        state.Detach();
        _connectionRegistry.DecSubscription();
        return true;
    }

    /// <summary>
    /// Explicitly tears down a durable subscription (client unsubscribe on a resumable event):
    /// disposes the source, discards the ring buffer, removes the state, decrements the gauge
    /// (only if a tap was still attached).
    /// </summary>
    public bool Destroy(string subscriptionId)
    {
        if (!_durable.TryRemove(subscriptionId, out var state)) return false;
        var wasAttached = state.HasTap;
        state.Dispose();
        if (wasAttached) _connectionRegistry.DecSubscription();
        return true;
    }

    private void OnDropped(string subscriptionId)
    {
        // Mirror the ephemeral EventObserver.OnDropped path: a single SleipnirMetrics.EventDropped
        // call bumps the registry accumulator (via SleipnirConnectionRegistry.Current) AND the OTel
        // counter. Do NOT also call _connectionRegistry.RecordEventDrop() here — in production
        // Current IS the same instance passed to this store, so a direct call would double-count.
        SleipnirMetrics.EventDropped(subscriptionId);
        _logger?.LogWarning("Replay-buffer overflow for durable subscription {SubscriptionId} (event evicted)", subscriptionId);
    }

    private void SweepGc()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var kv in _durable)
        {
            var state = kv.Value;
            // Evict completed sources (the observable finished — nothing left to resume) and
            // detached subscriptions past their idle TTL. Attached subscriptions are live.
            if (state.Completed || (!state.HasTap && now - state.LastActiveUtc > _resumeTtl))
            {
                if (_durable.TryRemove(kv.Key, out _))
                {
                    state.Dispose();
                    _logger?.LogDebug("GC durable subscription {SubscriptionId} (completed={Completed}, ttlExpired={Ttl})",
                        kv.Key, state.Completed, !state.Completed);
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_gcTimer != null) await _gcTimer.DisposeAsync();
        foreach (var state in _durable.Values)
            state.Dispose();
        _durable.Clear();
    }
}

/// <summary>
/// The per-durable-subscription state: the kept-alive source subscription, a stable monotonic
/// <c>eventId</c> counter, a bounded replay ring buffer, and the currently-attached client
/// <see cref="Tap"/> (null while disconnected). Thread-safe via a single lock (one producer
/// — the source observer — plus occasional attach/detach/snapshot).
/// </summary>
public sealed class DurableSubscriptionState : IDisposable
{
    public string SubscriptionId { get; }
    public IDisposable? SourceSubscription { get; set; }
    public EventBackpressureStrategy Strategy { get; }
    public DateTimeOffset LastActiveUtc { get; private set; } = DateTimeOffset.UtcNow;
    public bool Completed { get; private set; }
    public bool HasTap => _liveTap != null;

    private long _eventIdCounter;
    private readonly Queue<(long EventId, string Frame)> _ring = new();
    private readonly int _ringCap;             // 0 = unbounded
    private readonly Action<string> _onDrop;
    private readonly object _lock = new();
    private Channel<string>? _liveTap;
    private string? _terminalFrame;

    public DurableSubscriptionState(string subscriptionId, int ringCap, EventBackpressureStrategy strategy, Action<string> onDrop)
    {
        SubscriptionId = subscriptionId;
        _ringCap = ringCap;
        Strategy = strategy;
        _onDrop = onDrop;
    }

    /// <summary>Next monotonic eventId for this durable subscription (stable across reconnects).</summary>
    public long NextEventId() => Interlocked.Increment(ref _eventIdCounter);

    /// <summary>
    /// Appends a serialized event frame (the caller — the observer — already embedded
    /// <paramref name="eventId"/> via <see cref="NextEventId"/>). Records it in the replay
    /// ring buffer (evict-oldest on cap → drop counter) and forwards it to the attached live
    /// tap, if any. No-op once the source has completed.
    /// </summary>
    public void AppendEvent(long eventId, string frame)
    {
        Channel<string>? tap;
        lock (_lock)
        {
            if (Completed) return;
            _ring.Enqueue((eventId, frame));
            if (_ringCap > 0 && _ring.Count > _ringCap)
            {
                _ring.Dequeue();
                _onDrop(SubscriptionId);
            }
            tap = _liveTap;
        }
        tap?.Writer.TryWrite(frame);
    }

    /// <summary>
    /// Records a terminal frame (complete/error) — replayed to a resuming client and forwarded
    /// to the current live tap, then the subscription is marked completed (GC-eligible).
    /// </summary>
    public void SetTerminal(string frame)
    {
        Channel<string>? tap;
        lock (_lock)
        {
            if (Completed) return;
            _terminalFrame = frame;
            Completed = true;
            tap = _liveTap;
        }
        if (tap != null)
        {
            tap.Writer.TryWrite(frame);
            tap.Writer.TryComplete();
        }
    }

    /// <summary>
    /// Attaches a client tap and replays buffered events with <c>eventId &gt; lastEventId</c>
    /// (pass 0 on a fresh subscribe). Returns the tap (live reader + the first replayed
    /// eventId, or null when nothing was replayed). The lock serializes the snapshot with
    /// concurrent <see cref="AppendEvent"/> calls — events produced after the snapshot carry
    /// <c>eventId &gt; snapshotMax</c> and flow live, so there is no gap and no duplicate
    /// before client-side dedup.
    /// </summary>
    public Tap Attach(long lastEventId)
    {
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        long? replayedFrom = null;
        string? terminal;
        lock (_lock)
        {
            if (_ring.Count > 0)
            {
                foreach (var (eid, frame) in _ring)
                {
                    if (eid > lastEventId)
                    {
                        channel.Writer.TryWrite(frame);
                        replayedFrom ??= eid;
                    }
                }
            }
            _liveTap = channel;
            LastActiveUtc = DateTimeOffset.UtcNow;
            terminal = _terminalFrame;
        }
        if (terminal != null)
        {
            channel.Writer.TryWrite(terminal);
            channel.Writer.TryComplete();
        }
        return new Tap(SubscriptionId, channel.Reader, replayedFrom);
    }

    /// <summary>Detaches the client tap (on disconnect): completes the live channel, keeps source + ring.</summary>
    public void Detach()
    {
        Channel<string>? tap;
        lock (_lock)
        {
            tap = _liveTap;
            _liveTap = null;
            LastActiveUtc = DateTimeOffset.UtcNow;
        }
        tap?.Writer.TryComplete();
    }

    public void Dispose()
    {
        SourceSubscription?.Dispose();
        Channel<string>? tap;
        lock (_lock)
        {
            tap = _liveTap;
            _liveTap = null;
            _ring.Clear();
        }
        tap?.Writer.TryComplete();
    }
}

/// <summary>
/// A per-connection live consumer of a durable subscription. The reader yields replayed
/// frames (events with <c>eventId &gt; lastEventId</c>, in order) followed by live frames,
/// then completes when the source completes/errors or the client detaches.
/// <see cref="ReplayedFrom"/> is the first replayed eventId (null on a fresh subscribe or
/// when nothing was buffered) — surfaced as <c>replayedFrom</c> in the subscribe response.
/// </summary>
public sealed class Tap
{
    public string SubscriptionId { get; }
    public ChannelReader<string> Reader { get; }
    public long? ReplayedFrom { get; }

    public Tap(string subscriptionId, ChannelReader<string> reader, long? replayedFrom)
    {
        SubscriptionId = subscriptionId;
        Reader = reader;
        ReplayedFrom = replayedFrom;
    }
}