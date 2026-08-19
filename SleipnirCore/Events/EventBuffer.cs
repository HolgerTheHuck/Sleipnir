using System.Runtime.CompilerServices;

namespace SleipnirCore.Events;

/// <summary>
/// Per-subscription backpressure buffer with a selectable overflow strategy
/// (<see cref="EventBackpressureStrategy"/>). Single-writer (the event observer),
/// single-reader (the pump task). Unlike a <c>BoundedChannel(DropOldest)</c> (whose
/// <c>TryWrite</c> always returns <c>true</c> when full and so hides drops), this buffer counts
/// lost events correctly via the <c>onDropped</c> callback → <c>sleipnir.event.dropped</c>.
/// <para>
/// Transport-agnostic: the buffer holds serialized frame strings. The WebSocket pump drains it
/// into the socket send channel; the SSE pump drains it into the HTTP response body.
/// </para>
/// </summary>
internal sealed class EventBuffer
{
    private readonly int _capacity;                  // 0 = unbounded
    private readonly EventBackpressureStrategy _strategy;
    private readonly bool _unbounded;
    private readonly Queue<string> _queue = new();
    private readonly object _lock = new();
    private readonly SemaphoreSlim _items;           // released per enqueue; the reader awaits it
    private readonly SemaphoreSlim? _space;          // Block only: free slots; the writer awaits it
    private readonly CancellationToken _disposeToken;
    private bool _completed;

    public EventBuffer(int capacity, EventBackpressureStrategy strategy, CancellationToken disposeToken)
    {
        _disposeToken = disposeToken;
        _strategy = strategy;
        _unbounded = strategy == EventBackpressureStrategy.Unbounded || capacity <= 0;
        _capacity = _unbounded ? 0 : capacity;
        _items = new SemaphoreSlim(0);
        _space = (strategy == EventBackpressureStrategy.Block && !_unbounded) ? new SemaphoreSlim(_capacity) : null;
    }

    public long Count { get { lock (_lock) return _queue.Count; } }

    /// <summary>
    /// Tries to enqueue an event frame. DropOldest: when full, evicts the oldest element and
    /// invokes <paramref name="onDropped"/> (returns <c>true</c>). DropWrite: discards the newest
    /// and invokes <paramref name="onDropped"/> (returns <c>false</c>). Block: the caller waits
    /// synchronously for a free slot (producer backpressure; never invokes
    /// <paramref name="onDropped"/>). Unbounded: always <c>true</c>. <paramref name="onDropped"/>
    /// is invoked synchronously under the lock — the callback must not re-enter it.
    /// </summary>
    public bool TryEnqueue(string frame, Action onDropped)
    {
        if (_unbounded)
        {
            lock (_lock)
            {
                if (_completed) return false;
                _queue.Enqueue(frame);
            }
            _items.Release();
            return true;
        }

        if (_strategy == EventBackpressureStrategy.Block)
        {
            // Producer backpressure: wait synchronously for a free slot. Dispose wakes via the
            // cancellation token (OCE → we discard silently, no drop counter).
            try { _space!.Wait(_disposeToken); }
            catch (OperationCanceledException) { return false; }
            lock (_lock)
            {
                if (_completed)
                {
                    // Disposed while waiting — do not release the slot (the buffer is dead).
                    return false;
                }
                _queue.Enqueue(frame);
            }
            _items.Release();
            return true;
        }

        lock (_lock)
        {
            if (_completed) return false;
            if (_queue.Count >= _capacity)
            {
                if (_strategy == EventBackpressureStrategy.DropOldest)
                {
                    _queue.Dequeue();   // evict oldest
                    _queue.Enqueue(frame);
                    onDropped();        // synchronously, no lock re-entry
                    _items.Release();
                    return true;
                }
                // DropWrite: discard newest
                onDropped();
                return false;
            }
            _queue.Enqueue(frame);
        }
        _items.Release();
        return true;
    }

    /// <summary>
    /// Enqueues a terminal frame (complete/error) without a capacity check — it must reach the
    /// client regardless of backpressure. The buffer is then fully completed.
    /// </summary>
    public void EnqueueTerminal(string frame)
    {
        lock (_lock)
        {
            if (_completed) return;
            _queue.Enqueue(frame);
            _completed = true;
        }
        // Wake the reader (in case it is blocked on an empty buffer) and release the slot.
        _items.Release();
    }

    public async IAsyncEnumerable<string> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        while (true)
        {
            string? frame = null;
            bool mustWait = false;
            lock (_lock)
            {
                if (_queue.Count > 0)
                {
                    frame = _queue.Dequeue();
                    _space?.Release();
                }
                else if (_completed)
                {
                    yield break;               // drained + completed → stop without blocking
                }
                else
                {
                    mustWait = true;           // empty + live → wait for an item / completion wake
                }
            }
            if (frame != null)
            {
                yield return frame;
                continue;                      // re-check the queue before blocking again
            }
            if (mustWait)
            {
                await _items.WaitAsync(ct).ConfigureAwait(false);
                // loop: re-check under lock (item arrived, or a completion wake)
            }
        }
    }

    public void Complete()
    {
        bool wake;
        lock (_lock)
        {
            if (_completed) return;
            _completed = true;
            wake = _queue.Count == 0;   // only wake if the reader is blocked (empty queue)
        }
        if (wake) _items.Release();
    }
}