namespace Sleipnir.Guide.Api.Domain;

// A filled order. PlaceOrder creates one (returning its id); GetOrder fetches one by id — the
// chapter 8 chain target (PlaceOrder exposes $.Id as "orderId", GetOrder(@orderId) consumes it,
// one roundtrip). Time defaults to 0001-01-01 for a stable discovery example (a DateTime.UtcNow
// default would drift the contract every build — same rationale as Quote.Time).
public class Order
{
    public int Id { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
    public DateTime Time { get; set; }
}