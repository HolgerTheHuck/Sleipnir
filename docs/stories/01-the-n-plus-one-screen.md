# Story 01 — The N+1 Screen

> The client should declare **what depends on what**.
> The server should resolve it in one roundtrip.

---

## The business problem

The product owner wants an **Order Detail** screen. One page, one click, no spinners that
appear one after another. From a single order id the screen has to show:

- the order header (status, placed-at)
- the customer name
- every order line with its article name and price
- the shipping address
- the live stock status for each article on the order

The catch: the data lives in five different places. Order owns the header and the line
*ids*. Customer, Article, Address, and Stock are each their own service. There is no
single "get everything for an order" endpoint — and the team has decided **not** to build
one, because that endpoint would be a per-screen bespoke aggregate that nobody else can
reuse and that drifts the moment a second screen needs a different slice.

This is the most common shape of business-API pain: **one screen, six dependent reads**.

---

## The REST Way

With plain REST the client becomes the workflow engine. It knows the order, then it has
to ask: "now what do I fetch next, and with which id I just got?" Six sequential
roundtrips, each waiting on the previous, with the client extracting ids out of JSON
responses by hand:

```csharp
// 1 — order
var order = await http.GetFromJsonAsync<Order>($"/api/orders/{id}");

// 2 — customer (needs order.CustomerId, which the client just extracted)
var customer = await http.GetFromJsonAsync<Customer>($"/api/customers/{order.CustomerId}");

// 3 — order lines (needs order.Id)
var lines = await http.GetFromJsonAsync<List<OrderLine>>($"/api/orders/{order.Id}/lines");

// 4 — articles (needs every line.ArticleId — collected by the client)
var articleIds = lines.Select(l => l.ArticleId).Distinct();
var articles = await PostAsync<List<Article>>("/api/articles/mget", articleIds);

// 5 — shipping address (needs order.ShippingAddressId)
var address = await http.GetFromJsonAsync<Address>($"/api/addresses/{order.ShippingAddressId}");

// 6 — stock (needs the same articleIds — recomputed by the client)
var stock = await PostAsync<List<StockInfo>>("/api/stock/by-articles", articleIds);
```

### Pain points

- **Six sequential roundtrips.** Each waits for the one before it, because each needs an
  id the previous response carries. Network latency is paid six times, in series.
- **The client is the workflow engine.** It knows the call graph (order → customer,
  order → lines → articles, order → address, lines → stock). That knowledge is now
  duplicated in every client — the web app, the mobile app, the internal tool.
- **Manual id extraction.** The client reads `order.CustomerId`, collects
  `lines.Select(l => l.ArticleId)`, dedupes, recomputes the same list for stock. This is
  glue, and it is where the bugs live (wrong property, missing `.Distinct()`, off-by-one
  on the batch).
- **Latency you can feel.** At ~80 ms per hop, six serial reads are ~480 ms of network
  alone, before the client renders a single field.

---

## The Sleipnir Way

Sleipnir flips the direction. **The client declares the dependencies; the server resolves
them in one roundtrip.** The client no longer extracts ids or waits between calls — it
says "this call exposes `customerId` from `$.customerId`, and that call's `customerId`
parameter is `@customerId`", and sends the whole thing as **one batch**.

### The domain (code-first, no IDL)

The contract is the C# classes. Nothing else exists:

```csharp
[SleipnirController("Order")]
public class OrderController
{
    [SleipnirMethod("GetById")]
    public Order GetById(int id) => _orders[id];   // { Id, CustomerId, ShippingAddressId, Status, PlacedAt }
}

[SleipnirController("Customer")]
public class CustomerController
{
    [SleipnirMethod("GetById")]                       // parameter name matches the alias → binds by name
    public Customer GetById(int customerId) => _customers[customerId];
}

[SleipnirController("OrderLine")]
public class OrderLineController
{
    [SleipnirMethod("GetByOrder")]                    // returns List<OrderLine> { ArticleId, Qty }
    public List<OrderLine> GetByOrder(int orderId) => _lines[orderId];
}

[SleipnirController("Article")]
public class ArticleController
{
    [SleipnirMethod("GetByIds")]                      // List<int> injected from the multi-match path
    public List<Article> GetByIds(List<int> articleIds) => _articles.GetByIds(articleIds);
}

[SleipnirController("Address")]
public class AddressController
{
    [SleipnirMethod("GetById")]
    public Address GetById(int addressId) => _addresses[addressId];
}

[SleipnirController("Stock")]
public class StockController
{
    [SleipnirMethod("GetByArticles")]                 // same articleIds list feeds a second consumer
    public List<StockInfo> GetByArticles(List<int> articleIds) => _stock.GetMany(articleIds);
}
```

### The batch — one request, six calls, declared dependencies

```csharp
var batch = new SleipnirMultiRequest
{
    Mode = ExecutionMode.Parallel,   // ignored the moment a DependencyMapping is present:
                                     // the server auto-detects → topological execution.
    Requests = new()
    {
        // Provider: the order. Exposes three fragments for downstream consumers.
        SleipnirCall.Init("Order", "GetById").With(42).Named("order")
            .Exposes("$.customerId",        "customerId")
            .Exposes("$.id",                "orderId")
            .Exposes("$.shippingAddressId",  "addressId")
            .ToRequest(),

        // Consumer: customer ← @customerId. Parameter is named `customerId` → binds by name.
        SleipnirCall.Init("Customer", "GetById").WithAlias("@customerId").Named("customer").ToRequest(),

        // Provider + consumer: lines ← @orderId; exposes every line's ArticleId as one list.
        // "$[*].articleId" is a multi-match path → all matches collected into one array,
        // injected as a single List<int> parameter (fan-out into a parameter, never into N requests).
        SleipnirCall.Init("OrderLine", "GetByOrder").WithAlias("@orderId").Named("lines")
            .Exposes("$[*].articleId", "articleIds")
            .ToRequest(),

        // Two consumers of the SAME list — a diamond. The server orders this correctly.
        SleipnirCall.Init("Article", "GetByIds").WithAlias("@articleIds").Named("articles").ToRequest(),
        SleipnirCall.Init("Stock",   "GetByArticles").WithAlias("@articleIds").Named("stock").ToRequest(),

        SleipnirCall.Init("Address", "GetById").WithAlias("@addressId").Named("address").ToRequest(),
    }
};

// ONE roundtrip. Responses come back in request order.
var responses = (await client.Call(batch))!.ToList();
```

### The dependency graph the server executes

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

The server runs this in **topological order, with independent calls in parallel**, and
binds each `@alias` from the prior result that exposed it — all in a single HTTP
roundtrip. The client never read a single id. It never waited between calls. It declared
*what depends on what* and let the server execute the graph.

### Before / after

|                          | The REST Way      | The Sleipnir Way          |
|--------------------------|-------------------|------------------------|
| Roundtrips               | 6 (sequential)    | **1**                  |
| Client orchestration     | 6 calls + id glue | 1 batch, declared deps |
| Id extraction in client  | yes (manual)      | none (server binds)    |
| Workflow knowledge in    | every client      | the server (once)      |
| Network latency (~80ms)  | ~480 ms (serial)  | **~110 ms** (one hop + intra-server parallelism) |
| Add a 7th dependent call | a 7th client call + more glue | one more `SleipnirCall` in the list |

---

## Discussion

### Why is this simpler?

The client stopped being a workflow engine. It no longer owns the call graph, extracts
ids, or orders calls. That knowledge moved to **the one place every client shares: the
server**. A second screen that needs a different slice of the same data (say, Order +
Invoice, no stock) reuses the same controllers and just sends a different batch — it
does not fork the orchestration code.

The diamond is the part plain REST cannot do without client code: `Article.GetByIds` and
`Stock.GetByArticles` both consume the `articleIds` that `OrderLine.GetByOrder` exposes.
The client version recomputed that list twice. The Sleipnir version declares it once and
lets two consumers read it.

### Where NOT to use this

- **Replacing an efficient SQL join.** If all six reads are in one database, a single
  query with joins beats six service calls. Sleipnir chains *across services that you
  cannot or will not join in SQL* (separate ownership, separate deploys, separate
  consistency windows). If you own all the tables, join them.
- **Unbounded graphs.** Dependency chaining is for bounded intermediate results. A
  provider that returns 50 000 article ids feeds a 50 000-element consumer list — that is
  not what this is for. Sleipnir caps both sides (`MaxResultElementCount`, default 10 000;
  `MaxParameterArrayLength`, default 1 000) and you should keep the fan-out bounded.
- **CRUD that is genuinely CRUD.** If the screen is one resource by id with no
  dependencies, a single REST `GET` is already one roundtrip. Sleipnir earns its keep when
  there is a *graph*, not when there is a row.

### One thing to know about binding

The fragment a provider exposes is fed straight into the consumer's
`System.Text.Json` deserializer — never re-serialized through the consumer type. The
happy path binds normally. The case to watch is **object → object** (the consumer takes
a whole object as an `@alias`): a missing value-type property silently defaults to `0`/
`false` instead of erroring. That is JSON duck-typing, and Sleipnir takes it seriously —
three opt-in binding modes let you make it loud:

- **Weak** (default) — duck-typed, silent defaults. The fan-out subset case above
  (`{Id, Name}` → `{Id}`) is safe and useful; the reverse direction is the hazard.
- **Strict** — every top-level public read-write property of the consumer must be
  present in the fragment, else `400`.
- **Paranoid** — Strict plus recursive descent into nested objects and array elements,
  checked for *all* parameters including literals.

For the chain on this screen (scalars and a `List<int>`), Weak is fine — there is no
object→object duck-typing. Reach for Strict/Paranoid the moment a consumer takes a whole
DTO via `@alias` and you want a missing field to fail loudly instead of silently
defaulting. Set `SleipnirOptions.AliasBindingMode`. Full spec: `DEPENDENCY_BINDING.md`.

### What you do NOT get

Sleipnir resolves **data dependencies within one request**. It does not run long-lived
workflows, it does not schedule, and it does not roll back. "Approve Order → debit
inventory → bill → notify" is a *command fan-out* (Story 03), and if one of those fails,
Sleipnir tells you *which one* and *why* — it does not compensate the ones that already
ran. That boundary is deliberate: Sleipnir is a request-time dependency resolver, not a
saga engine.

---

## Try it

**Standalone solution — open in Visual Studio, press F5:**

```
stories/01-n-plus-one-screen/Story01.sln
```

That boots a Sleipnir server with the six controllers above and an in-memory store (Order
#42), and the browser lands directly in the Developer UI at `/Sleipnir` (port 5001). The
DevUI lists the contract (code-first — the C# classes are the contract, no IDL) and lets
you build the batch interactively — including a dependency builder that catches the
object→object silent-default direction **statically** where both schemas are known. The
one-batch call from this story is in the story README (`stories/01-n-plus-one-screen/README.md`),
ready to paste into the DevUI batch sender. Source: `stories/01-n-plus-one-screen/Program.cs`
+ `Domain.cs`.

The controllers are code-first: drop them into any Sleipnir server, register via
`[SleipnirController]`, and the batch runs against `POST /api/sleipnir/json/multi`.

Next story: **One Button, Seven Commands** — when the pain isn't reading a screen but
*acting* on one, and a single user click has to fan out to many business operations.