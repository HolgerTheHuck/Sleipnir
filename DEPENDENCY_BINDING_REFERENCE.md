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
exact 400 message text and symbol-anchored citations, the diagnostics catalog,
and a map of where the deeper docs live. For the precise specification read
`DEPENDENCY_BINDING.md`; for the wire-level protocol read `PROTOCOL.md`
§"Alias Serialization & Type Binding" / §"Casing Contract"; for the user-facing
overview read `README_DETAILS.md` §"Dependency Chaining — Binding, Types &
Casing". This doc consolidates those and links back for depth.

Citations are durable anchors: `path → Symbol` for code (method/property/type/
field), `path` "short verbatim quote" for specific strings, and
`doc.md §"heading"` for prose sections. Repo-relative paths are against the
repo root. Code-facing text is English per `CLAUDE.md`.

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
pipeline (`DEPENDENCY_BINDING.md §"1. The binding pipeline"`):

| Step | What happens | Code site |
|------|--------------|-----------|
| **1. Extract** | Run the JsonPath against the provider's serialized result; collect matches (match-count-aware). | `SleipnirCore/Services/Helper/DependencyResolver.cs → ExtractValue` |
| **2. Inject** | Store the extracted JSON fragment as a native `JsonValue` (the raw `@alias` token) in the consumer's parameter, recorded for later binding checks. | `SleipnirInvoker.cs → ReplaceDependencyByAliasCore` |
| **3. Bind** | Feed the fragment straight into the consumer's `System.Text.Json` deserializer — **never re-serialized through the consumer type**. The four outcomes (§2) fall out of this. | `SleipnirInvoker.cs → BuildParameters` (the `Deserialize` call) |

The fragment is fed **straight into STJ** — it is not re-serialized through the
consumer type (`DEPENDENCY_BINDING.md §"Step 1—Extract"`). This is why the four
outcomes are STJ's own behavior, not a Sleipnir re-implementation.

The wire model field is `SleipnirRequest.DependencyMapping`
(`SleipnirCommon/Models/SleipnirRequest.cs → DependencyMapping`) — a
`Dictionary<string, string>` of `alias → result-relative JsonPath`. The
`Params` payload is a `JsonNode` (`SleipnirCommon/Models/SleipnirRequest.cs → Params`).
(The `SleipnirCore/Model/Messages/SleipnirRequest.cs` stub comment notes the
type was consolidated into `SleipnirCommon.Models.SleipnirRequest`.)

Both the Serial path (`ExecuteSequentially`) and the auto-detect topological
path (`ExecuteInDependencyBatches`) resolve aliases against prior responses.
Auto-detect triggers when any request has a non-empty `DependencyMapping`
(`SleipnirInvoker.cs` "requestList.Any(r => r.DependencyMapping != null").

---

## 2. The four runtime outcomes

Because the fragment is fed straight into STJ, exactly four outcomes are
possible (`DEPENDENCY_BINDING.md §"2. The four outcomes"`,
`PROTOCOL.md §"Alias Serialization & Type Binding"`):

| Outcome | HTTP | Cause |
|---------|------|-------|
| **Compatible** | 2xx | The fragment's JSON kind matches the parameter type. Widening (`int`→`long`) is accepted silently (`DEPENDENCY_BINDING.md §"What is accepted silently"`). |
| **Cross-kind scalar** | 400 | `Parameter 'X' cannot be converted to type 'Y'.` — a scalar of the wrong kind (number↔string) with no `AllowReadingFromString`, so `"42"`→`int` and `42`→`string` are both rejected (`DEPENDENCY_BINDING.md §"What is rejected"`). |
| **object → object missing property** | 2xx silent default (Weak) / 400 (Strict top-level, Paranoid every depth) | Duck-typed: a missing value-type property silently defaults to `0`/`false`/`DateTime.MinValue` (the insidious case); a missing reference property → `null`; a kind mismatch on an *overlapping* property → 400. |
| **Unresolved** | 400 | `Unresolved dependencies: alias.` — no provider exposes the alias, or the JsonPath matched nothing. |

The safe direction is **consumer ⊆ fragment** (the "subset fan-out", §3); the
dangerous direction is consumer ⊋ fragment (silent defaults). The DevUI catches
the dangerous direction statically (§11); runtime is lenient in Weak with
optional Strict/Paranoid enforcement (§5).

---

## 3. object → object duck-typing & subset fan-out

Object→object binding is **duck-typed and directional**
(`DEPENDENCY_BINDING.md §"3. object→object"`):

- **Safe direction — consumer ⊆ provider:** the consumer type's properties are
  a subset of the fragment's. Extra provider properties are dropped silently.
  This is the **subset fan-out** pattern (`DEPENDENCY_BINDING.md §"Safe direction"`,
  `§"4. Subset fan-out"`): one alias exposes a whole object, and many typed
  consumers each read only the properties they need. `DEPENDENCY_BINDING.md §"4. Subset fan-out"`
  shows the pattern.
- **Dangerous direction — consumer ⊋ provider:** a consumer property absent
  from the fragment. A missing **value-type** property silently defaults
  (`0`/`false`/`DateTime.MinValue` — the insidious case); a missing **reference**
  property → `null` (`DEPENDENCY_BINDING.md §"Dangerous direction"`).

**Subset fan-out rule:** each consumer parameter must be an **object type**. A
bare scalar receiving a whole object is a cross-kind 400
(`DEPENDENCY_BINDING.md §"4. Subset fan-out"`).

> **What does NOT flow through aliases:** `byte[]` travels out-of-band in
> `SleipnirRequest.BinaryData`, not through `@alias` (`DEPENDENCY_BINDING.md §"8. What does NOT flow"`);
> `CancellationToken` is server-injected, never client-sent
> (`DEPENDENCY_BINDING.md §"8. What does NOT flow"`).

---

## 4. The three casing regimes

Casing has **three independent regimes** (`DEPENDENCY_BINDING.md §"5. Casing contract"`,
`PROTOCOL.md §"Casing Contract"`). Getting them mixed up is the most common binding bug:

| Regime | Sensitivity | Why |
|--------|-------------|-----|
| **Parameter NAMES** bind | **case-sensitive** (ordinal) | The server matches `SleipnirParameter.parameterName` to the method's `ParameterInfo.Name` via an `Ordinal` dictionary. `SleipnirInvoker.cs → BuildParameters`, `→ StrictBindingCheck`, `→ ParanoidBindingCheck`. |
| **Parameter VALUE properties** | read **case-insensitive**, written **camelCase** | STJ options: `PropertyNameCaseInsensitive = true`, `PropertyNamingPolicy = JsonNamingPolicy.CamelCase` (`SleipnirInvoker.cs → _jsonSerializerOptions`). No `AllowReadingFromString`. |
| **JsonPath extraction** | **case-sensitive** | JsonPath.Net is RFC 9535 case-sensitive (`PROTOCOL.md §"Casing Contract"`). The root is the already-camelCase-serialized result (`SleipnirCore/Services/Helper/DependencyResolver.cs → ExtractValue`). |

**The trap:** the wire document is camelCase, so a **PascalCase** JsonPath like
`$.Id` matches **nothing** → `Unresolved` → the dependent gets the propagation
400. Use `$.id`. Verified by `SleipnirTests/Unit/Core/AliasBindingTests.cs → Alias_JsonPath_PascalCase_DoesNotMatch`.

So a C# consumer property `Id` reads a camelCase fragment `id` (regime 2,
case-insensitive), but the provider's `DependencyMapping` JsonPath must be
`$.id` (regime 3, case-sensitive). "Why not drop case-sensitivity" rationale:
`DEPENDENCY_BINDING.md §"Why not drop case-sensitivity"`,
`PROTOCOL.md §"Casing Contract"`.

---

## 5. Binding modes — Weak / Strict / Paranoid

`SleipnirOptions.AliasBindingMode` selects how strictly a consumer parameter
must be covered by its fragment. Each mode is a superset of the previous.
Default is **Weak** (`SleipnirHub/Extensions/SleipnirOptions.cs → AliasBindingMode`,
`SleipnirInvoker.cs → AliasBindingMode`).

**Enum:** `SleipnirCommon/Models/AliasBindingMode.cs → AliasBindingMode` —
`Weak`, `Strict`, `Paranoid`.

**Plumbing chain:** `SleipnirHub/Extensions/SleipnirOptions.cs → AliasBindingMode` →
`SleipnirHub/Extensions/SleipnirServiceCollectionExtension.cs → AddSleipnir`
("AliasBindingMode = options.AliasBindingMode") →
`SleipnirInvoker.cs → AliasBindingMode` (`= Weak` default).

| Mode | What it checks | What it does NOT change | Check site |
|------|----------------|------------------------|-----------|
| **Weak** (default) | Duck-typed; silent defaults. | — | none (cost-neutral) |
| **Strict** | Each `@alias`-sourced parameter must be **fully covered at the top level** — every public read-write property of the consumer type present in the fragment (case-insensitive); literals not re-checked; nested objects not descended. | Cross-kind still 400; widening still accepted; the safe subset direction still binds. | `SleipnirInvoker.cs → StrictBindingCheck`, invoked from `→ ResolveParameterValues` |
| **Paranoid** | Strict + (a) checks **all parameters including literals** the caller sent, and (b) checks **recursively**, descending into nested object properties and array elements. | Cross-kind still 400; widening still accepted; safe subset still binds. | `SleipnirInvoker.cs → ParanoidBindingCheck`, invoked from `→ ResolveParameterValues` (literal-only and post-alias) |

**`RequiredPropertyNames`** (`SleipnirInvoker.cs → RequiredPropertyNames`) defines the
top-level property set Strict checks: skips Nullable/enum/string, indexers,
get-only and non-public setters.

### Exact 400 messages

**Strict** (`SleipnirInvoker.cs → StrictBindingCheck`):

> `Strict alias binding: parameter '{ParamName}' ({ParameterType.Name}) requires property {list}, which is absent from the '@{Alias}' fragment. In weak mode this would be silently defaulted; in strict mode it is rejected.`

**Paranoid** (`SleipnirInvoker.cs → ParanoidBindingCheck`):

> `Paranoid binding: parameter '{paramName}' ({paramType.Name}) is not fully covered by its fragment. Missing: {list}. In weak mode these would be silently defaulted; in strict mode the top-level check would pass (it checks only @alias parameters and does not recurse); paranoid mode enforces full coverage of every parameter — including literals — at every depth.`

**Unresolved (Serial path)** (`SleipnirInvoker.cs → ResolveParameterValues`):

> `Unresolved dependencies: {alias1, alias2, …}`

The Strict/Paranoid coverage check reads the fragment **case-insensitively**
(`HashSet<string>(StringComparer.OrdinalIgnoreCase)` in `SleipnirInvoker.cs → StrictBindingCheck`
and `→ CollectMissing`), so a PascalCase consumer `Id` is covered by a camelCase
fragment `id`.

**`CollectMissing` (recursive)** (`SleipnirInvoker.cs → CollectMissing`): required
props; present dict OrdinalIgnoreCase; missing → dotted path `P.X`,
`P.Address.Zip`; recurses into JsonObject nested and JsonArray elements via
`GetCollectionElementType`.

**`GetCollectionElementType`** (`SleipnirInvoker.cs → GetCollectionElementType`):
handles arrays, generic `List<>`/`IList<>`/`IEnumerable<>`/`ICollection<>`/
`IReadOnlyList<>`/`IReadOnlyCollection<>`/`HashSet<>`/`ISet<>`, interface search
excluding `IEnumerable<object>`. **Dictionaries explicitly excluded** — a
`Dictionary<K,V>` is treated as a collection of its values' shape only where
the code descends; dictionaries are not duck-typed key-by-key.

**`AliasReplacement` record** (`SleipnirInvoker.cs → AliasReplacement`):
`private readonly record struct AliasReplacement(string ParamName, string Alias, string FragmentJson)`,
populated in `ReplaceDependencyByAliasCore` only when the alias is
the direct value of a `SleipnirParameter` object.

Spec detail on modes: `DEPENDENCY_BINDING.md §"7. Binding modes"`;
"what neither changes" `§"What neither changes"`; the consumer⊆fragment
invariant `§"The invariant"`; where the checks live `§"Where the checks live"`.

---

## 6. The extract step — `DependencyResolver.ExtractValue`

`SleipnirCore/Services/Helper/DependencyResolver.cs → ExtractValue`:

```csharp
public static JsonNode? ExtractValue(JsonElement element, string jsonPath,
    int maxPathLength = 256, bool allowRecursiveDescent = true)
```

**JsonPath library: JsonPath.Net** (`using Json.Path;` in
`SleipnirCore/Services/Helper/DependencyResolver.cs`;
`CLAUDE.md §"Project Dependency Graph"` confirms the `SleipnirCore` ← `JsonPath.Net`
dependency). RFC 9535, case-sensitive (`PROTOCOL.md §"Casing Contract"`).

Guards: `maxPathLength` throws before parse; `allowRecursiveDescent` rejects
`$..`. The element is materialized via `JsonNode.Parse(element.GetRawText())` —
the already-camelCase result — then `JsonPath.Parse(jsonPath).Evaluate(root).Matches`
(all in `ExtractValue`).

**Match-count-aware behavior** (the key semantic):

| Matches | Result | Where |
|---------|--------|-------|
| 0 | `null` | `ExtractValue` — 0-match branch |
| 1 | `matches[0].Value` as-is — a scalar, or a whole array/object when the single match is one | `ExtractValue` — single-match branch |
| >1 | a `JsonArray` of all matches (DeepClone) | `ExtractValue` — multi-match branch |

A multi-match path (`$[*].Id`, `$..Id`) collects all matches into one
`JsonArray`, injected as **one list-typed parameter** (`List<T>`/`T[]`/
`IEnumerable<T>`) — **list fan-out into a parameter, never fan-out into N
requests** (`CLAUDE.md §"Dependency Chaining"`). The comment in `ExtractValue`
notes v1 collapsed to the first match; it now produces an array. `BuildParameters`
deserializes a list fragment via `Deserialize<List<T>>`.

---

## 7. ExposedDependencies — 2xx-only & provider-failure propagation

**Model field:** `SleipnirCommon/Models/SleipnirResponse.cs → ExposedDependencies` —
`public Dictionary<string, string>? ExposedDependencies { get; set; }`
(alias → serialized fragment). `IsSuccess` (`SleipnirCommon/Models/SleipnirResponse.cs → IsSuccess`)
is `Code >= 200 && <= 299`.

**2xx-only extraction gate** (`SleipnirInvoker.cs → ExecuteAuthorized`):

```csharp
if (request.DependencyMapping != null && result != null && result.IsSuccess && result.Data.HasValue)
```

Inside `ExecuteAuthorized`, the per-alias loop calls
`DependencyResolver.ExtractValue(result.Data.Value, jsonPath, MaxDependencyPathLength, AllowRecursiveDescent)`
and stores the fragment via `extracted.ToJsonString()`. A thrown exception →
log a warning and skip the alias. The loop's comment states explicitly that
**error payloads** (e.g. a `ProblemDetails` `title`/`status`) are **never
extracted** even when they path-match. `result.ExposedDependencies = exposed;`.

> **Consequence:** a non-2xx result (business `SleipnirResults` error, thrown
> exception → 500, or a 401/missing-route from the serial auth pre-pass) leaves
> `ExposedDependencies` empty, so no value is ever extracted from an error
> payload.

### Provider-failure dependent propagation (topological path only)

When a provider fails authorization, errors, or declares an alias it does not
actually expose, its **dependents do not run** — they receive an explanatory
`400` instead of reaching the missing alias at runtime with the uninformative
`Unresolved dependencies`. Built in `ExplainUnavailability`
(`SleipnirInvoker.cs → ExplainUnavailability`), called from
`ExecuteDependentRequestAsync` (`SleipnirInvoker.cs → ExecuteDependentRequestAsync`):

| Condition | Message | Where |
|-----------|---------|-------|
| No provider exposes the alias | `Dependency '@{alias}' unavailable: no provider exposes '@{alias}'.` | `ExplainUnavailability` |
| Provider produced no result | `Dependency '@{alias}' unavailable: provider '{providerKey}' produced no result.` | `ExplainUnavailability` |
| Provider non-2xx (401 special-cased) | `Dependency '@{alias}' unavailable: provider '{providerKey}' was unauthorized (401).` (or `… returned HTTP {code}.`) | `ExplainUnavailability` |
| Provider succeeded but did not expose the alias | `Dependency '@{alias}' unavailable: provider '{providerKey}' did not expose '@{alias}'.` | `ExplainUnavailability` |

Three properties (`DEPENDENCY_BINDING.md §"9. Provider failure"`): the dependent
does not run; propagation is **transitive** — a skipped provider has no
`ExposedDependencies`, so its own dependents are caught in the next batch; scope
is **topological only** — Parallel/Serial have no providers by definition.
Per-request authorization still holds (`DEPENDENCY_BINDING.md §"Batch authorization per-request"`),
the serial auth pre-pass rationale is at `§"Why authorization before fan-out"`
(see `CLAUDE.md §"Core Engine (`SleipnirCore`)"` for the `HttpContext` batch
safety contract).

---

## 8. The fluent builder — `SleipnirCall.Exposes` / `WithAlias`

`SleipnirClient/Sleipnir/SleipnirCall.cs`:

- `Exposes(string jsonPath, string alias)` (`SleipnirCall.cs → Exposes`) stores
  `_exposedDependencies[alias] = jsonPath;`. Its doc comment states the path is
  **result-relative**: `$` = whole result, `$.Id`/`$.Name` properties, `$[0].Id`
  first list element; **no `$.data` envelope level** (a `$.data` path only
  matches if the result itself has a `data` property).
- `WithAlias(string dependencyPlaceholder)` (`SleipnirCall.cs → WithAlias`) stores
  the raw `@alias` string as a native `JsonValue`.
- `ToRequest()` (`SleipnirCall.cs → ToRequest`) packs `_exposedDependencies`
  into `DependencyMapping` only if non-empty.

> **Note:** `SleipnirCall.cs` exposes only the single-arg `WithAlias(string)`
> (→ `WithAlias`); there is no two-argument `WithAlias("@alias", "default")`
> overload and no implicit default fallback in v1 — an unresolved `@alias` fails
> the call. `CLAUDE.md` was corrected accordingly (see §14).

---

## 9. Topological batch execution — `DependencyGraphBuilder`

`SleipnirCore/Services/Helper/DependencyGraphBuilder.cs → SortByDependencyBatches` —
`SortByDependencyBatches(List<SleipnirRequest>)`. Kahn's algorithm, batch-based:

1. **Provider map + request index**: `providers[alias] = id` from each
   request's `DependencyMapping` keys; the provider id falls back to
   `$"{Controller}.{Method}"` when empty.
2. **Dependency edges**: `ExtractAliases(request.Params)` collects `@alias`
   tokens; resolves each via `providers`; self-deps skipped.
3. **Kahn batches**: while remaining, a batch is the remaining requests whose
   deps are all `completed`; if a batch is empty → **cycle** →
   `InvalidOperationException` with the message
   `"Cycle detected in dependencies. Involved requests: …"`; append the batch,
   move to completed.

**`ExtractAliases`** (`SleipnirCore/Services/Helper/DependencyGraphBuilder.cs → ExtractAliases`)
returns a `HashSet<string>(StringComparer.Ordinal)`. `CollectAliases`
(`SleipnirCore/Services/Helper/DependencyGraphBuilder.cs → CollectAliases`) scans
`JsonValue` strings starting with `@`; the alias name is the alphanum+`_` run
after `@`; recurses into JsonObject/JsonArray.

**Topological executor** — `SleipnirInvoker.cs → ExecuteInDependencyBatches`:
calls `SortByDependencyBatches`; a cycle → 400 for **all** requests,
`"Circular dependency detected in request batch."`; builds `aliasToProvider`;
per batch runs a **serial auth pre-pass** then `Task.WhenAll` fan-out via
`ExecuteDependentRequestAsync`; writes results to `priorResponses` keyed by
`GraphKey`. `GraphKey` (`SleipnirInvoker.cs → GraphKey`) is the request id or
`Controller.Method`. `ExecuteDependentRequestAsync`
(`SleipnirInvoker.cs → ExecuteDependentRequestAsync`): auth error → traced
response; `ExplainUnavailability` propagation; `ResolveParameterValues`;
`ExecuteAuthorized`.

---

## 10. Configuration reference (binding & cardinality knobs)

All on `SleipnirHub/Extensions/SleipnirOptions.cs`, plumbed to the invoker
singleton in one block (`SleipnirHub/Extensions/SleipnirServiceCollectionExtension.cs → AddSleipnir`):

| Option | Type | Default | Where | Notes |
|--------|------|---------|-------|-------|
| `AliasBindingMode` | `AliasBindingMode` | `Weak` | `AliasBindingMode` | Weak / Strict / Paranoid (§5). Plumbed at `SleipnirServiceCollectionExtension.cs → AddSleipnir` ("AliasBindingMode = options.AliasBindingMode"); invoker field `SleipnirInvoker.cs → AliasBindingMode`. |
| `MaxDependencyPathLength` | `int` | 256 | `MaxDependencyPathLength` | JsonPath length cap (0 = unlimited); passed to `ExtractValue`. Invoker `SleipnirInvoker.cs → MaxDependencyPathLength`; enforced `SleipnirCore/Services/Helper/DependencyResolver.cs → ExtractValue`. |
| `AllowRecursiveDescent` | `bool` | `true` | `AllowRecursiveDescent` | Rejects `$..` when false. Invoker `SleipnirInvoker.cs → AllowRecursiveDescent`; enforced `SleipnirCore/Services/Helper/DependencyResolver.cs → ExtractValue`. |
| `MaxParameterArrayLength` | `int` | 1000 | `MaxParameterArrayLength` | Caps `@alias` whole-collection passthrough; `string`/`byte[]` excluded. Invoker `SleipnirInvoker.cs → MaxParameterArrayLength`; enforced `SleipnirInvoker.cs → BuildParameters`. |
| `MaxResultElementCount` | `int` | 10000 | `MaxResultElementCount` | Caps materialized collections + `IAsyncEnumerable` early-stop. Invoker `SleipnirInvoker.cs → MaxResultElementCount`; enforced `SleipnirInvoker.cs → ReturnResponse` and `SleipnirInvoker.cs → AsyncEnumerableConsumer.Consume` (413 / `ResultCardinalityExceededException`). |

> **Cardinality-cap 400 message** (`SleipnirInvoker.cs → BuildParameters`):
> `Parameter '{name}' exceeds MaxParameterArrayLength ({MaxParameterArrayLength}; is {colParam.Count}). Paginate or raise the cap (0 = unlimited).`

**North-bound hardening** (`BEST_PRACTICES.md §"1.1 Authenticate upstream"`)
recommends lowering `MaxDependencyPathLength = 128` and
`AllowRecursiveDescent = false` for internet-facing hosts.

---

## 11. DevUI static checker — `dependencyCheck.ts`

`SleipnirDeveloperUi/src/lib/utils/dependencyCheck.ts` is the **one place with
both schemas** (provider return + consumer parameter from discovery) and
statically reproduces the runtime rules. The header comment explains it
reproduces `DependencyResolver.ExtractValue` match-count semantics, notes
camelCase wire vs PascalCase schema + case-sensitive JsonPath, and is
advisory/non-blocking.

| Export | Where | Purpose |
|--------|-------|---------|
| `Severity = 'error' \| 'warn' \| 'info'`, `CheckIssue` | `Severity` / `CheckIssue` | Issue shape. |
| `parsePath` | `parsePath` | Subset JsonPath parser: `$`, `$.prop`, `..desc`, `$[n]`, `$[*]`; rejects filters/slices/`['key']`. |
| `evalPath` | `evalPath` | Walks `TypeShape`; `wild`/`desc` set `multi = true`; opaque `unknown` → `opaque: true`. |
| `kindsCompatible` | `kindsCompatible` | Same kind ok; cross-kind (number↔string, object↔scalar, array↔scalar) false; opaque/acceptsAny → true. |
| `analyzeObjectBinding` | `analyzeObjectBinding` | Returns `missing` (value-type, silent default), `missingRef` (→ null), `kindMismatch` (overlapping prop → 400). |
| `compatible` | `compatible` | The core comparator: array→scalar error with `$[0]`/`$[*]` hint; scalar→list-param error; object→object duck-typing — same type okMatch, missing TypeMeta warn, kindMismatch error (mentions STJ 400), missing value-type warn "still default (kein 400)", missing reference warn "null", consumer⊆provider okMatch; scalar==scalar okMatch; else error. |
| `checkExpose` | `checkExpose` | Validate one expose path against the return schema; no return error, unsupported error, opaque warn, not-found error "Unresolved" + hints, multi-match info. |
| `checkAliasBinding` | `checkAliasBinding` | Validate an `@alias` consumer param against the provider path; multi → `array<shape>`; ok-with-info/warn → inline issue, else error. |
| `checkSteps` | `checkSteps` | **The summary aggregator** — builds the `providers` map (last-wins), pushes error+warn issues (skips info), returns ordered `CheckIssue[]`. This is the source of the DevUI summary box. |

The summary box and "Send anyway" (the chain sends regardless — the issues are
non-blocking inline warnings + a summary box) are described in
`README_DETAILS.md §"DevUI static checker"` and
`DEPENDENCY_BINDING.md §"6. DevUI static checker"`.

---

## 12. Diagnostics & troubleshooting catalog

### The common mistakes

- **PascalCase JsonPath → `Unresolved`.** `$.Id` matches nothing on the
  camelCase wire document; use `$.id`. The dependent gets the propagation 400
  `… did not expose '@cid'.` (`SleipnirTests/Unit/Core/AliasBindingTests.cs → Alias_JsonPath_PascalCase_DoesNotMatch`).
- **Cross-kind scalar → 400.** No `AllowReadingFromString`, so `"42"`→`int`
  and `42`→`string` both reject with `Parameter 'X' cannot be converted to type 'Y'.`
  (`DEPENDENCY_BINDING.md §"2. The four outcomes"` / `§"What is rejected"`).
- **Missing value-type property → silent default (Weak).** The insidious case:
  a consumer `int Active` absent from the fragment binds `0`, no error. Use
  Strict or Paranoid to catch it
  (`SleipnirTests/Unit/Core/AliasBindingTests.cs → Alias_MissingValueTypeProperty_ConsumerWider_SilentlyDefaults`,
  `SleipnirTests/Unit/Core/AliasBindingStrictTests.cs → Strict_MissingValueTypeProperty_Rejects400`).
- **`$.data` envelope assumption.** There is **no** `$.data` level; `$` is the
  whole result. A `$.data.Id` path only matches if the result itself has a
  `data` property (`SleipnirClient/Sleipnir/SleipnirCall.cs → Exposes`).
- **Throwing `SleipnirException` to set a code.** The server has no
  `catch(SleipnirException)`; it becomes a generic 500. Control the code via
  `SleipnirResults.Error(...)` and return a `SleipnirResponse`
  (`CLAUDE.md` §"Error Handling").

### Exact error strings

- **Strict:** `Strict alias binding: parameter '{P}' ({Type}) requires property {list}, which is absent from the '@{alias}' fragment. In weak mode this would be silently defaulted; in strict mode it is rejected.` (`SleipnirInvoker.cs → StrictBindingCheck`)
- **Paranoid:** `Paranoid binding: parameter '{P}' ({Type}) is not fully covered by its fragment. Missing: {list}. …` (`SleipnirInvoker.cs → ParanoidBindingCheck`)
- **Unresolved (Serial):** `Unresolved dependencies: {alias1, alias2, …}` (`SleipnirInvoker.cs → ResolveParameterValues`)
- **Propagation:** see §7 table.
- **Cycle:** `Cycle detected in dependencies. Involved requests: …` (graph builder, `SleipnirCore/Services/Helper/DependencyGraphBuilder.cs → SortByDependencyBatches`); topological executor wraps as `Circular dependency detected in request batch.` (`SleipnirInvoker.cs → ExecuteInDependencyBatches`).
- **Cardinality cap:** `Parameter '{name}' exceeds MaxParameterArrayLength ({MaxParameterArrayLength}; is {count}). …` (`SleipnirInvoker.cs → BuildParameters`); result cardinality → 413 (`SleipnirInvoker.cs → ReturnResponse`).

### Reconnect/authorization note

Per-request authorization holds even in a chain: the serial auth pre-pass runs
before the fan-out, so a 401 on one request does not abort the others
(batch failure is per-request, JSON-RPC-conformant). See
`CLAUDE.md §"Core Engine (`SleipnirCore`)"` and
`DEPENDENCY_BINDING.md §"Batch authorization per-request"` / `§"Why authorization before fan-out"`.

---

## 13. How it is verified (the tests)

| Test file | Covers |
|-----------|--------|
| `SleipnirTests/Unit/Core/AliasBindingTests.cs` | Weak-mode end-to-end over `SleipnirInvoker` (fixture `DependencyChainController`, registered in the `AliasBindingTests` constructor): compatible int→int, widening int→long, object→object same type, subset-safe (drops Name/Id), subset fan-out (one alias many consumers), missing reference → null (2xx), missing value-type → silent false (2xx, insidious), cross-kind number→string 400, string→int 400, whole object→scalar int 400, kind-mismatch on overlapping property 400, unresolved `@bogus` 400, JsonPath PascalCase `$.Id` → "did not expose" / "provider 'p'". Uses raw `@alias` JsonValue (not JSON-quoted). |
| `SleipnirTests/Unit/Core/AliasBindingStrictTests.cs` | Strict mode (constructor sets `AliasBindingMode = Strict`): allows subset-safe + fan-out + compatible scalar; rejects missing value-type (expects "Strict alias binding"/"Active"/"@cust") and missing reference ("Name"); cross-kind still 400; kind-mismatch with full coverage still 400; kind-mismatch + missing reports missing first; property match case-insensitive (PascalCase consumer `Id` reads camelCase `id`). |
| `SleipnirTests/Unit/Core/AliasBindingParanoidTests.cs` | Paranoid mode (constructor sets `AliasBindingMode = Paranoid`): literals checked (missing value-type "Active"/`'d'`, missing reference); recursive nested missing `Address.Zip`; array element `[1]` missing nested (`list[1]`/`Zip`); full nested/array binds. Delta proof vs Strict: Strict literal-missing-value-type still 2xx (`Strict_LiteralMissingValueType_Still2xx`), Strict nested-missing-Zip still 2xx (`Strict_LiteralNestedMissingZip_Still2xx`). @alias-path Paranoid mirrors. Invariants: scalar literal not over-checked, widening int→long, subset literal binds, `List<int>` scalar-element literal binds (no recursion). |
| `SleipnirTests/Unit/Core/DependencyResolverTests.cs` | `ExtractValue`: simple path, nested, array index `$[1]`, string value, non-existent → null, root `$` over scalar, wildcard `$.items[*].id` → array `[1,2,3]`, wildcard over bare array `$[*].id`, wildcard string projection, single-match `$[0].id` returns scalar (not 1-element array), root `$` over array returns array as single match. |
| `SleipnirTests/Unit/Core/DependencyGraphBuilderTests.cs` | No deps → single batch; linear chain a→b→c → 3 batches; diamond a→{b,c}→d → 3 batches; cycle → `InvalidOperationException` (`"*Cycle*"`); empty → empty; independent + dependent → 2 batches. |
| `SleipnirTests/Fixtures/TestControllers.cs → DependencyChainController` | The `DependencyChainController` fixture with all the chain methods (`EchoLong`, `MakeDto`, `EchoDto`, `MakeDtoList`, `TakeIdOnly`, `TakeIdActive`, `MakeOrder`, `MakeOrderNoZip`, `TakeOrder`, `TakeOrderList`, …). |

The header of `DEPENDENCY_BINDING.md` (intro) points to `AliasBindingTests.cs`
as the **executable spec**.

---

## 14. Relationship to other docs

| Doc | Covers (binding-relevant) |
|-----|----------------------------|
| `DEPENDENCY_BINDING.md` | **The authoritative specification** — 3-step pipeline, four outcomes, object→object duck-typing, subset fan-out, three casing regimes, DevUI static checker, Weak/Strict/Paranoid modes with exact 400 messages, what does NOT flow (byte[]/CancellationToken), provider-failure & dependent propagation. |
| `PROTOCOL.md` | Wire spec: "Dependency Chaining" (`§"Dependency Chaining"`, cycle 400, propagation messages, JsonPath extraction), "Alias Serialization & Type Binding" (`§"Alias Serialization & Type Binding"`, pipeline, four-outcome table, modes with Strict/Paranoid 400 text, DevUI note), "Casing Contract" (`§"Casing Contract"`, three-regime table, cross-language consequences), "Limits" (`§"Limits"`, one-alias→one-value, no fan-out into N requests, server-side cardinality caps `MaxParameterArrayLength`/`MaxResultElementCount`). |
| `README_DETAILS.md` | User-facing overview "Dependency Chaining — Binding, Types & Casing" (`§"Dependency Chaining — Binding, Types & Casing"`): pipeline, four outcomes, subset fan-out, binding modes, casing contract, DevUI static checker, binary-not-through-aliases, provider failure & propagation table, per-request authorization. |
| `CLAUDE.md` | Architecture pointers: JsonPath.Net dependency (`§"Project Dependency Graph"`), Serial/topological/auto-detect (`§"Core Engine (`SleipnirCore`)"`), `HttpContext` batch pre-pass + dependent propagation paragraph (`§"Core Engine (`SleipnirCore`)"`), `DependencyGraphBuilder` Kahn's (`§"Core Engine (`SleipnirCore`)"`), `SleipnirCall.Exposes` result-relative (`§"Client Library (`SleipnirClient`)"`), `SleipnirResponse.ExposedDependencies` (`§"Request/Response Model"`), "Dependency Chaining" section (`§"Dependency Chaining"`). |
| `BEST_PRACTICES.md` | North-bound hardening: `MaxDependencyPathLength = 128`, `AllowRecursiveDescent = false` (`§"1.1 Authenticate upstream"`); scattered `@alias` mentions for batching/N+1 (`§"3.2 CRUD baseline"`, `§"4.2 batch beats REST loop"`, `§"Where speedup comes from"`, `§"4.5 Migrating incrementally"`). **No dedicated dependency-binding section.** |
| `TRANSPORT_REFERENCE.md` | Transport-level request/response model that carries `DependencyMapping`/`ExposedDependencies`. |
| `EVENTS_REFERENCE.md` | Events are **not chainable** — `@alias`/`exposes` apply to call results, not event streams (`§1`). |
| `CODEGEN_REFERENCE.md` | `@alias` literal serialization in generated clients, the `@`-normalization fix. |

> **Doc-bugs addressed:** the `CLAUDE.md` two-argument `WithAlias("@alias", "default")`
> overload was not present in `SleipnirCall.cs` (only the single-arg form at
> `SleipnirClient/Sleipnir/SleipnirCall.cs → WithAlias`); there is no implicit
> default fallback in v1, so an unresolved `@alias` fails the call. **Fixed** —
> `CLAUDE.md §"Client Library (`SleipnirClient`)"` now shows `.WithAlias("@alias")`
> and states the no-fallback contract. The German cardinality-cap and cycle
> messages (`SleipnirInvoker.cs → BuildParameters`,
> `SleipnirCore/Services/Helper/DependencyGraphBuilder.cs → SortByDependencyBatches`)
> were legacy strings CLAUDE.md flagged for opportunistic English migration.
> **Fixed** — both are now English ("…exceeds MaxParameterArrayLength…",
> "Cycle detected in dependencies…"), and the `DependencyGraphBuilderTests`
> assertion moved from `*Zyklus*` to `*Cycle*`.