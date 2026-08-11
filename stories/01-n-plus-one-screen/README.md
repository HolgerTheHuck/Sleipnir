# Story 01 — The N+1 Screen

> **The client should declare *what depends on what*. The server should resolve it in one roundtrip.**

One order-detail screen. Six dependent reads across five services. The REST way is six
sequential roundtrips with the client as the workflow engine. The Sleipnir way is one batch —
the client declares the dependency graph, the server executes it topologically with
intra-server parallelism, in a single roundtrip.

## Run it (F5 → the N+1 screen)

The web example is served **by the API itself** at **`/story01`** — same origin as
`/api/sleipnir/*`, so there is **no CORS, no separate Vite dev server, no proxy** to configure.
The first walkthrough is one `dotnet run` (or F5) away.

1. **One-time:** build the web bundle — `cd web && npm run build` (regenerates the typed
   client from the committed discovery fixture, then builds the UI into `web/dist`).
   `web/dist` is a build artifact (gitignored), so a fresh clone needs this step once.
2. Open **`Story01.sln`** in Visual Studio (or `dotnet build && dotnet run --project Story01.csproj`).
3. Press **F5** (or `dotnet run`). The browser opens at **`http://localhost:5001/story01/`** —
   the N+1 screen. Click **"Load — 1 typed Sleipnir batch"** to see the whole order-detail screen
   materialize in a single roundtrip; click **"Load — 6 serial roundtrips"** to compare.
4. The screen links to the **Sleipnir Developer UI** at **`/Sleipnir`** — six controllers from
   `Domain.cs` (`Order`, `Customer`, `OrderLine`, `Article`, `Address`, `Stock`), discovery
   shows the contract (code-first — the C# classes *are* the contract, no IDL, no `.proto`).

> The UI calls `new SleipnirClient("/")`, so every `/api/sleipnir/json` call is same-origin —
> the CORS error you'd hit serving the UI from a different origin does not arise.

### Layout

| Path | What |
|---|---|
| `/` | redirects to `/story01/` |
| `/story01/` | the N+1 screen (built `web/dist`) |
| `/Sleipnir` | the Sleipnir Developer UI |
| `/api/sleipnir/json`, `/api/sleipnir/json/multi`, `/api/sleipnir/discovery` | the API |

### If you want the Vite dev server instead (HMR while editing the UI)

`cd web && npm run dev` runs Vite on its own port with a proxy to
`http://localhost:5001` (see `web/vite.config.ts`). Use that for UI development; use the
integrated `/story01` endpoint for the no-setup walkthrough.

Call a single endpoint in the DevUI (REST wire):

```
POST http://localhost:5001/api/sleipnir/json
{
  "controller": "Order",
  "method": "GetById",
  "params": [{ "parameterName": "id", "data": 42 }],
  "id": "q1"
}
```

## The Sleipnir Way — one batch, declared dependencies

Paste this into the DevUI batch sender (`POST /api/sleipnir/json/multi`). It is the whole screen
in **one roundtrip**: the client declares the graph, never extracts an id.

```jsonc
{
  "mode": 0,   // ExecutionMode: 0 = Parallel, 1 = Serial. Ignored here — a dependencyMapping
               //              is present, so the server auto-detects and runs the topological path.
  "requests": [
    { "controller": "Order",     "method": "GetById",      "id": "order",
      "params": [{ "parameterName": "id", "data": 42 }],
      "dependencyMapping": { "customerId": "$.customerId", "orderId": "$.id", "addressId": "$.shippingAddressId" } },

    { "controller": "Customer",  "method": "GetById",      "id": "customer",
      "params": [{ "parameterName": "customerId", "data": "@customerId" }] },

    { "controller": "OrderLine",  "method": "GetByOrder",   "id": "lines",
      "params": [{ "parameterName": "orderId", "data": "@orderId" }],
      "dependencyMapping": { "articleIds": "$[*].articleId" } },

    { "controller": "Article",   "method": "GetByIds",     "id": "articles",
      "params": [{ "parameterName": "articleIds", "data": "@articleIds" }] },

    { "controller": "Stock",     "method": "GetByArticles", "id": "stock",
      "params": [{ "parameterName": "articleIds", "data": "@articleIds" }] },

    { "controller": "Address",   "method": "GetById",      "id": "address",
      "params": [{ "parameterName": "addressId", "data": "@addressId" }] }
  ]
}
```

### What the server does

```
                     Order.GetById(42)
                   exposes: customerId, orderId, addressId
              ┌──────────┬──────────────────┬───────────────┐
              ▼          ▼                  ▼               ▼
        Customer    OrderLine.GetByOrder  Address       (articleIds not yet available)
       (@customerId)  exposes: articleIds  (@addressId)
                         │          │
                         ▼          ▼
                    Article     Stock            ← diamond: one provider, two consumers
                  (@articleIds)(@articleIds)       both fed from the same `articleIds`
```

- `dependencyMapping` on a request says *"my result exposes this alias from this JsonPath"*
  (result-relative: `$` is the whole result, `$.customerId` a property, `$[*].articleId`
  a multi-match → one `List<int>`).
- `@alias` as a parameter value is a placeholder the server resolves from the prior result
  that exposed it. The client never reads an id.
- `Mode` is ignored the moment a `dependencyMapping` is present — the server auto-detects
  and runs the topological batch path.

### The REST Way (for contrast)

Six sequential roundtrips, the client extracting ids between each:

```csharp
var order    = await client.Call<Order?>    (SleipnirCall.Init("Order","GetById").With(42));
var customer = await client.Call<Customer?> (SleipnirCall.Init("Customer","GetById").With(order.CustomerId));
var lines    = await client.Call<List<OrderLine>>(SleipnirCall.Init("OrderLine","GetByOrder").With(order.Id));
var articleIds = lines.Select(l => l.ArticleId).Distinct().ToList();          // ← client glue
var articles = await client.Call<List<Article>>(SleipnirCall.Init("Article","GetByIds").With(articleIds));
var address  = await client.Call<Address?>   (SleipnirCall.Init("Address","GetById").With(order.ShippingAddressId));
var stock    = await client.Call<List<StockInfo>>(SleipnirCall.Init("Stock","GetByArticles").With(articleIds)); // recomputed
```

|                          | The REST Way      | The Sleipnir Way          |
|--------------------------|-------------------|------------------------|
| Roundtrips               | 6 (sequential)    | **1**                  |
| Client orchestration     | 6 calls + id glue | 1 batch, declared deps |
| Id extraction in client  | yes (manual)      | none (server binds)    |
| Workflow knowledge in    | every client      | the server (once)      |

### What this is — and isn't

The win above is the **cross-service** n+1: the chain Order → Customer → Lines → Articles, where
each call needs the previous call's output. That chain can't be bulked away, so a REST client
owns the extract-and-await glue for every link and pays six roundtrips. Sleipnir takes that glue
off the client and collapses the chain to one roundtrip with server-side graph resolution.

It is **not** "Sleipnir parallelizes the article fetch." The bulk endpoints here — `GetByIds`,
`GetByOrder` — are good API design you'd build for REST too; they kill the *intra-service* n+1
(n articles in one call vs n+1). Sleipnir doesn't replace them; it replaces the imperative glue
*between* them. A REST client could `Promise.all` the independent calls — but it would still pay
the roundtrips and own the fan-out. The framework win is the **roundtrip collapse and the removed
orchestration**, not raw parallelism. Full reasoning:
[BEST_PRACTICES.md §4.2](../../BEST_PRACTICES.md#42-when-the-sleipnir-batch-beats-the-rest-loop--and-where-the-win-actually-is).

The bulk endpoints here (`GetByIds`, `GetByOrder`, `GetById`) are service methods, not Sleipnir
inventions — the Story's controllers are thin facades over the same in-memory store a REST
controller would call. That is the seam that lets Sleipnir drop into an existing REST API without
rewriting endpoints: the service keeps the bulk logic, the transports are interchangeable facades
above it. See
[BEST_PRACTICES.md §4.6](../../BEST_PRACTICES.md#46-the-service-layer-is-the-seam--share-the-bulk-not-the-transport).

## Where NOT to use this

- **Replacing an efficient SQL join.** If all reads are in one DB, a single query with joins
  beats service calls. Sleipnir chains *across services you cannot or will not join in SQL*.
- **Unbounded graphs.** Chaining is for bounded intermediate results. Sleipnir caps both sides
  (`MaxResultElementCount` default 10 000, `MaxParameterArrayLength` default 1 000).
- **CRUD that is genuinely CRUD.** One resource by id with no dependencies is already one
  roundtrip. Sleipnir earns its keep when there is a *graph*, not a row.

## Files

- `Program.cs` — F5 wiring (`UseStaticWebAssets` + `AddSleipnir` + `UseSleipnirTransports` + `MapSleipnir`).
- `Domain.cs` — the six code-first controllers + in-memory store (Order #42, 30 ms/call simulated latency).
- Full narrative: `docs/stories/01-the-n-plus-one-screen.md`.

Next: **Story 02 — One Button, Seven Commands** (a write-side fan-out, per-command isolation).