# Trame Developer UI

The **Trame Developer UI** is the in-browser workbench that ships with every Trame
server. It is how you *see* a code-first contract, *call* a method, *build* a batch,
and *chain* dependencies — without writing a line of client glue. It is not a Swagger
page: there is no hand-written spec, because in Trame **the C# classes are the
contract**. Discovery is generated at runtime from the registered controllers, and the
DevUI renders it directly.

This walk-through uses **Story 01 — The N+1 Screen** as its running example: one
order-detail screen, one order (`Order #42`), and six dependent reads spread across
five services (`Order`, `Customer`, `OrderLine`, `Article`, `Address`, `Stock`). The
same six controllers live in `stories/01-n-plus-one-screen/Domain.cs`. If you boot that
solution, every screenshot below is reproducible verbatim.

> The DevUI is served by the Trame host at `/Trame` (configurable). It talks to the
> server over the native REST wire (`POST /api/trame/json`, `POST /api/trame/json/multi`,
> `GET /api/trame/discovery`). No backend rebuild is needed to use it — discovery and
> the batch sender are live against the running server.

---

## Run it (Story 01)

```
stories/01-n-plus-one-screen/Story01.sln
```

Open in Visual Studio and press **F5** (or `dotnet run --project Story01.csproj`). The
browser lands in the Developer UI at **`http://localhost:5001/Trame`**. Six controllers
are discovered from `Domain.cs`; order `#42` is in the in-memory store. The one-batch
call from the story is ready to paste (see *The Dependency Builder* below).

Against any other Trame server: open the DevUI it serves, or point this DevUI at a
cross-origin server from **Settings → Base URL / API path** (the target server must
enable CORS for the DevUI origin).

---

## Layout

<!--
SHOT: devui-01-overview.png
Scenario: Story01 running (http://localhost:5001/Trame), DARK theme. Full window.
  - Left pane (Explorer): six controllers listed (Order, Customer, OrderLine, Article,
    Address, Stock), with their methods visible (expanded). Types section below.
  - Center pane (Editor): a method tab open, e.g. Order.GetById — toolbar pill
    "Order.GetById", params count, "-- ms"; the JSON editor shows
    [{"parameterName":"id","data":42}].
  - Right pane (Result): Idle / empty ("Run a request to see the response here.").
  - Top bar visible (brand "Trame Developer", API-path pill "api/trame", Settings,
    Auth, Codegen, Dependency Builder, History, Refresh, theme toggle, Swagger).
  - Footer visible.
Show the three resizable panes and the top bar — this is the "you are here" shot.
-->
![DevUI overview — three panes, Story 01 loaded](docs/img/devui-01-overview.png)

The window is three resizable panes (drag the splitters; sizes persist):

| Pane        | Contents                                                                 |
|-------------|--------------------------------------------------------------------------|
| **Explorer** (left)   | **Discovery** tree (controllers → methods) with a filter box; **Types** tree (code-first contract schemas). |
| **Editor** (center)   | Tabs: per-method call tabs, the **Codegen** tab, and the **Dependency Builder** tab. |
| **Result** (right)    | Status / correlation ID / duration + the response JSON. |

A **History** dock toggles from the top bar. The top bar carries connection, auth,
codegen, and workspace controls (see *Around the workbench*).

---

## Discovery — the contract is the code

<!--
SHOT: devui-02-discovery.png
Scenario: close-up of the LEFT Explorer pane only (crop the rest). Story01, dark theme.
  - Filter box empty; "6 controllers" hint.
  - ControllerTree expanded showing all six controllers and their methods:
      Order.GetById, Customer.GetById, OrderLine.GetByOrder, Article.GetByIds,
      Address.GetById, Stock.GetByArticles.
  - Lower "Types" section expanded showing the contract types (Order, Customer,
    OrderLine, Article, Address, StockInfo) with their properties.
This proves the contract is inferred from the C# classes at runtime — no IDL.
-->
![Discovery pane — six controllers and their contract types](docs/img/devui-02-discovery.png)

The left pane is the live discovery view from `GET /api/trame/discovery`. Every
`[TrameController]` and `[TrameMethod]` the server registered is listed here, with the
contract types inferred from the method signatures (property schemas, nested types,
`[TrameExample]` samples, `[TrameDocumentation]` summaries). The filter box narrows by
controller or method name. **Refresh** re-fetches — important after setting a Bearer
token, since protected controllers only appear once authorized.

---

## A single call

<!--
SHOT: devui-03-single-call.png
Scenario: Story01, dark theme. A single-call tab for Order.GetById(42), AFTER Run.
  - Center toolbar pill "Order.GetById", "1 params", duration pill e.g. "12 ms".
  - JSON editor (or param editor) shows the request: [{"parameterName":"id","data":42}].
  - Right Result pane: status pill green "200", meta "Status: 200 / ID: … / Duration: 12 ms",
    and the response JSON body of the order, e.g.
    {"id":42,"customerId":7,"shippingAddressId":3,"status":"Placed","placedAt":"…"}.
Shows: pick a method → fill params → Run → result lands in the right pane.
-->
![Single call — Order.GetById(42) and its response](docs/img/devui-03-single-call.png)

Click a method in Discovery. A tab opens with a per-parameter editor and a raw JSON
editor (kept in sync). **Run** sends one `POST /api/trame/json`; the response lands in
the right pane with status, correlation ID, and duration. `Format` pretty-prints the
request, `Reset` restores defaults. This is the trivial path — one resource by id, one
roundtrip. The DevUI earns its keep on the next screen.

---

## Batches and dependency chaining

Trame's headline feature is **dependency chaining**: the client declares *what depends
on what*, the server resolves the graph in one roundtrip. The DevUI offers two entry
points:

- **In-tab Batch** — toggle *Batch* in any call tab to get a row-based batch editor with
  a Parallel/Serial mode switch and a live dependency graph. Good for quick multi-calls
  and light serial chains.
- **The Dependency Builder** — a dedicated tab (top-bar button) that is purpose-built for
  `@alias` chains: per-step controller/method, parameters as literal *or* `@alias`, an
  `Exposes` block (`alias ← JsonPath`), live validation, a static type-check, the
  generated client code, and a one-click execute. **This is the tool for Story 01.**

### The Dependency Builder

<!--
SHOT: devui-04-dependency-builder.png
Scenario: Story01, dark theme. The Dependency Builder tab (opened via top-bar
"Dependency Builder" button) with ALL SIX Story-01 steps filled in. Validation green
(no red "Validierung" box). Crop to the step list + the live Dependency Graph at the
bottom; the code-output section may be below the fold.
Steps (top to bottom), each a "Schritt":
  1. order      — Order.GetById       — param id = 42 (Wert)
                 Exposes: @customerId ← $.customerId, @orderId ← $.id,
                          @addressId ← $.shippingAddressId
  2. customer  — Customer.GetById    — param customerId = @customerId (Alias)
  3. lines     — OrderLine.GetByOrder — param orderId = @orderId (Alias)
                 Exposes: @articleIds ← $[*].articleId
  4. articles  — Article.GetByIds    — param articleIds = @articleIds (Alias)
  5. stock     — Stock.GetByArticles  — param articleIds = @articleIds (Alias)
  6. address   — Address.GetById      — param addressId = @addressId (Alias)
The "Mode: Serial (locked)" pill is visible. The live Dependency Graph shows the six
nodes (Order.GetById / Customer.GetById / OrderLine.GetByOrder / Article.GetByIds /
Stock.GetByArticles / Address.GetById) with the @alias edges between them.
This is THE screenshot of the feature.
-->
![Dependency Builder — the six-step Story-01 chain](docs/img/devui-04-dependency-builder.png)

Open the Dependency Builder from the top bar. **+ Schritt hinzufügen** adds a step. For
each step you pick a controller and method (free-text combobox — you can also type a
name not in discovery, with a raw-params fallback). Each parameter toggles between
**Wert** (literal) and **Alias** (an `@alias` from an earlier step's Exposes). The
**Exposes** block declares which fragments this step publishes for downstream steps:
`alias ← JsonPath`, where the path is **result-relative** (`$` is the whole serialized
result — `$.customerId`, `$.id`, `$[*].articleId`; there is no `$.data` envelope).

Validation is structural and **blocking** (red box): missing/duplicate ids, unresolved
`@alias` references, exposes without an alias. **Ausführen** stays disabled until the
graph is sound. Mode is locked to **Serial** here because `@alias` resolution requires
ordered execution (the server's topological path is auto-selected the moment a
`DependencyMapping` is present, so the wire `mode` is effectively ignored — but the
DevUI keeps Serial to be explicit).

A **live dependency graph** renders below the steps as soon as edges exist, so you see
the diamond (`Article.GetByIds` and `Stock.GetByArticles` both consume `@articleIds`)
before you send anything.

### Generated client code

<!--
SHOT: devui-05-codegen.png
Scenario: same Dependency Builder tab as shot 04, scrolled down to the "Code-Ausgabe"
section. The C# tab active (lang-tabs: TypeScript | C# | JSON, C# highlighted) and the
"Code kopieren" button visible. The code block shows the generated TrameMultiRequest
with six TrameRequest entries — Order/GetById with DependencyMapping (customerId/orderId/
addressId), Customer/GetById, OrderLine/GetByOrder with DependencyMapping (articleIds),
Article/GetByIds, Stock/GetByArticles, Address/GetById — all params as @alias strings,
Mode = ExecutionMode.Serial.
Shows: the visual builder produces copy-pasteable client code in three languages.
-->
![Generated client code — C# tab](docs/img/devui-05-codegen.png)

The same steps generate ready-to-paste client code in **TypeScript**, **C#**, or **JSON**
(the wire payload for `/api/trame/json/multi`). The JSON tab is the fastest way to reproduce
the story: copy it, open an in-tab Batch, and paste. The C# tab mirrors the
`TrameMultiRequest`/`TrameCall` fluent form from `stories/01-n-plus-one-screen/README.md`.

### One roundtrip, six responses

<!--
SHOT: devui-06-batch-result.png
Scenario: Story01, dark theme. The Dependency Builder tab AFTER clicking "Ausführen".
  - The inline "Ergebnis" box near the bottom shows a green "Batch OK" pill and the
    response JSON: an array of six responses in request order — order, customer,
    lines, articles, stock, address.
  - (Optional) the right Result pane also shows "Batch OK" + the same array.
Duration pill e.g. "23 ms". This is the payoff: six dependent reads, one roundtrip.
-->
![Batch result — six responses in one roundtrip](docs/img/devui-06-batch-result.png)

**Ausführen** sends the whole chain as one `POST /api/trame/json/multi`. The server
orders the graph topologically, runs independent calls in parallel, binds each
`@alias` from the prior result that exposed it, and returns the six responses in
request order. The client never extracted a single id; the latency is one network hop
plus intra-server parallelism, not six serial hops.

---

## Catching silent defaults (static type-check)

<!--
SHOT: devui-07-typecheck.png  (OPTIONAL — constructed example, NOT the Story-01 batch)
Scenario: Story01, dark theme, Dependency Builder tab with a DELIBERATELY BROKEN step
added to trigger the non-blocking amber "Typ-Check (nicht blockierend)" box.
Construction: add a step that Exposes a whole OBJECT (e.g. expose "@order" ← "$" from the
order step) and a consumer whose parameter is an object type that is MISSING a property
the provider carries, OR a cross-kind scalar (expose a string, consume into an int
without AllowReadingFromString). The amber box lists the issue(s) with the "where"
prefix (step / param) and a message; "Ausführen" stays enabled (non-blocking — runtime
shape may differ from the static schema). Show the amber box with at least one item.
Note in caption: the canonical Story-01 chain is all scalars + List<int> (Weak-safe),
so this box stays empty on it; this shot is a constructed example to show what it flags.
-->
![Static type-check — non-blocking warnings on a constructed mismatch](docs/img/devui-07-typecheck.png)

A provider's exposed fragment is fed straight into the consumer's
`System.Text.Json` deserializer — never re-serialized through the consumer type. The
happy path binds normally; the case to watch is **object → object**, where a missing
value-type property silently defaults to `0`/`false` instead of erroring. Where the
DevUI has both schemas (provider return + consumer parameter, from discovery), it
reproduces the binding rules **statically** and surfaces them as a non-blocking amber
box: cross-kind scalars, object→object subset/missing/kind-mismatch, array/scalar
cardinality. **Send anyway stays available** — the runtime shape may legitimately differ
from the static schema. The Story-01 chain is all scalars and one `List<int>`, which is
Weak-safe, so on the canonical batch this box is empty; the shot above is a constructed
mismatch to show what it catches. Full spec: `DEPENDENCY_BINDING.md`; the three opt-in
binding modes (Weak / Strict / Paranoid) are a server-side setting
(`TrameOptions.AliasBindingMode`).

---

## Around the workbench

The top bar holds the controls that surround the three panes:

- **API-path pill** — the active target (`api/trame` same-origin, or your custom base/ path).
- **Settings** — *Connection* (Base URL, API path; cross-origin needs CORS) and
  *Workspace* (**Export** saves connection + tabs + theme + layout + history as JSON,
  **no Bearer**; **Import** restores it, including the endpoint).
- **Auth** — a Bearer token; applying it re-fetches discovery so protected controllers
  appear. A green dot marks an active token.
- **Codegen** — opens a standalone code-generation tab.
- **Dependency Builder** — opens the chaining tab (above).
- **History** — toggles the history dock.
- **Refresh** — re-fetches discovery.
- **Theme** — dark / light, persisted.
- **Swagger** — link to the host's Swagger page if present.

<!--
SHOT: devui-08-workbench.png  (OPTIONAL)
Scenario: Story01, dark theme. The Settings panel open (top-bar gear) showing the
Connection section (Base URL "/ (Same-Origin)…", API path "api/trame", Reset/Apply) and
the Workspace section (Export/Import). Optionally the History dock also toggled open on
the right/bottom showing two past entries (the Order.GetById single call + the batch)
with timestamps, duration, and a snippet. Combine to show "everything around the panes".
-->
![Top bar — Settings (connection + workspace) and History](docs/img/devui-08-workbench.png)

### History

The History dock lists every executed call — single and batch — with its request,
response (or error), and duration. It is the local audit trail of what you actually
sent during a session.

---

## Where to look next

- **Story 01** (`docs/stories/01-the-n-plus-one-screen.md`) — the full N+1 reasoning,
  the REST-vs-Trame comparison, and the one-batch call this walk-through reproduces.
- **`DEPENDENCY_BINDING.md`** — the binding spec (Weak / Strict / Paranoid, casing
  regimes, the four runtime outcomes).
- **`PROTOCOL.md`** — the wire format, `@alias` serialization, and type-binding summary.
- **`README_DETAILS.md`** — user-facing dependency-chaining reference.
- The standalone solution: `stories/01-n-plus-one-screen/Story01.sln`.