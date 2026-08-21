# Sleipnir Discovery — User Reference

A consolidated lookup reference for **everything runtime discovery** in
Sleipnir: the `DiscoveryInfo` metadata the server generates at runtime from
registered controllers, the `TypeRef`/`TypeMeta` model, contract-type inference
(Weg C — the controller-assembly boundary), the `[SleipnirDataContract]`
override, `[SleipnirDocumentation]`/`[SleipnirExample]`, the discovery service's
caching and invalidation, name uniqueness at registration, the `AutoDiscover`
opt-out and auto-discovery scans, the discovery endpoint(s) and their
deterministic serialization, the codegen ingress gate (shape validation,
roll-forward, zero-controller guard), and DevUI consumption.

Sleipnir is **code-first**: the C# classes decorated with attributes *are* the
contract, and discovery metadata is generated at runtime from them — no
`.proto` files, no IDL (`README.md`, code-first framing in the intro). The
discovery payload is the single source of truth that the DevUI renders, that
`sleipnir-gen`/`Sleipnir.Generator` turn into a typed client, and that the
wire-vs-contract conformance gate checks.

This is a **reference**, not a tutorial. When discovery output does not look
right, look here first — the model field table, the inference rules, the
attribute table, the endpoint table with symbol-anchored citations, the codegen
ingress gate, a diagnostics catalog (incl. the non-deterministic-defaults drift
item), and a map of where the deeper docs live. For the type-system spec read
`docs/discovery-schema.md`; for the wire-level shape read `PROTOCOL.md`
§"Discovery (MEX)"; for onboarding read `GETTING_STARTED.md`. This doc
consolidates those and links back for depth.

All citations are `repo-relative/path.cs` → `Symbol` (or a short verbatim quote
in `"…"`). Line numbers are deliberately omitted so a citation survives any line
shift — find the spot by the symbol name or by grepping the quote. Code-facing
text is English per `CLAUDE.md`.

## Table of contents

1. [The discovery model](#1-the-discovery-model)
2. [The `DiscoveryInfo` model](#2-the-discoveryinfo-model)
3. [`SleipnirDiscoveryService` — builder, caching, invalidation](#3-sleipnirdiscoveryservice--builder-caching-invalidation)
4. [Contract-type inference (Weg C)](#4-contract-type-inference-weg-c)
5. [The `[SleipnirDataContract]` override](#5-the-sleipnirdatacontract-override)
6. [`[SleipnirDocumentation]` & `[SleipnirExample]`](#6-sleipnirdocumentation--sleipnirexample)
7. [Name uniqueness, `AutoDiscover`, auto-discovery scans](#7-name-uniqueness-autodiscover-auto-discovery-scans)
8. [The discovery endpoint & deterministic serialization](#8-the-discovery-endpoint--deterministic-serialization)
9. [Codegen consumption of discovery](#9-codegen-consumption-of-discovery)
10. [DevUI consumption](#10-devui-consumption)
11. [Configuration reference](#11-configuration-reference)
12. [Diagnostics & troubleshooting catalog](#12-diagnostics--troubleshooting-catalog)
13. [How it is verified (the tests)](#13-how-it-is-verified-the-tests)
14. [Relationship to other docs](#14-relationship-to-other-docs)

---

## 1. The discovery model

Sleipnir dispatches by `"{Controller}_{Method}"` — purely name-based, no
parameter-based overload resolution. Discovery is the runtime mirror of that
contract: the server scans registered controllers and emits, per method, its
name, return type, and parameters (with types and defaults), plus a `Types`
registry of the contract types referenced. One contract → one discovery
payload, regardless of which transport carries a call (`CLAUDE.md` §"Transports").

The payload is **deterministic**: the REST endpoint and the JSON-RPC
`sleipnir.discover` capability serialize with the same
`DiscoverySerialization.Options`, independent of host JSON config (§8). The
build-integrated codegen tool writes `contract.sleipnir.json` from the same
payload, sorted by controller name — so a committed golden contract is
byte-stable across builds and machines (`CLIENT_GENERATION.md` §"Phased plan",
`Sleipnir.Server.Codegen/Program.cs` "Controllers.OrderBy(c => c.Name").

---

## 2. The `DiscoveryInfo` model

The model lives in `SleipnirCore/Model/Messages/Mex/`.

### `DiscoveryInfo` — `DiscoveryInfo.cs` → `DiscoveryInfo`

| Field | Type | Default | Notes |
|-------|------|---------|-------|
| `DiscoveryVersion` | `string` | `"1"` | Additive-only versioning (`docs/discovery-schema.md` §1). |
| `Controllers` | `List<ControllerMeta>` | — | In registration order. |
| `Types` | `Dictionary<string, TypeMeta>` | — | Ordinal-ignore-case key set; the contract-type registry. |

### `ControllerMeta` / `MethodMeta` / `ParameterMeta` — `ControllerMeta.cs`

| Field | Type |
|-------|------|
| `ControllerMeta.Name` | `string` |
| `ControllerMeta.Methods` | `List<MethodMeta>` |
| `MethodMeta.MethodName` | `string` |
| `MethodMeta.ReturnType` | `TypeRef` (default = void) |
| `MethodMeta.Parameters` | `List<ParameterMeta>` |
| `MethodMeta.Documentation` | `string?` |
| `ParameterMeta.ParameterName` | `string` |
| `ParameterMeta.ParameterType` | `TypeRef` (default = opaque) |
| `ParameterMeta.DefaultValue` | `object?` |
| `ParameterMeta.Documentation` | `string?` |

### `TypeMeta` / `PropertyMeta` / `EnumMember` — `ControllerMeta.cs`

| Field | Type | Notes |
|-------|------|-------|
| `TypeMeta.Kind` | `string` | `"object"` or `"enum"`. |
| `TypeMeta.TypeName` | `string` | |
| `TypeMeta.Properties` | `List<PropertyMeta>` | |
| `TypeMeta.Members` | `List<EnumMember>?` | Enum only. |
| `TypeMeta.Example` | `object?` | See §6 + the drift item (§12). |
| `PropertyMeta.PropertyName` | `string` | camelCase on the wire. |
| `PropertyMeta.PropertyType` | `TypeRef` | |
| `PropertyMeta.Navigation` | `NavigationMeta?` | Optional `[SleipnirNavigation]` edge. |
| `NavigationMeta.Fetch`/`Key`/`ChildKey`/`Param` | — | |
| `EnumMember.Name`/`Value` | — | |

### `TypeRef` — `TypeRef.cs` → `TypeRef`

The discriminated union for every type reference. `Kind` is one of
`scalar | array | set | map | stream | event | ref | opaque | void`
(`PROTOCOL.md` §"Discovery (MEX)"; note: there is **no `kind:"call"`** on the
discovery wire — calls are method entries without a `kind` discriminator; `stream`/`event`
are return-type kinds, and `subscribe` is a request-frame discriminator, not a
discovery entry).

| Field | Type | Notes |
|-------|------|-------|
| `Kind` | `string` | one of scalar\|array\|set\|map\|stream\|event\|ref\|opaque\|void |
| `Name` | `string?` | |
| `Element` | `TypeRef?` | array/set element |
| `Key` | `TypeRef?` | map key |
| `Value` | `TypeRef?` | map value |
| `Ref` | `string?` | reference into `Types` |
| `NativeName` | `string?` | opaque type's CLR name |
| `Nullable` | `bool?` | |

The authoritative type-system spec is `docs/discovery-schema.md` (§2 `TypeRef`,
§3 scalars, §4 collections).

---

## 3. `SleipnirDiscoveryService` — builder, caching, invalidation

`SleipnirCore/Services/SleipnirDiscoveryService.cs` → `SleipnirDiscoveryService`.
Constructed by `SleipnirInvoker` with the shared `ConcurrentDictionary<string, Type> routeHandlers`
passed **by reference** (the `SleipnirDiscoveryService` ctor `_routeHandlers`
param; `SleipnirInvoker` ctor → `_discoveryService = new SleipnirDiscoveryService(_routeHandlers)`)
— so the discovery service reads the same route-handler dictionary the invoker
dispatches from.

| Member | Purpose |
|--------|---------|
| `BuildDiscoveryInfo()` | The builder. Iterates `_routeHandlers`, builds `ControllerMeta`/`MethodMeta`/`ParameterMeta`, registers expandable types into `discovery.Types`. |
| `GetDiscoveryInfo()` | Double-checked lazy public access. `SleipnirInvoker.GetDiscoveryInfo()` delegates here. |
| `InvalidateCache()` | Nulls `_cachedDiscovery` under `_cacheLock`. |
| `_cachedDiscovery` / `_cacheLock` | The single cached `DiscoveryInfo?` snapshot. |

**Invalidation is unconditional per registration:** there is no cache key —
`SleipnirInvoker.Register` calls `_discoveryService.InvalidateCache()` after
**each** controller registration (in `SleipnirInvoker` → `Register`).
The cache holds one snapshot derived from the shared `_routeHandlers`; the next
`GetDiscoveryInfo()` rebuilds it. (`CLAUDE.md` §"Core Engine (`SleipnirCore`)"
notes "cached with invalidation on new registrations".)

**Builder helpers:** `BuildTypeRef`, scalar/Any tables, collection definitions,
`TryCollection`, `EnsureRegistered`, `PopulateObjectMeta`, `BuildEnumTypeMeta`,
`IsExpandableType`, `TypeKey`, `ReadNullable`, `ReadDefaultValue`, per-build
`BuildCtx`.

---

## 4. Contract-type inference (Weg C)

The load-bearing rule (`CLAUDE.md` §"Core Engine (`SleipnirCore`)"): contract
types are **inferred from method signatures** by default. Any class type whose
assembly belongs to the registered controllers' assemblies is **fully
expanded** (property schema, example, nested types); types from other
assemblies (BCL, framework envelope, third-party) stay **opaque** unless
overridden.

- **Contract-assembly set** — computed once per build in `BuildDiscoveryInfo`:
  `_routeHandlers.Values.Select(t => t.Assembly).Distinct().ToHashSet()`
  (its comment states the Weg C rationale).
- **The boundary check** — `IsExpandableType`, final line
  `return contractAssemblies.Contains(type.Assembly);`. Foreign types become
  `Kind = "opaque"` with `NativeName = type.Name`.
- **Heuristic ordering** (the `IsExpandableType` docstring): primitives/enum/string
  → opaque; `[SleipnirDataContract(Exclude=true)]` → force-opaque; bare
  `[SleipnirDataContract]` → force-expand; assembly-in-set → expand; otherwise
  opaque.

---

## 5. The `[SleipnirDataContract]` override

**Attribute:** `SleipnirCommon/Attribute/SleipnirDataContractAttribute.cs` →
`SleipnirDataContractAttribute` —
`[AttributeUsage(AttributeTargets.Class, Inherited = false)]`, single property
`bool Exclude { get; set; }`. (Namespace `SleipnirCommon.Attribute`, consumed by
`SleipnirCore` via the `using SleipnirCommon.Attribute;` import at the top of
`SleipnirDiscoveryService.cs`.)

| Form | Effect |
|------|--------|
| bare `[SleipnirDataContract]` | **force-expand** a type (e.g. a third-party type you want documented). |
| `[SleipnirDataContract(Exclude = true)]` | **force-opaque** a type (e.g. an own-assembly type you want hidden). |

Honored in `IsExpandableType`: `if (attr.Exclude) return false;` then
`return true;` (read via `GetDataContractAttribute`).

---

## 6. `[SleipnirDocumentation]` & `[SleipnirExample]`

### `[SleipnirDocumentation]`

`SleipnirCommon/Attribute/SleipnirDocumentationAttribute.cs` →
`SleipnirDocumentationAttribute` —
`[AttributeUsage(Class|Method|Parameter, AllowMultiple=false)]`, ctor
`string summary`, `Summary` getter. Consumed for the method doc in
`SleipnirDiscoveryService` (`methodDoc`).

> **Doc-bug:** the attribute allows the `Parameter` target, but the discovery
> service reads only the **method-level** attribute for `paramDoc`
> (it uses `method.GetCustomAttribute<...>()`), so **per-parameter documentation
> is not wired today** — a parameter's `Documentation` resolves to the method's
> summary.

### `[SleipnirExample]`

`SleipnirCommon/Attribute/SleipnirExampleAttribute.cs` → `SleipnirExampleAttribute`
— `sealed`, `[AttributeUsage(Class, Inherited=false, AllowMultiple=false)]`,
ctor `string exampleJson`, `ExampleJson` getter. Consumed in `PopulateObjectMeta`:
if present, `JsonSerializer.Deserialize(exampleAttr.ExampleJson, type, WriteIndented=true)`
becomes `meta.Example`; on exception → `null`.

> **This is the only stabilization surface** for the non-deterministic-defaults
> drift item (§12). An explicit `[SleipnirExample]` bypasses the
> `Activator.CreateInstance` default-instance path entirely.

---

## 7. Name uniqueness, `AutoDiscover`, auto-discovery scans

### Name uniqueness — registration-time hard fail

`SleipnirInvoker.Register` throws `InvalidOperationException` at registration
when names collide (no parameter-based overload resolution —
`CLAUDE.md` §"Name Uniqueness (Registration-Time Hard Fail)"):

- **Controller name** — a different type already holds the name: in
  `SleipnirInvoker` → `Register` (the controller-name collision branch: lock +
  `TryAdd`). Same-type re-registration is idempotent.
- **Method/event name** — shares the `"{Controller}_{Method}"` key: in `Register`
  (the method/event-name collision branch; dispatch key `"{Controller}_{Method}"`).
  Same-`MethodInfo` re-registration idempotent.
- **Mutual exclusivity** of `[SleipnirMethod]`/`[SleipnirEvent]`: in `Register`
  (the mutual-exclusivity branch). Event return-type contract (`IObservable<T>`
  only): the event-validation branch.

### `AutoDiscover` opt-out

`[SleipnirController]` has `public bool AutoDiscover { get; set; } = true`
(`SleipnirCore/Attributes/SleipnirControllerAttribute.cs` → `AutoDiscover`
property; docstring on the property).
`AutoDiscover = false` excludes a controller from the bulk auto-discovery scans;
it must then be registered explicitly via `Register<T>()` or
`SleipnirControllerBuilder.Add<T>()`.

### Auto-discovery scans

| Site | Where | What it scans |
|------|-------|---------------|
| `AddSleipnir` (canonical) | `SleipnirServiceCollectionExtension.cs` → `AddSleipnir` | `AppDomain.CurrentDomain.GetAssemblies()` via `TypeScanning.SafeGetTypes`; registers DI services for `attr != null && attr.AutoDiscover`. |
| `UseSleipnir` | `SleipnirServiceCollectionExtension.cs` → `UseSleipnir` | Invoker registration scan, same `attr.AutoDiscover` filter. |
| `SleipnirControllerBuilder.FromAssemblies` | `SleipnirControllerBuilder.cs` → `FromAssemblies` | Given assemblies (or AppDomain if empty), `attr.AutoDiscover` filter. |
| `AddSleipnir(options, configureControllers)` | `SleipnirServiceCollectionExtension.cs` → `AddSleipnir` (fluent overload) | Sets `options.AutoDiscoverControllers = false`, disabling the bulk scan. |

**Tolerant enumeration:** `TypeScanning.SafeGetTypes`
(`SleipnirHub/Extensions/TypeScanning.cs`) swallows `ReflectionTypeLoadException`
— a type-load failure in one assembly does not abort discovery.

`AutoDiscoverControllers` is an option too — referenced in `AddSleipnir`/`UseSleipnir`
(`SleipnirServiceCollectionExtension.cs`) and documented on the
`AutoDiscoverControllers` property (`SleipnirOptions.cs`).

---

## 8. The discovery endpoint & deterministic serialization

| Surface | Route / method | Where | Gating |
|---------|----------------|-------|--------|
| **REST** | `GET {prefix}/discovery` (default `/api/sleipnir`) | `SleipnirEndpointExtensions.cs` → `MapGet("/discovery")` (prefix is the `MapSleipnirEndpoints` param) | 401 when `RequireAuthentication && !IsAuthenticated`. Serialized with `DiscoverySerialization.Options`, `application/json`. |
| **JSON-RPC** | `sleipnir.discover` capability | `JsonRpcDispatcher.cs` → the `sleipnir.discover` dispatch arm | Same RequireAuth gate; declared byte-identical to the REST endpoint (shared `DiscoveryOptions` field). Capability name in `JsonRpcModels.cs`. |
| **Build-integrated codegen** | `contract.sleipnir.json` (written to disk) | `Sleipnir.Server.Codegen/Program.cs` → the contract-export method ("Controllers.OrderBy(c => c.Name") | Sorts controllers by name, serializes with `DiscoverySerialization.Options`. |

**Deterministic serialization:** `DiscoverySerialization.Options`
(`SleipnirCore/Model/Messages/Mex/DiscoverySerialization.cs` → `Options`) —
`JsonNamingPolicy.CamelCase` + `DefaultIgnoreCondition = WhenWritingNull`.
The `Options` docstring states it is **independent of host JSON config** and is
shared by the REST endpoint and the JSON-RPC `sleipnir.discover` capability, so
both emit byte-identical bytes.

> **No WebSocket or SignalR discovery surface.** Grep across `SleipnirWebSocket`
> and `SleipnirHub` found no discovery endpoint/hub method. The `/sleipnirhub` hub
> does not serve `DiscoveryInfo`; `SleipnirHub` only references `/discovery` as
> the REST endpoint in comments (`SleipnirOptions.cs` — `/discovery` mentions in
> the `UseRest`/`RequireAuthentication`/`EnableObservability` docstrings). Discovery
> is served only by REST and JSON-RPC.

---

## 9. Codegen consumption of discovery

Discovery is the codegen input. There are **two** codegen cores that consume it:

### C# core — `Sleipnir.Codegen.Core`

Public facade `SleipnirCodegen` (`Sleipnir.Codegen.Core/SleipnirCodegen.cs` →
`SleipnirCodegen`). `EmitClient`: parse → `DiscoveryShape.Assert` →
`new NamingResolver()` → `EmitterBuilder.Build(discovery, resolver)` →
`CsEmitter.Emit`. `EmitContracts` additionally calls
`EmitterBuilder.ValidateNavigation(input)` before `CsContractsEmitter.Emit`.

- **`EmitterBuilder.Build`** — `Sleipnir.Codegen.Core/EmitterInput.cs` →
  `EmitterBuilder.Build`. Walks the validated `JsonObject`: registers object
  type names with the `NamingResolver`, skips enum keys, builds `ResolvedType`s
  with `Casing.ToCamelCase` property names, builds `ResolvedController`s
  preserving discovery order. The file header names the port from
  `clients/codegen/src/core/model.ts`.
- **`NamingResolver`** — `Sleipnir.Codegen.Core/NamingResolver.cs` →
  `NamingResolver`: `Register`/`Resolve`/`Disambiguate`.
- **Ingress gate — `DiscoveryShape.Assert`**
  (`Sleipnir.Codegen.Core/DiscoveryShape.cs` → `Assert`):
  `KnownDiscoveryVersions = { "1" }`, `ValidKinds`, `ScalarNames`,
  `DiscoveryShapeException`. This is the **no-drift ingress gate** — an unknown
  `discoveryVersion` or `TypeRef.kind` throws before any code is emitted (the
  `SLEIPNIR001` diagnostic, `CODEGEN_ONBOARDING.md` §"2.4 Diagnostics").

### TypeScript core — `clients/codegen/src`

The DevUI's `CodegenPage` dogfoods it: `buildEmitterInput`, `NamingResolver`,
`emitTsClient`/`emitCsClient`/`emitPyClient`
(`SleipnirDeveloperUi/src/lib/components/editor/CodegenPage.svelte` → the
`sleipnir-codegen` import block). Both cores must stay in parity
(`CODEGEN_REFERENCE` cross-reference; the `CsCodegenParityTests` gate).

### Codegen tool — roll-forward + zero-controller guard

- **Roll-forward fix** — `<RollForward>LatestMajor</RollForward>` baked into the
  tool's `runtimeconfig` (`Sleipnir.Server.Codegen/Sleipnir.Server.Codegen.csproj`
  → `<RollForward>` element; rationale in the comment above it). This fixed the
  empty-discovery-on-net10-consumers bug — the tool is net8-pinned and cannot
  reflect a net10 assembly without `LatestMajor`. `CHANGELOG.md` §"[1.1.3]".
  Memory `sleipnir-codegen-discovery-rollforward`.
- **Zero-controller guard** — `Sleipnir.Server.Codegen/Program.cs` → the
  zero-controller guard (`if (discovery.Controllers.Count == 0) throw …`).
  Exit code 2 (`CHANGELOG.md` §"[1.1.3]").
- **Regression test** — `SleipnirTests/Integration/ServerCodegenNet10RollForwardTests.cs`
  → `Net10Server_DiscoveredByTool_RollForwardLatestMajor_NonEmptyContract`
  (bare `dotnet <tool.dll>` with no `--roll-forward`, asserts exit 0).

### Navigation drift-gate

`EmitterBuilder.ValidateNavigation` (`Sleipnir.Codegen.Core/EmitterInput.cs` →
`ValidateNavigation`): resolves `Fetch`/`Param`/`Key`, validates scalar match,
opaque-target check; throws `DiscoveryShapeException` on any violation. Only on
the `EmitContracts` path.

---

## 10. DevUI consumption

| Piece | Where | Role |
|-------|-------|------|
| Discovery state | `discovery.svelte.ts` → `DiscoveryState` | `$state` runes: `data`, `loading`, `error`, `searchQuery`; `filteredControllers` getter; `fetchDiscovery()` → `apiFetchDiscovery()`; singleton export `discoveryState`. |
| Endpoint caller | `client.ts` → `fetchDiscovery()` | `fetchDiscovery()` → `client.discover()` from `sleipnir-client` (`SleipnirRestClient`); client rebuilt from `baseUrl`/`apiPath`/`bearer` via `rebuild()`. The DevUI dogfoods its own generated TS client. |
| Discovery panel | `ExplorerPane.svelte` → `ExplorerPane` | Imports `discoveryState`, "Discovery" header + controller count badge; composes `ControllerTree` + `TypesTree`; vertical splitter Discovery/Types. |
| Zero-controller guard | `DependencyBuilderPage.svelte` "discoveryState.data.controllers.length === 0" | The empty-discovery `{#if}` guard. |

---

## 11. Configuration reference

Discovery-relevant options on `SleipnirHub/Extensions/SleipnirOptions.cs`:

| Option | Type | Default | Notes |
|--------|------|---------|-------|
| `AutoDiscoverControllers` | `bool` | `true` | Enables the bulk auto-discovery scan in `AddSleipnir`/`UseSleipnir`. Set `false` by the fluent `AddSleipnir(options, configureControllers)` overload. Referenced in `AddSleipnir`/`UseSleipnir` (`SleipnirServiceCollectionExtension.cs`). |
| `RequireAuthentication` | `bool` | — | Gates the REST + JSON-RPC discovery endpoints (401 when unauthenticated). |

The inference behavior itself (Weg C, `[SleipnirDataContract]`) is **not
configurable** — it is fixed by the controller-assembly boundary and the
attributes on the types.

---

## 12. Diagnostics & troubleshooting catalog

### The non-deterministic-defaults drift item

`PopulateObjectMeta` generates a `TypeMeta.Example` via the **default-instance
path**:

```csharp
else if (type.GetConstructor(Type.EmptyTypes) != null) {
    try { meta.Example = Activator.CreateInstance(type); }
    catch { meta.Example = null; }
}
```

`Activator.CreateInstance` runs property initializers, so a
`[SleipnirDataContract]` type whose property initializer calls `Guid.NewGuid()`
yields a **fresh example per build** — the committed `contract.sleipnir.json`
golden drifts on every build (memory `sleipnir-contract-drift-random-defaults`).

**Status:** no code-level stabilization exists (no `Guid.NewGuid`-default
stabilization, no deterministic-instance override). The **only mitigation today
is the explicit `[SleipnirExample("json")]` override** (in `PopulateObjectMeta`),
which bypasses `Activator.CreateInstance` entirely. If a contract type has
non-deterministic defaults, annotate it with `[SleipnirExample]` to pin the
example, or the wire-vs-contract conformance gate (`DiscoveryContractTests`) and
the codegen golden will drift.

### Other gotchas / doc-bugs

- **No `kind:"call"` on the wire.** Calls are method entries without a `kind`
  discriminator; `stream`/`event` are return-type kinds; `subscribe` is a
  request-frame discriminator, not a discovery entry
  (`TypeRef.cs` → `Kind`, `PROTOCOL.md` §"Discovery", `README_DETAILS.md` §"Discovery").
- **Per-parameter `[SleipnirDocumentation]` not wired.** The attribute targets
  `Parameter`, but the discovery service reads only the method-level attribute
  for `paramDoc` (the `method.GetCustomAttribute<…>()` read in
  `SleipnirDiscoveryService`) — a parameter's `Documentation` resolves to the
  method's summary.
- **No WebSocket / SignalR discovery.** Only REST `GET /discovery` and JSON-RPC
  `sleipnir.discover` serve `DiscoveryInfo`.
- **`CODEGEN_REFERENCE.md` is the sibling reference in this set** — the
  consolidated codegen lookup. `TRANSPORT_REFERENCE.md`/`EVENTS_REFERENCE.md`/
  `DEPENDENCY_BINDING_REFERENCE.md` cross-link it. (`CODEGEN_ONBOARDING.md`
  and `CLIENT_GENERATION.md` remain the older, tutorial-shaped codegen docs.)
- **Name collision is a startup hard-fail** — a duplicated `[SleipnirController]`
  name or `[SleipnirMethod]` name throws at registration (in `Register` — the
  controller-name and method/event-name collision branches), not at call time.
- **`ReflectionTypeLoadException` is swallowed** by `TypeScanning.SafeGetTypes`
  (`TypeScanning.cs` → `SafeGetTypes`) — a type-load failure in one assembly
  silently skips those types; if a controller is missing from discovery, check
  for a load failure in its assembly.

---

## 13. How it is verified (the tests)

| Test file | Covers |
|-----------|--------|
| `SleipnirTests/Unit/Core/SleipnirDiscoveryServiceTests.cs` | Controller/method enumeration, `CancellationToken` exclusion, primitive/enum/`[SleipnirDataContract]`/Weg-C inference (expand vs opaque vs `Exclude`), collections (map/array/set/stream/event), bytes scalar, nullability, default values, schema version — across the class's tests. |
| `SleipnirTests/Unit/Core/SleipnirDiscoveryTypeRefTests.cs` | `TypeRef` shape: `HashSet`/`SortedSet` → set, object/`JsonDocument` → any scalar, nullable value-type unwrap, native array, nested list (array of array), map-of-lists, set-of-arrays, parameter default carried, byte-underlying enum, `[SleipnirExample]` populates example, self-referential type cycle — across the class's tests. |
| `SleipnirTests/Unit/Core/SleipnirDiscoveryServiceNavigationTests.cs` | `[SleipnirNavigation]` → discovery: property without attribute has null navigation, with attribute serializes the edge, camelCase JSON + omitted-when-absent — across the class's tests. |
| `SleipnirTests/Integration/DiscoveryContractTests.cs` | Wire-vs-contract conformance: `Story01Discovery_MatchesCommittedGolden`, `…_CarriesSchemaVersion`. |
| `SleipnirTests/Integration/ServerCodegenNet10RollForwardTests.cs` | Codegen roll-forward fix + zero-controller guard regression (`Net10Server_DiscoveredByTool_RollForwardLatestMajor_NonEmptyContract`). |

---

## 14. Relationship to other docs

| Doc | Covers (discovery-relevant) |
|-----|------------------------------|
| `docs/discovery-schema.md` | **The authoritative type-system spec** — §1 `DiscoveryInfo` envelope, §2 `TypeRef`, §3 scalars, §4 collections, `example`, §10 full synthetic payload, §11 versioning & no-drift gate. |
| `PROTOCOL.md` | Wire spec: §"Discovery (MEX)" (envelope, response shape, `TypeRef.kind` enumeration), §"Discovery" (events-in-discovery), §"JSON-RPC 2.0 Compatibility" (`sleipnir.discover`), §"Design Principles for Cross-Platform Implementations" (discovery-enables-codegen). |
| `README_DETAILS.md` | User-facing: event methods in `DiscoveryInfo` (§"Discovery"), code-first framing, DevUI-as-discovery-console (§"Developer UI"), `/api/sleipnir/discovery` returns full type metadata, media-not-in-discovery boundary (§"What is deliberately *not* in v1"), dependency-checker discovery schemas (§"DevUI static checker"). |
| `README.md` | Runtime discovery framing (intro), `GET /api/sleipnir/discovery`, DevUI turns runtime discovery into a console (§"Developer UI"), "Runtime discovery + Developer UI" (§"Features at a glance"), codegen row "typed client stubs from discovery". |
| `CLAUDE.md` | `SleipnirDiscoveryService` overview incl. Weg C + `[SleipnirDataContract]` + caching (§"Core Engine (`SleipnirCore`)"), attributes (§"Key Attributes"), name-uniqueness + `AutoDiscover = false` exclusion (§"Name Uniqueness"), test-list mention (§"Test Project"). |
| `CODEGEN_ONBOARDING.md` | Discovery as the generator input: schema pointer, versioning, build-time regeneration, `SLEIPNIR001` unknown-`discoveryVersion` (§"2.4 Diagnostics"), `contract.sleipnir.json` shape (§"9 Reference"), golden-conformance gate, drift-fail-build. |
| `CLIENT_GENERATION.md` | Discovery as the addressability bridge: runtime payload, schema pointer, non-C# producer emitting same shape, conformance gate, determinism, build-time regeneration, schema versioning (§"Discovery as a stable spec" / §"Phased plan"). |
| `TRANSPORT_REFERENCE.md` | The discovery endpoint row (REST `GET /discovery`). |
| `EVENTS_REFERENCE.md` | `kind:"event"` in discovery (§1, §2). |
| `DEPENDENCY_BINDING_REFERENCE.md` | Discovery as the schema source for the DevUI static checker (§11). |

> **Note:** `CODEGEN_REFERENCE.md` is referenced by `TRANSPORT_REFERENCE.md`,
> `EVENTS_REFERENCE.md`, and `DEPENDENCY_BINDING_REFERENCE.md`. It is the sibling
> consolidated-reference in this set; the older codegen docs on `main`
> (`CODEGEN_ONBOARDING.md`, `CLIENT_GENERATION.md`) remain as tutorial-shaped
> companions.