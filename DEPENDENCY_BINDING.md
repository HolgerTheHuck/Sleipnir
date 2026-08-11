# Dependency Binding — Alias Transfer & Type Mapping

> This is the authoritative, precise specification of how a provider's result becomes a
> consumer's argument through an `@alias`. It is the dedicated chapter for the JSON mapping
> between provider and receiver. The executable spec lives in
> [`SleipnirTests/Unit/Core/AliasBindingTests.cs`](SleipnirTests/Unit/Core/AliasBindingTests.cs);
> the short landing-page version is in [README.md](README.md#safety), the protocol-level
> summary in [PROTOCOL.md](PROTOCOL.md#alias-serialization--type-binding).

Dependency chaining exchanges **typed JSON fragments** between commands inside one batch.
A provider exposes a fragment from its result via a result-relative JsonPath
(`.Exposes("$.id", "orderId")`); a later command references it as a placeholder
(`.WithAlias("@orderId")`). The server resolves placeholders **before** the consumer runs.
This document specifies exactly what crosses the wire, step by step, and what happens when
the shapes do not match.

---

## 1. The binding pipeline

For each `@alias` parameter the server runs three steps, in order:

### Step 1 — Extract

`DependencyResolver.ExtractValue` evaluates the provider's JsonPath against the provider's
**serialized result** — a JSON document in **camelCase** (the server writes results with
`JsonNamingPolicy.CamelCase`). The match count determines the shape (this is the rule the
DevUI checker reproduces statically):

| JsonPath match count | Extracted value |
|---|---|
| **0** | `null` — the alias is left unset. |
| **1** | The matched node **as-is**. A scalar yields the scalar; if the match itself is an array or object, that array/object is yielded whole (e.g. `$` over `{"items":[1,2,3]}` yields the array `[1,2,3]`). |
| **>1** (`$[*].id`, `$..id`) | A JSON **array** of all matches. |

The extracted `JsonNode` is re-encoded as a JSON string in `exposedDependencies` via
`extracted.ToJsonString()` — this preserves the JSON *kind* (number/bool/string/array/object)
for the next step. `ToJsonString`, not `ToString`, is essential: a number `7` becomes `"7"`,
a string `alice` becomes `"\"alice\""`, an object becomes `"{...}"`.

### Step 2 — Inject

In the consuming request, `@alias` placeholders inside `params` are replaced. The
placeholder is recognized as a **native `string` value whose text starts with `@`** in the
`params` array. Concretely, a consumer parameter carrying an alias looks like this on the
wire (`data` is a native JSON string, not a JSON-string-wrapped value):

```json
[{"parameterName":"orderId","data":"@orderId"}]
```

Note `data` is the **raw text `@orderId`** — a native JSON string, not a double-quoted
`"\"@orderId\""`. The detector (`ContainsAlias`) checks `value.StartsWith("@")`; a value
that starts with `"` is not recognized as an alias and would be deserialized as a literal
string (and then likely fail binding). The replacement parses the JSON text stored in step
1 into a native `JsonNode` and installs it as the parameter's `data`, so after substitution
the field carries the native fragment value (`7`, `"alice"`, `[1,2,3]`, `{...}`) — not a
JSON string.

### Step 3 — Bind

`BuildParameters` deserializes each parameter's `data` into the method's declared parameter
type via `JsonSerializer.Deserialize(data, parameterType, options)`, using the **same options
as every other call**:

- `PropertyNameCaseInsensitive = true`
- `PropertyNamingPolicy = CamelCase` (for writing)
- **no** `JsonNumberHandling.AllowReadingFromString`

The fragment is **never re-serialized through the consumer type and back**. It is the exact
JSON the provider produced, fed straight into the consumer's deserializer. That is why
object→object binds by duck-typing and why a type mismatch surfaces as a deserialization
failure rather than a silent conversion.

---

## 2. The four outcomes

Step 3 has exactly four outcomes. They are the contract.

| Fragment → Parameter | Runtime behavior | Response |
|---|---|---|
| **Compatible** — same JSON kind, or object→object with overlapping properties | Binds normally | **2xx** |
| **Cross-kind scalar** — JSON number into `string`, JSON string into `int`, bool into number, object/array into scalar | `System.Text.Json` throws | **400** `Parameter 'X' cannot be converted to type 'Y'.` |
| **object → object, missing property** | Duck-typed: overlapping props bind case-insensitively, **extra** provider props ignored, **missing** props silently defaulted (value types → `0`/`false`/`DateTime.MinValue`, reference types → `null`) | **2xx** (silent default) — **Weak** only; **Strict** turns the top-level missing case into a 400 (alias params); **Paranoid** turns *every* missing property — at any depth, in any parameter including literals — into a 400 (see §7) |
| **Unresolved** — JsonPath matched 0 nodes, or no prior request exposed that alias | No value to bind | **400** `Unresolved dependencies: alias.` (Serial / dangling-alias path). On the **topological** path, a provider that fails or does not expose is caught *before* this point — see [§9](#9-provider-failure--dependent-propagation). |

### What is accepted silently (genuine widening, not coercion)

Within the **same JSON kind**, `System.Text.Json` widens silently when the value fits:
`int`→`long`/`double`/`decimal` (all JSON-number kind). Parseable string conversions
(`"…"`→`Guid`/`DateTime`/`Uri`/`TimeSpan` when the format parses) also bind. These are
*conversions within a kind*, not cross-kind coercion.

### What is rejected

Cross-kind is a hard 400. There is **no silent string↔number coercion**: because
`AllowReadingFromString` is off, `"42"`→`int` and `42`→`string` are **both** rejected.
Narrowing/lossy conversions (`3.5`→`int`, overflow) are rejected too.

---

## 3. object → object — duck-typing, and why it is directional

This is the one binding failure the server does **not** raise. `System.Text.Json` maps
properties that match by name (case-insensitively), ignores extra properties, and silently
defaults missing ones. The behavior is **directional**:

### Safe direction — consumer ⊆ provider (the useful pattern)

Provider `{Id, Name}` → consumer `{Id}`. The consumer declares only `Id`; the fragment has
`Id`; `Id` binds; the extra `Name` is ignored. **Nothing is missing, nothing is defaulted.**
This is the **subset fan-out** pattern (see §4) and it is safe.

### Dangerous direction — consumer ⊋ provider (silent default)

Provider `{Id}` → consumer `{Id, Active}`. `Active` is a **value type** the fragment does
not carry. STJ silently sets `Active = false`, the call returns **2xx with wrong business
data**. Missing *reference-type* properties default to `null` (usually visible quickly);
missing *value-type* properties are the **insidious case** — `0`, `false`, `DateTime.MinValue`
— no error, no log, just a wrong result.

This is inherent to JSON duck-typing. Sleipnir has no runtime schema to enforce structural
equality, by design: the C# classes **are** the contract, discovered at runtime. The DevUI
dependency builder catches the dangerous direction **statically** where both schemas are
known (see §6); the runtime stays lenient by default, with optional **Strict** and
**Paranoid** modes that turn the silent default into a loud 400 (specified in §7).

---

## 4. Subset fan-out — one alias, many typed consumers

The silent-drop direction is a **feature**, not only a hazard. Load a `Customer` once,
expose the whole object as a single alias, and feed the **same** `@customer` into several
subsequent commands whose parameter types are each shaped to receive only the fields they
need:

```csharp
SleipnirCall.Init("Customer", "Get")          // → { id, name, ... }
    .Exposes("$", "customer")              // one alias, whole object
    .ToRequest(),

SleipnirCall.Init("Billing", "ChargeById")    // param: CustomerId { public int Id; }
    .WithAlias("@customer")
    .ToRequest(),

SleipnirCall.Init("Directory", "Label")       // param: CustomerName { public string Name; }
    .WithAlias("@customer")
    .ToRequest()
```

Each consumer duck-types the overlapping property; the rest silently drop. One provider
fragment, many typed consumers, no per-field exposes.

**The one rule that makes this work:** each consumer **parameter must be an object type** —
a class declaring the wanted property the same way the provider does. If a consumer
parameter were a **bare scalar** (`int id`, `string name`), injecting the whole `{…}` object
is the cross-kind row of §2 → **400**, not a silent drop. For bare scalars, expose per field
instead (`$.id` → `@cid`, `$.name` → `@cname`).

---

## 5. Casing contract

.NET and JavaScript handle casing differently, and Sleipnir sits between them. There are
**three independent casing regimes**, each applying to a different part of the call:

| Regime | Applies to | Casing | Consequence |
|---|---|---|---|
| **Parameter NAME binding** | matching `params` entries to method parameters | **case-sensitive, ordinal** | `{"parameterName":"CustomerId"}` binds to `int CustomerId`, **not** to `int customerId`. Send the exact C# parameter name. |
| **Parameter VALUE properties** | JSON properties inside an object argument or result | **read case-insensitive, written camelCase** | STJ reads `{"Id":1}` and `{"id":1}` into `int Id` equally. The server *writes* results camelCase (`id`, `customerId`). |
| **JsonPath extraction** | the `.Exposes("$.…", …)` path against a result | **case-sensitive** | The path runs against the camelCase wire document, so it must use camelCase: `$.customerId`, `$.items[*].id`. A PascalCase `$.Id` matches **nothing** → `Unresolved`. |

### Cross-language consequences

- **JS reading C# results** — works without effort. The server emits camelCase, which is
  what JS expects: `result.id`, `result.customerId`.
- **JS sending object arguments to C#** — works either way. STJ reads property names
  case-insensitively, so `{id: 1}` and `{Id: 1}` both bind to `int Id`. Send camelCase for
  consistency with the wire.
- **C# reading JS-sent arguments** — works either way, same reason.
- **JsonPath in `.Exposes(...)`** — must be camelCase. This is the one place
  case-sensitivity bites, because the path is evaluated against the already-serialized
  camelCase document, not against the C# property names. The DevUI suggests camelCase paths
  for exactly this reason.

### Why not drop case-sensitivity entirely?

It is tempting but not achievable in one place. Parameter *names* are matched by a
case-sensitive dictionary key for dispatch determinism — Sleipnir dispatches by name only (no
parameter-based overloading), so a case-insensitive name match would introduce ambiguity
with no schema to resolve it. Parameter *values* go through STJ's case-insensitive property
matching, which is the right behavior for JSON payloads. The two regimes are kept separate;
each is internally consistent.

---

## 6. DevUI static checker

The dependency builder in the developer UI (`/Sleipnir`) is the one place that has **both**
schemas — the provider's return type and the consumer's parameter type — from discovery. It
runs a static type/casing/structural check
([`SleipnirDeveloperUi/src/lib/utils/dependencyCheck.ts`](SleipnirDeveloperUi/src/lib/utils/dependencyCheck.ts))
that reproduces the runtime rules above **before** execution:

- **Expose paths** validated against the provider's return schema — a PascalCase `$.Id` is
  flagged because it will not match the camelCase wire output.
- **`@alias` bindings** checked against the provider's exposed shape:
  - cross-kind scalar mismatch → **error** (will 400);
  - object→object, missing *value-type* property → **warn** (silent default — the insidious case);
  - object→object, missing *reference* property → **warn** (will be `null`);
  - object→object, kind mismatch on an *overlapping* property → **error** (will 400);
  - object→object, consumer ⊆ provider → **no finding** (safe; extra provider props ignored);
  - array/scalar cardinality mismatch (multi-match path into a scalar param) → **error**, with a fix hint (`$[0]` vs `$[*]`).

The check is **non-blocking** — "Send anyway" stays available, because the runtime shape
can differ from the static schema (polymorphism, dynamic results, opaque return types). For
opaque return types (BCL/third-party without a `[SleipnirDataContract]` override) it warns that
the path cannot be statically verified, rather than claiming a false green.

---

## 7. Binding modes — Weak, Strict, Paranoid

By default Sleipnir uses **weak** binding — the duck-typing described above, with silent
defaults. This is powerful and convenient (the subset fan-out in §4 relies on it). For teams
that want rigid, fail-loud typing — where a silent default is a correctness KO-criterion —
two stronger modes are available. They are per-server options, off by default, and each is a
**superset** of the one before it in strictness:

| Mode | Scope | Depth | Silent default on missing property |
|---|---|---|---|
| **Weak** (default) | — | — | allowed (duck-typed) |
| **Strict** | `@alias` params only | top level only | turned into a 400 |
| **Paranoid** | **all** params (alias + literals) | **recursive** (nested objects + array elements) | turned into a 400 at every depth |

### Option

```csharp
services.AddSleipnir(new SleipnirOptions
{
    AliasBindingMode = AliasBindingMode.Paranoid, // Weak | Strict | Paranoid (default: Weak)
});
```

`AliasBindingMode` (`SleipnirCommon.Models`) is plumbed through `SleipnirOptions` to the
`SleipnirInvoker` singleton, alongside the cardinality caps. `Weak` is the default and the
behavior described in §1–§6; a bare `new SleipnirInvoker()` is also `Weak`.

### What Strict checks

Strict applies **only to `@alias`-sourced parameters** — values that arrived via the
extract/inject pipeline. Literals the caller sent deliberately are not re-checked, and the
check is **shallow**: it covers only the top-level properties of the consuming object type.
For each such parameter, after injection and **before** `System.Text.Json` deserialization,
the server verifies that the fragment **fully covers** the consuming object type's
top-level properties:

> Every public read-write instance property the consumer type declares must be present in
> the fragment JSON, matched **case-insensitively** (as STJ reads). Indexers, get-only
> (computed) properties, and non-public setters are not required.

If a required top-level property is absent from the fragment, strict returns:

```
400 — Strict alias binding: parameter 'P' (TypeName) requires property 'X',
      which is absent from the '@alias' fragment. In weak mode this would be
      silently defaulted; in strict mode it is rejected.
```

`(TypeName)` is the consumer parameter's **.NET type name** (read server-side from the
route cache's `ParameterInfo`, e.g. `Order`) — not the wire `TypeRef` ([`docs/discovery-schema.md`](docs/discovery-schema.md)).
Strict/Paranoid validate against the CLR parameter type directly, so they are independent
of the discovery type-system change.

Strict's two remaining gaps — the reason Paranoid exists — are: (a) it ignores **literal**
parameters, and (b) it does **not recurse** into nested objects, so a missing value-type
property *inside* a present nested object is still silently defaulted.

### What Paranoid adds

Paranoid is Strict with both gaps closed:

1. **All parameters are checked — including literals.** A literal `{"id":7}` sent to a
   method taking `{int Id; bool Active}` is a 400 in Paranoid (silent `Active=false` in
   Weak *and* Strict). This is server-side input validation against the declared contract —
   the closest Sleipnir gets to schema validation without a schema language.
2. **Recursive depth.** The coverage check descends into nested object properties and array
   elements. A `TakeOrder(OrderDto { Id; Address { Street; Zip } })` receiving
   `{"id":1,"address":{"street":"A"}}` is a 400 in Paranoid (missing `Address.Zip`); Strict
   sees only that `Address` is present and passes, and `Zip` is silently `0`. A
   `List<OrderDto>` parameter is checked element-by-element; a missing nested property in any
   single element is a 400.

If any required property — at any depth, in any parameter — is absent, paranoid returns:

```
400 — Paranoid binding: parameter 'P' (TypeName) is not fully covered by its fragment.
      Missing: 'P.X', 'P.Address.Zip', 'P.list[1].Address.Zip'. In weak mode these would
      be silently defaulted; in strict mode the top-level check would pass (it checks only
      @alias parameters and does not recurse); paranoid mode enforces full coverage of
      every parameter — including literals — at every depth.
```

The recursion follows the **fragment** structure (a JSON tree), so it terminates naturally
and has no cycle risk. It descends only where the declared CLR type has coverable properties
and the fragment value is a matching object/array.

### What neither Strict nor Paranoid changes

- **Cross-kind mismatches** (number→string, object→scalar, …) are `400` in **all** modes —
  `System.Text.Json` throws regardless. Neither mode re-checks these.
- **Widening** within a JSON kind (`int`→`long`/`double`/`decimal`) is accepted in all
  modes — it is safe and not a silent default.
- **The safe subset direction** (consumer ⊆ fragment, §4) still binds in all modes:
  nothing is missing, so the check passes. The subset fan-out pattern works everywhere.
- **Scalars, collections-of-scalars, dictionaries, `object`/`dynamic`/`JsonElement`**
  consumer types have no coverable properties → the check skips them (STJ handles binding,
  including its own cross-kind 400s). A `List<int>` literal is not recursed (`int` has no
  coverable properties); a `Dictionary<,>` is never recursed (open key set, no
  "missing property" semantics on the value side).
- **Unresolved aliases** are `400` in all modes.

### The invariant

Strict and Paranoid both enforce **consumer ⊆ fragment**: the fragment must contain at
least every (top-level / at-every-depth) property the consumer type declares. Extra provider
properties (the fan-out drop) are always allowed. Only the dangerous direction — consumer
declares a property the fragment lacks — is turned from a silent 2xx default into a loud 400.
Paranoid extends that protection to literals and to every nesting depth; Strict is the
lighter, alias-only, top-level-only variant.

### Where the checks live

- **Strict** runs in `ResolveParameterValues` (both the Serial and the topological-batch
  paths), using the consumer `ParameterInfo` from the route cache and the fragment `JsonNode`
  recorded during `@alias` substitution (`AliasReplacement`). Cost-neutral in `Weak` (the
  recording is a small list, the check is skipped).
- **Paranoid** runs in the same `ResolveParameterValues` sites, but on the resolved
  parameter node directly (so it sees alias-replaced *and* literal parameters) via
  `ParanoidBindingCheck` → `CollectMissing` (recursive) → `GetCollectionElementType`. It
  walks every parameter of the method and recurses the fragment tree; cost is proportional
  to fragment size, so it is the most expensive mode and runs on every call.

The executable specs are
[`SleipnirTests/Unit/Core/AliasBindingStrictTests.cs`](SleipnirTests/Unit/Core/AliasBindingStrictTests.cs)
and [`SleipnirTests/Unit/Core/AliasBindingParanoidTests.cs`](SleipnirTests/Unit/Core/AliasBindingParanoidTests.cs).

### When the coverage check fires before System.Text.Json

If a fragment is both *incomplete* (a required property missing) and *kind-mismatched* on an
overlapping property, the coverage check reports the **missing property** first (it runs
before STJ deserializes). If the fragment *fully covers* but a kind mismatch remains on an
overlapping property, the check passes and STJ's `400 cannot be converted` surfaces. Either
way the result is `400`; the modes just make the incomplete-coverage case loud and specific.

---

## 8. What does NOT flow through aliases

`byte[]` travels out-of-band (`binaryData` / `content`), never in the JSON `data` field, so
a `byte[]` parameter **cannot** be the target of an `@alias`. Aliases carry JSON fragments
only. See [Binary](README_DETAILS.md#binary).

`CancellationToken` is injected by the server and is never an alias target. Parameters are
matched by name (ordinal, case-sensitive); `@alias` is a value-level placeholder for one
named parameter, not a parameter-level rerouting.

## 9. Provider failure & dependent propagation

Alias binding (sections 1–8) describes the **happy path**: a provider ran, exposed its
fragments, and a consumer bound them. What happens when the **provider itself fails** — it
is unauthorized, throws, or simply does not produce the fragment it declared — is the other
half of the contract. It is well-defined, not a crash, and it is **propagated**:

| Provider outcome | Dependent's result |
|---|---|
| Provider **succeeded** and **exposed** the consumed alias | binds normally (sections 1–8) → `2xx` |
| Provider was **unauthorized** (`401`) | `400` — `Dependency '@a' unavailable: provider '<id>' was unauthorized (401).` |
| Provider returned **any other non-2xx** (`400`/`404`/`500`/…) | `400` — `Dependency '@a' unavailable: provider '<id>' returned HTTP <code>.` |
| Provider succeeded but **did not expose** the alias (JsonPath matched nothing, the method returned `null`/void, or a declared path produced no fragment) | `400` — `Dependency '@a' unavailable: provider '<id>' did not expose '@a'.` |
| **No provider** in the batch exposes that alias (dangling `@alias`) | `400` — `Dependency '@a' unavailable: no provider exposes '@a'.` |

> **Extraction is gated on success.** A provider exposes fragments **only when its response is `2xx`**. Any non-2xx response — a business error returned via `SleipnirResults` (`NotFound`/`BadRequest`/`Error(ProblemDetails)`/…), a thrown exception (→ `500`), or an unauthorized/missing-route decision from the pre-pass — leaves `exposedDependencies` empty, *even if the error payload itself contains fields the declared JsonPath would match* (e.g. a `ProblemDetails` body with `title`/`status`). No value is ever extracted from an error payload and forwarded to a dependent; the dependent sees the propagation row above instead. This is the guarantee behind property 2 (a failed provider produces no `exposedDependencies`).

Three properties make this predictable:

1. **The dependent does not run.** When a provider is known to have failed or not exposed,
   the consumer's method is **not invoked** — its parameters could not be satisfied, so the
   server short-circuits it with the explanatory `400`. No wasted execution, and the cause is
   named (which provider, which alias, why), instead of the consumer reaching the missing
   alias at runtime and reporting the uninformative `Unresolved dependencies`.
2. **Propagation is transitive.** A skipped provider produces no `exposedDependencies`, so it
   is itself a "did not expose" case for *its* dependents, which are skipped in turn, and so
   on down the chain. A single unauthorized provider at the root cancels the entire branch
   beneath it — each dependent gets its own `400` naming its immediate provider.
3. **Scope.** Propagation applies on the **topological** path (auto-detected whenever any
   request carries a `dependencyMapping`). The **Serial** path has no providers by definition
   (a `dependencyMapping` anywhere routes to topological), so a dangling `@alias` there keeps
   the legacy `400 Unresolved dependencies: <alias>.` message. The **Parallel** path has no
   providers either and does not resolve `@alias` at all.

### Batch authorization is per-request

Authorization is checked **per request**, not per batch. In a batch of independent commands, a
`401` on one request does **not** abort the others — each response is independent
(JSON-RPC-conformant). A batch may mix unauthenticated reads with `[SleipnirAuthorise]` writes;
the writes fail with `401`, the reads succeed, and the client gets a mixed response array.
Only the dependency **chain** is coupled: a failed provider propagates to its dependents (above).

### Why authorization is checked before the parallel fan-out

`HttpContext` is not thread-safe, yet every request in a batch shares the *same* incoming
context (REST/WebSocket connection). The server therefore splits each batched invocation into
two phases: **authorization** (controller/method lookup + `[SleipnirAuthorise]` check) runs
**serially in a pre-pass** before the fan-out; **execution** (parameter binding, the compiled
delegate, `exposedDependencies` extraction) runs **parallel via `Task.WhenAll` and never
touches `HttpContext`**. This eliminates the framework's concurrent context access
structurally. Authorization is cheap (claims reads, microseconds), so the serial pre-pass
does not regress parallel throughput — the heavy work stays fully parallel.

> **User-code contract.** A controller can still obtain the context via
> `IHttpContextAccessor` (the standard ASP.NET pattern; Sleipnir does not register it but
> cannot prevent users from doing so). Because `AsyncLocal` flows into all `Task.WhenAll`
> children, user code in a parallel batch sees the same shared context — it must treat it
> as **read-only** (no writes to `HttpContext.Items`, the response, or the request body).
> The same applies to overrides of `OnAuthorization(HttpContext?)`. The framework's own
> concurrent access is eliminated by the pre-pass; this contract covers user code.