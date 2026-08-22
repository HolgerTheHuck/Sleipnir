# Product Direction — Concrete Work Items

> Turns the product-direction discussion (2026-08-22, dependency-chaining audit session)
> into concrete, actionable items. This is the *strategy* layer above the two audit
> roadmaps (`2026-08-08-consolidation-roadmap.md` R-items,
> `2026-08-22-dependency-chaining-audit.md` D-items): it says **why** and **what next**,
> while those documents say **how**.
>
> Guiding sentence: *"The concept deserves to be opened; the substance is there — but the
> next two releases must prove to the market that you treat your own protocol harder than
> any external caller ever could."* (D1–D7 in v1.4.2 were exactly that proof.)

---

## P1 — Finish consolidation (in progress)

**Why.** Without a correct foundation every other direction is worthless.

| # | Item | Source | Status |
|---|---|---|---|
| 1.1 | Dependency-chaining defects F1–F8 (D1–D7) | 08-22 audit | ✅ released in v1.4.2 |
| 1.2 | Registration drift + fluent builder (R1–R2), WS correlation (R3–R4), interceptor-bypass docs (R5), regression tests (R6) | 08-08 roadmap, hotfix train | open |
| 1.3 | Batch path through interceptor pipeline (R7), security hardening set (R8a–m), invoker consolidation (R9), client unification (R10) | 08-08 roadmap, 1.2 train | open |
| 1.4 | CI legs (JS tests, CodeQL, Windows), packaging (XML docs, SourceLink, CPM), docs/release process (R11–R13) | 08-08 roadmap, 1.3 train | open |

**Done when.** Both audit roadmaps have no open hotfix/correctness items; STABILITY.md §2
batch-pipeline caveat removed (via R7).

---

## P2 — Make chaining the flagship

**Why.** Server-side dependency chaining is the differentiator (GraphQL-style roundtrip
saving with plain RPC semantics). It should be the first thing a newcomer *sees working*.
README headline ✅ shipped in v1.4.2; these items extend that into product experience.

### 2.1 DevUI dependency-graph visualization ⭐ highest single lever

**What.** Render the chain: provider→consumer edges from `dependencyMapping` + `@alias`
placeholders, topological batches as visual levels, failure propagation highlighted
(skipped dependents marked with their propagation cause).

**Existing basis.** `SleipnirDeveloperUi/src/lib/utils/dependencyCheck.ts` already
reproduces the runtime binding rules statically (expose-path/casing, cross-kind,
subset/kind-mismatch, cardinality). The graph is "half the way to a demo people can
*see*" — the checker supplies edge data, a rendering layer draws it.

**Concrete steps.**
1. Extend `dependencyCheck.ts` to emit an edge list per batch: `{from, to, alias, path,
   status}` (status = ok / warning / propagated-skip).
2. New Svelte component rendering the DAG (levels = Kahn batches from the same file's
   ordering logic); nodes clickable → existing call editor.
3. Wire into the existing serial-batch editor view; no new server surface needed
   (discovery + request shape suffice).
4. Optional polish: animate execution order on a live run.

**Acceptance.** A user can build the README two-call chain in the UI and see both calls,
the edge between them, and what happens when the provider fails — without reading
DEPENDENCY_BINDING.md.

**Effort.** M (UI-only; no wire changes).

### 2.2 Chaining-first onboarding in Docs/Samples

**What.** The first thing a newcomer encounters demonstrates chaining — not a single call.

**Concrete steps.**
1. `GETTING_STARTED.md`: after the first successful call, immediately add the second
   chained step (the guide already does this late in chapter structure — pull forward).
2. `samples/HelloSleipnir`: add one `[SleipnirMethod]` pair forming a chain + a curl for
   the multi endpoint as second example block.
3. Templates (`sleipnir-server`): ship with a chainable controller pair instead of a lone
   echo method.
4. Guide chapter on chaining: verify it appears before auth/events in reading order.

**Acceptance.** `dotnet new sleipnir-server && dotnet run` → the DevUI opens on a controller
pair that chains; the quickstart shows a two-command chain within the first ten minutes.

**Effort.** S.

### 2.3 Strengthen declarative client chaining (`Sleipnir.Client.Linq`)

**What.** The LINQ façade is the right instinct — compile-time-safe `Dep<T>`/`Arg<T>`
wiring. Deepen rather than widen.

**Concrete steps.**
1. Audit current gaps: which chain shapes still require raw `SleipnirCall`? (multi-match
   fan-out `$[*].id`, nested-object exposes, conditional chains.)
2. Close the top gap only; keep the API surface small.
3. Add the LINQ variant to the DevUI codegen preview (see 5.x below) so users discover it.

**Acceptance.** The README chain example has a compile-checked LINQ twin in the docs;
gap audit documented in `LINQ_QUERY.md`.

**Effort.** M (depends on gap findings).

---

## P3 — Interop bridge without giving up code-first

**Why.** The contract being implicit is the concept's open flank: cross-language/tooling
consumers need a standard, not proprietary discovery JSON.

**What.** Export discovery metadata as industry-standard schemas.

### 3.1 Discovery → OpenAPI document

**What.** A read-only endpoint (or build-time export) emitting an OpenAPI 3.1 doc:
each `[SleipnirMethod]` becomes a POST operation against the known REST envelope;
contract types expand to component schemas (the Weg-C inference already computes these
for discovery — reuse `SleipnirDiscoveryService` output).

**Concrete steps.**
1. Spike: map `DiscoveryInfo` → OpenAPI paths/schemas in a new `Sleipnir.Server.Codegen`
   export mode (build-time first — no new runtime surface).
2. Decide runtime endpoint later (`GET /api/sleipnir/openapi`) behind an option.
3. Validate round-trip: import the emitted doc into Postman/Swagger UI and execute a call.

**Acceptance.** Postman can import and successfully call a Sleipnir method from the
generated document alone.

**Effort.** M-L (schema mapping is mechanical; envelope details need care).

### 3.2 Discovery → JSON Schema per contract type

**What.** Simpler sibling of 3.1: emit standalone JSON Schema for each expanded contract
type. Feeds mocks, form generators, other-language clients directly.

**Concrete steps.** Reuse the Weg-C expansion; emit `$defs` per type; expose via codegen
export alongside `contract.sleipnir.json`.

**Acceptance.** Every type visible in DevUI has a downloadable/standalone JSON Schema.

**Effort.** S-M.

*(AsyncAPI for events is a candidate after 3.1/3.2 prove the pattern — not started.)*

---

## P4 — NativeAOT / trimming compatibility

**Why.** Expression trees are AOT-hostile; the Roslyn source generator is the right second
leg. If `dotnet publish -p:PublishAot=true` works out of the box, Sleipnir beats gRPC-based
alternatives on modern .NET adoption criteria (containers, serverless).

**Concrete steps.**
1. **Measure first:** publish the sample server with PublishAot; catalog every failure
   (expression-tree compilation at registration, reflection in discovery/binding,
   JSON serialization depth).
2. **Registration path:** make `SleipnirInvoker.Register<T>()` delegate to the
   `Sleipnir.SourceGenerator` when present (generated compiled delegates + route table),
   falling back to expression trees otherwise.
3. **Binding path:** replace per-call reflection (`ParameterInfo[]`, property walks) with
   source-generated metadata where AOT is targeted (aligns with R9.5 caching anyway).
4. CI: an AOT publish smoke test (build succeeds, sample answers one call) so the claim
   stays true.

**Acceptance.** Sample server publishes AOT, starts, serves a single call and a chained
batch. Documented as supported configuration.

**Effort.** L (multi-release; start with the measurement spike).

---

## Explicitly NOT doing

- **More transports** (gRPC transport sits in the samples folder — leave it there).
- **More binding modes** beyond Weak/Strict/Paranoid.
- **Feature accretion** generally. Sleipnir gets more mature by
  *less-capable-with-guarantees*, not more-capable-with-asterisks.

## Housekeeping

- **Branding:** repo/path say Trame, code says Sleipnir, NuGet was `Trame.Server`. The
  rename happened for legal reasons (Kitware's Python `trame`). Do the remaining rename
  once, early, and completely — the longer both coexist, the more confusing for outsiders.
  *(README already carries the rename banner; remaining: repo name itself if intended.)*

---

## Sequencing view

```
P1 consolidation ────────────────► (R6→R7→R9 order per 08-08 roadmap)
P2 flagship      ── 2.2 (S) now · 2.1 (M) after R7/R9 touch DevUI-adjacent code · 2.3 (M)
P3 interop       ── 3.2 (S-M) anytime · 3.1 (M-L) after R12 packaging work
P4 AOT           ── measurement spike anytime; real work after R9 (shared files)
```

Rule of thumb: P1 gates everything; P2.2/P3.2 are safe parallel tracks (docs/export only);
P2.1/P3.1/P4 schedule inside or after the R9 invoker window to avoid double-touching core.
