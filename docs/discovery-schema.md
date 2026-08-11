# Discovery Schema — The Language-Neutral Type Contract

> This is the authoritative specification of the `DiscoveryInfo` payload returned by
> `GET /api/sleipnir/discovery` (and the JSON-RPC `sleipnir.discover` capability). It defines a
> **language-neutral type system** (`TypeRef`) that the .NET server emits and any client
> generator — or any non-C# server — consumes. The protocol-level landing page is
> [PROTOCOL.md](../PROTOCOL.md#discovery-mex); the generator's runtime guard that enforces
> this shape is `assertDiscoveryShape` in
> [`clients/codegen/src/core/discovery.ts`](../clients/codegen/src/core/discovery.ts); the
> producer is [`SleipnirCore/Services/SleipnirDiscoveryService.cs`](../SleipnirCore/Services/SleipnirDiscoveryService.cs).

Sleipnir is code-first: the C# classes decorated with attributes *are* the contract, and the
discovery payload is that contract's runtime projection. For that projection to be a real
contract — usable by independent implementers and never drifting from wire behavior — it
must be **language-neutral**, **versioned**, and **pinned to the server's actual output**.
This document specifies how. The guiding invariant:

> **The discovery payload is the contract. The contract equals the server's observed wire
> output. Drift between the two is a release blocker.** See §11 for the gate that enforces it.

---

## 1. The `DiscoveryInfo` envelope

```jsonc
{
  "discoveryVersion": "1",
  "controllers": [ ControllerMeta, … ],
  "types": { "<typeKey>": TypeMeta, … }   // the contract-type registry, keyed by opaque id
}
```

| Field | Type | Meaning |
|---|---|---|
| `discoveryVersion` | `string` | Schema version, additive-only (see §11). Current: `"1"`. |
| `controllers` | `ControllerMeta[]` | One per registered `[SleipnirController]`. |
| `types` | `Record<string, TypeMeta>` | Registry of every expandable contract type (object or enum) referenced anywhere in the payload. Keys are **opaque producer-chosen identifiers** (the .NET producer uses the fully-qualified type name); they are *identity*, not type *syntax*. A `TypeRef` with `kind:"ref"` points here. The .NET producer keys and resolves this map with an **OrdinalIgnoreCase** comparer, and writes both a `ref` and its target `types` key from the same `TypeKey`, so the two always agree on casing in producer output. A non-C# producer should pick one casing convention and keep `ref` and `types` keys consistent. |

`ControllerMeta`:

```jsonc
{
  "name": "Customer",
  "methods": [ MethodMeta, … ]
}
```

`MethodMeta`:

```jsonc
{
  "methodName": "GetById",
  "returnType": TypeRef,            // the method's effective return type (Task<T> unwrapped; void → {kind:"void"})
  "parameters": [ ParameterMeta, … ],
  "documentation": "Returns a customer by ID"   // from [SleipnirDocumentation] on the method, or null
}
```

`ParameterMeta`:

```jsonc
{
  "parameterName": "id",
  "parameterType": TypeRef,
  "defaultValue": 0,                 // C# default parameter value, or absent/JSON null if none
  "documentation": "…"             // v1: copied from the method-level [SleipnirDocumentation]; per-parameter docs are not yet read
}
```

> `CancellationToken` parameters are dropped (the framework injects them); they never appear in discovery.

`TypeMeta` (a registry entry):

```jsonc
{
  "kind": "object",                  // "object" | "enum"
  "typeName": "Example.Shop.Order",   // the registry key (opaque id) repeated
  "properties": [ PropertyMeta, … ],            // present when kind:"object"
  "members": [ { "name": "Pending", "value": 0 }, … ],  // present when kind:"enum"
  "example": { … }                   // from [SleipnirExample] or a default instance; may be null
}
```

`PropertyMeta`:

```jsonc
{
  "propertyName": "Id",               // PascalCase C# name — the WIRE is camelCase (see §12)
  "propertyType": TypeRef
}
```

---

## 2. `TypeRef` — the neutral type model

Every type-bearing slot (`returnType`, `parameterType`, `propertyType`) holds a `TypeRef`,
not a string. A `TypeRef` is a discriminated object identified by `kind`:

```jsonc
TypeRef =
  | { "kind": "scalar",  "name": <scalarName>, "nullable": bool? }
  | { "kind": "array",   "element": TypeRef,   "nullable": bool? }
  | { "kind": "set",     "element": TypeRef,   "nullable": bool? }
  | { "kind": "map",     "key": TypeRef, "value": TypeRef, "nullable": bool? }
  | { "kind": "stream",  "element": TypeRef }              // IAsyncEnumerable<T> — never nullable
  | { "kind": "ref",     "ref": <typeKey>,    "nullable": bool? }
  | { "kind": "opaque",  "nativeName": "SleipnirResponse", "nullable": bool? }
  | { "kind": "void" }
```

| Field | Applies to | Meaning |
|---|---|---|
| `kind` | all | The discriminator. One of `scalar · array · set · map · stream · ref · opaque · void`. |
| `name` | `scalar` | A name from the fixed scalar table (§3). |
| `element` | `array`, `set`, `stream` | The element's `TypeRef`. |
| `key`, `value` | `map` | The key and value `TypeRef`s. Keys are restricted to scalar kinds in practice. |
| `ref` | `ref` | The `types` registry key this usage resolves to. |
| `nativeName` | `opaque` | Informational hint of the unmodelled framework/BCL type (e.g. `"SleipnirResponse"`, `"ExpandoObject"`). **Never** used as identity by consumers; present for diagnostics only. |
| `nullable` | `scalar`, `array`, `set`, `map`, `ref`, `opaque` | Occurrence-level nullability from C# nullable reference types (NRT). Absent ⟹ not nullable. `stream` and `void` are never nullable. See §7. |

Design rules:

- A `TypeRef` is a *usage site*, not a type definition. A type's structure lives once in the
  `types` registry (`TypeMeta`); every usage points to it with `kind:"ref"`. There is **no
  inline type definition on a `TypeRef`** — single source of truth, no duplication to drift.
- `kind:"ref"` is used for **both** object types and enum types; the consumer resolves the
  key into `types` and reads `TypeMeta.kind` to know which. Enums therefore carry their
  members in exactly one place (`TypeMeta.members`).

---

## 3. Scalar table

The producer maps .NET primitive/BCL types to these neutral names; the generator maps each
neutral name to the target-language spelling (its per-language tables live in
`clients/codegen/src/core/scalars.ts`). The neutral name is the contract; the .NET and
target-language spellings are producer/consumer concerns.

| Neutral `name` | .NET source (representative) |
|---|---|
| `string` | `System.String` |
| `char` | `System.Char` |
| `bool` | `System.Boolean` |
| `int` | `System.Int32` |
| `long` | `System.Int64` |
| `double` | `System.Double` |
| `float` | `System.Single` |
| `decimal` | `System.Decimal` |
| `datetime` | `System.DateTime` |
| `datetimeoffset` | `System.DateTimeOffset` |
| `dateonly` | `System.DateOnly` |
| `timeonly` | `System.TimeOnly` |
| `timespan` | `System.TimeSpan` |
| `guid` | `System.Guid` |
| `bytes` | `byte[]` (binary — flows via `SleipnirRequest.BinaryData`) |
| `uri` | `System.Uri` |
| `version` | `System.Version` |
| `any` | `object`, `dynamic`, `JsonElement`, `JsonNode`, `JsonObject`, `JsonArray`, `JsonValue`, `JsonDocument`, `ExpandoObject` |

`byte`, `sbyte`, `short`, `ushort`, `uint`, `ulong` map to their integer neutral names
(`int`/`long` family by width — see the producer for the exact width table). An unknown
scalar that the producer cannot classify must be emitted as `kind:"opaque"` with a
`nativeName` hint, never as a free-form `scalar` name — the scalar table is closed.

---

## 4. Collection kinds

The payload carries an **explicit collection-kind flag** (the prior string shape did not —
`Dictionary<,>` and `HashSet<>` collapsed to an unparseable `List<T>`-style string and were
emitted as `string`/`str` by the generator). The kinds:

| `kind` | Semantics | .NET sources |
|---|---|---|
| `array` | Ordered, duplicates allowed | `T[]`, `List<T>`, `IList<T>`, `IReadOnlyList<T>`, `ICollection<T>`, `IReadOnlyCollection<T>`, `IEnumerable<T>`, `Collection<T>` |
| `set` | Distinct elements, unordered | `HashSet<T>`, `ISet<T>`, `SortedSet<T>` |
| `map` | Keyed entries | `Dictionary<K,V>`, `IDictionary<K,V>`, `IReadOnlyDictionary<K,V>`, `SortedDictionary<K,V>`, `SortedList<K,V>` |
| `stream` | Asynchronous sequence | `IAsyncEnumerable<T>` |

`stream` is a **contract declaration only**. At runtime the Sleipnir invoker consumes an
`IAsyncEnumerable<T>` into a `List<T>` and serializes it as a JSON array on the wire (see
`CLAUDE.md` → `InvokeDi`). The discovery contract still reports `kind:"stream"` so a
non-C# server or client can model streaming authentically; the *result* wire shape for a
streaming method is the materialized array, and that is documented separately in
`PROTOCOL.md`. Consumers that do not model streaming may treat `stream` as `array` for the
materialized result without losing correctness.

---

## 5. `ref`, the registry, objects vs enums

A contract type (a class/struct/enum whose assembly belongs to the controller assemblies,
or which is force-expanded via `[SleipnirDataContract]`) is registered once in `types`. A usage
site is `{kind:"ref", ref:"<key>", nullable}`. The `IsExpandableType` boundary (opaque vs
expanded) is unchanged by this schema — only the *representation* of a type reference changes.

- **`kind:"object"`** — `TypeMeta.properties` is a flat list of `PropertyMeta`, each with a
  `TypeRef`. Nested expandable types referenced by a property are themselves registered (the
  registry is transitively closed). Recursive cycles are broken by registering the type name
  without re-expanding its properties a second time (a `ref` still resolves).
- **`kind:"enum"`** — `TypeMeta.members` is the full member list:
  `{ "name": "Pending", "value": <underlyingValue> }`. The underlying value is serialized as
  the enum's underlying numeric type. Enums were previously emitted as an opaque name with no
  members (a known gap); they are now first-class.

> Enums are registered in `types` and referenced by `ref` like any other type. The enum's
> *underlying* scalar type is not separately reported — consumers that need it can infer it
> from the `value` JSON kind in `members`, or treat the enum as its underlying scalar plus a
> constraint. (Reporting the explicit underlying scalar is a candidate future addition; it is
> additive and does not change existing fields.)

---

## 6. Opaque

`kind:"opaque"` covers types the server knows about but does not model structurally:
framework envelopes (`SleipnirResponse`), BCL types outside the contract-assembly boundary and
without a `[SleipnirDataContract]` override, and types the producer declines to expand. The
`nativeName` is a **diagnostic hint only**; consumers emit a dynamic/`any`/`object`/`Any`
placeholder and must not branch on `nativeName`. `opaque` is the explicit, named successor of
the prior "unrecognized .NET name" fallback — nothing is ever silently `unknown` on the wire.

---

## 7. Nullability

`nullable` is **occurrence-level** and lives on the `TypeRef`, not on the type definition.
It is read from C# nullable reference types (NRT) via `NullabilityInfoContext`:

- For reference types: `nullable:true` when the NRT state is `Nullable`, absent when
  `NotNullable`. (When NRTs are disabled at the assembly, all references are reported as
  non-nullable — the producer cannot infer a truth the compiler did not assert.)
- **`Task<T>` / `ValueTask<T>` returns are reported as non-nullable** (`nullable` absent),
  even when `T` is a nullable reference type. The NRT of `T` *inside* `Task<T>` is not exposed
  by `NullabilityInfoContext`, so the producer reads occurrence nullability only for non-Task
  reference returns. Since most Sleipnir methods return `Task<T>`, this is the common case; a
  consumer that wants true async-result nullability must derive it from the declared type
  separately.
- For value types: nullable only via `Nullable<T>` (`int?`), which the producer represents as
  the inner scalar with `nullable:true`. A plain value type is non-nullable.
- `stream` and `void` are never nullable; the field is absent.

Consumers render nullability per target language (TS: optional `?` / `T | null`; C#:
`Nullable<T>` for value types, NRT `T?` for references; Python: `Optional[T]`). The prior
shape carried no nullability, so the generator emitted every property optional as a
conservative default — that default is no longer needed when `nullable` is present.

---

## 8. Default values

`ParameterMeta.defaultValue` carries a C# default parameter value when the method declares
one (`void M(int id = 0)`). It is the JSON representation of the compile-time constant the
compiler recorded (`ParameterInfo.DefaultValue`); non-constant defaults (e.g. `= new X()`)
are not representable and the field is absent. The field is also absent when the parameter has
no default. Consumers may render defaulted parameters as optional in the generated signature;
previously every parameter was required because no default was reported.

---

## 9. What is *not* in the schema

Intentionally absent (and why):

- **Method overloads.** Sleipnir dispatches by `Controller_Method` name only; overloads are
  modeled with distinct `[SleipnirMethod]` names. The schema reflects what is callable.
- **The `*TypeDefinition` inline overrides.** The prior shape embedded a nested `TypeMeta`
  on each parameter/return/property (`returnTypeDefinition`, `typeDefinition`,
  `nestedTypeDefinition`). These are gone — a `ref` resolves into the single `types`
  registry, eliminating a duplication that could drift.
- **Parameter `num` / positional info.** Parameters bind by name on the wire (see
  `PROTOCOL.md`); discovery reports names, not positions.
- **Authorization metadata.** `[SleipnirAuthorise]` is not surfaced in discovery; discovery is an
  attack-surface oracle and is itself auth-gated (see `BEST_PRACTICES.md` §4.4).

---

## 10. A complete example

> The payload below is **synthetic and illustrative** — it uses the fictional `Example.Shop`
> namespace to exercise every `TypeRef` kind (scalar, array, set, map, stream, ref, enum) in
> one document. It is **not** the Story-01 contract; Story-01's real payload is the committed
> golden fixture in §11, derived from the running server, not authored here.

```jsonc
{
  "discoveryVersion": "1",
  "controllers": [
    {
      "name": "Order",
      "methods": [
        {
          "methodName": "GetById",
          "returnType": { "kind": "ref", "ref": "Example.Shop.Order" },
          "parameters": [
            { "parameterName": "id", "parameterType": { "kind": "scalar", "name": "int" } }
          ],
          "documentation": "Returns an order by ID"
        },
        {
          "methodName": "GetByArticles",
          "returnType": { "kind": "array", "element": { "kind": "ref", "ref": "Example.Shop.StockInfo" } },
          "parameters": [
            { "parameterName": "articleIds", "parameterType": { "kind": "array", "element": { "kind": "scalar", "name": "int" } } }
          ]
        },
        {
          "methodName": "LookupTags",
          "returnType": { "kind": "map", "key": { "kind": "scalar", "name": "guid" }, "value": { "kind": "set", "element": { "kind": "scalar", "name": "string" } } },
          "parameters": [
            { "parameterName": "status", "parameterType": { "kind": "ref", "ref": "Example.Shop.OrderStatus" }, "defaultValue": 0 }
          ]
        },
        {
          "methodName": "StreamOrders",
          "returnType": { "kind": "stream", "element": { "kind": "ref", "ref": "Example.Shop.Order" } },
          "parameters": [
            { "parameterName": "customerId", "parameterType": { "kind": "scalar", "name": "int", "nullable": true } }
          ]
        }
      ]
    }
  ],
  "types": {
    "Example.Shop.Order": {
      "kind": "object",
      "typeName": "Example.Shop.Order",
      "properties": [
        { "propertyName": "Id",         "propertyType": { "kind": "scalar", "name": "int" } },
        { "propertyName": "CustomerId", "propertyType": { "kind": "scalar", "name": "int", "nullable": true } },
        { "propertyName": "Status",     "propertyType": { "kind": "ref", "ref": "Example.Shop.OrderStatus" } },
        { "propertyName": "Lines",      "propertyType": { "kind": "array", "element": { "kind": "ref", "ref": "Example.Shop.OrderLine" } } }
      ],
      // example is serialized with WhenWritingNull, so a null CustomerId is OMITTED, not written.
      "example": { "id": 0, "status": 0, "lines": [] }
    },
    "Example.Shop.OrderStatus": {
      "kind": "enum",
      "typeName": "Example.Shop.OrderStatus",
      "members": [
        { "name": "Pending",   "value": 0 },
        { "name": "Confirmed", "value": 1 },
        { "name": "Shipped",   "value": 2 }
      ]
    },
    "Example.Shop.StockInfo": {
      "kind": "object",
      "typeName": "Example.Shop.StockInfo",
      "properties": [
        { "propertyName": "ArticleId", "propertyType": { "kind": "scalar", "name": "int" } },
        { "propertyName": "OnHand",    "propertyType": { "kind": "scalar", "name": "int" } }
      ]
    }
  }
}
```

---

## 11. Versioning & the no-drift gate

`discoveryVersion` follows an **additive-only** rule:

- A producer MUST set `discoveryVersion` to the highest version whose shape it emits.
- A consumer MUST accept any version whose lower-or-equal prefix it understands; it MUST
  **reject an unknown higher version loudly** (via `assertDiscoveryShape`), never silently.
- A new version MAY add fields or kinds; it MUST NOT remove or reframe existing ones. New
  `kind` values, new scalar names, and new `TypeMeta.kind` values are themselves versioned
  additions — a consumer that meets one it does not know rejects the payload rather than
  guessing.

**The contract is the server's observed output, by construction.** Three gates keep it so,
and weakening any of them is a release blocker:

1. **Producer-side derivation.** `SleipnirTests/Integration/DiscoveryContractTests.cs` fetches
   `GET /api/sleipnir/discovery` from a running host and asserts structural equality against the
   committed golden fixture
   [`clients/codegen/test/fixtures/story01-discovery.json`](../clients/codegen/test/fixtures/story01-discovery.json).
   The golden is **derived from wire behavior**, not authored: regenerating it
   (`SLEIPNIR_REGEN_GOLDEN=1`) re-fetches from the server; default mode **fails on diff**, so
   any server change that alters the contract is caught in CI.
2. **Consumer-side validation.** `assertDiscoveryShape` enforces `discoveryVersion`
   (additive-only) and validates every `TypeRef` (kind ∈ the enum, scalar `name` ∈ the table,
   `map` has `key`+`value`, `array`/`set`/`stream` have `element`, `ref` resolves to a `types`
   entry, enum `TypeMeta` has non-empty `members`). A payload the generator accepts is, by
   construction, conformant to this document.
3. **Real-Server-Konformenz.** `clients/codegen/test/e2e/story01.live.test.ts` loads discovery
   from a *live* server, asserts `discoveryVersion`, and confirms conformance to the golden
   shape — the cross-language gate that re-derives the contract from a running server and
   fails on drift.

Because the golden round-trips through the C# producer and the TypeScript consumer, the
`TypeRef` class and the `TypeRef` interface cannot silently diverge: a mismatch breaks one
of the three gates. This is how Sleipnir holds the invariant *the discovery payload is the
contract, and the contract equals the wire*.

---

## 12. Wire casing

Property **names** on the wire are **camelCase** (the server serializes with
`JsonNamingPolicy.CamelCase`): `discoveryVersion`, `controllers`, `methodName`,
`returnType`, `parameterType`, `defaultValue`, `typeName`, `propertyName`, `members`, etc.
The `propertyName` field inside `PropertyMeta` carries the **C# PascalCase** name as authored
(`Id`, `CustomerId`); the generator applies the camelCase wire fix when it emits target
properties (see `clients/codegen/src/core/model.ts` → `resolveProperty`). This casing regime
is unchanged from the prior shape — only the type-reference representation changed.