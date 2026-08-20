using SleipnirCommon.Attribute;     // [SleipnirDocumentation]
using SleipnirCommon.Models;       // SleipnirResponse
using SleipnirCommon.Results;       // SleipnirResults
using SleipnirCore.Attributes;     // [SleipnirController], [SleipnirMethod], [SleipnirAuthorise]
using Sleipnir.Guide.Api.Domain;
using Sleipnir.Guide.Api.Services;

namespace Sleipnir.Guide.Api.Controllers;

// The authed surface (chapter 8). Class-level [SleipnirAuthorise] means EVERY method here
// requires a valid bearer (any role) — GetHoldings, PlaceOrder, GetOrder. The two feed
// controls are admin-only: a method-level [SleipnirAuthorise(Role = "Admin")] overrides the
// class attribute and additionally demands the Admin role (403 for a Customer token).
//
// GetHoldings/PlaceOrder read the caller's identity via IHttpContextAccessor — the same
// pattern as AccountController. PlaceOrder is the chain PROVIDER for GetOrder: it exposes
// $.Id as "orderId", and GetOrder(@orderId) consumes it (chapter 6 pattern, now authed).
[SleipnirController("Portfolio")]
[SleipnirAuthorise]
public class PortfolioController
{
    private static readonly List<Holding> SeedHoldings = new()
    {
        new() { Symbol = "BTC", Quantity = 0.75m, AveragePrice = 42_000m },
        new() { Symbol = "ETH", Quantity = 6m,    AveragePrice = 2_100m },
    };

    // Per-process order store keyed by id. Good enough for the tutorial (a restart resets it);
    // the chain demo only needs PlaceOrder's returned id to round-trip into GetOrder.
    private static readonly Dictionary<int, Order> Orders = new();
    private static int _nextId = 1;

    private readonly IHttpContextAccessor _http;
    private readonly FeedControlService _feed;

    public PortfolioController(IHttpContextAccessor http, FeedControlService feed)
    {
        _http = http;
        _feed = feed;
    }

    [SleipnirMethod("GetHoldings")]
    [SleipnirDocumentation("Return the caller's portfolio holdings. Requires authentication (any role).")]
    public List<Holding> GetHoldings()
    {
        // A real app would scope holdings to the caller (read from HttpContext.User). The
        // guide returns the same seeded book for every authed user — enough to prove the gate.
        return SeedHoldings;
    }

    // The chain provider (chapter 6, now authed): returns the created Order whose $.Id a
    // consumer resolves via @orderId. Executes at the current Market seed price.
    [SleipnirMethod("PlaceOrder")]
    [SleipnirDocumentation("Place a market order for a symbol + quantity. Returns the filled Order. Chain provider for GetOrder(@orderId): expose $.Id as 'orderId'.")]
    public Order PlaceOrder(string symbol, decimal quantity)
    {
        if (string.IsNullOrWhiteSpace(symbol) || quantity <= 0)
            return new Order();   // invalid input → an empty order (the gate already proved auth)

        var id = Interlocked.Increment(ref _nextId);
        var order = new Order
        {
            Id = id,
            Symbol = symbol.ToUpperInvariant(),
            Quantity = quantity,
            Price = 60_000m,   // chapter 9 will price from the live feed; the static seed price is fine here
            Time = DateTime.UtcNow,
        };
        Orders[id] = order;
        return order;
    }

    // The chain consumer: fetch an order by id. PlaceOrder exposes $.Id → GetOrder(@orderId).
    [SleipnirMethod("GetOrder")]
    [SleipnirDocumentation("Fetch a previously placed order by id. Chain consumer: PlaceOrder exposes $.Id as 'orderId', GetOrder(@orderId) resolves it.")]
    public SleipnirResponse GetOrder(int id)
    {
        if (Orders.TryGetValue(id, out var order))
            return SleipnirResults.Ok(order);
        return SleipnirResults.NotFound($"order {id} not found");
    }

    // Admin-only feed control. A Customer token is authenticated but lacks the Admin role →
    // the invoker throws ForbiddenAccessException → 403 (not 401). This is the 401-vs-403
    // distinction the chapter walks through.
    [SleipnirMethod("StartFeed")]
    [SleipnirAuthorise(Role = "Admin")]
    [SleipnirDocumentation("Start the live price feed (chapter 9). Admin role required — a Customer token gets 403.")]
    public bool StartFeed()
    {
        _feed.IsRunning = true;
        return _feed.IsRunning;
    }

    [SleipnirMethod("StopFeed")]
    [SleipnirAuthorise(Role = "Admin")]
    [SleipnirDocumentation("Stop the live price feed (chapter 9). Admin role required — a Customer token gets 403.")]
    public bool StopFeed()
    {
        _feed.IsRunning = false;
        return _feed.IsRunning;
    }
}