namespace Sleipnir.Guide.Api.Domain;

// A position in the customer's portfolio. GetHoldings returns the seeded list; it is the
// [SleipnirAuthorise]-gated endpoint that distinguishes a logged-in customer from anonymous.
public class Holding
{
    public string Symbol { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    // Average fill price. Decimal on the wire for exactness.
    public decimal AveragePrice { get; set; }
}