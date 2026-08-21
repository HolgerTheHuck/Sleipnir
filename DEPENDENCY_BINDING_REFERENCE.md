# Sleipnir Dependency Binding — User Reference

A consolidated lookup reference for **everything dependency chaining** in
Sleipnir: how request A exposes values from its result via a result-relative
JsonPath, how request B consumes them as an `@alias` parameter, and the rules
that govern whether the consumer's `System.Text.Json` deserializer accepts or
rejects the extracted fragment. Covers the three-step binding pipeline, the four
runtime outcomes, the three casing regimes, the Weak/Strict/Paranoid binding
modes (with exact 400 messages), match-count-aware extraction, the 2xx-only
gate, provider-failure dependent propagation, the topological batch executor,
and every related knob on `SleipnirOptions`.

This is a **reference**, not a tutorial. When a chain does not bind, look here
first — the pipeline, the outcome table, the casing rules, the mode table with
exact 400 message text and `path:line` citations, the diagnostics catalog, and a
map of where the deeper docs live. For the precise specification read
`DEPENDENCY_BINDING.md`; for the wire-level protocol read `PROTOCOL.md`
§"Alias Serialization & Type Binding" / §"Casing Contract"; for the user-facing
overview read `README_DETAILS.md` §"Dependency Chaining — Binding, Types &
Casing". This doc consolidates those and links back for depth.

All citations are `repo-relative/path.cs:line` against the repo root. Code-facing
text is English per `CLAUDE.md`.

## Table of contents

1. [The binding pipeline](#1-the-binding-pipeline)
2. [The four runtime outcomes](#2-the-four-runtime-outcomes)
3. [object → object duck-typing & subset fan-out](#3-object--object-duck-typing--subset-fan-out)
4. [The three casing regimes](#4-the-three-casing-regimes)
5. [Binding modes — Weak / Strict / Paranoid](#5-binding-modes--weak--strict--paranoid)
6. [The extract step — `DependencyResolver.ExtractValue`](#6-the-extract-step--dependencyresolverextractvalue)
7. [ExposedDependencies — 2xx-only & provider-failure propagation](#7-exposeddependencies--2xx-only--provider-failure-propagation)
8. [The fluent builder — `SleipnirCall.Exposes` / `WithAlias`](#8-the-fluent-builder--sleipnircallexposes--withalias)
9. [Topological batch execution — `DependencyGraphBuilder`](#9-topological-batch-execution--dependencygraphbuilder)
10. [Configuration reference (binding & cardinality knobs)](#10-configuration-reference-binding--cardinality-knobs)
11. [DevUI static checker — `dependencyCheck.ts`](#11-devui-static-checker--dependencycheckts)
12. [Diagnostics & troubleshooting catalog](#12-diagnostics--troubleshooting-catalog)
13. [How it is verified (the tests)](#13-how-it-is-verified-the-tests)
14. [Relationship to other docs](#14-relationship-to-other-docs)

---

## 1. The binding pipeline

Dependency chaining lets a single roundtrip carry multiple calls where later
calls reference earlier results. Request A declares
`DependencyMapping: { "alias" → "$.Path" }` (a **result-relative** JsonPath — `$`
is the whole serialized result, e.g. `$`, `$.Id`, `$[0].Id`; there is **no
`$.data` envelope level`). Request B uses `@alias` as a parameter placeholder;
the server resolves it before execution. The spec calls this a three-step
pipeline (`DEPENDENCY_BINDING.md:19`):

| Step | What happens | Code site |
|------|--------------|-----------|
| **1. Extract** | Run the JsonPath against the provider's serialized result; collect matches (match-count-aware). | `SleipnirCore/Services/DependencyResolver.cs:20-21` |
| **2. Inject** | Store the extracted JSON fragment as a native `JsonValue` (the raw `@alias` token) in the consumer's parameter, recorded for later binding checks. | `SleipnirInvoker.cs:1296-1302` (`ReplaceDependencyByAliasCore`) |
| **3. Bind** | Feed the fragment straight into the consumer's `System.Text.Json` deserializer — **never re-serialized through the consumer type**. The four outcomes (§2) fall out of this. | `SleipnirInvoker.cs:1645-1646` (`BuildParameters` → `Deserialize`) |

The fragment is fed **straight into STJ** — it is not re-serialized through the
consumer type (`DEPENDENCY_BINDING.md:36-39`). This is why the four outcomes
are STJ's own behavior, not a Sleipnir re-implementation.

The wire model field is `SleipnirRequest.DependencyMapping`
(`SleipnirCommon/Models/SleipnirRequest.cs:33`) — a `Dictionary<string, string>`
of `alias → result-relative JsonPath`. The `Params` payload is a `JsonNode`
(`:24`). (The `SleipnirCore/Model/Messages/SleipnirRequest.cs:1-2` stub notes the
type was consolidated into `SleipnirCommon.Models.SleipnirRequest`.)

Both the Serial path (`ExecuteSequentially`) and the auto-detect topological
path (`ExecuteInDependencyBatches`) resolve aliases against prior responses.
Auto-detect triggers when any request has a non-empty `DependencyMapping`
(`SleipnirInvoker.cs:310-313`).

---

## 2. The four runtime outcomes

Because the fragment is fed straight into STJ, exactly four outcomes are
possible (`DEPENDENCY_BINDING.md:81-86`, `PROTOCOL.md:349-360`):

| Outcome | HTTP | Cause |
|---------|------|-------|
| **Compatible** | 2xx | The fragment's JSON kind matches the parameter type. Widening (`int`→`long`) is accepted silently (`:89-92`). |
| **Cross-kind scalar** | 400 | `Parameter 'X' cannot be converted to type 'Y'.` — a scalar of the wrong kind (number↔string) with no `AllowReadingFromString`, so `"42"`→`int` and `42`→`string` are both rejected (`:96-99`). |
| **object → object missing property** | 2xx silent default (Weak) / 400 (Strict top-level, Paranoid every depth) | Duck-typed: a missing value-type property silently defaults to `0`/`false`/`DateTime.MinValue` (the insidious case); a missing reference property → `null`; a kind mismatch on an *overlapping* property → 400. |
| **Unresolved** | 400 | `Unresolved dependencies: alias.` — no provider exposes the alias, or the JsonPath matched nothing. |

The safe direction is **consumer ⊆ fragment** (the "subset fan-out", §3); the
dangerous direction is consumer ⊋ fragment (silent defaults). The DevUI catches
the dangerous direction statically (§11); runtime is lenient in Weak with
optional Strict/Paranoid enforcement (§5).

---

## 3. object → object duck-typing & subset fan-out

Object→object binding is **duck-typed and directional**
(`DEPENDENCY_BINDING.md:103`):

- **Safe direction — consumer ⊆ provider:** the consumer type's properties are
  a subset of the fragment's. Extra provider properties are dropped silently.
  This is the **subset fan-out** pattern (`:109-113`, `:131`): one alias exposes
  a whole object, and many typed consumers each read only the properties they
  need. `DEPENDENCY_BINDING.md:139-150` shows the pattern.
- **Dangerous direction — consumer ⊋ provider:** a consumer property absent
  from the fragment. A missing **value-type** property silently defaults
  (`0`/`false`/`DateTime.MinValue` — the insidious case); a missing **reference**
  property → `null` (`:115-121`).

**Subset fan-out rule:** each consumer parameter must be an **object type**. A
bare scalar receiving a whole object is a cross-kind 400
(`DEPENDENCY_BINDING.md:155-159`).

> **What does NOT flow through aliases:** `byte[]` travels out-of-band in
> `SleipnirRequest.BinaryData`, not through `@alias` (`DEPENDENCY_BINDING.md:362`);
> `CancellationToken` is server-injected, never client-sent (`:365-367`).

---

## 4. The three casing regimes

Casing has **three independent regimes** (`DEPENDENCY_BINDING.md:168-172`,
`PROTOCOL.md:414-418`). Getting them mixed up is the most common binding bug:

| Regime | Sensitivity | Why |
|--------|-------------|-----|
| **Parameter NAMES** bind | **case-sensitive** (ordinal) | The server matches `SleipnirParameter.parameterName` to the method's `ParameterInfo.Name` via an `Ordinal` dictionary. `SleipnirInvoker.cs:1586` (`BuildParameters`), `:1612-1615`; also `:967`, `:1044`. |
| **Parameter VALUE properties** | read **case-insensitive**, written **camelCase** | STJ options: `PropertyNameCaseInsensitive = true`, `PropertyNamingPolicy = JsonNamingPolicy.CamelCase` (`SleipnirInvoker.cs:143-148`). No `AllowReadingFromString`. |
| **JsonPath extraction** | **case-sensitive** | JsonPath.Net is RFC 9535 case-sensitive (`PROTOCOL.md:418`). The root is the already-camelCase-serialized result (`DependencyResolver.cs:37`). |

**The trap:** the wire document is camelCase, so a **PascalCase** JsonPath like
`$.Id` matches **nothing** → `Unresolved` → the dependent gets the propagation
400. Use `$.id`. Verified by `SleipnirTests/Unit/Core/AliasBindingTests.cs:307-324`
(`Alias_JsonPath_PascalCase_DoesNotMatch`).

So a C# consumer property `Id` reads a camelCase fragment `id` (regime 2,
case-insensitive), but the provider's `DependencyMapping` JsonPath must be
`$.id` (regime 3, case-sensitive). "Why not drop case-sensitivity" rationale:
`DEPENDENCY_BINDING.md:187-194`, `PROTOCOL.md:435-439`.

---

## 5. Binding modes — Weak / Strict / Paranoid

`SleipnirOptions.AliasBindingMode` selects how strictly a consumer parameter
must be covered by its fragment. Each mode is a superset of the previous.
Default is **Weak** (`SleipnirHub/Extensions/SleipnirOptions.cs:204`,
`SleipnirInvoker.cs:79`).

**Enum:** `SleipnirCommon/Models/AliasBindingMode.cs:10` —
`Weak (:20)`, `Strict (:35)`, `Paranoid (:51)`.

**Plumbing chain:** `SleipnirOptions.cs:204` →
`SleipnirServiceCollectionExtension.cs:156`
(`invoker.AliasBindingMode = options.AliasBindingMode;`) →
`SleipnirInvoker.cs:79` (`public AliasBindingMode AliasBindingMode { get; set; } = Weak;`).

| Mode | What it checks | What it does NOT change | Check site |
|------|----------------|------------------------|-----------|
| **Weak** (default) | Duck-typed; silent defaults. | — | none (cost-neutral) |
| **Strict** | Each `@alias`-sourced parameter must be **fully covered at the top level** — every public read-write property of the consumer type present in the fragment (case-insensitive); literals not re-checked; nested objects not descended. | Cross-kind still 400; widening still accepted; the safe subset direction still binds. | `SleipnirInvoker.cs:960` `StrictBindingCheck`, invoked `:932-937` |
| **Paranoid** | Strict + (a) checks **all parameters including literals** the caller sent, and (b) checks **recursively**, descending into nested object properties and array elements. | Cross-kind still 400; widening still accepted; safe subset still binds. | `SleipnirInvoker.cs:1037` `ParanoidBindingCheck`, invoked `:893-898` (literal-only) and `:938-946` (post-alias) |

**`RequiredPropertyNames`** (`SleipnirInvoker.cs:1011-1025`) defines the
top-level property set Strict checks: skips Nullable/enum/string, indexers,
get-only and non-public setters.

### Exact 400 messages

**Strict** (`SleipnirInvoker.cs:997-1000`):

> `Strict alias binding: parameter '{ParamName}' ({ParameterType.Name}) requires property {list}, which is absent from the '@{Alias}' fragment. In weak mode this would be silently defaulted; in strict mode it is rejected.`

**Paranoid** (`SleipnirInvoker.cs:1104-1109`):

> `Paranoid binding: parameter '{paramName}' ({paramType.Name}) is not fully covered by its fragment. Missing: {list}. In weak mode these would be silently defaulted; in strict mode the top-level check would pass (it checks only @alias parameters and does not recurse); paranoid mode enforces full coverage of every parameter — including literals — at every depth.`

**Unresolved (Serial path)** (`SleipnirInvoker.cs:927`):

> `Unresolved dependencies: {alias1, alias2, …}`

The Strict/Paranoid coverage check reads the fragment **case-insensitively**
(`HashSet<string>(StringComparer.OrdinalIgnoreCase)` at `:990`, `:1150`), so a
PascalCase consumer `Id` is covered by a camelCase fragment `id`.

**`CollectMissing` (recursive)** (`SleipnirInvoker.cs:1142`): required props
(`:1145-1146`); present dict OrdinalIgnoreCase (`:1150-1152`); missing → dotted
path `P.X`, `P.Address.Zip` (`:1158-1160`); recurses into JsonObject nested
(`:1172-1175`) and JsonArray elements via `GetCollectionElementType`
(`:1176-1187`).

**`GetCollectionElementType`** (`SleipnirInvoker.cs:1199`): handles arrays
(`:1201`), generic `List<>`/`IList<>`/`IEnumerable<>`/`ICollection<>`/
`IReadOnlyList<>`/`IReadOnlyCollection<>`/`HashSet<>`/`ISet<>` (`:1206-1209`),
interface search excluding `IEnumerable<object>` (`:1214-1218`). **Dictionaries
explicitly excluded** (`:1197-1198`) — a `Dictionary<K,V>` is treated as a
collection of its values' shape only where the code descends; dictionaries are
not duck-typed key-by-key.

**`AliasReplacement` record** (`SleipnirInvoker.cs:1333`):
`private readonly record struct AliasReplacement(string ParamName, string Alias, string FragmentJson)`,
populated in `ReplaceDependencyByAliasCore` (`:1296-1302`) only when the alias is
the direct value of a `SleipnirParameter` object.

Spec detail on modes: `DEPENDENCY_BINDING.md:223-343`; "what neither changes"
`:309-322`; the consumer⊆fragment invariant `:326-331`; where the checks live
`:334-343`.

---

## 6. The extract step — `DependencyResolver.ExtractValue`

`SleipnirCore/Services/DependencyResolver.cs:20-21`:

```csharp
public static JsonNode? ExtractValue(JsonElement element, string jsonPath,
    int maxPathLength = 256, bool allowRecursiveDescent = true)
```

**JsonPath library: JsonPath.Net** (`using Json.Path;` at `DependencyResolver.cs:3`;
`CLAUDE.md:36` confirms the `SleipnirCore` ← `JsonPath.Net` dependency). RFC 9535,
case-sensitive (`PROTOCOL.md:418`).

Guards: `maxPathLength` throws before parse (`:25-27`); `allowRecursiveDescent`
rejects `$..` (`:32-34`). The element is materialized via
`JsonNode.Parse(element.GetRawText())` (`:37`) — the already-camelCase result —
then `JsonPath.Parse(jsonPath).Evaluate(root).Matches` (`:38-39`).

**Match-count-aware behavior** (the key semantic):

| Matches | Result | Line |
|---------|--------|------|
| 0 | `null` | `:42-43` |
| 1 | `matches[0].Value` as-is — a scalar, or a whole array/object when the single match is one | `:48-49` |
| >1 | a `JsonArray` of all matches (DeepClone) | `:58-61` |

A multi-match path (`$[*].Id`, `$..Id`) collects all matches into one
`JsonArray`, injected as **one list-typed parameter** (`List<T>`/`T[]`/
`IEnumerable<T>`) — **list fan-out into a parameter, never fan-out into N
requests** (`CLAUDE.md:117`). The comment at `:51-57` notes v1 collapsed to the
first match; it now produces an array. `BuildParameters` deserializes a list
fragment via `Deserialize<List<T>>`.

---

## 7. ExposedDependencies — 2xx-only & provider-failure propagation

**Model field:** `SleipnirCommon/Models/SleipnirResponse.cs:83` —
`public Dictionary<string, string>? ExposedDependencies { get; set; }`
(alias → serialized fragment). `IsSuccess` (`:97`) is `Code >= 200 && <= 299`.

**2xx-only extraction gate** (`SleipnirInvoker.cs:1474`):

```csharp
if (request.DependencyMapping != null && result != null && result.IsSuccess && result.Data.HasValue)
```

Inside, the per-alias loop (`:1481-1501`) calls
`DependencyResolver.ExtractValue(result.Data.Value, jsonPath, MaxDependencyPathLength, AllowRecursiveDescent)`
(`:1487-1488`) and stores the fragment via `extracted.ToJsonString()` (`:1493`).
A thrown exception → log a warning and skip the alias (`:1495-1500`). The comment
at `:1467-1473` states explicitly that **error payloads** (e.g. a
`ProblemDetails` `title`/`status`) are **never extracted** even when they
path-match. `result.ExposedDependencies = exposed;` at `:1502`.

> **Consequence:** a non-2xx result (business `SleipnirResults` error, thrown
> exception → 500, or a 401/missing-route from the serial auth pre-pass) leaves
> `ExposedDependencies` empty, so no value is ever extracted from an error
> payload.

### Provider-failure dependent propagation (topological path only)

When a provider fails authorization, errors, or declares an alias it does not
actually expose, its **dependents do not run** — they receive an explanatory
`400` instead of reaching the missing alias at runtime with the uninformative
`Unresolved dependencies`. Built in `ExplainUnavailability`
(`SleipnirInvoker.cs:819-857`), called from `ExecuteDependentRequestAsync`
(`:778`):

| Condition | Message | Line |
|-----------|---------|------|
| No provider exposes the alias | `Dependency '@{alias}' unavailable: no provider exposes '@{alias}'.` | `:834-835` |
| Provider produced no result | `Dependency '@{alias}' unavailable: provider '{providerKey}' produced no result.` | `:838-839` |
| Provider non-2xx (401 special-cased) | `Dependency '@{alias}' unavailable: provider '{providerKey}' was unauthorized (401).` (or `… returned HTTP {code}.`) | `:841-847` |
| Provider succeeded but did not expose the alias | `Dependency '@{alias}' unavailable: provider '{providerKey}' did not expose '@{alias}'.` | `:850-853` |

Three properties (`DEPENDENCY_BINDING.md:386-401`): the dependent does not run
(`:388`); propagation is **transitive** — a skipped provider has no
`ExposedDependencies`, so its own dependents are caught in the next batch
(`:393`); scope is **topological only** — Parallel/Serial have no providers by
definition (`:397-401`). Per-request authorization still holds
(`:403-409`); the serial auth pre-pass rationale is at `:411-427` (see
`CLAUDE.md:60` for the `HttpContext` batch safety contract).

---

## 8. The fluent builder — `SleipnirCall.Exposes` / `WithAlias`

`SleipnirClient/Sleipnir/SleipnirCall.cs`:

- `Exposes(string jsonPath, string alias)` (`:45`) stores
  `_exposedDependencies[alias] = jsonPath;` (`:47-48`). The doc (`:34-44`)
  states the path is **result-relative**: `$` = whole result, `$.Id`/`$.Name`
  properties, `$[0].Id` first list element; **no `$.data` envelope level** (a
  `$.data` path only matches if the result itself has a `data` property).
- `WithAlias(string dependencyPlaceholder)` (`:59-74`) stores the raw `@alias`
  string as a native `JsonValue` (`:70`).
- `ToRequest()` (`:132`) packs `_exposedDependencies` into `DependencyMapping`
  only if non-empty.

> **Doc-bug note:** `CLAUDE.md:89` references a two-argument overload
> `WithAlias("@alias", "default")`. That overload is **not** present in
> `SleipnirCall.cs` (only the single-arg `WithAlias(string)` at `:59`). The
> two-arg form may live in `Sleipnir.Client.Linq/SleipnirCallSpec.cs` or the
> CLAUDE.md line may be stale — verify before relying on it.

---

## 9. Topological batch execution — `DependencyGraphBuilder`

`SleipnirCore/Services/DependencyGraphBuilder.cs:22` —
`SortByDependencyBatches(List<SleipnirRequest>)`. Kahn's algorithm, batch-based:

1. **Provider map + request index** (`:28-51`): `providers[alias] = id` from
   each request's `DependencyMapping` keys (`:43-49`); the provider id falls
   back to `$"{Controller}.{Method}"` when empty (`:37-38`).
2. **Dependency edges** (`:54-73`): `ExtractAliases(request.Params)` (`:64`)
   collects `@alias` tokens; resolves each via `providers` (`:67`); self-deps
   skipped (`:69`).
3. **Kahn batches** (`:75-103`): while remaining, a batch is the remaining
   requests whose deps are all `completed` (`:83-85`); if a batch is empty →
   **cycle** → `InvalidOperationException` with the message
   `"Zyklus in Abhängigkeiten erkannt. Beteiligte Requests: …"` (`:87-93`);
   append the batch, move to completed (`:95-102`).

**`ExtractAliases`** (`:115-120`) returns a `HashSet<string>(StringComparer.Ordinal)`.
`CollectAliases` (`:122-145`) scans `JsonValue` strings starting with `@`
(`:125`); the alias name is the alphanum+`_` run after `@` (`:128-134`); recurses
into JsonObject/JsonArray (`:137-144`).

**Topological executor** — `SleipnirInvoker.cs:671` `ExecuteInDependencyBatches`:
calls `SortByDependencyBatches` (`:679`); a cycle → 400 for **all** requests,
`"Circular dependency detected in request batch."` (`:682-691`); builds
`aliasToProvider` (`:696-704`); per batch runs a **serial auth pre-pass**
(`:721-723`) then `Task.WhenAll` fan-out via `ExecuteDependentRequestAsync`
(`:727-728`); writes results to `priorResponses` keyed by `GraphKey` (`:730-736`).
`GraphKey` (`:748-752`) is the request id or `Controller.Method`.
`ExecuteDependentRequestAsync` (`:759`): auth error → traced response
(`:766-772`); `ExplainUnavailability` propagation (`:778-783`);
`ResolveParameterValues` (`:789-795`); `ExecuteAuthorized` (`:806`).

---

## 10. Configuration reference (binding & cardinality knobs)

All on `SleipnirHub/Extensions/SleipnirOptions.cs`, plumbed to the invoker
singleton in one block (`SleipnirServiceCollectionExtension.cs:152-172`):

| Option | Type | Default | Line | Notes |
|--------|------|---------|------|-------|
| `AliasBindingMode` | `AliasBindingMode` | `Weak` | `:204` | Weak / Strict / Paranoid (§5). Plumbed at `SleipnirServiceCollectionExtension.cs:156`; invoker field `SleipnirInvoker.cs:79`. |
| `MaxDependencyPathLength` | `int` | 256 | `:166` | JsonPath length cap (0 = unlimited); passed to `ExtractValue`. Invoker `:131`; enforced `DependencyResolver.cs:25-27`. |
| `AllowRecursiveDescent` | `bool` | `true` | `:176` | Rejects `$..` when false. Invoker `:137`; enforced `DependencyResolver.cs:32-34`. |
| `MaxParameterArrayLength` | `int` | 1000 | `:185` | Caps `@alias` whole-collection passthrough; `string`/`byte[]` excluded. Invoker `:59`; enforced `BuildParameters` `SleipnirInvoker.cs:1659-1668`. |
| `MaxResultElementCount` | `int` | 10000 | `:193` | Caps materialized collections + `IAsyncEnumerable` early-stop. Invoker `:67`; enforced `ReturnResponse` `:2077-2087` and `ConsumeAsyncEnumerable` `:1967-1978` (413 / `ResultCardinalityExceededException`). |

> **Cardinality-cap 400 message** (`SleipnirInvoker.cs:1659-1668`, German —
> legacy): `Parameter '{name}' überschreitet MaxParameterArrayLength ({n}; Ist {colParam.Count}). Paginieren oder Cap erhöhen (0 = unbegrenzt).` This is
> one of the legacy German strings CLAUDE.md notes should be migrated to
> English opportunistically (not a 1.0 blocker).

**North-bound hardening** (`BEST_PRACTICES.md:23-33`) recommends lowering
`MaxDependencyPathLength = 128` and `AllowRecursiveDescent = false` for
internet-facing hosts.

---

## 11. DevUI static checker — `dependencyCheck.ts`

`SleipnirDeveloperUi/src/lib/utils/dependencyCheck.ts` is the **one place with
both schemas** (provider return + consumer parameter from discovery) and
statically reproduces the runtime rules. Header comment (`:1-30`) explains it
reproduces `DependencyResolver.ExtractValue` match-count semantics (`:11-18`),
notes camelCase wire vs PascalCase schema + case-sensitive JsonPath (`:19-23`),
and is advisory/non-blocking (`:21-25`).

| Export | Line | Purpose |
|--------|------|---------|
| `Severity = 'error' \| 'warn' \| 'info'`, `CheckIssue` | `:51-58` | Issue shape. |
| `parsePath` | `:83` | Subset JsonPath parser: `$`, `$.prop`, `..desc`, `$[n]`, `$[*]`; rejects filters/slices/`['key']` (`:71-74`, `:111`). |
| `evalPath` | `:161` | Walks `TypeShape`; `wild`/`desc` set `multi = true` (`:189`, `:196`); opaque `unknown` → `opaque: true` (`:167-169`). |
| `kindsCompatible` | `:208` | Same kind ok; cross-kind (number↔string, object↔scalar, array↔scalar) false (`:213`); opaque/acceptsAny → true (`:210-211`). |
| `analyzeObjectBinding` | `:230-255` | Returns `missing` (value-type, silent default), `missingRef` (→ null), `kindMismatch` (overlapping prop → 400). |
| `compatible` | `:272-402` | The core comparator: array→scalar error with `$[0]`/`$[*]` hint (`:292-298`); scalar→list-param error (`:308-313`); object→object duck-typing (`:338-391`) — same type okMatch, missing TypeMeta warn, kindMismatch error (mentions STJ 400), missing value-type warn "still default (kein 400)", missing reference warn "null", consumer⊆provider okMatch; scalar==scalar okMatch; else error. |
| `checkExpose` | `:412-446` | Validate one expose path against the return schema; no return error, unsupported error, opaque warn, not-found error "Unresolved" + hints, multi-match info. |
| `checkAliasBinding` | `:449-495` | Validate an `@alias` consumer param against the provider path; multi → `array<shape>`; ok-with-info/warn → inline issue, else error. |
| `checkSteps` | `:519-545` | **The summary aggregator** — builds the `providers` map (last-wins `:525`), pushes error+warn issues (skips info), returns ordered `CheckIssue[]`. This is the source of the DevUI summary box. |

The summary box and "Send anyway" (the chain sends regardless — the issues are
non-blocking inline warnings + a summary box) are described in
`README_DETAILS.md:441-456` and `DEPENDENCY_BINDING.md:198-219`.

---

## 12. Diagnostics & troubleshooting catalog

### The common mistakes

- **PascalCase JsonPath → `Unresolved`.** `$.Id` matches nothing on the
  camelCase wire document; use `$.id`. The dependent gets the propagation 400
  `… did not expose '@cid'.` (`AliasBindingTests.cs:307-324`).
- **Cross-kind scalar → 400.** No `AllowReadingFromString`, so `"42"`→`int`
  and `42`→`string` both reject with `Parameter 'X' cannot be converted to type 'Y'.`
  (`DEPENDENCY_BINDING.md:84`, `:96-99`).
- **Missing value-type property → silent default (Weak).** The insidious case:
  a consumer `int Active` absent from the fragment binds `0`, no error. Use
  Strict or Paranoid to catch it (`AliasBindingTests.cs:220`,
  `AliasBindingStrictTests.cs:128`).
- **`$.data` envelope assumption.** There is **no** `$.data` level; `$` is the
  whole result. A `$.data.Id` path only matches if the result itself has a
  `data` property (`SleipnirCall.cs:34-44`).
- **Throwing `SleipnirException` to set a code.** The server has no
  `catch(SleipnirException)`; it becomes a generic 500. Control the code via
  `SleipnirResults.Error(...)` and return a `SleipnirResponse`
  (`CLAUDE.md` "Error Handling").

### Exact error strings

- **Strict:** `Strict alias binding: parameter '{P}' ({Type}) requires property {list}, which is absent from the '@{alias}' fragment. In weak mode this would be silently defaulted; in strict mode it is rejected.` (`SleipnirInvoker.cs:997-1000`)
- **Paranoid:** `Paranoid binding: parameter '{P}' ({Type}) is not fully covered by its fragment. Missing: {list}. …` (`SleipnirInvoker.cs:1104-1109`)
- **Unresolved (Serial):** `Unresolved dependencies: {alias1, alias2, …}` (`SleipnirInvoker.cs:927`)
- **Propagation:** see §7 table.
- **Cycle:** `Zyklus in Abhängigkeiten erkannt. Beteiligte Requests: …` (graph builder, German legacy, `DependencyGraphBuilder.cs:87-93`); topological executor wraps as `Circular dependency detected in request batch.` (`SleipnirInvoker.cs:682-691`).
- **Cardinality cap:** `Parameter '{name}' überschreitet MaxParameterArrayLength ({n}; Ist {count}). …` (German legacy, `SleipnirInvoker.cs:1659-1668`); result cardinality → 413 (`:2077-2087`).

### Reconnect/authorization note

Per-request authorization holds even in a chain: the serial auth pre-pass runs
before the fan-out, so a 401 on one request does not abort the others
(batch failure is per-request, JSON-RPC-conformant). See `CLAUDE.md:60` and
`DEPENDENCY_BINDING.md:403-427`.

---

## 13. How it is verified (the tests)

| Test file | Covers |
|-----------|--------|
| `SleipnirTests/Unit/Core/AliasBindingTests.cs` | Weak-mode end-to-end over `SleipnirInvoker` (fixture `DependencyChainController` `:39`): compatible int→int, widening int→long, object→object same type, subset-safe (drops Name/Id), subset fan-out (one alias many consumers), missing reference → null (2xx), missing value-type → silent false (2xx, insidious), cross-kind number→string 400, string→int 400, whole object→scalar int 400, kind-mismatch on overlapping property 400, unresolved `@bogus` 400, JsonPath PascalCase `$.Id` → "did not expose" / "provider 'p'". Uses raw `@alias` JsonValue (not JSON-quoted). |
| `SleipnirTests/Unit/Core/AliasBindingStrictTests.cs` | Strict mode (`:38`): allows subset-safe + fan-out + compatible scalar; rejects missing value-type (expects "Strict alias binding"/"Active"/"@cust") and missing reference ("Name"); cross-kind still 400; kind-mismatch with full coverage still 400; kind-mismatch + missing reports missing first; property match case-insensitive (PascalCase consumer `Id` reads camelCase `id`). |
| `SleipnirTests/Unit/Core/AliasBindingParanoidTests.cs` | Paranoid mode (`:40`): literals checked (missing value-type "Active"/`'d'`, missing reference); recursive nested missing `Address.Zip`; array element `[1]` missing nested (`list[1]`/`Zip`); full nested/array binds. Delta proof vs Strict: Strict literal-missing-value-type still 2xx (`:164`), Strict nested-missing-Zip still 2xx (`:177`). @alias-path Paranoid mirrors. Invariants: scalar literal not over-checked, widening int→long, subset literal binds, `List<int>` scalar-element literal binds (no recursion). |
| `SleipnirTests/Unit/Core/DependencyResolverTests.cs` | `ExtractValue`: simple path, nested, array index `$[1]`, string value, non-existent → null, root `$` over scalar, wildcard `$.items[*].id` → array `[1,2,3]`, wildcard over bare array `$[*].id`, wildcard string projection, single-match `$[0].id` returns scalar (not 1-element array), root `$` over array returns array as single match. |
| `SleipnirTests/Unit/Core/DependencyGraphBuilderTests.cs` | No deps → single batch; linear chain a→b→c → 3 batches; diamond a→{b,c}→d → 3 batches; cycle → `InvalidOperationException` (`"*Zyklus*"`); empty → empty; independent + dependent → 2 batches. |
| `SleipnirTests/Fixtures/TestControllers.cs:171` | The `DependencyChainController` fixture with all the chain methods (`EchoLong`, `MakeDto`, `EchoDto`, `MakeDtoList`, `TakeIdOnly`, `TakeIdActive`, `MakeOrder`, `MakeOrderNoZip`, `TakeOrder`, `TakeOrderList`, …). |

The header of `DEPENDENCY_BINDING.md:4-8` points to `AliasBindingTests.cs` as
the **executable spec**.

---

## 14. Relationship to other docs

| Doc | Covers (binding-relevant) |
|-----|----------------------------|
| `DEPENDENCY_BINDING.md` | **The authoritative specification** — 3-step pipeline, four outcomes, object→object duck-typing, subset fan-out, three casing regimes, DevUI static checker, Weak/Strict/Paranoid modes with exact 400 messages, what does NOT flow (byte[]/CancellationToken), provider-failure & dependent propagation. |
| `PROTOCOL.md` | Wire spec: "Dependency Chaining" (`:255-298`, cycle 400, propagation messages, JsonPath extraction), "Alias Serialization & Type Binding" (`:317-405`, pipeline, four-outcome table, modes with Strict/Paranoid 400 text, DevUI note), "Casing Contract" (`:407-439`, three-regime table, cross-language consequences), "Limits" (`:441-491`, one-alias→one-value, no fan-out into N requests, server-side cardinality caps `MaxParameterArrayLength`/`MaxResultElementCount`). |
| `README_DETAILS.md` | User-facing overview "Dependency Chaining — Binding, Types & Casing" (`:371`): pipeline, four outcomes, subset fan-out, binding modes, casing contract, DevUI static checker, binary-not-through-aliases, provider failure & propagation table, per-request authorization. |
| `CLAUDE.md` | Architecture pointers: JsonPath.Net dependency (`:36`), Serial/topological/auto-detect (`:56-57`), `HttpContext` batch pre-pass + dependent propagation paragraph (`:60`), `DependencyGraphBuilder` Kahn's (`:62`), `SleipnirCall.Exposes` result-relative (`:89`), `SleipnirResponse.ExposedDependencies` (`:104`), "Dependency Chaining" section (`:113-119`). |
| `BEST_PRACTICES.md` | North-bound hardening: `MaxDependencyPathLength = 128`, `AllowRecursiveDescent = false` (`:23-33`); scattered `@alias` mentions for batching/N+1 (`:228`, `:296`, `:349`, `:387`). **No dedicated dependency-binding section.** |
| `TRANSPORT_REFERENCE.md` | Transport-level request/response model that carries `DependencyMapping`/`ExposedDependencies`. |
| `EVENTS_REFERENCE.md` | Events are **not chainable** — `@alias`/`exposes` apply to call results, not event streams (`§1`). |
| `CODEGEN_REFERENCE.md` | `@alias` literal serialization in generated clients, the `@`-normalization fix. |

> **Doc-bugs to fix when convenient:** the `CLAUDE.md:89` two-argument
> `WithAlias("@alias", "default")` overload is not present in `SleipnirCall.cs`
> (only the single-arg form at `:59`) — verify whether it lives in
> `Sleipnir.Client.Linq` or the line is stale. The German cardinality-cap and
> cycle messages (`SleipnirInvoker.cs:1659-1668`, `DependencyGraphBuilder.cs:87-93`)
> are legacy strings CLAUDE.md flags for opportunistic English migration.