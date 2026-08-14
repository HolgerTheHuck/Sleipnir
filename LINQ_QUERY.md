# Sleipnir.Client.Linq — Tier 2: `SleipnirQuery<T>` (typed `.Include`/`.ThenInclude` over a known return)

> Status: **implemented**. Tier 1 (`SleipnirLinqClient.Build`, `Dep<T>`, `SleipnirBatch`,
> `EmitContracts`) and Tier 2 (`SleipnirQuery<T>` — `.From`/`.Include`/`.ThenInclude`/`.Where`/`.Build`/
> `.Materialize`) are both shipped, and the §8 one-declaration pipeline is complete: the server-side
> `[SleipnirNavigation]` (SleipnirCommon) flows through discovery (`navigation` field) into the
> `sleipnir-linq` codegen, which drift-checks each edge and emits the client-side `[SleipnirNavigation]`
> onto the contract DTOs — so generated clients drive `.Include(...)` without hand-annotation.
> See `README_DETAILS.md` → "Dependency Chaining" and `DEPENDENCY_BINDING.md` for the underlying
> `@alias`/`dependencyMapping` mechanism this builds on.

## 1. Goal & the reframe

Tier 1 made a single `@alias` chain compile-time type-safe (`Dep<int>` only fits `Arg<int>`). The
ergonomics are still explicit: the author writes each `SleipnirCallSpec`, calls `Expose(selector)`, and
threads `Dep<T>` into the next call's `Arg<T>` by hand. Tier 2 removes that hand-threading for the common
case — eager-loading related data — with an EF-Core-shaped fluent API.

The design rests on one reframe, established in the design discussion:

> **The entry point is always a controller-method call (RPC), not an entity set (DB/EF). The return type
> is statically known from the generated contract.** Therefore the entire `.Include`/`.ThenInclude` type
> progression is checked client-side over a known DTO graph; the server never sees an expression tree and
> is never asked to evaluate a query. "Filtering" is parameter binding on the chosen method, which the
> server already does (params matched by name). No query engine exists or is needed.

What the server *does* see is unchanged: a `SleipnirMultiRequest` of plain method calls wired with
`@alias`/`dependencyMapping` — exactly the Tier-1 wire. The façade compiles the fluent chain into that
wire; the topological batch executor (`DependencyGraphBuilder`, Kahn) resolves the `@alias` edges. **Tier 2
is a client-side compile-time layer over the existing RPC wire.** It adds no server semantics.

## 2. Consumer surface

Against generated contracts (`Sleipnir.Linq.Contracts`, from `EmitContracts`):

```csharp
using Sleipnir.Client.Linq;
using Sleipnir.Linq.Contracts;

var linq = new SleipnirLinqClient(restClient);

// Root: a controller-method call. Return type is known (List<Customer>), so TEntity = Customer,
// collection-root. Args bind the method's params (compile-checked via Arg<T>), same as Tier-1 Build.
var q = linq.From((ICustomerService c) => c.SelectCustomer(10, "hallo"));

// Eager-load a navigation. c : Customer is the known return element; Kontakt must be a property of
// Customer (compile-checked). Reads [SleipnirNavigation] on Customer.Kontakt for the fetch edge.
// Leaf advances Customer -> Kontakt.
var q1 = q.Include(c => c.Kontakt);

// ThenInclude operates on the current leaf (Kontakt), not the root. Ansprechpartner must be a
// property of Kontakt (compile-checked). Leaf advances Kontakt -> Ansprechpartner.
var q2 = q1.ThenInclude(k => k.Ansprechpartner);

// Optional: bind further root-method params via an eq-predicate (see §7). Sugar over method args.
// var q3 = q2.Where(c => c.Region == "EU");

// Compile the chain into a multi-request batch (3 nodes, @alias edges) and send.
SleipnirMultiRequest batch = q2.Build();
IReadOnlyList<SleipnirResponse> responses = await linq.SendAsync(batch);

// Materialize the nested graph client-side (see §6): List<Customer>{ Kontakt{ Ansprechpartner } }.
List<Customer> customers = linq.Materialize<Customer, Kontakt, Ansprechpartner>(q2, responses);
```

### Type progression (the EF parallel — but client-only)

| API | Type after | Selector checked against |
|-----|-----------|--------------------------|
| `From((ICustomerService c) => c.SelectCustomer(...))` | `SleipnirQuery<Customer>` | method exists on `ICustomerService` (Tier-1 `Build`) |
| `.Include(c => c.Kontakt)` | `SleipnirQuery<Customer, Kontakt>` | `Kontakt` is a property of `Customer` |
| `.ThenInclude(k => k.Ansprechpartner)` | `SleipnirQuery<Customer, Ansprechpartner>` | `Ansprechpartner` is a property of `Kontakt` |
| `.Include(c => c.Bestellungen)` | `SleipnirQuery<Customer, Bestellung>` | sibling from the **root** `Customer` (EF parity) |

`SleipnirQuery<TEntity>` (no leaf) is the post-root state; `SleipnirQuery<TEntity, TLeaf>` is the state
after ≥1 navigation. `.Include` always navigates from `TEntity`; `.ThenInclude` from `TLeaf`. Every
selector is `Expression<Func<TPrev, TNext>>` with `TPrev` a known contract DTO, so the compiler enforces
the whole chain. This is exactly EF's `IIncludableQueryable<TEntity, TProperty>` trick, obtained for free
from the known return type — with the crucial difference that the expression trees are evaluated **only on
the client at `.Build()` time** and never serialized to the server.

## 3. The navigation attribute and how it flows

A navigation edge needs three facts the lambda cannot carry: which server method fetches the related
set, which property on the parent is the key, and which property on the child joins back. These are
**declared once on the server DTO** and flow through codegen to the client attribute — the same pattern
as `[SleipnirController]` (server, `SleipnirCommon`) → `[SleipnirServiceContract]` (client,
`Sleipnir.Client.Linq`) via `EmitContracts`.

### Server-side declaration (`SleipnirCommon`, new)

```csharp
// On the real server DTO property. Consumed by SleipnirDiscoveryService → serialized into DiscoveryInfo.
[AttributeUsage(AttributeTargets.Property)]
public sealed class SleipnirNavigationAttribute : Attribute
{
    /// <summary>"Controller.Method" of the fetch method, e.g. "Kontakt.GetByKontaktIds".</summary>
    public string Fetch { get; init; }

    /// <summary>The per-element key path on the PARENT (one element), e.g. "kontaktId" or "id".
    /// The façade composes the full result-relative JsonPath from the parent query's cardinality
    /// (collection root -> "$[*].{Key}"; single root -> "$.{Key}"). NOT a wildcard string.</summary>
    public string Key { get; init; }

    /// <summary>Optional: the child property that joins back to the parent key. Convention defaults:
    /// reference navigation -> child PK "Id"; collection navigation -> child FK "{ParentEntity}Id".
    /// Required only when the convention does not hold.</summary>
    public string? ChildKey { get; init; }

    /// <summary>Optional: the fetch method's parameter name that receives the key list. Inferred as
    /// the method's single Arg<List<_>> parameter when omitted.</summary>
    public string? Param { get; init; }
}
```

```csharp
// Server DTO (the real model)
public class Customer
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int? KontaktId { get; set; }

    [SleipnirNavigation(Fetch = "Kontakt.GetByKontaktIds", Key = "kontaktId")]
    public Kontakt? Kontakt { get; set; }              // reference navigation
}
```

### Client-side attribute (`Sleipnir.Client.Linq`, new — the emitted one)

`CsContractsEmitter` emits an attribute with the **same name** in the `Sleipnir.Client.Linq` namespace
onto the corresponding contract DTO property. The façade reads *this* one. The two attributes are
distinct types in different namespaces; codegen translates server → wire JSON → client, mirroring the
existing controller/service-contract split.

```csharp
// Emitted onto Sleipnir.Linq.Contracts.Customer.Kontakt by EmitContracts:
[SleipnirNavigation(Fetch = "Kontakt.GetByKontaktIds", Key = "kontaktId")]
public Kontakt? Kontakt { get; set; }
```

### Why the `Key` is a (codegen-generated) string, not a lambda

The navigation *selector* (`c => c.Kontakt`) is compile-checked — it identifies which property, so
`Kontakt` must exist on `Customer`. The `Key`/`Fetch`/`ChildKey` are strings, so not C#-compile-checked,
**but they are codegen-generated from the server model and drift-checked against the contract** — exactly
as `[JsonPropertyName]` already is. The split is deliberate and consistent with the existing design:

- **which navigation** → compile-checked (the selector lambda).
- **how to fetch it** → drift-checked (codegen-generated attribute strings, validated at generation time).

Codegen validates, at emission, that `Key` names a real property of the parent DTO whose element type
matches the `Fetch` method's collection-parameter element type (see §8). A mismatch is a generation-time
error, not a runtime one.

## 4. Wire compilation — `.Build()` → `SleipnirMultiRequest` (export + alias)

This is the heart of Tier 2 and the mechanism the design discussion converged on: **each navigation edge
becomes one node in the batch; the parent node *exports* its key via `dependencyMapping` (the existing
expose), and the child node *consumes* it as an `@alias` placeholder in its fetch method's parameter.**
The façade reuses the same safe-alias logic as `SleipnirCallSpec.ExposePath` (aliases stay `[A-Za-z0-9_]+`
for `DependencyGraphBuilder.ExtractAliases`).

For the running example `From(SelectCustomer(10,"hallo")).Include(Kontakt).ThenInclude(Ansprechpartner)`
(collection root), `.Build()` produces:

| # | Request (Controller.Method) | consumes | exports (`dependencyMapping`) |
|---|----------------------------|----------|-------------------------------|
| 0 | `Customer.SelectCustomer(10, "hallo")` | — | `{alias0 → "$[*].kontaktId"}` (from `Customer.Kontakt` nav) |
| 1 | `Kontakt.GetByKontaktIds(@alias0)` | `@alias0` → its `kontaktIds` param | `{alias1 → "$[*].id"}` (from `Kontakt.Ansprechpartner` nav) |
| 2 | `Ansprechpartner.GetByKontaktIds(@alias1)` | `@alias1` → its `kontaktIds` param | — |

Three properties fall out of the existing engine:

- **Request count = number of entity types loaded, not row count.** Each edge fans out into a *list
  parameter* (`GetByKontaktIds(@alias0)` is one request), never into N requests — the "list fan-out into a
  *parameter*, never into N *requests*" rule from `DEPENDENCY_BINDING.md`. 10 or 10 000 customers → still
  3 requests. (Requires the server to offer collection-fetch methods; see §10.)
- **A node is both consumer and provider.** Node 1 consumes `@alias0` *and* exports `alias1`. The
  `SleipnirRequest` model already carries `Params` (with `@alias` placeholders) and `DependencyMapping`
  side-by-side, so this is first-class, not a special case.
- **Sibling `.Include` branches parallelize.** `…Include(c => c.Kontakt).Include(c => c.Bestellungen)`
  makes node 0 export two aliases (`kontaktId` and `id`); nodes Kontakt and Bestellungen both depend only
  on node 0 → Kahn groups them in the **same parallel batch**. The topological executor is already a DAG
  executor; `.Include` breadth and `.ThenInclude` depth are the same problem shape it already solves. A
  cyclic navigation graph is detected by Kahn and surfaces as the existing cycle error.

`AliasN` naming follows `SleipnirCallSpec.ExposePath`: `{safeId}__dep{n}` with the id sanitized to
`[A-Za-z0-9_]`, so the server's `ExtractAliases` recognizes every edge.

### Single-root (degenerate case — out of Tier-2 scope, noted)

When the root returns a single `Customer` (not `List<Customer>`), the parent key path is `$.kontaktId`
(scalar), which would feed a single-id fetch (`GetKontakt(int)`) rather than a list fetch. Tier 2
targets **collection-root** queries (the common eager-loading case); single-root navigation needs a
second fetch-method variant in the attribute and is deferred (§10).

## 5. The query-node model (internal)

```csharp
internal sealed class QueryNode
{
    public SleipnirCallSpec Spec;                 // the method call producing this entity set
    public Type EntityType;                        // Customer / Kontakt / Ansprechpartner
    public bool IsCollection;                       // root: from Task<List<E>?> vs Task<E?>
    public NavigationEdge? Outgoing;               // the edge to the next node (null on the leaf)
}

internal sealed class NavigationEdge
{
    public string FetchController, FetchMethod;   // from [SleipnirNavigation].Fetch
    public string KeyPath;                          // composed: "$[*].kontaktId" (collection) / "$.kontaktId" (single)
    public string FetchParam;                      // inferred (single Arg<List<_>>) or from [SleipnirNavigation].Param
    public string Alias;                            // the @alias this node exports for the next node to consume
    public string ChildKey;                        // join-back property on the child (stitch)
}
```

`SleipnirQuery<TEntity>` / `SleipnirQuery<TEntity, TLeaf>` hold the root `QueryNode` plus the ordered
list of navigation edges (one per `.Include`/`.ThenInclude`). `.Build()` walks the list: for each edge it
(1) registers `dependencyMapping[aliasN] = keyPath` on the *provider* node's spec, (2) constructs the
*consumer* node's `SleipnirRequest` directly (`Controller`/`Method` from the edge, one `SleipnirParameter`
with `Data = JsonValue.Create("@" + aliasPrev)`), and (3) chains it. The root spec is produced by the
existing `SleipnirLinqClient.Build` (so root param binding reuses Tier 1 verbatim); fetch specs are built
directly from attribute metadata (the fetch method is named by a string, so no typed lambda is possible
here — the attribute's codegen-time validation (§8) is what makes that safe).

## 6. Client-side materialization (the stitcher)

`@alias` resolves the *fetch* (the right rows come back), but the **nesting** is client-side: the batch
returns one flat list per node; the stitcher joins children onto parents by the edge key. For each edge
in order:

- reference navigation (`Customer.Kontakt`, parent carries the FK): for each parent, set
  `parent.Nav = childWhere(child.ChildKey == parent.Key)`. Key = `parent.KontaktId`, ChildKey = child `Id`
  (convention) or declared.
- collection navigation (`Customer.Bestellungen`, child carries the FK): for each parent, set
  `parent.Nav = childrenWhere(child.ChildKey == parent.Id)`. Key = `parent.Id`, ChildKey = child
  `CustomerId` (convention) or declared.

The stitcher reflects over the **known contract DTO types** (`TEntity`, `TLeaf`, …) — all properties are
known — and deserializes each node's response via the same `JsonSerializerOptions` as
`SleipnirLinqClient.Deserialize<T>`. Because the graph is a DAG and edges are processed in topological
order, a child stitched onto a parent is itself already stitched to its own children (ThenInclude
depth). `Materialize<TEntity, …TLeaves…>(query, responses)` is the typed entry; the variadic leaf type
list mirrors the include chain so the return type is `List<TEntity>` with the declared navigations set.

> The nullable-DTO papercut (Tier 1: generated props are `int? Id`) reappears at each navigation step —
> `Kontakt? Kontakt` is a nullable reference. The façade accepts `Expression<Func<TPrev, TProp?>>` and
> null-forgives internally, so the consumer writes `k => k.Ansprechpartner`, not `k => k!.Ansprechpartner`.
> (This is the same papercut documented in the Tier-1 README; a future non-nullable-DTO emission would
> retire it everywhere at once.)

## 7. `.Where` — parameter binding (not row filtering)

`.Where(c => c.Region == "EU" && c.Id == 10)` is an `Expression<Func<TEntity, bool>>` interpreted on
**only** `==` / `&&` / member-access / constants. Each `c.Prop == value` clause binds the root method's
parameter whose **wire name** matches the property's wire name (via `[JsonPropertyName]`, CamelCase
fallback) to `value`. Type-safe: `c.Region` must be a real `Customer` property and `value` must match its
type; a property with no matching method parameter is a clear runtime error at `.Build()` (no silent
drop). This is *parameter binding*, so the server filters however the method implements it — the method
**is** the filter. No query engine is invoked.

This is sugar: the primary binding is the method call in `From(...)`, which is already compile-checked via
`Arg<T>`. `.Where` earns its place only when binding is to be expressed separately/composed; for the
common case `c.SelectCustomer(10, "hallo")` already binds `id`/`name` with zero new machinery. Out of
scope for `.Where`: any operator beyond `==`/`&&` (no `<`, `>`, `Contains`, `StartsWith` — those would need
a server query engine or dedicated filter methods).

## 8. Codegen + discovery changes (the one-declaration pipeline)

The server declares the navigation once; it flows to the client attribute with no second maintenance
point:

1. **`SleipnirCommon`** — add `[SleipnirNavigation]` (server-side, on DTO properties) and a
   `Navigation` field on the discovery property model (`{propertyName, propertyType, navigation?}`).
2. **`SleipnirCore`/`SleipnirDiscoveryService`** — when a property carries `[SleipnirNavigation]`,
   serialize `navigation: {fetch, key, childKey?, param?}` into `DiscoveryInfo`. (This is where the
   "relationship is known to the server" lives; the server has the model, discovery just reports it.)
3. **`Sleipnir.Codegen.Core`**:
   - `DiscoveryShape.Assert` accepts an optional `navigation` object on properties (forward-compat —
     contracts without it still validate).
   - `EmitterBuilder` resolves it into a new `ResolvedProperty.Navigation` (`Fetch`/`Key`/`ChildKey`/`Param`).
   - **Codegen-time validation** (the drift-check that makes string keys safe): assert that `Key` names a
     real property of the parent DTO, that its element type equals the `Fetch` method's collection-parameter
     element type, and that the `Fetch` controller+method exists among the resolved controllers with a
     matching collection param. A violation is a **generation-time error** (the contract drifts; refuse
     to emit), not a runtime failure.
   - `CsContractsEmitter` emits the client `[SleipnirNavigation(...)]` on the contract DTO property.
4. **`Sleipnir.Client.Linq`** — add the client-side `SleipnirNavigationAttribute` (the emitted target),
   `SleipnirQuery<TEntity>` / `SleipnirQuery<TEntity, TLeaf>`, `.From/.Include/.ThenInclude/.Where/.Build`,
   the `QueryNode`/`NavigationEdge` model, the `@alias` assembly (reusing `ExposePath` safe-alias logic),
   and the stitcher.

The `Sleipnir.SourceGenerator` link-compiles `Sleipnir.Codegen.Core\*.cs` via an explicit file list (see
`Sleipnir.SourceGenerator.csproj`) — any new core file (e.g. a navigation resolver) must be added to that
list, or the generator build breaks with CS0103 (the same fix applied for `CsContractsEmitter.cs` in Tier 1).

## 9. Worked example (full)

```csharp
// Server DTO (declared once):
public class Customer {
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int? KontaktId { get; set; }
    [SleipnirNavigation(Fetch = "Kontakt.GetByKontaktIds", Key = "kontaktId")]
    public Kontakt? Kontakt { get; set; }
}
public class Kontakt {
    public int Id { get; set; }
    [SleipnirNavigation(Fetch = "Ansprechpartner.GetByKontaktIds", Key = "id")]
    public List<Ansprechpartner>? Ansprechpartner { get; set; }   // collection nav: Key = parent's own Id
}
public class Ansprechpartner { public int Id { get; set; } public string Name { get; set; } = ""; }

// Client (generated contract carries [SleipnirNavigation] on the same props):
var q = linq.From((ICustomerService c) => c.SelectCustomer(10, "hallo"))
            .Include(c => c.Kontakt)            // ref nav: parent.kontaktId -> Kontakt.GetByKontaktIds
            .ThenInclude(k => k.Ansprechpartner); // coll nav: kontakt.id -> Ansprechpartner.GetByKontaktIds
var responses = await linq.SendAsync(q.Build());
var customers  = linq.Materialize<Customer, Kontakt, Ansprechpartner>(q, responses);
// customers[i].Kontakt.Ansprechpartner[j] populated; 3 requests total regardless of row count.
```

## 10. Limitations & out of scope

- **No server query engine.** `.Where` binds params only (`==`/`&&`); no `<`/`>`/`Contains`/`OrderBy`/
  `Skip`/`Take`. Those need dedicated server methods, not a LINQ provider — and adding them is a server
  contract decision, separate from this façade.
- **Collection-root only.** Single-root navigation (one `Customer` → its `Kontakt`) needs a single-id
  fetch variant in the attribute; deferred. The common eager-load case is collection-root.
- **Requires collection-fetch server methods.** Each edge assumes a `GetBy…Ids(List<int>)`-style method.
  If only `GetById(int)` exists, the edge cannot fan out into one list param and the façade refuses to
  build with a clear error (the codegen validation in §8 catches this at generation time when the fetch
  method is declared).
- **`ChildKey` convention.** Reference nav assumes child PK `Id`; collection nav assumes child FK
  `{ParentEntity}Id`. Non-conventional joins require explicit `[SleipnirNavigation(ChildKey = …)]`.
- **Cyclic navigations** surface as the existing Kahn cycle error (a navigation cycle creates a request
  cycle). The façade does not special-case this; the server's topological detector does.
- **No streaming/binary/void through the façade** (same as Tier 1; use the transport client directly).

## 11. Implementation plan & verification

Two independent pieces, each valuable alone:

**Piece A — wildcard-`Expose` + multi-hop chain (pure client, on Tier 1).** Extend `JsonPathBuilder` to
build `$[*].kontaktId` from an expression (a `Select`/element-projection shape) so explicit
`SleipnirBatch` chains of any depth are ergonomic without the façade. The server already supports
`$[*]` multi-match (`DependencyResolver`); only the client emit is missing. This unblocks ad-hoc chains
today and is reused by the façade's explicit-Expose path (though the façade itself uses declared key
strings — §3 — so it does not *depend* on Piece A).

**Piece B — `SleipnirQuery<T>` + navigation model (the façade).** The full §8 pipeline + §5/§6 runtime.
Depends on the server-side `[SleipnirNavigation]` + discovery field; the client façade can be built and
unit-tested against hand-annotated contract DTOs before the codegen pipeline lands.

**Verification:**
1. `Unit/Client/Linq/QueryBuildTests` — `.Build()` produces the exact `SleipnirMultiRequest` (controller/
   method/`@alias`/`dependencyMapping` per the §4 table) for 1/2/3-hop and sibling-`.Include` chains;
   assert alias names are `[A-Za-z0-9_]+`.
2. `Unit/Client/Linq/NavigationAttributeTests` — the façade reads `[SleipnirNavigation]` correctly,
   composes collection (`$[*].key`) vs single (`$.key`) key paths, and infers `FetchParam`/`ChildKey`.
3. `Integration/LinqQueryChainTests` — against `TransportTestFixture` with a dedicated
   `QueryChainController` (Customer → Kontakt → Ansprechpartner, deterministic in-memory data):
   `From(SelectCustomers).Include(Kontakt).ThenInclude(Ansprechpartner)` round-trips a nested graph in
   one batch; `Materialize` produces the correctly stitched objects.
4. `Unit/Codegen/ContractsEmitterNavigationTests` — feed a discovery with `navigation` fields to
   `EmitContracts`; assert the emitted `[SleipnirNavigation(...)]` and that a key/param type mismatch is a
   generation-time `DiscoveryShapeException`/error (the drift gate).
5. Compile-time-safety: `ThenInclude(k => k.NotAProperty)` does not compile (the selector is
   `Expression<Func<Kontakt, _>>`); `Include(c => c.Kontakt)` on a non-navigation property is rejected at
   `.Build()` with a clear error (no `[SleipnirNavigation]` on the property).
6. Existing `CsCodegenParityTests` green (the new optional `navigation` field is forward-compat;
   `EmitClient` is untouched).