using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Sleipnir.Guide.Api.Domain;

namespace Sleipnir.Guide.Api.Services;

// The live price feed (chapter 9). An IHostedService that owns one HotObservable<PriceTick> per
// symbol and a Timer that random-walks each seeded symbol's price once per second, pushing a
// PriceTick to that symbol's stream. The [SleipnirEvent("Ticks", Resumable = true)] method on
// PriceFeedController yields the per-symbol singleton stream; the framework subscribes to it
// (once per durable subscription) and pushes each tick as a frame over WebSocket / SSE / SignalR.
//
// The admin-gated toggle lives on FeedControlService.IsRunning (flipped by Portfolio.StartFeed
// / StopFeed, admin-only). When it is false the timer still fires but skips Push — no ticks are
// produced, in-flight subscriptions simply go quiet. The framework keeps the durable source
// alive across the pause; only this producer logic honours the flag.
//
// IMPORTANT: the event-frame id used for Last-Event-Id resume is assigned by the framework's
// subscription store (a monotonic Interlocked.Increment per subscription), NOT by PriceTick —
// PriceTick carries no id field by design. Resume replays eventId > lastEventId from the ring.
public sealed class PriceFeedService : IHostedService, IDisposable
{
    // The same seeds Market uses for its static snapshot, so the live feed and the snapshot quote
    // share a starting point. The feed diverges as soon as the random walk runs.
    private static readonly Dictionary<string, decimal> SeedPrices = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BTC"] = 60_000m,
        ["ETH"] = 3_200m,
        ["SOL"] = 145m,
        ["DOGE"] = 0.12m,
    };

    private readonly FeedControlService _control;
    private readonly ConcurrentDictionary<string, decimal> _prices = new(SeedPrices, StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, HotObservable<PriceTick>> _streams = new(StringComparer.OrdinalIgnoreCase);
    // Random is not thread-safe; the timer is the only writer, so a single instance is fine.
    private readonly Random _rng = new();
    private Timer? _timer;

    public PriceFeedService(FeedControlService control) => _control = control;

    // The controller calls this for its `symbol` param. Lazily creates a stream for any symbol;
    // only the seeded symbols have a running random walk, so an unknown symbol's stream is simply
    // quiet (subscribing to it is legal but produces no ticks). Returning the SAME singleton per
    // symbol across calls is what makes Resumable = true work — a reconnect re-attaches to the
    // existing source + ring buffer rather than a fresh cold observable.
    public HotObservable<PriceTick> GetStream(string symbol)
    {
        var key = symbol.ToUpperInvariant();
        return _streams.GetOrAdd(key, _ => new HotObservable<PriceTick>());
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // 1s interval, no due-time delay — the first tick lands ~1s after startup. A 1s cadence
        // is lively enough for a live chart without flooding the wire.
        _timer = new Timer(OnTick, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        // Complete every stream so attached observers get a terminal frame (GC-eligible). This
        // runs on application shutdown — in-flight subscriptions are torn down with the host.
        foreach (var stream in _streams.Values) stream.Complete();
    }

    private void OnTick(object? state)
    {
        // The toggle: when the admin stops the feed the timer keeps firing but produces nothing.
        // No nulls, no sentinel ticks — the stream simply goes quiet until the admin resumes.
        if (!_control.IsRunning) return;

        foreach (var (symbol, stream) in _streams.ToArray())
        {
            // Only the seeded symbols have a random walk. A lazily-created stream for some other
            // symbol sits in the dictionary but is never walked (and is quiet by construction).
            if (!SeedPrices.ContainsKey(symbol)) continue;

            var prev = _prices[symbol];
            // ±0.2% per tick — a gentle random walk that stays plausible over a demo session.
            var delta = (decimal)(_rng.NextDouble() * 0.004 - 0.002);
            var next = Math.Round(prev * (1m + delta), 2);
            _prices[symbol] = next;

            stream.Push(new PriceTick
            {
                Symbol = symbol,
                Price = next,
                Change = Math.Round(next - prev, 2),
                Time = DateTime.UtcNow,
            });
        }
    }
}