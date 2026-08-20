namespace Sleipnir.Guide.Api.Services;

// A tiny shared toggle the admin-gated Portfolio.StartFeed / StopFeed methods flip, and that
// chapter 9's PriceFeedService (IHostedService) reads to decide whether to push ticks. It is
// a singleton registered in DI; the bool is volatile so both controllers see updates. Keeping
// the control surface on Portfolio (admin-only) rather than on PriceFeed means the feed has a
// single blessed "operator" surface — the customer tier can subscribe but cannot start/stop.
public class FeedControlService
{
    // Default on so chapter 9's live feed works out of the box; the admin can stop it.
    public volatile bool IsRunning = true;
}