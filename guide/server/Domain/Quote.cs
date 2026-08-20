namespace Sleipnir.Guide.Api.Domain;

// A snapshot price quote for a single market symbol. This is the first contract type
// the guide introduces — discovery expands it into a full JSON schema for the DevUI
// and the generated clients (chapter 2+), because it lives in the server's own assembly.
public class Quote
{
    public string Symbol { get; set; } = string.Empty;
    public decimal Price { get; set; }
    // Absolute change vs. the previous tick (positive = up). Kept as a decimal so the
    // wire JSON is exact, not a float.
    public decimal Change { get; set; }
    // Default is deterministic (0001-01-01) so the discovery *example* is stable build-to-build
    // (a DateTime.UtcNow default would drift the contract every build). The actual GetQuote
    // call sets Time to the real "now".
    public DateTime Time { get; set; }
}