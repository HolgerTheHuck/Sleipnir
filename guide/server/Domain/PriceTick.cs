namespace Sleipnir.Guide.Api.Domain;

// A single tick on the live BTC price feed (chapter 9). PriceFeedService pushes these on a
// timer; the [SleipnirEvent("Ticks", Resumable = true)] method yields IObservable<PriceTick>,
// and each tick becomes one pushed frame on WebSocket / SSE / SignalR. The event-frame id used
// for Last-Event-Id resume is assigned by the framework's subscription store, NOT by this
// payload — PriceTick is just the data. Kept parallel to Quote so a tick and a snapshot quote
// share a shape (Symbol / Price / Change / Time).
public class PriceTick
{
    public string Symbol { get; set; } = string.Empty;
    public decimal Price { get; set; }
    // Absolute change vs. the previous tick (positive = up). The random-walk feed updates it
    // each tick so the portal's chart can colour the move.
    public decimal Change { get; set; }
    public DateTime Time { get; set; }
}