using SleipnirCommon.Attribute;   // [SleipnirDocumentation]
using SleipnirCore.Attributes;   // [SleipnirController], [SleipnirMethod]
using Sleipnir.Guide.Api.Domain;

namespace Sleipnir.Guide.Api.Controllers;

// The Market controller is the guide's first RPC surface. A tiny in-memory price table
// stands in for a real exchange — enough to exercise the full wire, the DevUI, and the
// generated clients. Later chapters add batch (5), chain (6), and the live feed (8).
[SleipnirController("Market")]
public class MarketController
{
    // Seed prices for a handful of symbols. The live BTC feed in chapter 8 takes over
    // BTC; until then GetQuote returns this static snapshot.
    private static readonly Dictionary<string, decimal> SeedPrices = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BTC"] = 60_000m,
        ["ETH"] = 3_200m,
        ["SOL"] = 145m,
        ["DOGE"] = 0.12m,
    };

    // Returns the domain type directly (null = unknown symbol), the same shape the
    // canonical Story-01 controllers use. This keeps the discovery return type as `Quote`
    // so the generated clients in chapter 2+ get a typed `Quote`, not an opaque envelope.
    // See the "Return types vs error codes" callout in the chapter.
    [SleipnirMethod("GetQuote")]
    [SleipnirDocumentation("Get a snapshot price quote for a single market symbol. Returns null if the symbol is unknown.")]
    public Quote? GetQuote(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return null;

        if (!SeedPrices.TryGetValue(symbol, out var price))
            return null;

        // A deterministic pseudo "change" so the DevUI example is stable, not random.
        var change = Math.Round((price % 13m) - 6m, 2);
        return new Quote
        {
            Symbol = symbol.ToUpperInvariant(),
            Price = price,
            Change = change,
            Time = DateTime.UtcNow,
        };
    }

    // A bulk fetch: one method, one roundtrip, server-side loop. Chapter 5 contrasts this
    // with a SleipnirMultiRequest BATCH of N GetQuote calls — same one roundtrip, but
    // composing existing methods without touching the server. Unknown symbols are skipped
    // (a bulk endpoint chooses its own not-found semantics; a batch would surface null per call).
    [SleipnirMethod("GetQuotes")]
    [SleipnirDocumentation("Bulk-fetch quotes for many symbols in one call. Unknown symbols are skipped. For composing arbitrary methods in one roundtrip, prefer a SleipnirMultiRequest batch (chapter 5).")]
    public List<Quote> GetQuotes(string[] symbols)
    {
        var quotes = new List<Quote>();
        if (symbols is null || symbols.Length == 0)
            return quotes;

        foreach (var s in symbols)
        {
            var q = GetQuote(s);
            if (q is not null)
                quotes.Add(q);
        }
        return quotes;
    }
}