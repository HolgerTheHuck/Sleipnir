# Spike: Discovery → OpenAPI 3.1 Export (P3.1)

**Status:** spike complete (2026-09-01) — mapper + `--openapi` flag implemented and validated;
graduation decision open. Work item: `product-direction-work-items.md` §3.1. Branch:
`feature/openapi-export`.

## What was built

- `Sleipnir.Server.Codegen/OpenApiExporter.cs` — a **pure** mapper `DiscoveryInfo → OpenAPI 3.1`
  JSON (no I/O, no assembly loading; deterministic by the same sorting `BuildDiscovery` applies).
- `Program.cs` — optional `--openapi <path>` / `--openapi-title` / `--openapi-server` flags. The
  export is emitted whenever `--openapi` is passed, **independent of the drift verdict**. The
  MSBuild targets pass no `--openapi`, so existing builds are unchanged (opt-in only).
- `RegenerateContract` was split into `BuildDiscovery` (returns the object; serialization stays
  in `Run`) so the exporter and the contract serializer consume the same discovery instance.

## Document shape (the decisions)

- `openapi: 3.1.0`; `info.version` = discovery schema version (reproducible from the contract).
- **One pseudo-operation per `[SleipnirMethod]`**: `POST {BasePath}/{Controller}/{Method}`,
  `operationId = {Controller}_{Method}`, controller as tag, method summary as summary.
- **Flat request body** `{param: value}`; `required` = parameters without a C# default;
  C# defaults ride along as JSON Schema `default`.
- **Typed response** from the `returnType` TypeRef, plus a generic `default` error response
  (SleipnirError payload).
- `x-sleipnir-canonical-call` per operation (controller/method identity) and a top-level
  `x-sleipnir.canonicalEndpoint` recording the ONE real wire endpoint — the document never
  lets a tool forget that per-method paths are a description device.
- **Component schemas from `DiscoveryInfo.Types`** under the registry key as-is
  (`$ref: "#/components/schemas/SleipnirStories.Story01.Order"` — dots are JSON-Pointer-safe).

### TypeRef → JSON Schema mapping

| Discovery | OpenAPI 3.1 |
|---|---|
| `scalar` (closed table §3) | `string`/`boolean`/`integer(+format)`/`number(+format)`; `datetime`→`date-time`, `guid`→`uuid`, `dateonly`→`date`, `timespan`→`duration`, `bytes`→`contentEncoding:base64`, `any`→empty schema |
| `array` / `stream` | `type: array, items` — **stream is the materialized array** (§4 wire truth), noted in a description |
| `set` | `array` + `uniqueItems: true` |
| `map` | `object` + `additionalProperties` (JSON keys are strings; discovery map keys are scalar) |
| `ref` | `$ref` into `components/schemas` |
| `opaque` | empty schema + description (`nativeName` as hint only, §6) |
| `void` | empty schema (200 has no meaningful content) |
| `nullable: true` | scalar/array/map → `type: [x, "null"]`; `$ref` usages → `anyOf: [$ref, {type:"null"}]` (a `$ref` sibling would be ignored by older validators) |
| enum TypeMeta | **numeric** `enum` (System.Text.Json's wire default) + `x-sleipnir-enum-names` |
| object TypeMeta | `type: object` with **wire (camelCase)** property names via `JsonNamingPolicy.CamelCase`, `required` = non-nullable properties, `example` from TypeMeta |

## Bugs the spike caught (all fixed)

1. **Operations placed directly on the path item** instead of under `"post"` — caught by the
   structural check, invisible to a casual JSON read.
2. **PascalCase `example` values**: `TypeMeta.Example` is a live .NET instance; serializing it
   with default options re-emits CLR names. Fixed by serializing with
   `DiscoverySerialization.Options` (the same options the discovery wire uses).
3. **Method summary repeated onto every parameter**: discovery seeds
   `ParameterMeta.Documentation` from the method summary; the exporter now applies it only when
   distinct.
4. **Enum values via `JsonValue.Create(object?)`** produced untyped nodes that crash at write
   time (`TypeInfoResolver`); fixed with `SerializeToNode`. Also added the missing
   `default` emission for parameters with C# defaults.

## Validation performed

- Exports for `guide/server` (11 ops / 5 schemas), `stories/01-n-plus-one-screen` (6/6), and the
  `Sleipnir` sample app (14/5) — all with the drift-check passing in the same run.
- Structural script check per document: every path has a `post`, operationIds unique, every
  `$ref` resolves in `components/schemas`, every operation carries `200` + the canonical-call
  extension.
- A reflection probe exercised the paths **no sample covers** (no sample contract has an enum):
  numeric enum + names extension, nullable scalar (`type` array), nullable `$ref` (`anyOf`),
  map-of-arrays, C# default value, recursive self-ref — all render correctly.

## Not yet done (graduation checklist)

- ~~**Postman import round-trip**~~ **VALIDATED 2026-09-01** (manual, the work item's
  acceptance criterion): OpenAPI doc imports (typed parameter editors per method); a
  generated Postman collection with a pre-request-script shim — flat typed body in the
  editor, wrapped into the canonical envelope at send time — **executes successfully**
  (Account/Login → 200 + JWT). Key finding: the collection must target the canonical
  endpoint with the REAL wire shape `params: [{ num, parameterName, data }]`; the spike's
  first draft used a `parameters: [{name, data}]` envelope that bound every parameter to
  null — fixed in `OpenApiExporter` (commit `5d2f999`) and the shim. The shim generator
  (`postman-shim.js`) is a ~100-line node script, uncommitted (`obj/`), regenerates all
  three sample collections from the `.openapi.json` docs. **No 3.3 needed for the
  Postman path** — the shim covers execution; 3.3 remains only for generated foreign
  clients (openapi-generator).
- Unit tests pinning the mapper decisions (the exporter is pure — an in-process test project
  needs no assembly-isolation machinery, unlike the drift tests).
- Decide: runtime endpoint (`GET /api/sleipnir/openapi`) behind an option, or build-time only.
- `x-sleipnir` naming/format review if this becomes a documented public surface.
- If graduating the Postman export: promote the shim generator into the tool as
  `--postman <path>` (node script → could stay plain string templating, node-free).

## Deeper-testing notes (parking 2026-09-01 — Holger: strong feature, revisit)

The Postman round-trip validated the happy path (one REST server, Login flow). Before a
merge, these need real coverage:

- **Mapper unit tests** — first priority; the exporter is pure, so an in-process test
  project needs no assembly-isolation machinery. Pin every mapping decision from the table
  above (scalar formats, stream→array, set→uniqueItems, map→additionalProperties, opaque→
  empty schema, nullable type-array vs anyOf-ref, enum numeric + names, camelCase props,
  example casing via DiscoverySerialization.Options).
- **Shim collections vs. WS/SignalR servers** — the spike exercised REST only; the shim's
  envelope assumption should hold (same invoker), but it is untested.
- **Postman edge cases:** empty controllers (a path-item with no requestBody), `bytes`
  parameters (base64 in a JSON string vs SleipnirRequest.BinaryData — the OpenAPI says
  `contentEncoding: base64`, but the canonical wire carries binary OUT of band — likely a
  mis-edit risk to document), `[SleipnirAuthorise]` flows (Bearer header handling per
  request vs collection variable), dependency-chaining (`@alias` — response
  `exposedDependencies` must be hand-copied into the dependent call's body in Postman;
  consider a shim post-response variable that auto-extracts).
- **Real contract breadth:** the three sample servers have no enums and thin nullability —
  run the export against a consumer project with rich enums/nullable/collections before
  trusting the component schemas.
- **Postman 3.1 maturity matrix:** which of `type:[x,"null"]` / `anyOf` / `contentEncoding`
  actually render correctly in the imported editor — a 3.0 fallback flag (`nullable: true`)
  is the prepared escape hatch if 3.1 rendering degrades.

## Recommendation

The mapping is mechanical and the discovery registry carries everything needed — no gaps were
found that would force discovery-schema changes. Effort estimate "M-L" holds mostly for the
tests + tooling-validation polish, not the mapper itself. The real product decision remains
**3.3 (flat path convention)**: it is what separates "Postman shows typed fields" from
"Postman executes calls". Suggested sequencing if graduating: ship the exporter
(build-time, opt-in) + tests as an additive ride-along, decide 3.3 separately.