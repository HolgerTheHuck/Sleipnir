# Chapter 5 — Batching: one roundtrip, many calls

> **Goal:** fetch N quotes in **one roundtrip** instead of N. Three ways, contrasted:
> a single bulk method, a `SleipnirMultiRequest` batch of N existing `GetQuote` calls in
> **Parallel**, and the same batch in **Serial**. The batch composes methods you already have —
> no new server endpoint — and can mix any controllers/methods in one request.

Chapter 4's portal fetched its four-symbol board with `Promise.all` of four `GetQuote`
calls. That is **four roundtrips** (four HTTP requests, four server entries). Fine for four;
wrong for forty. Sleipnir's batch wire (`SleipnirMultiRequest`) folds N calls into one request
and one response array — the server fans them out.

## The three options

| Approach | Server change | Roundtrips | When |
|---|---|---|---|
| `Promise.all` of N `GetQuote` (chapter 4) | none | **N** | never, for more than a handful |
| Single `Market.GetQuotes(symbols)` bulk call | **new method** | **1** | a known hot path; you own the shape |
| `SleipnirMultiRequest` of N `GetQuote` | **none** | **1** | compose any existing methods; mix controllers |

The bulk endpoint and the batch both cost one roundtrip. The difference is *who* decides the
shape: a bulk method is a server-side loop you authored; a batch is a client-side composition
the server executes. This chapter adds the bulk method *and* teaches the batch, so you see
both — and reach for the batch when you don't want to change the server.

## The server: a bulk `GetQuotes`

```csharp
[SleipnirMethod("GetQuotes")]
[SleipnirDocumentation("Bulk-fetch quotes for many symbols in one call. Unknown symbols are skipped. …")]
public List<Quote> GetQuotes(string[] symbols)
{
    var quotes = new List<Quote>();
    foreach (var s in symbols ?? Array.Empty<string>())
    {
        var q = GetQuote(s);
        if (q is not null) quotes.Add(q);   // skip unknowns — a bulk endpoint chooses its own not-found semantics
    }
    return quotes;
}
```

A bulk method chooses its own contract for unknowns (here: skip). A batch surfaces a **per-
request** 404 — each `GetQuote` in the batch is its own response, so `NOPE` comes back as a
`200` with `data: null` (or whatever `GetQuote` returns for an unknown symbol), not a
short-circuit of the whole batch.

Rebuild the server (the `Sleipnir.Server.Codegen` `AfterTargets=Build` task regenerates
`contract.sleipnir.json` and drift-checks it):

```bash
dotnet build guide/server
# → contract.sleipnir.json now lists Market.GetQuotes
```

Then the contract loop: rebuild the admin (the C# source generator picks up `GetQuotes` from
the linked contract → `SleipnirGenerated.cs` gains `Market.GetQuotes`), and `npm run gen` in the
portal (the TS client gains `market.getQuotes`). Both clients now see the bulk method —
**without anyone touching the clients by hand**.

## The admin: Parallel vs Serial batch (Blazor)

`Batch.razor` runs all three side by side, with a stopwatch:

```csharp
// 1) Bulk — one method, one roundtrip.
var call = Sleipnir.Market.GetQuotes(symbols);
var bulk = await Sleipnir.Call<List<Quote>>(call) ?? new();

// 2) Batch — one roundtrip, N existing GetQuote calls. Build the raw SleipnirMultiRequest
//    so we can pick ExecutionMode; dispatch via the underlying ISleipnirClient the generated
//    client exposes, and read results back by request id.
var multi = new SleipnirMultiRequest
{
    Requests = symbols
        .Select(s => SleipnirCall.Init("Market", "GetQuote").Param("symbol", s).Named(s).ToRequest())
        .ToList(),
    Mode = ExecutionMode.Parallel,   // or ExecutionMode.Serial
};
var resp = await SleipnirMultiCallResponse.Call(Sleipnir.Client, multi);
foreach (var s in symbols)
    quotes.Add(resp.Get<Quote>(s));   // by the .Named(s) id
```

**`ExecutionMode`** — the only knob on a batch:

- **`Parallel`** — the server runs the N requests with `Task.WhenAll`. Fast, but calls can't
  see each other's results.
- **`Serial`** — sequential. Slower, but each call can resolve an `@alias` placeholder against
  prior responses (chapter 6 builds on this).
- **Topological (auto)** — if *any* request carries a `dependencyMapping` (an `@alias`), the
  server **ignores** `Mode` and runs a Kahn topological sort instead, so dependents always run
  after their providers. You'll see that in chapter 6.

For a pure fan-out of independent calls, Parallel is the right choice; the result is identical
to Serial, just faster. The admin page shows the timing so you can feel the difference (small
here — the in-memory seed is instant — but real on a database-backed controller).

> **Why the raw `SleipnirMultiRequest` here, not the typed `Batch` builder?** The generated
> `Sleipnir.Batch(Batch)` helper is **Serial-only** — it's shaped for `@alias` chaining
> (chapter 6). To pick `Parallel` you build a `SleipnirMultiRequest` directly and dispatch it
> through `Sleipnir.Client` (the `ISleipnirClient` the generated client wraps). For Serial you
> can use either; chapter 6 uses the typed builder.

> **`EmitCompilerGeneratedFiles`** — the admin's `.csproj` sets this so the generated
> `SleipnirGenerated.cs` lands in `obj/.../generated/` on disk. Not required (the type is in
> the compilation either way), but it makes the contract loop tangible: after a rebuild you
> can open the file and read `public Call GetQuotes(Arg<List<string>> symbols)`.

## The portal: one roundtrip over `auto` (Svelte)

Chapter 4's `refreshAll` did `Promise.all(cards.map(c => fetchOne(c.symbol)))` — N roundtrips.
Chapter 5 replaces it with a single request, switchable between the batch and the bulk call:

```ts
// Batch — one roundtrip, N existing GetQuote calls. The generated Batch builder is Serial
// (designed for @alias chaining, chapter 6); for independent fan-out, Serial still means one
// roundtrip — the server just sequences the calls.
const b = new Batch();
for (const s of symbols) b.add(client.market.getQuote(s)).named(s);
const responses = await client.batch(b);
// responses[i] is the i-th call's SleipnirResponse — read .data as Quote.
```

```ts
// Bulk — one roundtrip, the single Market.GetQuotes endpoint.
const res = await client.call(client.market.getQuotes(symbols));
// res.data is Quote[] (unknown symbols skipped by the server).
```

Both go through the unified transport's `auto` profile — one roundtrip over WebSocket, or one
over REST+SSE if the WS probe failed. A radio toggles the mode live; **Refresh all** re-runs.

> **Why Serial on the portal?** The generated `client.batch(b)` helper is Serial-only (same
> reason as the C# `Batch` builder — it's shaped for `@alias`). For independent fan-out it
> doesn't matter: one roundtrip, same results. A Parallel batch over `auto` would need the
> raw `SleipnirMultiRequest` dispatched through `client.rest.callBatch(..., Parallel)` — an
> escape hatch that pins you to REST. For a pure fan-out, the Serial batch is the cleaner win.

## Try it

```bash
# terminal 1 — the API (now with GetQuotes)
dotnet run --project guide/server

# terminal 2 — the admin (Blazor)
dotnet run --project guide/admin   # → https://localhost:5011/batch

# terminal 3 — the portal (Svelte)
cd guide/portal && npm run dev     # → http://localhost:5173
```

On the admin `/batch` page, enter `BTC, ETH, SOL, DOGE, NOPE` and run each mode. The table
fills; `NOPE` shows per-row "unknown" (bulk skips it; both batch modes surface it per call).
The stopwatch reads a few ms — the in-memory seed is instant, but the shape is what matters.

On the portal, switch the **Fetch** radio between **Batch** and **Bulk** and click **Refresh
all** — same board, one roundtrip either way. The transport badge still shows what `auto`
settled on.

> **Verify the batch wire without a UI** — the multi endpoint is `POST /api/sleipnir/json/multi`.
> Note `mode` is an **int** on the wire (`0 = Parallel`, `1 = Serial`), not a string — the
> typed clients serialise the enum for you; raw `curl` must send the int:
> ```bash
> curl -s -X POST https://localhost:5010/api/sleipnir/json/multi \
>   -H "Content-Type: application/json" \
>   -d '{"requests":[
>         {"controller":"Market","method":"GetQuote","params":[{"parameterName":"symbol","data":"BTC"}],"id":"BTC"},
>         {"controller":"Market","method":"GetQuote","params":[{"parameterName":"symbol","data":"ETH"}],"id":"ETH"}],
>       "mode":0}'
> # → [{"code":200,"data":{"symbol":"BTC",…},"id":"BTC"},
> #    {"code":200,"data":{"symbol":"ETH",…},"id":"ETH"}]
> ```
> (Set `NODE_TLS_REJECT_UNAUTHORIZED=0` / `--insecure` for the self-signed dev cert, or trust it
> with `dotnet dev-certs https --trust`.)

---

**Next:** [Chapter 6 — Chaining](06-chaining.md). The batch becomes a **chain**: one call's
result feeds the next via an `@alias` placeholder — `Search("bit")` exposes its symbol list,
`GetQuotes(@symbols)` consumes it, `$[*]` fan-out and all, **one roundtrip, no client glue**.