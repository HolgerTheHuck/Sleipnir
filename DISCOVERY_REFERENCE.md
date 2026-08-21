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
`.proto` files, no IDL. `README.md:14`. The discovery payload is the single
source of truth that the DevUI renders, that `sleipnir-gen`/`Sleipnir.Generator`
turn into a typed client, and that the wire-vs-contract conformance gate checks.

This is a **reference**, not a tutorial. When discovery output does not look
right, look here first — the model field table, the inference rules, the
attribute table, the endpoint table with `path:line` citations, the codegen
ingress gate, a diagnostics catalog (incl. the non-deterministic-defaults drift
item), and a map of where the deeper docs live. For the type-system spec read
`docs/discovery-schema.md`; for the wire-level shape read `PROTOCOL.md`
§"Discovery (MEX)"; for onboarding read `GETTING_STARTED.md`. This doc
consolidates those and links back for depth.

All citations are `repo-relative/path.cs:line` against the repo root. Code-facing
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
payload, regardless of which transport carries a call (`CLAUDE.md:70-76`).

The payload is **deterministic**: the REST endpoint and the JSON-RPC
`sleipnir.discover` capability serialize with the same
`DiscoverySerialization.Options`, independent of host JSON config (§8). The
build-integrated codegen tool writes `contract.sleipnir.json` from the same
payload, sorted by controller name — so a committed golden contract is
byte-stable across builds and machines (`CLIENT_GENERATION.md:183-184`,
`Sleipnir.Server.Codegen/Program.cs:117-137`).

---

## 2. The `DiscoveryInfo` model

The model lives in `SleipnirCore/Model/Messages/Mex/`.

### `DiscoveryInfo` — `DiscoveryInfo.cs:9`

| Field | Type | Default | Line | Notes |
|-------|------|---------|------|-------|
| `DiscoveryVersion` | `string` | `"1"` | `:12` | Additive-only versioning (`docs/discovery-schema.md:35`). |
| `Controllers` | `List<ControllerMeta>` | — | `:13` | In registration order. |
| `Types` | `Dictionary<string, TypeMeta>` | — | `:14` | Ordinal-ignore-case key set; the contract-type registry. |

### `ControllerMeta` / `MethodMeta` / `ParameterMeta` — `ControllerMeta.cs:9-30`

| Field | Type | Line |
|-------|------|------|
| `ControllerMeta.Name` | `string` | `:11` |
| `ControllerMeta.Methods` | `List<MethodMeta>` | `:12` |
| `MethodMeta.MethodName` | `string` | `:17` |
| `MethodMeta.ReturnType` | `TypeRef` | `:19` (default = void) |
| `MethodMeta.Parameters` | `List<ParameterMeta>` | `:20` |
| `MethodMeta.Documentation` | `string?` | `:21` |
| `ParameterMeta.ParameterName` | `string` | `:26` |
| `ParameterMeta.ParameterType` | `TypeRef` | `:27` (default = opaque) |
| `ParameterMeta.DefaultValue` | `object?` | `:29` |
| `ParameterMeta.Documentation` | `string?` | `:30` |

### `TypeMeta` / `PropertyMeta` / `EnumMember` — `ControllerMeta.cs:33-80`

| Field | Type | Line | Notes |
|-------|------|------|-------|
| `TypeMeta.Kind` | `string` | `:36` | `"object"` or `"enum"`. |
| `TypeMeta.TypeName` | `string` | `:38` | |
| `TypeMeta.Properties` | `List<PropertyMeta>` | `:39` | |
| `TypeMeta.Members` | `List<EnumMember>?` | `:41` | Enum only. |
| `TypeMeta.Example` | `object?` | `:43` | See §6 + the drift item (§12). |
| `PropertyMeta.PropertyName` | `string` | `:48` | camelCase on the wire. |
| `PropertyMeta.PropertyType` | `TypeRef` | `:49` | |
| `PropertyMeta.Navigation` | `NavigationMeta?` | `:56` | Optional `[SleipnirNavigation]` edge. |
| `NavigationMeta.Fetch`/`Key`/`ChildKey`/`Param` | — | `:67-73` | |
| `EnumMember.Name`/`Value` | — | `:79-80` | |

### `TypeRef` — `TypeRef.cs:11`

The discriminated union for every type reference. `Kind` (`:17`) is one of
`scalar | array | set | map | stream | event | ref | opaque | void`
(`PROTOCOL.md:971-976`; note: there is **no `kind:"call"`** on the discovery
wire — calls are method entries without a `kind` discriminator; `stream`/`event`
are return-type kinds, and `subscribe` is a request-frame discriminator, not a
discovery entry).

| Field | Type | Line |
|-------|------|------|
| `Kind` | `string` | `:17` |
| `Name` | `string?` | `:20` |
| `Element` | `TypeRef?` | `:23` | array/set element |
| `Key` | `TypeRef?` | `:26` | map key |
| `Value` | `TypeRef?` | `:29` | map value |
| `Ref` | `string?` | `:32` | reference into `Types` |
| `NativeName` | `string?` | `:35` | opaque type's CLR name |
| `Nullable` | `bool?` | `:41` |

The authoritative type-system spec is `docs/discovery-schema.md` (§2 `TypeRef`
at `:113`, §3 scalars at `:151`, §4 collections at `:186`).

---

## 3. `SleipnirDiscoveryService` — builder, caching, invalidation

`SleipnirCore/Services/SleipnirDiscoveryService.cs:19`. Constructed by
`SleipnirInvoker` with the shared `ConcurrentDictionary<string, Type> routeHandlers`
passed **by reference** (`:21, :25`; `SleipnirInvoker.cs:155`) — so the discovery
service reads the same route-handler dictionary the invoker dispatches from.

| Member | Line | Purpose |
|--------|------|---------|
| `BuildDiscoveryInfo()` | `:109` | The builder. Iterates `_routeHandlers`, builds `ControllerMeta`/`MethodMeta`/`ParameterMeta`, registers expandable types into `discovery.Types`. |
| `GetDiscoveryInfo()` | `:30` | Double-checked lazy public access. `SleipnirInvoker.GetDiscoveryInfo()` delegates here (`SleipnirInvoker.cs:274-278`). |
| `InvalidateCache()` | `:41-47` | Nulls `_cachedDiscovery` under `_cacheLock`. |
| `_cachedDiscovery` / `_cacheLock` | `:22-23` | The single cached `DiscoveryInfo?` snapshot. |

**Invalidation is unconditional per registration:** there is no cache key —
`SleipnirInvoker.Register` calls `_discoveryService.InvalidateCache()` after
**each** controller registration (`SleipnirInvoker.cs:199`, comment `:194-198`).
The cache holds one snapshot derived from the shared `_routeHandlers`; the next
`GetDiscoveryInfo()` rebuilds it. (`CLAUDE.md:66` notes "cached with invalidation
on new registrations".)

**Builder helpers:** `BuildTypeRef` (`:206`), scalar/Any tables (`:51-89`),
collection definitions (`:93-107`), `TryCollection` (`:259`), `EnsureRegistered`
(`:286`), `PopulateObjectMeta` (`:321`), `BuildEnumTypeMeta` (`:305`),
`IsExpandableType` (`:376`), `TypeKey` (`:398`), `ReadNullable` (`:401`),
`ReadDefaultValue` (`:418`), per-build `BuildCtx` (`:434`).

---

## 4. Contract-type inference (Weg C)

The load-bearing rule (`CLAUDE.md:66`): contract types are **inferred from
method signatures** by default. Any class type whose assembly belongs to the
registered controllers' assemblies is **fully expanded** (property schema,
example, nested types); types from other assemblies (BCL, framework envelope,
third-party) stay **opaque** unless overridden.

- **Contract-assembly set** — computed once per build in `BuildDiscoveryInfo`
  (`:117-120`): `_routeHandlers.Values.Select(t => t.Assembly).Distinct().ToHashSet()`
  (comment `:113-116` states the Weg C rationale).
- **The boundary check** — `IsExpandableType` (`:376-389`), final line
  `return contractAssemblies.Contains(type.Assembly);` (`:388`). Foreign types
  become `Kind = "opaque"` with `NativeName = type.Name` (`:256`).
- **Heuristic ordering** (docstring `:367-375`): primitives/enum/string →
  opaque; `[SleipnirDataContract(Exclude=true)]` → force-opaque; bare
  `[SleipnirDataContract]` → force-expand; assembly-in-set → expand; otherwise
  opaque.

---

## 5. The `[SleipnirDataContract]` override

**Attribute:** `SleipnirCommon/Attribute/SleipnirDataContractAttribute.cs:17` —
`[AttributeUsage(AttributeTargets.Class, Inherited = false)]`, single property
`bool Exclude { get; set; }` (`:23`). (Namespace `SleipnirCommon.Attribute`,
consumed by `SleipnirCore` via `using SleipnirCommon.Attribute;`
at `SleipnirDiscoveryService.cs:1`.)

| Form | Effect |
|------|--------|
| bare `[SleipnirDataContract]` | **force-expand** a type (e.g. a third-party type you want documented). |
| `[SleipnirDataContract(Exclude = true)]` | **force-opaque** a type (e.g. an own-assembly type you want hidden). |

Honored in `IsExpandableType` (`SleipnirDiscoveryService.cs:381-386`):
`if (attr.Exclude) return false;` then `return true;` (read via
`GetDataContractAttribute` `:391`).

---

## 6. `[SleipnirDocumentation]` & `[SleipnirExample]`

### `[SleipnirDocumentation]`

`SleipnirCommon/Attribute/SleipnirDocumentationAttribute.cs:6` —
`[AttributeUsage(Class|Method|Parameter, AllowMultiple=false)]`, ctor
`string summary` (`:10`), `Summary` getter (`:8`). Consumed for the method doc
at `SleipnirDiscoveryService.cs:154` (`methodDoc`).

> **Doc-bug:** the attribute allows the `Parameter` target, but the discovery
> service reads only the **method-level** attribute for `paramDoc`
> (`SleipnirDiscoveryService.cs:171` uses `method.GetCustomAttribute<...>()`),
> so **per-parameter documentation is not wired today** — a parameter's
> `Documentation` resolves to the method's summary.

### `[SleipnirExample]`

`SleipnirCommon/Attribute/SleipnirExampleAttribute.cs:6` — `sealed`,
`[AttributeUsage(Class, Inherited=false, AllowMultiple=false)]`, ctor
`string exampleJson` (`:10`), `ExampleJson` getter (`:8`). Consumed in
`PopulateObjectMeta` (`SleipnirDiscoveryService.cs:324-333`): if present,
`JsonSerializer.Deserialize(exampleAttr.ExampleJson, type, WriteIndented=true)`
becomes `meta.Example`; on exception → `null` (`:332`).

> **This is the only stabilization surface** for the non-deterministic-defaults
> drift item (§12). An explicit `[SleipnirExample]` bypasses the
> `Activator.CreateInstance` default-instance path entirely.

---

## 7. Name uniqueness, `AutoDiscover`, auto-discovery scans

### Name uniqueness — registration-time hard fail

`SleipnirInvoker.Register` throws `InvalidOperationException` at registration
when names collide (no parameter-based overload resolution —
`CLAUDE.md:132-138`):

- **Controller name** — a different type already holds the name:
  `SleipnirInvoker.cs:183-190` (lock `:178`, `TryAdd` `:192`). Same-type
  re-registration is idempotent (`:184`).
- **Method/event name** — shares the `"{Controller}_{Method}"` key:
  `SleipnirInvoker.cs:240-248` (key at `:220`). Same-`MethodInfo` re-registration
  idempotent (`:241`).
- **Mutual exclusivity** of `[SleipnirMethod]`/`[SleipnirEvent]`:
  `:209-213`. Event return-type contract (`IObservable<T>` only): `:226-233`.

### `AutoDiscover` opt-out

`[SleipnirController]` has `public bool AutoDiscover { get; set; } = true`
(`SleipnirCore/Attributes/SleipnirControllerAttribute.cs:23`, docstring `:14-22`).
`AutoDiscover = false` excludes a controller from the bulk auto-discovery scans;
it must then be registered explicitly via `Register<T>()` or
`SleipnirControllerBuilder.Add<T>()`.

### Auto-discovery scans

| Site | Line | What it scans |
|------|------|---------------|
| `AddSleipnir` (canonical) | `SleipnirHub/Extensions/SleipnirServiceCollectionExtension.cs:241-252` | `AppDomain.CurrentDomain.GetAssemblies()` via `TypeScanning.SafeGetTypes`; registers DI services for `attr != null && attr.AutoDiscover` (`:247`). |
| `UseSleipnir` | `:304-315` | Invoker registration scan, same `attr.AutoDiscover` filter (`:310`). |
| `SleipnirControllerBuilder.FromAssemblies` | `SleipnirHub/Extensions/SleipnirControllerBuilder.cs:28-46` | Given assemblies (or AppDomain if empty, `:30`), `attr.AutoDiscover` filter (`:38`). |
| `AddSleipnir(options, configureControllers)` | `:133` | Sets `options.AutoDiscoverControllers = false`, disabling the bulk scan. |

**Tolerant enumeration:** `TypeScanning.SafeGetTypes` (`SleipnirHub/Extensions/TypeScanning.cs:18-32`) swallows `ReflectionTypeLoadException` — a type-load failure in one assembly does not abort discovery.

`AutoDiscoverControllers` is an option too — referenced at
`SleipnirServiceCollectionExtension.cs:239, 247, 301, 310` and documented at
`SleipnirOptions.cs:113`.

---

## 8. The discovery endpoint & deterministic serialization

| Surface | Route / method | Line | Gating |
|---------|----------------|------|--------|
| **REST** | `GET {prefix}/discovery` (default `/api/sleipnir`) | `SleipnirRest/SleipnirEndpointExtensions.cs:86-99` (prefix `:33`) | 401 when `RequireAuthentication && !IsAuthenticated` (`:92`). Serialized with `DiscoverySerialization.Options` (`:97`), `application/json` (`:98`). |
| **JSON-RPC** | `sleipnir.discover` capability | `SleipnirRest/JsonRpc/JsonRpcDispatcher.cs:146` | Same RequireAuth gate (`:138-140`); declared byte-identical to the REST endpoint (`:23`). Capability name at `JsonRpcModels.cs:46`. |
| **Build-integrated codegen** | `contract.sleipnir.json` (written to disk) | `Sleipnir.Server.Codegen/Program.cs:117-137` | Sorts controllers by name (`:118`), serializes with `DiscoverySerialization.Options` (`:137`). |

**Deterministic serialization:** `DiscoverySerialization.Options`
(`SleipnirCore/Model/Messages/Mex/DiscoverySerialization.cs:16-20`) —
`JsonNamingPolicy.CamelCase` + `DefaultIgnoreCondition = WhenWritingNull`.
The docstring (`:6-13`) states it is **independent of host JSON config** and is
shared by the REST endpoint and the JSON-RPC `sleipnir.discover` capability, so
both emit byte-identical bytes.

> **No WebSocket or SignalR discovery surface.** Grep across `SleipnirWebSocket`
> and `SleipnirHub` found no discovery endpoint/hub method. The `/sleipnirhub` hub
> does not serve `DiscoveryInfo`; `SleipnirHub` only references `/discovery` as
> the REST endpoint in comments (`SleipnirOptions.cs:73, 139, 222`). Discovery
> is served only by REST and JSON-RPC.

---

## 9. Codegen consumption of discovery

Discovery is the codegen input. There are **two** codegen cores that consume it:

### C# core — `Sleipnir.Codegen.Core`

Public facade `SleipnirCodegen` (`Sleipnir.Codegen.Core/SleipnirCodegen.cs:9`).
`EmitClient` (`:16`): parse → `DiscoveryShape.Assert` (`:19`) →
`new NamingResolver()` (`:20`) → `EmitterBuilder.Build(discovery, resolver)`
(`:21`) → `CsEmitter.Emit`. `EmitContracts` (`:37`) additionally calls
`EmitterBuilder.ValidateNavigation(input)` (`:46`) before `CsContractsEmitter.Emit`.

- **`EmitterBuilder.Build`** — `Sleipnir.Codegen.Core/EmitterInput.cs:22`. Walks
  the validated `JsonObject`: registers object type names with the
  `NamingResolver` (`:33`), skips enum keys (`:32`), builds `ResolvedType`s with
  `Casing.ToCamelCase` property names (`:44`), builds `ResolvedController`s
  preserving discovery order (`:48-54`). Header `:1` names the port from
  `clients/codegen/src/core/model.ts`.
- **`NamingResolver`** — `Sleipnir.Codegen.Core/NamingResolver.cs:11`:
  `Register`/`Resolve`/`Disambiguate` (`:17, :30, :44`).
- **Ingress gate — `DiscoveryShape.Assert`** (`Sleipnir.Codegen.Core/DiscoveryShape.cs:41`):
  `KnownDiscoveryVersions = { "1" }` (`:23`), `ValidKinds` (`:25`),
  `ScalarNames` (`:30`), `DiscoveryShapeException` (`:15`). This is the **no-drift
  ingress gate** — an unknown `discoveryVersion` or `TypeRef.kind` throws before
  any code is emitted (the `SLEIPNIR001` diagnostic, `CODEGEN_ONBOARDING.md:231`).

### TypeScript core — `clients/codegen/src`

The DevUI's `CodegenPage` dogfoods it: `buildEmitterInput`, `NamingResolver`,
`emitTsClient`/`emitCsClient`/`emitPyClient`
(`SleipnirDeveloperUi/src/lib/components/editor/CodegenPage.svelte:10-16`). Both
cores must stay in parity (`CODEGEN_REFERENCE` cross-reference; the
`CsCodegenParityTests` gate).

### Codegen tool — roll-forward + zero-controller guard

- **Roll-forward fix** — `<RollForward>LatestMajor</RollForward>` baked into the
  tool's `runtimeconfig` (`Sleipnir.Server.Codegen/Sleipnir.Server.Codegen.csproj:18`,
  rationale `:9-17`). This fixed the empty-discovery-on-net10-consumers bug — the
  tool is net8-pinned and cannot reflect a net10 assembly without
  `LatestMajor`. `CHANGELOG.md:341` (1.1.3). Memory `sleipnir-codegen-discovery-rollforward`.
- **Zero-controller guard** — `Sleipnir.Server.Codegen/Program.cs:127`:
  `if (discovery.Controllers.Count == 0) throw new InvalidOperationException(...)`
  (`:129-134`, comment `:120-126`). Exit code 2 (`CHANGELOG.md:345-348`).
- **Regression test** — `SleipnirTests/Integration/ServerCodegenNet10RollForwardTests.cs:103`
  (`Net10Server_DiscoveredByTool_RollForwardLatestMajor_NonEmptyContract`), bare
  `dotnet <tool.dll>` (no `--roll-forward`) `:160`, exit-0 `:165-167`.

### Navigation drift-gate

`EmitterBuilder.ValidateNavigation` (`Sleipnir.Codegen.Core/EmitterInput.cs:71-142`):
resolves `Fetch`/`Param`/`Key`, validates scalar match, opaque-target check
(`:138`); throws `DiscoveryShapeException` on any violation. Only on the
`EmitContracts` path.

---

## 10. DevUI consumption

| Piece | File:line | Role |
|-------|-----------|------|
| Discovery state | `SleipnirDeveloperUi/src/lib/state/discovery.svelte.ts:4` | `DiscoveryState` class with `$state` runes: `data` (`:5`), `loading` (`:6`), `error` (`:7`), `searchQuery` (`:8`); `filteredControllers` getter (`:10`); `fetchDiscovery()` (`:27`) → `apiFetchDiscovery()` (`:31`); singleton export (`:45`). |
| Endpoint caller | `SleipnirDeveloperUi/src/lib/api/client.ts:49-51` | `fetchDiscovery()` → `client.discover()` from `sleipnir-client` (`SleipnirRestClient`); client rebuilt from `baseUrl`/`apiPath`/`bearer` (`:16-47`). The DevUI dogfoods its own generated TS client (`:1-4`). |
| Discovery panel | `SleipnirDeveloperUi/src/lib/components/explorer/ExplorerPane.svelte` | Imports `discoveryState` (`:2`), "Discovery" header (`:47-49`), controller count badge (`:56`); composes `ControllerTree` + `TypesTree` (`:4-5`); vertical splitter Discovery/Types (`:12-43`). |
| Zero-controller guard | `SleipnirDeveloperUi/src/lib/components/editor/DependencyBuilderPage.svelte:442` | `{#if !discoveryState.data || discoveryState.data.controllers.length === 0}`. |

---

## 11. Configuration reference

Discovery-relevant options on `SleipnirHub/Extensions/SleipnirOptions.cs`:

| Option | Type | Default | Line | Notes |
|--------|------|---------|------|-------|
| `AutoDiscoverControllers` | `bool` | `true` | `:113` | Enables the bulk auto-discovery scan in `AddSleipnir`/`UseSleipnir`. Set `false` by the fluent `AddSleipnir(options, configureControllers)` overload. Referenced at `SleipnirServiceCollectionExtension.cs:239, 247, 301, 310`. |
| `RequireAuthentication` | `bool` | — | (see `SleipnirOptions.cs`) | Gates the REST + JSON-RPC discovery endpoints (401 when unauthenticated). |

The inference behavior itself (Weg C, `[SleipnirDataContract]`) is **not
configurable** — it is fixed by the controller-assembly boundary and the
attributes on the types.

---

## 12. Diagnostics & troubleshooting catalog

### The non-deterministic-defaults drift item

`PopulateObjectMeta` (`SleipnirDiscoveryService.cs:321-365`) generates a
`TypeMeta.Example` via the **default-instance path** at `:334-338`:

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
is the explicit `[SleipnirExample("json")]` override** (`:324-333`), which
bypasses `Activator.CreateInstance` entirely. If a contract type has
non-deterministic defaults, annotate it with `[SleipnirExample]` to pin the
example, or the wire-vs-contract conformance gate (`DiscoveryContractTests`) and
the codegen golden will drift.

### Other gotchas / doc-bugs

- **No `kind:"call"` on the wire.** Calls are method entries without a `kind`
  discriminator; `stream`/`event` are return-type kinds; `subscribe` is a
  request-frame discriminator, not a discovery entry
  (`TypeRef.cs:17`, `PROTOCOL.md:865`, `README_DETAILS.md:738-749`).
- **Per-parameter `[SleipnirDocumentation]` not wired.** The attribute targets
  `Parameter`, but the discovery service reads only the method-level attribute
  for `paramDoc` (`SleipnirDiscoveryService.cs:171`) — a parameter's
  `Documentation` resolves to the method's summary.
- **No WebSocket / SignalR discovery.** Only REST `GET /discovery` and JSON-RPC
  `sleipnir.discover` serve `DiscoveryInfo`.
- **`CODEGEN_REFERENCE.md` is not yet on `main`** — it exists only on the
  sibling branch `devui-json-view-codegen-relabel` (commit `994dcac`); the nearest
  existing codegen docs on `main` are `CODEGEN_ONBOARDING.md` and
  `CLIENT_GENERATION.md`. `TRANSPORT_REFERENCE.md`/`EVENTS_REFERENCE.md`/
  `DEPENDENCY_BINDING_REFERENCE.md` cross-link it; those links resolve once
  `devui-json-view-codegen-relabel` merges.
- **Name collision is a startup hard-fail** — a duplicated `[SleipnirController]`
  name or `[SleipnirMethod]` name throws at registration (`SleipnirInvoker.cs:183-190`,
  `:240-248`), not at call time.
- **`ReflectionTypeLoadException` is swallowed** by `TypeScanning.SafeGetTypes`
  (`TypeScanning.cs:18-32`) — a type-load failure in one assembly silently skips
  those types; if a controller is missing from discovery, check for a load
  failure in its assembly.

---

## 13. How it is verified (the tests)

| Test file | Covers |
|-----------|--------|
| `SleipnirTests/Unit/Core/SleipnirDiscoveryServiceTests.cs` | Controller/method enumeration, `CancellationToken` exclusion, primitive/enum/`[SleipnirDataContract]`/Weg-C inference (expand vs opaque vs `Exclude`), collections (map/array/set/stream/event), bytes scalar, nullability, default values, schema version (`:33`–`:340`). |
| `SleipnirTests/Unit/Core/SleipnirDiscoveryTypeRefTests.cs` | `TypeRef` shape: `HashSet`/`SortedSet` → set, object/`JsonDocument` → any scalar, nullable value-type unwrap, native array, nested list (array of array), map-of-lists, set-of-arrays, parameter default carried, byte-underlying enum, `[SleipnirExample]` populates example, self-referential type cycle (`:44`–`:251`). |
| `SleipnirTests/Unit/Core/SleipnirDiscoveryServiceNavigationTests.cs` | `[SleipnirNavigation]` → discovery: property without attribute has null navigation, with attribute serializes the edge, camelCase JSON + omitted-when-absent (`:32`–`:56`). |
| `SleipnirTests/Integration/DiscoveryContractTests.cs` | Wire-vs-contract conformance: `Story01Discovery_MatchesCommittedGolden` (`:197`), `…_CarriesSchemaVersion` (`:229`). |
| `SleipnirTests/Integration/ServerCodegenNet10RollForwardTests.cs` | Codegen roll-forward fix + zero-controller guard regression (`:103`, `:160-167`). |

---

## 14. Relationship to other docs

| Doc | Covers (discovery-relevant) |
|-----|-----------------------------|
| `docs/discovery-schema.md` | **The authoritative type-system spec** — `DiscoveryInfo` envelope (`:23`), `TypeRef` (`:113`), scalars (`:151`), collections (`:186`), `example` (`:77-80`), full synthetic payload (`:297`), `discoveryVersion` additive-only (`:35`). |
| `PROTOCOL.md` | Wire spec: Discovery (MEX) (`:928`), response shape (`:936-968`), `TypeRef.kind` enumeration (`:971-976`), events-in-discovery (`:857`, `:865`), JSON-RPC `sleipnir.discover` (`:1189`), discovery-enables-codegen (`:1168-1169`). |
| `README_DETAILS.md` | User-facing: event methods in `DiscoveryInfo` (`:736-749`), code-first framing (`:38, :49-50`), DevUI-as-discovery-console (`:205-222`), `/api/sleipnir/discovery` returns full type metadata (`:237, :367`), media-not-in-discovery boundary (`:349, :356`), dependency-checker discovery schemas (`:443, :454`). |
| `README.md` | "discovery metadata is generated at runtime" (`:14`), `GET /api/sleipnir/discovery` (`:200`), DevUI turns runtime discovery into a console (`:241`), "Runtime discovery + Developer UI" (`:261`), codegen row "typed client stubs from discovery" (`:320`). |
| `CLAUDE.md` | `SleipnirDiscoveryService` overview incl. Weg C + `[SleipnirDataContract]` + caching (`:66`), attributes (`:93-98`), name-uniqueness + `AutoDiscover = false` exclusion (`:132-138`), test-list mention (`:143`). |
| `CODEGEN_ONBOARDING.md` | Discovery as the generator input: schema pointer (`:11-12`), versioning (`:43-44`), build-time regeneration (`:85, :98`), `SLEIPNIR001` unknown-`discoveryVersion` (`:231, :235, :388`), `contract.sleipnir.json` shape (`:414-428`), golden-conformance gate (`:142-149`), drift-fail-build (`:196`). |
| `CLIENT_GENERATION.md` | Discovery as the addressability bridge: runtime payload (`:25-26`), schema pointer (`:129-130`), non-C# producer emitting same shape (`:134`), conformance gate (`:142-144`), determinism (`:183-184`), build-time regeneration (`:196`), schema versioning (`:210`). |
| `TRANSPORT_REFERENCE.md` | The discovery endpoint row (REST `GET /discovery`). |
| `EVENTS_REFERENCE.md` | `kind:"event"` in discovery (§1, §2). |
| `DEPENDENCY_BINDING_REFERENCE.md` | Discovery as the schema source for the DevUI static checker (§11). |

> **Note:** `CODEGEN_REFERENCE.md` is referenced by `TRANSPORT_REFERENCE.md`,
> `EVENTS_REFERENCE.md`, and `DEPENDENCY_BINDING_REFERENCE.md`. It is not yet on
> `main` — it lives on the sibling branch `devui-json-view-codegen-relabel`
> (commit `994dcac`); the codegen docs currently on `main` are
> `CODEGEN_ONBOARDING.md` and `CLIENT_GENERATION.md`. The cross-links resolve
> once `devui-json-view-codegen-relabel` merges.