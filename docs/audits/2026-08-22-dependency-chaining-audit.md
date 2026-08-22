# Dependency-Chaining Audit & Roadmap — 2026-08-22

> Full audit of the dependency-chaining feature (`@alias` / `DependencyMapping`) on top of
> v1.1.x. This file **extends** `docs/audits/2026-08-08-consolidation-roadmap.md`: findings
> that overlap an existing R-item are folded into it as amendments (marked ⤴), genuinely new
> work is added as D-items. Do not treat the overlapping items as separate tasks.
>
> Priority rule mirrors the consolidation roadmap: correctness defects first (hotfix train),
> hardening/consolidation into the 1.2 train where they share files with R7–R9.
>
> **Global Definition of Done (every item):** regression test(s), CHANGELOG entry,
> `DEPENDENCY_BINDING.md` / `DEPENDENCY_BINDING_REFERENCE.md` updated where user-visible,
> all new user-facing text in English.

---

## Part A — Audit summary

### Architecture (verified end-to-end)

| Stage | Location | Behavior |
|---|---|---|
| Wire model | `SleipnirCommon`: `SleipnirRequest.DependencyMapping`, `SleipnirResponse.ExposedDependencies` | alias → result-relative JsonPath / alias → fragment JSON string; covered by JSON converter + MessagePack formatter |
| Routing | `SleipnirCore/Services/SleipnirInvoker.cs`, `InvokeDi(batch)` | any non-empty `DependencyMapping` forces `ExecuteInDependencyBatches` regardless of requested `ExecutionMode` |
| Topology | `SleipnirCore/Services/Helper/DependencyGraphBuilder.cs` | Kahn batch sort; provider map from mapping keys; edges from `ExtractAliases`; cycle → per-request 400 |
| Per batch | `SleipnirInvoker.ResolveAndAuthorizeAsync` then `Task.WhenAll(ExecuteDependentRequestAsync)` | serial auth pre-pass (only HttpContext contact), context-free parallel fan-out |
| Extraction | `SleipnirInvoker.ExecuteAuthorized` → `DependencyResolver.ExtractValue` | only on 2xx + data present; match-count semantics (0→null, 1→node, >1→cloned `JsonArray`); caps: `MaxDependencyPathLength`, `AllowRecursiveDescent` |
| Substitution/binding | `ResolveParameterValues` → `ReplaceDependencyByAliasCore`, `StrictBindingCheck`, `ParanoidBindingCheck` | deep-cloned params tree, merge of all prior `ExposedDependencies`, native `@…` strings replaced by parsed `JsonNode`; binding via `JsonSerializer.Deserialize(JsonNode, type)` case-insensitive, no `AllowReadingFromString` |

Verified sound (implemented *and* tested): cycle detection incl. transitive propagation
(`ParallelAuthPropagationTests`), 2xx gate on extraction (no leak from error payloads),
match-count semantics incl. `$`-over-array, serial-path result ordering, caps
(`NorthBoundHardeningTests`), DevUI static checker parity (`dependencyCheck.ts`).

### Findings

| # | Severity | Finding |
|---|---|---|
| F1 | P1 | **Nondeterministic alias resolution with duplicate providers.** `aliasToProvider` takes last-in-request-order (`SleipnirInvoker.cs` ~700), while `ResolveParameterValues` merges `ExposedDependencies` of *all* `priorResponses.Values` — `ConcurrentDictionary.Values` is unordered. Availability check may pass against provider A while provider B's fragment is injected. Untested. |
| F2 | P1 | **No escape for literal `@` strings + Trim mismatch.** `ContainsAlias` uses `Trim().StartsWith("@")` (~1225, incl. a commented-out escape check); `ReplaceDependencyByAliasCore` matches without Trim; `DependencyGraphBuilder.ExtractAliases` is a third variant. Consequences: literal `"@username"` is blocked ("no provider exposes") or silently substituted; `" @x"` is detected but never substituted nor booked unresolved. Documented behavior (DEPENDENCY_BINDING.md §1), but footgun and mismatch are not. Already listed as "Single alias grammar" in the 08-08 roadmap (R9.4). |
| F3 | P2 | **GraphKey collisions.** Id-less requests on the same route collide in `requestById` (graph builder overwrites), `priorResponses` (last write wins), and `ExecuteSequentially.responses` (`TryAdd` fails silently for the second). An id equal to another request's `Controller.Method` collides too → wrong alias source possible. No validation/warning. Partially addressed by R9.3's shared `RequestKeys.Of`. |
| F4 | P2 | **Auth-vs-resolution order inconsistent between paths.** Serial path (`ExecuteSequentially`): `ResolveParameterValues` runs *before* authorization → unresolved alias yields 400 where the request deserves 401. Topological path: auth first. No security impact, but client-visible semantics differ per mode. |
| F5 | P2 | **Strict/Paranoid ignore STJ metadata.** `RequiredPropertyNames` reads raw CLR properties: `[JsonIgnore]` props demanded as required (false-positive 400s), `[JsonPropertyName]` renames compared under CLR name (false positives *and* negatives). DEPENDENCY_BINDING.md §7 claims STJ-equivalent matching — untrue for attributed types. No tests with either attribute. |
| F6 | P2 | **Extraction failures masked as "did not expose".** `ExtractValue` failure (invalid path, cap violation) is only logged; response stays 2xx without `ExposedDependencies`. Dependent correctly gets the propagation 400 but with the misleading cause. |
| F7 | P3 | Redundant double round-trip in `ReplaceInParent` (parse → serialize → parse). Perf/cosmetic; folded into R9.6 (DeepClone). |
| F8 | P3 | Self-dependency (request consumes its own alias) passes the graph builder silently and fails at runtime instead of being reported as a configuration error. |
| S1 | P0-context | `MaximumBatchSize` defaults to 0 (unbounded) and Kahn is O(V²) worst case; filter expressions (`$[?(@…)]`) remain legal CPU amplifiers over large provider results. **Note:** STABILITY §3.6 forbids tightening the default within 1.x — the fix is posture, not default change (⤴ R8b). |
| S2 | note | Batch path bypasses the interceptor pipeline — own auth interceptors do not run on batches, only `CheckAuthorisation`. Must be explicit in SECURITY.md (⤴ R5/R7). |

---

## Part B — Execution plan

### Train 1 — hotfix (with or directly after `hotfix/1.1.2`)

Small, independently shippable correctness fixes. If the 1.1.2 train has already branched,
these form `hotfix/1.1.3`.

**Status 2026-08-22:** D1 ✅ landed (working tree), D2 ✅ landed (working tree),
D3 ✅ landed (working tree). Implementation notes beyond the original plan:
- D2's escape unescaping lives **centrally in `BuildParameters`** (all paths: single,
  parallel, topological, subscribe) — not in the substitution walk, which only the
  topological path runs.
- D3's gate was moved up into `InvokeDi` so it applies to **all batch modes**: duplicate
  ids also break client response correlation even without any aliases involved.
  One existing test (`InvokeDi_ParallelBatch_ExecutesAllRequests`) exposed that its
  helper-derived ids (`Controller.Method`) collided for two same-route requests — exactly
  the footgun this gate exists for; test fixed with distinct ids.

#### D1 — Reject duplicate alias providers at batch entry (F1)

**Why.** Two requests exposing the same alias make resolution nondeterministic
(`ConcurrentDictionary.Values` merge order vs. last-write `aliasToProvider`). Silent wrong-data
class — worse than a loud failure.

**Change.** In `ExecuteInDependencyBatches` (and `ExecuteSequentially` for symmetry), after
building the provider map: if an alias is declared by more than one request, return per-request
400 `Duplicate alias 'x': provided by '<key1>' and '<key2>'.` — fail-loud, same style as the
registration-time name-uniqueness rule. Ordinal comparison, consistent with `aliasToProvider`.

**Tests.** New `AliasCollisionTests`: two providers for one alias → both get 400, neither
executes; distinct aliases unaffected; collision across different batches still detected
(provider map is built from the full request list up front).

**Effort.** S. **Depends on.** —

#### D2 — Single alias grammar: trim-free detection + escape rule (F2, ⤴ R9.4)

**Why.** Three detection sites disagree (`ContainsAlias` trims, replacer doesn't, graph builder
is a third variant); literals starting with `@` are unusable; `" @x"` falls between the cracks.
R9.4 already mandates one parser function — this defines its contract and pulls it forward
from the 1.2 refactor train because the current behavior is user-visible breakage, not just
duplication.

**Change.**

1. One internal static helper (e.g. `SleipnirCore/Services/Helper/AliasGrammar.cs`) with
   `TryReadAlias(string value, out string alias)` used by `ContainsAlias`,
   `ReplaceDependencyByAliasCore`, and `DependencyGraphBuilder.ExtractAliases`/`CollectAliases`.
   **Trim-free**: only `value.StartsWith("@")` counts.
2. Escape rule: `@@foo` is the literal string `@foo` (double-`@` prefix strips one `@`).
   Update all three sites; a lone `"@"` is not an alias.
3. Remove the commented-out escape experiment in `ContainsAlias`.
4. Document in `DEPENDENCY_BINDING.md` §1: `@`-prefixed literals require `@@`; breaking-change
   note in CHANGELOG for anyone who relied on trimmed detection (unlikely, undocumented).

**Tests.** Literal `"@user"` with no provider → clean 400 unresolved (not silent pass-through);
literal `" @x"` stays literal everywhere (detection, substitution, graph edges);
`"@@order"` reaches the controller as `"@order"`; alias named `@order` still resolves;
graph builder does not create an edge for escaped literals.

**Effort.** M (three call sites + wire-semantics decision + docs).
**Depends on.** — (coordinates with R9.4: land the helper now, R9 consumes it during the seam extraction).

#### D3 — Batch-entry key validation (F3)

**Why.** Duplicate ids / id-less same-route requests collide in `GraphKey` space and can bind
an alias to the wrong provider's response.

**Change.** At batch entry (both batch paths): reject with per-request 400 when (a) two requests
share a non-empty id, or (b) two requests have an empty id and the same `Controller.Method`.
An id that equals another request's `Controller.Method` fallback also collides — validate the
effective `GraphKey` set for uniqueness instead of the raw fields. Message names the colliding keys.

**Tests.** Duplicate ids → 400s; two id-less calls to the same route in one batch → 400s;
same route twice *with* distinct ids → works (regression guard against over-blocking).

**Effort.** S. **Depends on.** —

### Train 2 — release/1.2.0 (lands with R7–R9, shared files)

**Status 2026-08-22:** D4 ✅, D5 ✅, D6 ✅, D7 ✅ landed (working tree). Implementation
notes beyond the original plan:
- D5: the coverage check now works on (wire-name → CLR-property) pairs — the recursion in
  `CollectMissing` descends via the CLR property while comparing fragment keys against the
  wire name; results cached per type (`CoverablePropertiesCache`). **Wire-visible change:**
  Strict/Paranoid error messages now name camelCase wire names (`'name'`, `'address.zip'`)
  instead of CLR names (`'Name'`, `'Address.Zip'`) — three existing tests updated accordingly.
- D6: only *exceptional* extraction failures get the new diagnosis (`invalid JsonPath`,
  `extraction error`); a valid path that matches nothing remains the documented
  "did not expose" case.
- D7: the graph builder throws with a specific message; the invoker's invalid-graph catch
  now forwards `ex.Message` instead of the hardcoded cycle text.

#### D4 — Align serial-path auth/resolution order (F4)

**Why.** Serial path answers 400 (unresolved alias) before checking authorization; topological
path checks auth first. Client-visible inconsistency; also leaks route existence to unauthorized
callers in the serial path (resolution errors reveal mapping shape).

**Change.** In `ExecuteSequentially`, run `ResolveAndAuthorizeAsync` before
`ResolveParameterValues`, mirroring the topological path. Keep the existing result-ordering
guarantee (index-exact responses).

**Tests.** Unauthorized request with an unresolvable alias → 401 (not 400); authorized request
unchanged; ordering test extended to assert status precedence.

**Effort.** XS-S. **Depends on.** — (touches `ExecuteSequentially`; schedule adjacent to R9 seam work to avoid conflicts).

#### D5 — Strict/Paranoid honor STJ metadata (F5)

**Why.** `RequiredPropertyNames` produces false 400s for `[JsonIgnore]` members and mis-keys
`[JsonPropertyName]` renames — documented as STJ-equivalent, isn't. Contract-types with
attributes are common enough that this will bite real users of Strict/Paranoid.

**Change.** Extend `RequiredPropertyNames` (and the Paranoid recursive walk) to consult STJ
metadata via `JsonTypeInfo`/`DefaultJsonTypeInfoResolver` (or attribute inspection where
resolver access is unavailable): skip `[JsonIgnore]` (respect `Condition`), compare under the
wire name from `[JsonPropertyName]`/naming policy. Cache per type
(`ConcurrentDictionary<Type, …>` — folds into R9.5's caching item).

**Tests.** Strict: consumer type with `[JsonIgnore]` prop + fragment lacking it → 2xx;
`[JsonPropertyName("id")]` prop satisfied by fragment key `id` → 2xx, and by `Id` only under
case-insensitive match of the *wire* name; Paranoid: same recursively through nested objects
and `List<T>` elements. Add the missing tests called out in the audit.

**Effort.** M. **Depends on.** — (file-shared with R9.5; implement together).

#### D6 — Honest extraction-failure diagnostics (F6)

**Why.** Provider-side extraction failures surface to clients as "did not expose", sending
debuggers down the wrong path.

**Change.** When `ExtractValue` throws in `ExecuteAuthorized`, record the reason on the
response (internal field or side-channel keyed by `GraphKey`, e.g. a per-batch
`extractionFailures` map) and let `ExplainUnavailability` append
`provider '<id>' failed to extract '@a' (<reason>)` instead of "did not expose". Reason text
stays generic unless `EnableDetailedErrors` (no path contents leaked by default).

**Tests.** Invalid JsonPath in a provider mapping → dependent gets the propagation 400 with
the extraction-failure wording; with detailed errors off, no path/payload fragments in the message.

**Effort.** S. **Depends on.** —

#### D7 — Self-dependency rejected at graph build (F8)

**Why.** A request consuming its own alias is always a configuration error but currently fails
late with a runtime-unresolved message.

**Change.** In `DependencyGraphBuilder.SortByDependencyBatches` (and the invoker's cycle catch),
treat an edge from a request to itself as an immediate invalid-graph case → per-request 400
`Request '<key>' depends on its own alias '@a'.`

**Tests.** Self-referencing mapping → immediate 400 with the specific message; normal chains
unaffected.

**Effort.** XS. **Depends on.** —

### Amendments to existing items (no new code tracks)

| Item | Amendment |
|---|---|
| ⤴ **R8b** (`MaximumBatchSize` posture) | Confirmed as the correct treatment of S1: keep default 0 in 1.x (STABILITY §3.6), set explicit cap in templates/samples, elevate SECURITY.md recommendation, queue non-zero default for 2.0. Additionally note the O(V²) Kahn rescan in the R9 invoker-refactor acceptance criteria (batch entry validation from D1/D3 slightly reduces V; a proper O(V+E) rewrite belongs to R9, not the hotfix train). |
| ⤴ **R9.3** (`RequestKeys.Of`) | Fold in the D3 validation so key construction and collision detection live in one place. |
| ⤴ **R9.4** (single alias grammar) | Superseded in scope by D2 — R9.4 shrinks to "consume `AliasGrammar` during the seam extraction"; the grammar itself ships in the hotfix train. |
| ⤴ **R9.6** (DeepClone instead of string round-trip) | Covers F7 verbatim; no additional work. |
| ⤴ **R5/R7** (interceptor batch bypass) | Add S2 explicitly to the SECURITY_GUIDE.md bullet list in R13.4: "auth interceptors do not run on batch elements until R7 lands." |
| ⤴ **DEPENDENCY_BINDING.md** | §2: correct the unresolved-message example to the comma-joined multi-alias form actually produced (`string.Join(", ", …)`). Cosmetic, ship with D2's doc pass. |

### Test-gap matrix (audit section 3 → owning items)

| Gap | Covered by |
|---|---|
| Duplicate alias providers | D1 |
| `@` literals / trim / escape | D2 |
| GraphKey/id collisions | D3 |
| `JsonIgnore`/`JsonPropertyName` under Strict/Paranoid | D5 |
| Serial-path auth-before-resolution | D4 |
| Self-dependency | D7 |
| Extraction-failure message content | D6 |

### Sequencing & effort

```
hotfix train        1.2 train
─────────────       ────────────────────────────────────────
D1 ─┐                                          ┌─ R9.3 (+D3 keys)
D2 ─┼─ independent,    D4 ─┐                   ├─ R9.4 (consume AliasGrammar)
D3 ─┘  ~2–3 days         D5 ─┼─ alongside       ├─ R9.5 (metadata cache)
                         D6 │  R7–R9 file      ├─ R9.6 (DeepClone, covers F7)
                         D7 ─┘  adjacency   ~3–4 └─ R8b posture notes
                                days extra
```

- Hotfix additions (D1–D3): **S+S+M ≈ 2–3 working days**, branch `hotfix/1.1.2` (or `.3`).
- 1.2 additions (D4–D7): **≈ 3–4 working days on top of R7–R10**, scheduled inside the R9
  invoker window to touch `SleipnirInvoker.cs` once.

### Acceptance (track-level)

- Every finding F1–F8 has a regression test named in its item, and that test fails on `main`
  before the fix (where deterministically expressible — F1's unordered merge needs a seeded/
  many-iteration test, state that limitation in the test comment).
- `DEPENDENCY_BINDING.md` and `DEPENDENCY_BINDING_REFERENCE.md` describe exactly the shipped
  grammar (trim-free, `@@` escape), the duplicate/self-dependency rejections, and the
  extraction-failure diagnostics.
- No default tightened within 1.x; S1 tightenings recorded as 2.0 candidates in `ROADMAP.md`
  (via R8b/R13.6).
