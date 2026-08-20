# Chapter 6 — Chaining: one call's result feeds the next

> **Goal:** run two calls in **one roundtrip** where the second depends on the first — with
> **no client glue** between them. `Market.Search("bit")` finds tickers; `GetQuotes` fetches
> their prices. Instead of "search → read symbols → send a second request", `Search`
> *exposes* its result under an alias and `GetQuotes` *consumes* that alias. The server
> resolves the dependency in **Serial** mode. One request in, one response array out.

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
   one SleipnirMultiRequest, mode = Serial, one roundtrip
```

The `$[*]` path is a **multi-match JsonPath** — "every element of the returned array".
The framework collects all matches into one list and injects that list as a single
parameter (`string[]` / `List<string>`). This is **list fan-out into a *parameter***,
never fan-out into N *requests* — one consumer call, one array argument. (See
`DEPENDENCY_BINDING.md` for the full binding matrix and `PROTOCOL.md` → "Alias
Serialization & Type Binding".)

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

var resp = await Sleipnir.Batch(batch);           // Serial — required for @alias
var symbols = resp.Get<List<string>>("search") ?? new();
var quotes  = resp.Get<List<Quote>>("quotes")  ?? new();
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

### Serial is the only mode that resolves `@alias`

A `Batch` is always `ExecutionMode.Serial` — chapter 5's `Parallel` batch can't chain, because
parallel calls run with `Task.WhenAll` and can't see each other's results. If you set
`dependencyMapping` on any request, the server actually **ignores** `Mode` and runs a
topological (Kahn) sort instead — so dependents always run after their providers, and
independent requests within the batch still parallelize. You don't have to pick; the server
auto-detects.

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
three rows, still one roundtrip. The provider and consumer ran in the same request.

On the portal, the **Chain** section does the same: type `o`, click **Chain**, see the matched
tickers and their quotes appear together.

> **Verify the chain wire without a UI** — the multi endpoint resolves `@alias` in **Serial**
> mode (`"mode":1`). The provider carries `dependencyMapping` (alias → result-relative JsonPath);
> the consumer sends `@alias` as the parameter value:
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
typed ergonomic layer on top — `Dep<T>` and `SleipnirQuery<T>` — so a chain reads like a query
and the `@alias` wiring is inferred. _(This chapter is planned; the `@alias` mechanics it builds
on are fully covered above. Skip ahead to [Chapter 8 — Auth](08-auth.md) to continue the
running story.)_