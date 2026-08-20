using SleipnirCommon.Attribute;     // [SleipnirDocumentation]
using SleipnirCore.Attributes;     // [SleipnirController], [SleipnirEvent]
using Sleipnir.Guide.Api.Domain;
using Sleipnir.Guide.Api.Services;

namespace Sleipnir.Guide.Api.Controllers;

// The live price feed (chapter 9). This is the guide's first [SleipnirEvent] controller — the
// repo's first runnable event sample (stories/05 has the doc, no project). PriceFeedService
// (an IHostedService) owns a HotObservable<PriceTick> per symbol and a timer that random-walks
// each seeded price; this controller simply yields the per-symbol stream. The framework does
// the rest: it subscribes to the returned IObservable<PriceTick> (once per durable subscription),
// assigns the monotonic eventId used for Last-Event-Id resume, and pushes each tick as a frame
// over the active event backend — WebSocket, SSE, or SignalR depending on the client's transport.
//
// Resumable = true opts in to the durable path: the subscription outlives the client connection
// (up to EventResumeTtl, default 60s) and replays the missed eventId tail on reconnect. That only
// works because GetStream returns the SAME long-lived singleton per symbol — a cold observable
// that restarts per subscribe has no resume semantics.
//
// No [SleipnirAuthorise] here by design: the customer tier can SUBSCRIBE (read the feed) but the
// admin-only StartFeed / StopFeed on Portfolio control whether anything is produced. A stopped
// feed simply goes quiet; subscriptions stay open and resume pushing when the admin restarts it.
[SleipnirController("PriceFeed")]
public class PriceFeedController
{
    private readonly PriceFeedService _feed;

    public PriceFeedController(PriceFeedService feed) => _feed = feed;

    // Server-push: returns IObservable<PriceTick> (NOT Task<IObservable<T>> — the invoker enforces
    // the return type at registration). The `symbol` param selects which per-symbol stream to
    // subscribe to; only the seeded symbols (BTC, ETH, SOL, DOGE) have a running random walk, so
    // subscribe to "BTC" for the live feed the portal charts. Each tick becomes one pushed frame;
    // the framework assigns the eventId, the payload is just { symbol, price, change, time }.
    [SleipnirEvent("Ticks", Resumable = true)]
    [SleipnirDocumentation("Live price feed. Subscribe to a symbol (e.g. BTC) to receive a PriceTick ~once per second while the feed is running. Resumable: reconnect within 60s and the server replays the missed ticks by eventId. The feed is anonymous (subscribe as anyone); the admin starts/stops it via Portfolio.StartFeed/StopFeed.")]
    public IObservable<PriceTick> Ticks(string symbol) => _feed.GetStream(symbol);
}