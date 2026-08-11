# Sleipnir — Best Practices

**How to set up, structure, and operate a Sleipnir server well: secure configuration, binary and
stream handling, controller/method naming conventions, and how Sleipnir sits next to conventional
REST.**

This is the engineering guide. For the security posture specifically see
[SECURITY_GUIDE.md](SECURITY_GUIDE.md); for the wire format [PROTOCOL.md](PROTOCOL.md); for
step-by-step setup [GETTING_STARTED.md](GETTING_STARTED.md); for the feature tour
[README.md](README.md).

---

## 1. Set up and operate securely

### 1.1 Authenticate upstream, enforce in Sleipnir

Sleipnir reads `HttpContext.User`; it does not run an identity provider. Put your auth scheme
(JWT bearer, cookies, mTLS terminated at the reverse proxy) **before** the Sleipnir transport so
that `HttpContext.User` is populated when the invoker's `CheckAuthorisation` runs. Then flip
the default-deny toggle:

```csharp
builder.Services.AddSleipnir(new SleipnirOptions
{
    RequireAuthentication = true,
    RateLimitPermitLimit  = 20,
    MaximumBatchSize      = 16,
    MaxDependencyPathLength = 128,
    AllowRecursiveDescent = false,
    EnableDetailedErrors  = builder.Environment.IsDevelopment(),
});
```

Full posture matrix, transport gates, and the deployment checklist:
[SECURITY_GUIDE.md](SECURITY_GUIDE.md).

### 1.2 Two error channels — use the right one

Controllers signal failure two ways, and confusing them is the most common Sleipnir mistake:

- **Business / domain errors → return `SleipnirResponse`** via the `SleipnirResults` factory:
  `SleipnirResults.NotFound("…")`, `SleipnirResults.BadRequest("…")`, `SleipnirResults.Error(code, message, details?)`,
  `SleipnirResults.Ok(obj)`. The invoker passes it through verbatim — your `code`, `data`, and
  `error` reach the client unchanged, and the message is **not** gated by `EnableDetailedErrors`.
- **Unexpected / internal failures → throw.** Any throw becomes a generic `500` with no message
  leak; the stack lands in `error.details` only in Development.

Do **not** throw `SleipnirException` to set a custom code — the server has no `catch(SleipnirException)`;
it becomes a generic `500`. Control the code via `SleipnirResults.Error(...)`.

Because business-error messages always reach the client, keep them free of sensitive context
(internal ids, connection strings, stack snippets). Treat the message text as public output.

### 1.3 Controller lifetime and DI

Controllers are resolved **per call** via `IServiceScopeFactory.CreateScope()` — the invoker is a
singleton, controllers are scoped. Inject scoped services through the constructor; do not store
per-request state in controller fields. `CancellationToken` is injected automatically into any
method that declares it — pass it through to downstream work so batches and long calls stay
cancellable.

### 1.4 Environment and the Developer UI

- Run **Production** for anything exposed: `ASPNETCORE_ENVIRONMENT = Production`. This turns off
  `EnableDetailedErrors` (no stack leaks).
- The Developer UI (`/Sleipnir`, served by `MapSleipnir`) is a dev tool. **Do not ship it to
  untrusted clients** — omit it in production or put it behind auth. It is invaluable internally
  (browse the contract, build batches visually, keep many call tabs open, codegen, history,
  workspace snapshots) but it is not a production surface.

### 1.5 Host and proxy

Sleipnir caps the framework layer (1 MB REST body, 1 MB WS message, cardinality caps). The **global**
limits are the host's responsibility:

```csharp
builder.WebHost.ConfigureKestrel(k =>
{
    k.Limits.MaxConcurrentConnections = 200;
    k.Limits.MaxConcurrentUpgradedConnections = 100;
    k.Limits.MaxRequestBodySize = 2_000_000;
});
```

Put a reverse proxy (nginx, Caddy, App Gateway) in front for TLS termination, header filtering,
and — importantly for WebSocket — **connection-rate limiting** (the WS transport is branch
middleware, so per-connection rate limiting belongs to the proxy, e.g. `limit_conn`). Configure
CORS with a named, scoped policy if browser clients call you cross-origin; avoid `*`.

### 1.6 Compression — enable at the transport, not in Sleipnir

Sleipnir does **not** compress payloads itself — compression is transport infrastructure and is
handled at the host/proxy layer. This keeps Sleipnir's wire format clean and lets you tune
compression per deployment without framework code. The options, by transport:

**REST (HTTP).** Use ASP.NET Core's response compression middleware (gzip/brotli). It honors
`Accept-Encoding` and compresses JSON responses automatically:

```csharp
builder.Services.AddResponseCompression(o =>
{
    o.EnableForHttps = true;            // safe with TLS after .NET 7
    o.Providers.Add<BrotliCompressionProvider>();
    o.Providers.Add<GzipCompressionProvider>();
});
var app = builder.Build();
app.UseResponseCompression();           // before MapSleipnir
```

A reverse proxy (nginx `gzip on;`, Caddy `encode gzip zstd`) does the same at the edge and is
the recommended north-bound choice — it keeps the app process free of compression CPU.

**WebSocket.** Enable `permessage-deflate` (RFC 7692) on Kestrel — it negotiates compression per
WS connection transparently:

```csharp
builder.WebHost.ConfigureKestrel(k =>
{
    k.ConfigureWebSocketOptions(o => o.AllowedCompression = WebSocketCompressionFlags.All);
});
```

The Sleipnir WS transport inherits this; no Sleipnir-side change is needed. Note: `permessage-deflate`
adds per-message overhead for very small frames — for tiny high-frequency calls it may *hurt*;
profile before enabling broadly.

**SignalR.** MessagePack is already compact (binary, no base64 for `byte[]`). For JSON-hub
mode, ASP.NET Core's hub response compression applies. Sleipnir's `UseMessagePack = true` is the
single most effective "compression" for SignalR.

**What *not* to do.** Don't add a Sleipnir-side compression layer that double-compresses with the
transport (wasteful) or competes with `Content-Encoding` (confusing for clients). Don't
compress the small Sleipnir envelope when the large `data` is already compressed by the transport.
The transport layer is the right place; Sleipnir stays out of it.

---

## 2. Binary and streams

### 2.1 `byte[]` travels out of band

A method may take or return `byte[]`. Bytes never sit inside the JSON `data` field — they travel
out of band (`binaryData` on the request, `content` on the response):

| Transport | `binaryData` (request) | `content` (response) |
|-----------|------------------------|----------------------|
| REST (JSON)              | base64       | base64       |
| WebSocket (JSON text)    | base64       | base64       |
| SignalR (MessagePack)    | native `bin` | native `bin` |

**Practice.** Keep binary payloads small and infrequent through Sleipnir — they are capped at the
1 MB body / message limit, and base64 over REST/WS inflates by ~33%. For large or frequent
binary (file upload, media, bulk export), run a **plain REST or WebSocket endpoint alongside
Sleipnir** and let it stream bytes directly. Sleipnir's value is the call graph and the typed contract;
it is not a bulk-transfer channel.

If you need `byte[]` native (no base64) and you control both ends, use the SignalR transport with
`UseMessagePack = true`.

### 2.2 `IAsyncEnumerable<T>` is materialized on the wire

A method returning `IAsyncEnumerable<T>` is consumed server-side into a `List<T>` and serialized
as a JSON array. That is **not** streaming on the wire — the client receives the whole array once
the enumeration completes. Two consequences:

- **Bound it.** `MaxResultElementCount` (default 10 000) caps the materialized list at the source.
  Raise it deliberately; do not disable it (`0`) for untrusted callers — an unbounded stream is a
  memory DoS.
- **For true streaming semantics** (chunked delivery, backpressure, long-running producers), use
  the WebSocket or SignalR transport's native streaming channel, or a side-by-side streaming
  endpoint. Do not hold a Sleipnir batch open for a minutes-long producer — dependency chaining is
  for bounded request-time work.

> There is no on-the-wire streaming for REST in v1. WebSocket/SignalR are the streaming
> transports. See [ROADMAP.md](ROADMAP.md) for native streaming evolution.

---

## 3. Naming conventions — command-oriented, J2EE-style

Sleipnir dispatches by `"{Controller}_{Method}"` — **name only, no parameter-based overload
resolution**. Two `[SleipnirMethod]`s on the same controller, or two `[SleipnirController]`s app-wide,
must have distinct names or `Register` throws at startup (no silent shadowing). Naming is
therefore not cosmetic; it is part of the dispatch contract.

### 3.1 Controller = noun, method = verb

Sleipnir is **command-oriented**, not resource-oriented. Mirror that in the names:

- **Controller** = the domain noun / aggregate (`Customer`, `Order`, `Invoice`, `Loyalty`).
- **Method** = the action verb (`Create`, `GetById`, `Update`, `Delete`, `Search`, `Cancel`,
  `Approve`).

```csharp
[SleipnirController("Customer")]
public class CustomerController(CustomerService service)
{
    [SleipnirMethod("GetById")]   public Task<Customer?>  GetById(int id, CancellationToken ct) => service.Get(id, ct);
    [SleipnirMethod("Create")]    public Task<int>        Create(string name)                   => service.Add(name);
    [SleipnirMethod("Update")]    public Task             Update(Customer c)                     => service.Save(c);
    [SleipnirMethod("Delete")]    public Task             Delete(int id)                         => service.Remove(id);
}
```

This is the J2EE/EJB convention translated to code-first: the session-facade method set was always
`create` / `findByPrimaryKey` / `findBy<Criteria>` / `update` / `remove`, grouped on the entity.
Here the controller *is* the entity facade, and the `[SleipnirMethod]` names are the verbs.

### 3.2 CRUD baseline

Adopt a small, consistent verb set so the contract is predictable across controllers:

| Operation | Method name | Notes |
|-----------|-------------|-------|
| Create    | `Create`    | returns the new id / entity |
| Read one  | `GetById`   | by primary key |
| Read many by id | `GetByIds` | bulk by primary key — `WHERE id IN (...)` / batched multi-get, one store roundtrip. The intra-service n+1 killer; the chain's collection path ([§4.6](#46-the-service-layer-is-the-seam--share-the-bulk-not-the-transport)). |
| Read many by criteria | `Search` / `FindOpen` / `GetByCustomer` | finder-style, see §3.3 |
| Update    | `Update`    | full or patch — your domain decides |
| Delete    | `Delete`    | by id |

`GetById` and `GetByIds` are the single / bulk pair on the primary key — name the bulk one
`GetByIds`, not the generic `GetMany` (which reads as "many, by what?"). Reads by a foreign key
or other criterion are **finders**, not bulk-PK reads: name them `GetByOrder`, `GetByArticles`,
`GetByCustomer` ([§3.3](#33-finders--variant-reads-get-distinct-names-not-overloads)). The
distinction matters on the wire: `GetByIds` is the symmetric bulk a dependency chain binds a
multi-match `@alias` into; `GetBy*` is a finder keyed by some other field.

### 3.3 Finders — variant reads get distinct names, not overloads

Because Sleipnir does not resolve overloads by signature, "get customer by X" variants are
**separate method names**, in the EJB `find*` tradition:

```csharp
[SleipnirMethod("GetById")]     public Task<Customer?>  GetById(int id)               => …;
[SleipnirMethod("GetByEmail")]  public Task<Customer?>  GetByEmail(string email)      => …;
[SleipnirMethod("FindOpen")]    public Task<List<Order>> FindOpen(int customerId)     => …;
[SleipnirMethod("Search")]      public Task<List<Customer>> Search(string term, int page, int size) => …;
```

If you need C#-style "overloads," model them with distinct names (`Add`, `AddRange`, `Create`,
`CreateBatch`). This is explicit on the wire and unambiguous in discovery.

### 3.4 Domain verbs beyond CRUD

The strength of command-orientation is that the real verbs surface. Name methods after the
business operation, not after HTTP:

```csharp
[SleipnirController("Order")]
public class OrderController(OrderService s)
{
    [SleipnirMethod("Place")]    public Task<int>            Place(int customerId, List<int> articleIds) => s.Place(customerId, articleIds);
    [SleipnirMethod("Cancel")]   public Task                 Cancel(int orderId, string reason)          => s.Cancel(orderId, reason);
    [SleipnirMethod("Approve")]  public Task                 Approve(int orderId)                        => s.Approve(orderId);
    [SleipnirMethod("Reopen")]   public Task                 Reopen(int orderId)                         => s.Reopen(orderId);
}
```

`Place`, `Cancel`, `Approve` are the language your domain uses — keep it. There is no need to bend
them into `POST /orders/{id}/cancel` resource-sub-path thinking; the method name *is* the
operation.

### 3.5 Versioning

There is no built-in API versioning in v1. Encode the version in the controller name
(`Customer.v1`, `Customer.v2`) so old and new contracts coexist on one server. Keep the method
names stable across versions; add new methods rather than reshaping old ones.

---

## 4. Interplay with REST

### 4.1 Two models, one host

Conventional REST is **resource-oriented**: endpoints are nouns, the HTTP verb carries the
operation, status is the HTTP status. Sleipnir is **command-oriented**: the method name carries the
operation, everything is a `POST` to `/api/sleipnir/json` (or `/json/multi`, or a WebSocket/SignalR
frame), and status lives in the body `code` (envelope-at-200 — see
[SECURITY_GUIDE.md §5](SECURITY_GUIDE.md)).

They coexist cleanly on one host: map your REST endpoints with `app.MapControllers()` / minimal
APIs as usual, and map Sleipnir with `app.MapSleipnir()`. Use each for what it does well:

| Reach for… | when |
|------------|------|
| **Plain REST** | a single resource by id; cacheable `GET`s; proxy- and curl-friendly ops surfaces; webhook receivers; large binary uploads/downloads. |
| **Sleipnir** | a screen or action that needs multiple dependent calls in one roundtrip (dependency chaining), command fan-out with per-call isolation, a typed contract shared across REST/WebSocket/SignalR, or a .NET-to-.NET binary channel (SignalR+MessagePack). |

### 4.2 When the Sleipnir batch beats the REST loop — and where the win actually is

A client that needs `Order → Customer + Lines → Articles + Address` over plain REST pays six
serial roundtrips and owns the call graph (extract ids, order calls, dedupe). The same screen as
a Sleipnir batch is one roundtrip with the dependencies declared; the server runs the topological
graph and binds `@alias` values itself. See [docs/stories/01-the-n-plus-one-screen.md](docs/stories/01-the-n-plus-one-screen.md).

The rule of thumb: **if there is a graph, use Sleipnir; if there is a row, use REST.** A single
`GET /api/orders/{id}` with no dependencies is already one roundtrip — Sleipnir adds nothing. A
screen with four dependent reads is where the batch earns its keep.

#### Two n+1 problems, two different owners

The "n+1" the Story is named after has two layers, and confusing them produces a pitch a REST
defender can dismantle:

| n+1 layer | Example | Who fixes it | REST | Sleipnir |
|-----------|---------|---------------|------|-------|
| **Intra-service** | n articles fetched as n+1 single calls instead of one `GetByIds(ids)` | the developer (API design) | build a bulk endpoint | build a bulk endpoint |
| **Inter-service** | Order → Customer → Lines → Articles, call *k* needs call *k−1*'s output | the framework (orchestration) | client writes the glue, 6 roundtrips | declared graph, 1 roundtrip |

A bulk endpoint kills the *intra-service* n+1 — and a REST developer can build the same endpoint,
so it is **not** a Sleipnir argument. What no bulk endpoint can kill is the *inter-service* n+1:
call *k*'s inputs come from call *k−1*'s outputs, so the chain cannot be parallelized or bulked
away. That cross-service orchestration glue — extract an id, await, pass it on, repeat — is
exactly what Sleipnir takes off the client and collapses into one roundtrip with server-side graph
resolution. **That is the framework win, and it is the one REST cannot structurally match.**

So: keep designing bulk endpoints exactly as you would for REST (`GetByIds`, `GetByOrder`,
`Search`). Sleipnir does not replace them; it replaces the *imperative glue between them*. The two
levers are complementary — bulk endpoints (dev) remove the intra-service n+1, Sleipnir (framework)
removes the inter-service n+1 and the roundtrips it forces. This is also why Sleipnir drops into an
existing REST API without rewriting endpoints: the endpoints stay, the client-side glue goes.

#### Where the speedup comes from — be precise

The batch is not magic parallelism over your data. Know which lever moves the number, because
the levers have different ceilings and different owners:

- **Roundtrip count — framework, the strong axis.** A REST client that must fan out without a
  bulk endpoint pays O(n) roundtrips; the Sleipnir batch is O(1) roundtrip — the server resolves the
  whole graph in one request. This is the axis a REST client cannot match: even a `Promise.all`
  client still issues n requests over the network and owns the fan-out. As RTT grows the batch
  advantage grows toward the roundtrip-count ceiling (6× in the Story's six-call chain).
- **Server-side parallelism across batch requests — framework.** Independent requests in the
  batch run in parallel on the server — the Story's diamond (`Article.GetByIds` and
  `Stock.GetByArticles` off the same `@articleIds`) runs as one layer, not two. This is wall-clock
  parallelism **across requests**, bounded by the server's concurrency cap and the cardinality
  caps (`MaxParameterArrayLength`, `MaxResultElementCount`): realistically O(n/c) for a wide
  fan-out, not unbounded O(1), and n itself is capped.
- **Wall-clock parallelism alone — not Sleipnir-unique.** A REST client can parallelize client-side
  and also reach O(1) wall-clock — but it pays the n roundtrips and writes the orchestration. Do
  not pitch "Sleipnir makes it parallel"; a REST defender will correctly reply that `Promise.all`
  does too. Pitch the roundtrip collapse and the server-side graph resolution, which REST cannot.
- **Per-call work inside one method — developer, not framework.** If each article costs 100 ms
  inside `GetByIds` and the method iterates sequentially, both REST and Sleipnir pay n·100 ms for that
  call — Sleipnir parallelizes *requests in a batch*, not *elements inside one request*. To fan n
  articles out into parallel batch requests, declare n `Article.GetById` calls with per-article
  `@alias` (the server then parallelizes them), or parallelize inside the controller method.
  Either is a developer decision, available in both worlds; it is not a framework feature.

The honest headline, then, is not "Sleipnir parallelizes your reads" but **"Sleipnir collapses the
cross-service call graph to one roundtrip and runs its independent branches server-side; you
keep designing the bulk endpoints."** Pitch the roundtrip axis and the removed orchestration glue
— those survive the counterexample.

### 4.3 Status semantics differ — bridge deliberately

When you mix the two, remember clients experience two status regimes:

- **REST**: the HTTP status is the status (`200`, `404`, `422`, `500`).
- **Sleipnir native**: HTTP is always `200`; the body `code` is the status. Per-method auth failure
  is HTTP 200 + `code:401`. Framework-level gates (discovery, batch cap, WS upgrade) return real
  HTTP 401/400 because they reject before the invoker.

If a single client talks to both, handle them separately: REST by HTTP status, Sleipnir by body
`code`. `SleipnirClient` already does this for you (`SleipnirException` on a non-2xx body `code`).

### 4.4 Discovery as the Swagger alternative

`GET /api/sleipnir/discovery` returns the full contract — controllers, methods, parameter types,
examples, documentation — generated from your code at runtime. Types are structured,
language-neutral `TypeRef` objects (`kind` ∈ `scalar | array | set | map | ref | stream | opaque |
void`) versioned by an additive-only `discoveryVersion` field; see
[`docs/discovery-schema.md`](docs/discovery-schema.md) for the spec. For internal consumers this
replaces the Swagger/OpenAPI page; the Developer UI (`/Sleipnir`) is the interactive browser over
it. For external consumers, gate discovery behind auth (it is an attack-surface oracle — see
[SECURITY_GUIDE.md §2](SECURITY_GUIDE.md)) and hand clients the contract through your normal docs
channel, or let authenticated clients fetch it.

### 4.5 Migrating incrementally

You do not have to pick one model for a whole app. A common path: keep existing REST resources
untouched, add Sleipnir for the new screens that need batching or chaining, and let both run on the
same host. Clients migrate screen by screen. The JSON-RPC 2.0 compat adapter
(`EnableJsonRpcCompat = true`) lets an existing JSON-RPC client drive the Sleipnir contract
unchanged, as an adoption lure before graduating to the native wire for `@alias` chaining and
binary. See [JSONRPC_COMPAT.md](JSONRPC_COMPAT.md).

### 4.6 The service layer is the seam — share the bulk, not the transport

The reason Sleipnir drops into an existing REST API without rewriting endpoints is structural,
not cosmetic: **REST controllers and Sleipnir controllers are both thin facades over the same
service (or repository). The bulk logic lives in the service; the transports are interchangeable
endpoints above it.** Design the service once, expose it twice. That is the pattern.

#### The shape: Query → GetById / GetByIds, in the service

The chain-friendly endpoint shape from [§4.2](#42-when-the-sleipnir-batch-beats-the-rest-loop--and-where-the-win-actually-is)
is a *service* shape first. Put the single read, the bulk read, and the id-only query on the
service — then both transports get them for free:

```csharp
public interface ArticleService
{
    // Read one — single primary key. The chain's single-shot path
    // (Order.GetById exposes customerId → Customer.GetById(@customerId)).
    Task<Article?> GetById(int id, CancellationToken ct);

    // Read many — bulk, one store roundtrip (WHERE id IN (...) / batched multi-get).
    // The intra-service n+1 killer. The chain's collection path
    // (OrderLine exposes articleIds → Article.GetByIds(@articleIds)).
    Task<IReadOnlyList<Article>> GetByIds(IReadOnlyCollection<int> ids, CancellationToken ct);

    // Query → ids only. Cheap, indexable; the filtering/sorting lives here, not in the hydrate.
    Task<IReadOnlyList<int>> FindIdsByOrder(int orderId, CancellationToken ct);
}
```

Two thin facades over the same service — no duplicated logic:

```csharp
// REST facade (minimal API) — thin
app.MapPost("/api/articles/by-ids",
    async (int[] ids, ArticleService s, CancellationToken ct) =>
        Results.Ok(await s.GetByIds(ids, ct)));

// Sleipnir facade — thin, same service
[SleipnirController("Article")]
public class ArticleController(ArticleService s)
{
    [SleipnirMethod("GetById")]
    public Task<Article?> GetById(int id, CancellationToken ct)
        => s.GetById(id, ct);

    [SleipnirMethod("GetByIds")]
    public Task<IReadOnlyList<Article>> GetByIds(IReadOnlyCollection<int> ids, CancellationToken ct)
        => s.GetByIds(ids, ct);
}
```

#### Migrating an existing REST controller

The mechanical path onto Sleipnir, when the REST service is already well-factored: take the
controller, **remove the routing** (`[HttpGet]`, `[Route]`, minimal-API lambdas), **re-declare
the same methods with `[SleipnirController]` / `[SleipnirMethod]`**, and **leave the service calls
unchanged**. The controller body barely moves — the verbs were already domain verbs
(`GetById`, `Place`, `Cancel`) or become so in the rename (see [§3.1](#31-controller--noun-method--verb)).
If you built the REST service behind a clean interface, both facades sit on it verbatim.

```text
Before (REST)                         After (REST + Sleipnir, same service)
─────────────────────                 ──────────────────────────────────
[ApiController]                       [SleipnirController("Article")]      // new facade
[Route("api/articles")]              public class ArticleController(ArticleService s)
public class ArticleController(       {
    ArticleService s)                     [SleipnirMethod("GetById")]  … => s.GetById(id, ct);
{                                        [SleipnirMethod("GetByIds")] … => s.GetByIds(ids, ct);
    [HttpGet("{id}")]                 }   // s.GetById / s.GetByIds — unchanged
    public Task<Article?> GetById(int id)
        => s.GetById(id);             // REST facade stays too — same s.GetById / s.GetByIds
    …
}
```

Both run on one host ([§4.1](#41-two-models-one-host)); clients migrate screen by screen.

#### Rules that make the seam hold

1. **One service method → two facades, no duplication.** The intra-service n+1 (n articles as
   n+1 single calls instead of one `GetByIds`) is a *REST* problem too — so `GetByIds` belongs
   on both transports. If you build the bulk only for Sleipnir, your REST client still pays the n+1.
   The service is the seam; the transports are interchangeable endpoints above it.
2. **`GetByIds` must be deterministic.** Same length as the input, in input order (or id-sorted
   with a documented convention), with a sentinel/`null` for a missing id rather than gap-closing.
   A downstream consumer that binds `$[*].articleId` → `GetByIds(@articleIds)` correlates by
   position; a reordering or a dropped row desyncs the whole chain. This is a service discipline
   that affects both transports — not Sleipnir-specific.
3. **The query returns ids, not entities.** `FindIdsByOrder` / `Search` returns `IReadOnlyList<int>`
   (cheap, indexable); the hydrate is a separate `GetByIds` call. Splitting "which" from "what"
   is what lets the Sleipnir chain declare `query → hydrate` as a dependency and lets the bulk
   hydrate run server-side. (See [§3.3](#33-finders--variant-reads-get-distinct-names-not-overloads)
   for the finder naming.)

#### The streaming boundary — where REST stays, deliberately

Sleipnir is a complement to REST, not a replacement. The call graph and the typed contract are
Sleipnir's job; **large or streaming binary is not**. `byte[]` over Sleipnir is for small, infrequent,
graph-relevant binary (a profile thumbnail, an avatar) — capped at the 1 MB body/message limit,
base64-inflated ~33% over REST/WS. For images, files, media, bulk export, or anything that
genuinely streams, run a **plain REST or WebSocket endpoint alongside Sleipnir** and let it stream
bytes directly (see [§2.1](#21-byte-travels-out-of-band), [§2.2](#22-iasyncenumerablet-is-materialized-on-the-wire)).
The line: *small + typed + part of the graph* → Sleipnir; *large + streaming* → REST/WS side-by-side.
Both sit on the same host, both above the same services where the bulk logic lives.