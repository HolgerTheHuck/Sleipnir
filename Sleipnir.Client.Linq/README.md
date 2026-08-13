# Sleipnir.Client.Linq

A LINQ-provider client for [Sleipnir](https://github.com/HolgerTheHuck/Sleipnir) that builds typed,
dependency-chained batch calls from lambdas over **generated service-contract interfaces**.

## Why

The stock generated client wires `@alias` dependency chains with a hand-typed JsonPath string and an
untyped `Alias` placeholder that converts into *any* `Arg<T>` — so a placeholder wired into the wrong
parameter type compiles fine and fails at runtime. `Sleipnir.Client.Linq` closes that gap at compile time:

- `Dep<T>` is a typed alias marker. `create.Expose()` returns `Dep<int>`; `GetById(Arg<int>)` accepts it.
  `AddNote(Arg<string> note)` does **not** accept a `Dep<int>` — the compiler rejects the wiring.
- `Expose(o => o!.Status)` builds the result-relative JsonPath from a selector expression, so the path
  cannot be mistyped. The wire name is read from the DTO's `[JsonPropertyName]` (falling back to the
  `CamelCase` policy the server uses), so it cannot drift from the wire.

Two tiers ship in this package:

| Tier | Surface | What it types |
|------|---------|----------------|
| **1** | `Build` / `Expose` / `SleipnirBatch` / `Dep<T>` | a single `@alias`-wired call chain — which `Dep<T>` fits which `Arg<T>` |
| **2** | `From` / `Include` / `ThenInclude` / `Where` / `Build` / `Materialize` | a whole eager-load navigation graph — which navigation is selected, against which leaf type |

## Usage — Tier 1 (a typed `@alias` chain)

Generate the contract interfaces with the companion `sleipnir-linq` dotnet tool (from a
`contract.sleipnir.json` or a discovery URL), then:

```csharp
using Sleipnir.Client.Linq;
using Sleipnir.Linq.Contracts; // generated IOrderService, Order, CreateOrderDto, …

var linq = new SleipnirLinqClient(restClient);

var create = linq.Build((IOrderService c) => c.Create(new CreateOrderDto { CustomerId = 7 }));
Dep<int> orderId = create.Expose();                       // "$"  → Dep<int>

var fetch  = linq.Build((IOrderService c) => c.GetById(orderId));
Dep<string> status = fetch.Expose(o => o!.Status);        // "$.status" → Dep<string>

var batch = new SleipnirBatch(create, fetch);
var responses = await linq.SendAsync(batch);
var order = linq.ResultOf<Order>(fetch, responses);
```

The wire model is exactly Sleipnir's native `SleipnirMultiRequest` with `dependencyMapping` + `@alias`
parameters — no server changes, no new transport.

## Usage — Tier 2 (eager-load a navigation graph)

Tier 2 is an EF-Core-shaped façade over the *same* native wire. `.From` starts from a collection-root
method; `.Include`/`.ThenInclude` declare navigation edges; `.Build` compiles them client-side into a
plain `@alias`/`dependencyMapping` multi-request; `.Materialize` stitches the flat per-node response
lists back into the nested client-side graph. **The server sees no query** — it only ever receives the
ordinary `@alias`-wired multi-request, and because that request carries `dependencyMapping` it
auto-selects the topological batch executor (Kahn). `Mode=Serial` gives sibling-`Include` parallelism for
free.

```csharp
using Sleipnir.Client.Linq;
using Sleipnir.Linq.Contracts; // generated ICustomerService, Customer, Kontakt, …

var linq = new SleipnirLinqClient(restClient);

// 1. Root: a collection-root method (Task<List<Customer>?>). TEntity=Customer is known from the
//    contract, so the whole chain is compile-checked.
var query = linq.From((ICustomerService c) => c.SelectCustomers())
    // 2. .Include = a navigation off the root. c => c.Kontakt is compile-checked (Kontakt must exist
    //    on Customer); [SleipnirNavigation] on that property supplies the fetch edge.
    .Include(c => c.Kontakt)
    // 3. .ThenInclude = continue the chain off the current leaf. After .Include(c => c.Kontakt) the
    //    leaf is Kontakt, so k => k.Ansprechpartner is checked against Kontakt.
    .ThenInclude(k => k.Ansprechpartner)
    // 4. A sibling .Include goes back to the root (EF parity):
    .Include(c => c.Bestellungen);

// 5. .Build compiles the chain client-side into a normal @alias/dependencyMapping multi-request.
SleipnirMultiRequest batch = query.Build();
var responses = await linq.SendAsync(batch);

// 6. .Materialize stitches the flat per-node lists into the nested graph:
//    Customer → Kontakt (reference, 1:1) → Ansprechpartner (collection, 1:n),
//    Customer → Bestellungen (collection, 1:n).
List<Customer> customers = linq.Materialize(query, responses);
```

For the 3-hop chain above, the server receives exactly this (no query shape — only `@alias` wiring):

```json
{
  "requests": [
    { "controller": "Customer", "method": "SelectCustomers",
      "dependencyMapping": { "nav0": "$[*].kontaktId", "nav2": "$[*].id" } },
    { "controller": "Customer", "method": "GetKontakte",
      "params": [{ "parameterName": "kontaktIds", "data": "@nav0" }],
      "dependencyMapping": { "nav1": "$[*].id" } },
    { "controller": "Customer", "method": "GetAnsprechpartner",
      "params": [{ "parameterName": "kontaktIds", "data": "@nav1" }] },
    { "controller": "Customer", "method": "GetBestellungen",
      "params": [{ "parameterName": "customerIds", "data": "@nav2" }] }
  ],
  "mode": "Serial"
}
```

### What is compile-checked vs. drift-checked

| Aspect | How it is verified |
|--------|--------------------|
| **Which** navigation (`c => c.Kontakt`) | compile-time — the selector lambda against the contract type |
| `.ThenInclude` off a **collection leaf** (`b => b.Positions` after `.Include(c => c.Bestellungen)`) | compile-time — a covariant `ISleipnirQuery<out TEntity, out TLeaf>` + two `ThenInclude` overloads (EF's `IIncludableQueryable<,>` trick) check the lambda against the *element*, not the collection |
| A mistyped leaf (`ThenInclude(k => k.Bestellungen)` after `.Include(c => c.Kontakt)`) | compile error — no applicable overload |
| **How** to fetch (`Fetch`/`Key`/`ChildKey`/`Param`) | drift-checked via codegen — strings generated from the server model and validated against the contract at generation time |

### `[SleipnirNavigation]`

Each navigation property on the contract DTO carries `[SleipnirNavigation]`, emitted by `EmitContracts`
from the server-side model:

```csharp
public class Customer
{
    public int Id { get; set; }
    public int? KontaktId { get; set; }

    // Reference nav: one Kontakt, joined Customer.KontaktId → Kontakt.Id.
    [SleipnirNavigation(Fetch = "Customer.GetKontakte", Key = "kontaktId", Param = "kontaktIds")]
    public Kontakt? Kontakt { get; set; }

    // Collection nav: many Bestellungen, joined Customer.Id → Bestellung.CustomerId.
    [SleipnirNavigation(Fetch = "Customer.GetBestellungen", Key = "id", Param = "customerIds")]
    public List<Bestellung>? Bestellungen { get; set; }
}
```

- `Fetch` — `"Controller.Method"` of the fetch method (split at the last dot).
- `Key` — the per-element key on the **parent**, as a wire name. The façade composes the full
  result-relative JsonPath from the parent's cardinality; Tier 2 is collection-root, so it is
  `$[*].{Key}` (e.g. `$[*].kontaktId`, `$[*].id`). Not a wildcard string.
- `ChildKey` — optional: the child property (wire name) that joins back to the parent. Conventions
  applied when omitted: **reference** navigation → child PK `"id"`; **collection** navigation → child
  FK `"{parentEntityName}Id"` (camelCase, e.g. `"customerId"`). Set it explicitly only when the convention
  does not hold.
- `Param` — the fetch method's parameter name that receives the key list; the façade wires the parent's
  exported alias into this parameter as `@alias`. Required (codegen validates and emits it).

`.Where` is sugar over the root-method parameters — supported operators are `==` and `&&` only (there is
no query engine; the method *is* the filter):

```csharp
var q = linq.From((IOrderService c) => c.Search(0, ""))
            .Where(o => o.Status == "open" && o.Region == "EU");
```

## Nullable ergonomics

Generated DTO properties are nullable (`int? Id`), so `Expose(o => o.Id)` yields `Dep<int?>`, which does
not satisfy `Arg<int>`. Use the null-forgiving operator: `Expose(o => o.Id!)` → `Dep<int>`.
