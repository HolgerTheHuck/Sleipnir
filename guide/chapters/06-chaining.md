# Chapter 6 — Chaining: one call's result feeds the next

> **Goal:** run two calls in **one roundtrip** where the second depends on the first — with
> **no client glue** between them. `Market.Search("bit")` finds tickers; `GetQuotes` fetches
> their prices. Instead of "search → read symbols → send a second request", `Search`
> *exposes* its result under an alias and `GetQuotes` *consumes* that alias. The server
> auto-detects the `dependencyMapping` and runs a **topological execution graph** (Kahn):
> dependents run after their providers, independent requests within a batch run in parallel.
> One request in, one response array out.

Chapter 5 folded N independent calls into one roundtrip (a batch). Chapter 6 folds **dependent**
calls into one roundtrip (a chain) — the standout feature. The trick is the `@alias` placeholder:
a producer call declares "my result `$[*]` is available as `symbols`"; a consumer call sends
`@symbols` where a normal parameter value would go. The server substitutes the real value
between the two calls, inside the single roundtrip.

## The provider: `Market.Search`

```csharp
[SleipnirMethod("Search")]
[SleipnirDocumentation("Find symbols whose ticker or full name contains the query (case-insensitive). …")]
public string[] Search(string query)
{
    if (string.IsNullOrWhiteSpace(query))
        return Array.Empty<string>();

    var q = query.Trim();
    var hits = new List<string>();
    foreach (var (symbol, name) in SymbolNames)   // BTC=Bitcoin, ETH=Ethereum, SOL=Solana, DOGE=Dogecoin
    {
        if (symbol.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            name.Contains(q, StringComparison.OrdinalIgnoreCase))
            hits.Add(symbol);
    }
    return hits.ToArray();
}
```

`Search` returns the *tickers* it matched (`string[]`). That array is what the consumer
needs as its input. Rebuild the server and the contract loop carries `Market.Search` to both
generated clients — same as chapter 5's `GetQuotes`:

```bash
dotnet build guide/server      # contract.sleipnir.json now lists Market.Search
dotnet build guide/admin       # C# generator: Sleipnir.Market.Search(query)
( cd guide/portal && npm run gen )   # TS generator: client.market.search(query)
```

## The chain, in one picture

```
  ┌─ request 1 (provider) ────────────────────────────────┐
  │ Market.Search("o")                                    │
  │   exposes  $[*]  ──►  alias "symbols"                  │
  └──────────────────────────┬───────────────────────────┘
                             │  server resolves @symbols = ["BTC","SOL","DOGE"]
                             ▼
  ┌─ request 2 (consumer) ───────────────────────────────┐
  │ Market.GetQuotes(@symbols)   →  GetQuotes(["BTC"…])  │
  └──────────────────────────────────────────────────────┘
   one SleipnirMultiRequest, one roundtrip
   (the server auto-detects dependencyMapping → topological; mode is ignored for chains)
```

The `$[*]` path is a **multi-match JsonPath** — "every element of the returned array".
The framework collects all matches into one list and injects that list as a single
parameter (`string[]` / `List<string>`). This is **list fan-out into a *parameter***,
never fan-out into N *requests* — one consumer call, one array argument. (See
`DEPENDENCY_BINDING.md` for the full binding matrix and `PROTOCOL.md` → "Alias
Serialization & Type Binding".)

## Not only `$[*]` — scalar extraction (`$[0]`, `$.id`)

`$[*]` is the **list fan-out** shape (one producer, a whole array, injected as one list
parameter). Chaining also has a **scalar** shape: extract a **single value** and feed it into a
method that takes one argument. Two flavours:

- **`$[0]`** — one element of an array (a single index, not a wildcard). `Search` exposes
  `$[0]` (the first matched ticker) and `GetQuote(@first)` consumes that one string:
  ```
    Market.Search("bit")  →  ["BTC"]        exposes $[0]  ──►  alias "first"
                                        server resolves @first = "BTC"
                                              ▼
    Market.GetQuote(@first)  →  GetQuote("BTC")  →  one Quote
  ```
  `$[0]` is a one-match path → the server injects a single `string`, not a list. The consumer
  `GetQuote(string)` takes one symbol — a scalar fits a scalar parameter. (If `Search` matches
  nothing, `$[0]` is unresolved and the consumer gets a `400` — the chain fails closed, see
  below.)

- **`$.id`** — one property of an object (the common **create → getById** pattern). A provider
  that returns a single object exposes a property: `PlaceOrder` returns an `Order`, exposes
  `$.id` as `orderId`, and `GetOrder(@orderId)` consumes it. This is the chain behind the authed
  `Portfolio` surface in [chapter 8](08-auth.md) and the typed `Expose(o => o.Id)` in [chapter 7's
  LINQ provider](07-linq.md). The guide's anonymous `Market` domain has no single-object
  producer with an `Id`, so the runnable scalar demo here is the `$[0]` form — but the wire is
  identical: only the JsonPath shape (`$[0]` vs `$.id` vs `$[*]`) changes.

The admin's `/chain` page runs both: **Chain 1** (`$[*]` → `GetQuotes`) and **Chain 2`
(`$[0]` → `GetQuote`), same one-roundtrip contract, only the extracted shape differs.

## The admin: a typed chain (Blazor)

`Chain.razor` builds the chain with the generated `Batch` builder and reads each call's
result back by its id:

```csharp
var batch = new Sleipnir.Generated.Batch();
// .Named(id) lives on Call; .Exposes / .Alias live on BatchEntry. Name the call, then
// Add → BatchEntry, then Exposes on the entry. (Fully qualified because the sibling
// Batch.razor page class would otherwise win the unqualified "Batch" name lookup.)
var search = batch.Add(Sleipnir.Market.Search(query).Named("search"))
                   .Exposes("$[*]", "symbols");
batch.Add(Sleipnir.Market.GetQuotes(search.Alias("symbols")).Named("quotes"));

var resp = await Sleipnir.Batch(batch);   // chain → server auto-detects dependencyMapping
var symbols = resp.Get<List<string>>("search") ?? new();   //  → topological graph; mode is
var quotes  = resp.Get<List<Quote>>("quotes")  ?? new();   //  ignored for chains (see below)
```

The shape mirrors the diagram exactly:

- **Provider** — `Sleipnir.Market.Search(query)`, named `"search"`, `.Exposes("$[*]", "symbols")`.
- **Consumer** — `Sleipnir.Market.GetQuotes(search.Alias("symbols"))`, named `"quotes"`. The
  `Alias("symbols")` call returns the `@symbols` wire placeholder, and `Arg<List<string>>`
  (the type of `GetQuotes`'s `symbols` parameter) implicitly converts from it — so the
  consumer typechecks against the producer's exposed type, not a stringly-typed `@`.

### `@`-normalization — both styles work

`Exposes(path, "symbols")` strips a leading `@` (the wire `dependencyMapping` key is the
*bare* name — the server strips the consumer's `@alias` placeholder before lookup). `Alias("symbols")`
*ensures* a leading `@` (the consumer sends `data: "@symbols"`). So `Alias("symbols")` and
`Alias("@symbols")` both send `"@symbols"`; `Exposes(…, "symbols")` and `Exposes(…, "@symbols")`
both map to the bare key `symbols`. This symmetry was a 1.2.1 fix — returning the bare name
from `Alias` sent `"symbols"` on the wire, which the server's `ReplaceDependencyByAlias` never
matched; the typed chain *compiled* but the dependent call got an unresolved literal instead
of the alias value.

### The server builds the execution graph itself (auto-detect)

You do **not** pick a mode to chain. The `mode` field (chapter 5: `0 = Parallel`,
`1 = Serial`) governs only **pure batches** — a flat list of independent calls. The moment
**any** request carries a `dependencyMapping`, the `InvokeDi` dispatcher switches *before* the
mode switch to the **topological batch executor** (`ExecuteInDependencyBatches` →
`DependencyGraphBuilder.SortByDependencyBatches`, Kahn's algorithm). It builds the execution
graph from the `dependencyMapping` — the dependency is statically computable, no runtime
probing — and runs it as **Kahn-ordered parallel batches**: dependents always run after their
providers, and **independent requests within a batch run in parallel** (`Task.WhenAll` per
batch). `mode` is ignored for chains.

So the two shapes are:

- **Pure batch** (no `dependencyMapping`) — `mode` decides: `Parallel` (`Task.WhenAll`, all at
  once, the chapter-5 fan-out) or `Serial` (sequential). Parallel here genuinely cannot chain —
  the calls fire simultaneously and can't see each other's results.
- **Chain** (any `dependencyMapping`) — `mode` is irrelevant; the topological executor wins.
  Dependent requests are ordered; independent ones parallelize.

```
  linear chain (this chapter)        branching chain (diamond)
  Search ──► GetQuotes                Place ──┬─► GetOrder       ← batch 1: Place
            (batch 1: Search)                 └─► GetQuote(symbol) ← batch 2: GetOrder ‖ GetQuote
            (batch 2: GetQuotes)              (both depend on Place, not each other → parallel)
```

The `Search → GetQuotes` chain here is **linear** — each batch happens to hold one request, so
it runs sequentially *by shape*, not by mode. A **diamond** — one provider, two consumers that
both depend on it but not each other (`PlaceOrder` → `GetOrder` *and* `GetQuote(@symbol)`) —
runs the two consumers in parallel in batch 2. You don't wire the parallelism; the graph gives
it to you for free. `SleipnirBatch` (chapter 7) and the JSON-RPC dispatcher default to `Serial`
on the wire, but for a chain that value is overridden by auto-detect — it is a no-op, not a
requirement.

## The portal: a typed chain over `auto` (Svelte)

The TS client's chain is the same shape, and the alias type is **compile-time-checked** via the
generated path-type record (`_StringArrayPaths["$[*]"]` is `string[]`, so `search.alias("symbols")`
is typed `string[]` and `getQuotes(string[])` accepts it):

```ts
const b = new Batch();
const search = b.add(client.market.search(q)).exposes("$[*]", "symbols").named("search");
b.add(client.market.getQuotes(search.alias("symbols"))).named("quotes");
const responses = await client.batch(b);        // one SleipnirMultiRequest, one roundtrip
const symbols = responses[0].data as string[];  // the provider's matched tickers
const quotes  = responses[1].data as Quote[];    // the consumer's quotes for those tickers
```

Type `q` = `"bit"`, `"eth"`, `"sol"`, `"doge"`, `"coin"`, or a single letter like `"o"` (which
matches **B**tc**o**in, S**o**lana, D**o**gecoin → three tickers, a nice fan-out). The chain runs
over the `auto` profile — one roundtrip over WebSocket, or one over REST+SSE if the WS probe
failed.

## Try it

```bash
# terminal 1 — the API (now with Search)
dotnet run --project guide/server

# terminal 2 — the admin (Blazor)
dotnet run --project guide/admin   # → https://localhost:5011/chain

# terminal 3 — the portal (Svelte)
cd guide/portal && npm run dev     # → http://localhost:5173, the Chain section at the bottom
```

On the admin `/chain` page, enter `bit` and click **Chain Search → GetQuotes**: the page shows
`matched: [BTC]` and one quote row — one roundtrip. Try `o`: `matched: [BTC, SOL, DOGE]` and
three rows, still one roundtrip. The provider and consumer ran in the same request. **Chain 2**
on the same page does the scalar extraction — **Chain Search → GetQuote** exposes `$[0]` (the
first match) and fetches one quote for it; same one roundtrip, a single value instead of a list.

On the portal, the **Chain** section does the fan-out: type `o`, click **Chain**, see the matched
tickers and their quotes appear together.

> **Verify the chain wire without a UI** — the multi endpoint auto-detects the `dependencyMapping`
> and runs the topological batch executor **regardless of `mode`** (the `mode` field is ignored
> for chains; it only governs pure batches). The provider carries `dependencyMapping`
> (alias → result-relative JsonPath); the consumer sends `@alias` as the parameter value:
> ```bash
> curl -sk -X POST https://localhost:5010/api/sleipnir/json/multi \
>   -H "Content-Type: application/json" \
>   -d '{"requests":[
>         {"controller":"Market","method":"Search",
>          "params":[{"parameterName":"query","data":"o"}],
>          "dependencyMapping":{"symbols":"$[*]"},"id":"search"},
>         {"controller":"Market","method":"GetQuotes",
>          "params":[{"parameterName":"symbols","data":"@symbols"}],"id":"quotes"}],
>       "mode":1}'
> # → [{"code":200,"data":["BTC","SOL","DOGE"],"id":"search",
> #      "exposedDependencies":{"symbols":"[\"BTC\",\"SOL\",\"DOGE\"]"}},
> #     {"code":200,"data":[{"symbol":"BTC",…},{"symbol":"SOL",…},{"symbol":"DOGE",…}],
> #      "id":"quotes"}]
> ```
> Note the `exposedDependencies` on the provider response: that's the `$[*]` fan-out the
> server extracted and substituted into the consumer's `@symbols` placeholder — the consumer
> never saw the symbols client-side.

### When the provider fails

The framework only extracts `exposedDependencies` on a **2xx** provider response. If the
provider returns a business error (a `SleipnirResults` error), throws (→ 500), or fails the
auth pre-pass (401), its `exposedDependencies` is empty — so no value is ever extracted from an
error payload. The dependent then gets a `400` such as
`dependency '@symbols' unavailable: provider 'search' returned HTTP 500` instead of reaching a
missing alias at runtime with the uninformative `Unresolved dependencies`. Transitivity falls
out: a skipped provider exposes nothing, so *its* dependents are caught in the next batch. The
chain fails closed, never with a wrong value.

### Binding modes (optional strictness)

`SleipnirOptions.AliasBindingMode` controls how strictly the consumer's parameters must match
the provider's fragment: **Weak** (default, duck-typed), **Strict** (every top-level
read-write property of the consumer type must be present), **Paranoid** (Strict + recursive +
checks literals too). The safe subset direction (consumer ⊆ fragment, the fan-out in this
chapter) binds in all three modes; cross-kind (e.g. string→int) is `400` in all modes. See
`DEPENDENCY_BINDING.md` → "Binding modes" and the DevUI dependency builder, which statically
reproduces these rules as inline warnings.

---

**Next:** [Chapter 7 — The LINQ provider](07-linq.md). Chaining by hand (`.Exposes` /
`.Alias`, `exposes` / `alias`) is explicit and exact. The `Sleipnir.Client.Linq` package adds a
typed ergonomic layer on top — `Dep<T>` and a selector expression infer the `@alias` wiring, so
the scalar `$.id` chain from this chapter reads like a query and the JsonPath is built for you.