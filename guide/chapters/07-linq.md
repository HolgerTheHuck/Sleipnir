# Chapter 7 — The LINQ provider: a typed layer over `@alias`

> **Goal:** take the chapter-6 chain (`Search → GetQuotes`, `PlaceOrder → GetOrder`) and express it
> as a **typed** query. `Sleipnir.Client.Linq` replaces the hand-typed JsonPath string and the
> untyped `@alias` placeholder with `Dep<T>` / `Arg<T>` and a selector expression: the chain reads
> like LINQ, the expose/alias bookkeeping disappears, and a placeholder wired into the wrong
> parameter type is a **compile error**, not a runtime 400.

Chapter 6 wired the chain by hand:

```csharp
// chapter 6 — untyped: the JsonPath is a string, the alias fits ANY parameter
var search = SleipnirCall.Init("Market", "Search")
    .With(("query", "o"))
    .Exposes("$[*]", "symbols")
    .ToRequest();
var fetch = SleipnirCall.Init("Market", "GetQuotes")
    .WithAlias("@symbols", "symbols", default(List<string>))
    .ToRequest();
```

The `"$[*]"` is a string — mistype it as `"$.[*]"` and you get a runtime `Unresolved`. The
`Alias` placeholder converts into *any* `Arg<T>`, so wiring `@symbols` into a `GetQuote(symbol:
string)` parameter compiles fine and fails at runtime. The LINQ provider closes both gaps.

## The contract: generated service interfaces

The LINQ layer builds calls against **generated interface types**, not strings. The companion
`sleipnir-linq` dotnet tool reads the server's `contract.sleipnir.json` (the same single source of
truth the C# generator in chapter 2 uses) and emits `Linq/SleipnirContracts.g.cs`:

```bash
# from guide/admin — regenerates Linq/SleipnirContracts.g.cs from the linked contract
dotnet run --project ../../Sleipnir.Client.Linq.Codegen -- \
  --discovery ../server/contract.sleipnir.json --out Linq/SleipnirContracts.g.cs \
  --namespace Sleipnir.Guide.Admin.Linq
```

The output is a set of service interfaces + DTOs — the interfaces **are** the contract:

```csharp
namespace Sleipnir.Guide.Admin.Linq
{
    public interface IMarketService
    {
        Task<List<string>?> Search(Arg<string> query);
        Task<List<Quote>?> GetQuotes(Arg<List<string>> symbols);
        Task<Quote?> GetQuote(Arg<string> symbol);
    }
    public interface IPortfolioService
    {
        Task<List<Holding>?> GetHoldings();
        Task<Order?> PlaceOrder(Arg<string> symbol, Arg<decimal> quantity);
        Task<object?> GetOrder(Arg<int> id);   // real return is SleipnirResponse → opaque
        Task<bool> StartFeed();
        Task<bool> StopFeed();
    }
    public class Order
    {
        [JsonPropertyName("id")]   public int? Id { get; set; }
        [JsonPropertyName("symbol")] public string? Symbol { get; set; }
        // … Quantity, Price, Time — all nullable (the codegen emits every DTO prop as nullable)
    }
}
```

Two things to notice. First, **parameters are `Arg<T>`**, not `T` — that is what makes a `Dep<T>`
fit (or not) at compile time. Second, **every DTO property is nullable** (`int? Id`). That is the
one ergonomic rough edge this chapter has to deal with (see the caveat below).

## Tier 1 — a typed `@alias` chain

`SleipnirLinqClient` wraps the generated client's transport router (the same `SleipnirTransportRouter`
from chapter 2, so `AdminAuth.SetBearer` arms LINQ calls too):

```csharp
// Linq.razor — OnInitialized
_linq = new SleipnirLinqClient(Sleipnir.Client);
```

### Chain 1 — `Search → GetQuotes` (anonymous)

```csharp
var search = _linq.Build((Contract.IMarketService c) => c.Search(q));
Dep<List<string>> symbols = search.Expose();                    // "$"  → Dep<List<string>>

var fetch = _linq.Build((Contract.IMarketService c) => c.GetQuotes(symbols));
var batch = new SleipnirBatch(search, fetch);                   // Serial on the wire; the server
                                                               // auto-detects dependencyMapping
                                                               // → topological (mode is a no-op)
var responses = await _linq.SendAsync(batch);

matched     = _linq.ResultOf(search, responses) ?? new();       // List<string>?
linqQuotes  = _linq.ResultOf(fetch,  responses) ?? new();       // List<Quote>?
```

- `Build(lambda)` captures the controller, method, and parameters from a call expression **against
  the interface** — no strings. `c.Search(q)` binds `q` as a literal; `c.GetQuotes(symbols)` binds
  `symbols` as a `Dep<T>` placeholder (the implicit `Arg<T>(Dep<T>)` conversion fires inside the
  lambda, so the placeholder is recorded, not serialized).
- `Expose()` registers `dependencyMapping: { alias → "$" }` on the producing call and returns
  `Dep<List<string>>` — the whole result (Search returns the array directly, so `$` is the list;
  no `$[*]` needed). `Expose(selector)` (next chain) builds a result-relative path from a lambda.
- `SleipnirBatch(specs...)` is a `SleipnirMultiRequest` (Serial on the wire, chapter 5); the server
  auto-detects the `dependencyMapping` and runs the topological batch executor regardless of `mode`
  (chapter 6), so dependents run after providers and independent calls parallelize. `SendAsync`
  dispatches it over the transport and returns the per-call responses. `ResultOf(spec, responses)`
  correlates by `Id` and deserializes the typed result.

### Chain 2 — `PlaceOrder → GetOrder` (authed, path `$.id`)

```csharp
var place = _linq.Build((Contract.IPortfolioService c) => c.PlaceOrder(orderSymbol, qty));
Dep<int> orderId = place.Expose(o => (int)o!.Id);              // "$.id" → Dep<int>

var fetchOrder = _linq.Build((Contract.IPortfolioService c) => c.GetOrder(orderId));
var batch = new SleipnirBatch(place, fetchOrder);
var responses = await _linq.SendAsync(batch);
placedOrder = _linq.ResultOf(place, responses);                // Order?
```

`Expose(o => (int)o!.Id)` builds the result-relative JsonPath `$.id` from the selector —
`JsonPathBuilder` walks the expression tree (member access → `.id`, `[0]` → `[0]`,
`.Select(e => e.Id)` → `[*].id`), and reads each property's wire name from its
`[JsonPropertyName]`, so the path **cannot drift** from the wire even if a future emitter
overrides a name. The same `$.id` you typed by hand in chapter 6 is now inferred.

## The nullable-value-type caveat (the one rough edge)

The cast in `(int)o!.Id` is **load-bearing**, and the reason is the chapter's one subtlety. The
codegen emits every DTO property as nullable (`int? Id`), so `Expose(o => o.Id)` yields
`Dep<int?>`. But `GetOrder(Arg<int>)` wants `Dep<int>`, and **only the same-`T` implicit
conversion exists** (`Arg<T>(Dep<T>)` — that is the compile-time check itself). `Dep<int?>` does
not convert to `Arg<int>`:

```
error CS0029: cannot convert type 'Dep<int?>' to 'Dep<int>'
```

You reach for the null-forgiving operator — and it does **not** work:

```csharp
Dep<int> orderId = place.Expose(o => o!.Id!);   // CS0029 — still Dep<int?>
```

The `!` is a **compile-time flow-analysis hint** that suppresses nullable warnings; it does *not*
change the type of an expression. `o!.Id!` is still `int?` at the type level, so `Expose<TProp>`
infers `TProp = int?` → `Dep<int?>`. (This is true of reference types too — `string? x; var y = x!;`
leaves `y` as `string?` — but for reference types `T?` and `T` are the *same* type, so
`Dep<List<string>?>` happily converts to `Arg<List<string>>`. Value types are different: `int?`
is `Nullable<int>`, a distinct type from `int`.)

The fix is an explicit cast, which lowers to a `Convert` node that `JsonPathBuilder` recurses
through — the path stays `$.id` while `TProp` is inferred as `int`:

```csharp
Dep<int> orderId = place.Expose(o => (int)o!.Id);   // ✓ "$.id" → Dep<int>
```

> **Note:** the `Sleipnir.Client.Linq` README's "Nullable ergonomics" section suggests
> `Expose(o => o.Id!) → Dep<int>`. That guidance does not hold — the `!` does not unwrap a
> nullable value type, as the CS0029 above shows. The explicit cast is the working pattern. (This
> is a known doc bug, tracked separately from the guide.)

## The wire is unchanged — no server changes

The LINQ layer is a **frontend over the chapter-6 wire contract**; it adds no server semantics.
`SleipnirBatch` compiles to exactly the `SleipnirMultiRequest` you built by hand. Chain 1 on the
wire (verified against the running server):

```bash
curl -sk -X POST https://localhost:5010/api/sleipnir/json/multi -H "Content-Type: application/json" -d '{
  "requests":[
    {"controller":"Market","method":"Search",
     "params":[{"parameterName":"query","data":"o"}],
     "dependencyMapping":{"symbols":"$"},"id":"search"},
    {"controller":"Market","method":"GetQuotes",
     "params":[{"parameterName":"symbols","data":"@symbols"}],"id":"fetch"}],
  "mode":1}'
```
```json
[
  {"code":200,"data":["BTC","SOL","DOGE"],"id":"search",
   "exposedDependencies":{"symbols":"[\"BTC\",\"SOL\",\"DOGE\"]"}},
  {"code":200,"data":[{"symbol":"BTC","price":60000,…},{"symbol":"SOL",…},{"symbol":"DOGE",…}],"id":"fetch"}
]
```

That is the same `dependencyMapping` + `@alias` from chapter 6 — the LINQ client just builds it
from lambdas and type-checks it. The `"mode":1` is the wire default `SleipnirBatch` sends; the
server ignores it for chains (auto-detect on the `dependencyMapping` → topological, chapter 6), so
`mode:0` would resolve identically. Chain 2 is the authed `PlaceOrder → GetOrder` wire from
chapter 8's curl step 6, with `$.id` instead of `$`.

## Tier 2 — `SleipnirQuery<T>` (cross-link, beyond this guide)

The package ships a second tier — an EF-Core-shaped eager-load façade:

```csharp
var customers = linq.From((ICustomerService c) => c.SelectCustomers())
    .Include(c => c.Kontakt)
    .ThenInclude(k => k.Ansprechpartner)
    .Include(c => c.Bestellungen)
    .Build();                          // compiles to the same @alias multi-request
var graph = linq.Materialize(query, responses);   // stitches flat lists → nested graph
```

Tier 2 compiles to the **same native wire** (the server sees no "query" — only the ordinary
`@alias`/`dependencyMapping` multi-request, which auto-selects the topological executor). It
requires `[SleipnirNavigation]` annotations on the **server** DTOs, which the Market/Portfolio
domain in this guide does not have (no navigation edges — `Order` has no child collections). So
Tier 2 is out of scope here; the full pipeline — `From`/`Include`/`ThenInclude`/`Where`/`Materialize`,
the `[SleipnirNavigation]` attribute, and the codegen that drift-checks the fetch edges — is
specified in [`LINQ_QUERY.md`](../LINQ_QUERY.md).

## Why not LINQ everywhere?

The explicit `.Exposes` / `.WithAlias` builders from chapter 6 are **transparent** — you see the
JsonPath and the alias name on the page, which is what you want when learning the wire. The LINQ
provider is **opt-in ergonomics** for a codebase that wires many typed chains: the path is
inferred and the type is checked, at the cost of a codegen step and the nullable-value-type
caveat. Both produce identical wire. Use chapter 6 to *understand* `@alias`; use chapter 7 to
*scale* it.

> The authed chain here reuses the chapter-8 `[SleipnirAuthorise]` surface — `Portfolio` is gated,
> so chain 2 needs an admin login (the admin keeps its bearer server-side, chapter 8). LINQ calls
> carry the bearer because they share the same `SleipnirTransportRouter`.

## Try it

```bash
# terminal 1 — the API
dotnet run --project guide/server

# terminal 2 — the admin (Blazor Pflege-Backend)
dotnet run --project guide/admin   # → https://localhost:5011/linq
```

On `/linq`: **Typed chain** (chain 1) runs anonymous — type a query, see the matched symbols and
the typed `List<Quote>`. Log in as `admin` / `admin` (chapter 8) to enable chain 2 — **Typed
chain** places an order and fetches it back in one roundtrip, with the `Dep<int>` for `$.id`
feeding `GetOrder(@orderId)`.

---

**Next:** [Chapter 8 — Auth: JWT Bearer, three tiers, 401 vs 403](08-auth.md). The `Portfolio`
behind the authed chain here is `[SleipnirAuthorise]`-gated; chapter 8 is the full auth story —
JWT issuance, the one ordering rule, the admin-vs-customer bearer split, and the 401/403
distinction. (If you read this chapter first, chapter 8 is where you'd log in.)