# Sleipnir Wire Protocol Specification

> Version: 1.0.0 · Status: Draft
>
> This specification defines the Sleipnir wire protocol so that clients and servers
> can be implemented in any language (JavaScript/TypeScript, Python, Go, Rust, etc.).

## Overview

Sleipnir is a method-oriented RPC protocol. A client sends a `SleipnirRequest` containing
a controller name, method name, and parameters (a native JSON array). The server invokes the
matching method and returns a `SleipnirResponse` with a status code and JSON-encoded result.

Sleipnir supports three transports (REST, WebSocket, SignalR) but the request/response
format is identical across all of them.

---

## Message Types

### SleipnirRequest

Single method invocation.

```json
{
  "controller": "Customer",
  "method": "GetById",
  "params": [{ "parameterName": "id", "data": 42 }],
  "id": "Customer.GetById",
  "dependencyMapping": null,
  "binaryData": null
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `controller` | string | ✅ | Target controller name. A dotted namespace is allowed (e.g. `Customer.Address.Contact`) to express arbitrarily deep hierarchies — there is no fixed two-level limit. Controller names must be unique app-wide. |
| `method` | string | ✅ | Target method name. Dispatch is **name-only** — the server resolves `{controller}_{method}` against registered handlers and does **not** consider the parameter set, so there is no signature-based overloading over the wire. Method names must be unique within a controller. Duplicate controller or method names throw `InvalidOperationException` at startup instead of silently shadowing. Model C# overloads with distinct names (`add`, `addAll`). |
| `params` | array \| null | ❌ | Native JSON array of `SleipnirParameter` objects (`null` or omitted = no parameters). The array and each `data` value are native JSON — there is no double encoding (no JSON-string-wrapping of values). |
| `id` | string | ✅ | Request identifier for correlation |
| `dependencyMapping` | object\|null | ❌ | Map of alias → JsonPath for dependency chaining |
| `binaryData` | base64\|null | ❌ | Optional binary payload. base64 over REST and WebSocket (JSON text wire); native MessagePack `bin` over SignalR. Injected into the first `byte[]` parameter of the target method (first-match-only; a method with more than one `byte[]` parameter is not supported in v1). |

### SleipnirParameter

Method parameter within `params`.

```json
{
  "parameterName": "id",
  "data": 42
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `parameterName` | string | ✅ | Parameter name (matched server-side by name) |
| `data` | value | ✅ | Native JSON parameter value — a number, string, boolean, object, or array (NOT a JSON string). The server deserializes this value directly into the target parameter type. An `@alias` dependency placeholder is a native **string** value with an `@` prefix, e.g. `"@newId"`. |
| `num` | int | ❌ | Positional index (fallback when `parameterName` does not match) |

> **Binding order:** the server binds each method parameter by `parameterName` first.
> If the name does not match, it falls back to the positional `num` index (counting
> non-`CancellationToken` parameters). This is what lets a fluent client send arguments
> positionally without knowing the server-side parameter names. `byte[]` parameters
> are never bound positionally — they are injected from `binaryData`.
>
> `data` is a **native JSON value**, not a JSON string. Example: `42` (number),
> `"hello"` (string), `true` (boolean), `{"id":1,"name":"Alice"}` (object),
> `[1,2,3]` (array). The server feeds it straight into its `System.Text.Json`
> deserializer for the declared parameter type. The one special case is `@alias`
> dependency chaining: a `data` value that is the **string** `"@aliasName"` is
> resolved against an earlier request's `exposedDependencies` before invocation.

### SleipnirResponse

Result of a method invocation.

```json
{
  "code": 200,
  "data": { "id": 42, "name": "Alice" },
  "content": null,
  "id": "Customer.GetById",
  "exposedDependencies": null,
  "error": null
}
```

| Field | Type | Description |
|-------|------|-------------|
| `code` | int | HTTP-like status code (200, 400, 401, 404, 500, etc.) |
| `data` | any (JSON value)\|null | The method result serialized **directly as a structured JSON value** — an object, array, string, number, or boolean, exactly as `System.Text.Json` (camelCase) would emit it. Type-preserving: a `string` result arrives as a JSON string (`"Alice"`), an `int` as a JSON number (`42`), an object as `{...}`. It is **not** a JSON-encoded string blob and there is no `$.data` envelope level. `null` on `204` (no content) and on non-2xx (the error message lives in `error.message`, not here). |
| `content` | base64\|null | Binary response for `byte[]`-returning methods (the bytes live exclusively here — never duplicated as a base64 string in `data`). base64 over REST and WebSocket; native MessagePack `bin` over SignalR. Buffered into `byte[]` in v1 — a `ContentStream` field exists on the model but is not wired by any transport (see [ROADMAP.md](ROADMAP.md)). |
| `id` | string\|null | Correlates with request ID |
| `exposedDependencies` | object\|null | Map of alias → value (each value is a JSON-encoded scalar/structure) for dependency chaining |
| `error` | object\|null | Structured error (when code != 2xx) |
| `isSuccess` | bool | Derived: true if code is 200-299 (not serialized — clients compute it from `code`) |

> **`data` is raw, not wrapped.** Because `data` is emitted in one pass straight from the
> serialized result, a cross-platform client reads it as a native JSON value directly —
> no `JSON.parse(data)` / `json.loads(data)` step. A `string` result is a JSON string on
> the wire, so a client binding `data` to a `string` target receives it as-is; an object
> result binds to an object. The C# `Call<T>` deserializes the raw `data` bytes into `T`
> in a single pass; the TS `sleipnir-client` casts the already-parsed `data` to `T`.

> **Wire key order** (REST + WebSocket, JSON): `code`, `data`, `content`, `id`,
> `exposedDependencies`, `error`. Parsers read by name and tolerate reordered keys.
> Over SignalR/MessagePack the same six fields travel as a positional array in
> `[Key]` order: `[code, data, content, id, exposedDependencies, error]`; `data` is
> encoded as native MessagePack tokens (JSON→MessagePack 1:1, no string wrapping).

### SleipnirError

Structured error details, included in `SleipnirResponse.error` when `code != 2xx`.

```json
{
  "code": 404,
  "message": "Customer '99' not found.",
  "details": null,
  "requestId": "Customer.GetById"
}
```

| Field | Type | Description |
|-------|------|-------------|
| `code` | int | HTTP-like status code |
| `message` | string | Human-readable error message |
| `details` | string\|null | Additional details (stack trace in development only) |
| `requestId` | string\|null | Correlates with request ID |

### SleipnirMultiRequest

Batch of multiple requests in one roundtrip.

```json
{
  "requests": [
    { "controller": "Customer", "method": "Add", "params": [...], "id": "step1",
      "dependencyMapping": { "newId": "$" } },
    { "controller": "Customer", "method": "GetById", "params": [{ "parameterName": "id", "data": "@newId" }], "id": "step2" }
  ],
  "mode": 1
}
```

| Field | Type | Description |
|-------|------|-------------|
| `requests` | array of SleipnirRequest | Individual RPC calls |
| `mode` | int | 0 = Parallel, 1 = Serial |

> When `mode = 1` (Serial) and any request has `dependencyMapping`, the server
> performs topological sorting and executes in dependency-aware batches.

---

## Status Codes

`SleipnirResponse.code` is a **logical result code carried inside the response body**,
not an HTTP status. Over REST, the HTTP envelope is always `200 OK` and the
`SleipnirResponse` (with its `code` and `error`) is returned in the body — the same way
JSON-RPC carries its error object inside an HTTP 200. WebSocket and SignalR pass
`code` as a field of the returned object the same way.

Real HTTP/WebSocket status codes are only used for **transport-level** failures
that happen before or outside a method invocation (e.g. `400` for a malformed
request body, `429` from the rate limiter, `499` for a cancelled request).

| Code | Meaning |
|------|---------|
| 200 | OK – method executed successfully |
| 204 | No Content – void / `Task`-without-result method completed (no `data`) |
| 400 | Bad Request – invalid parameters, duplicate parameter name, unresolved dependency, cycle, or malformed JSON |
| 401 | Unauthorized – `[SleipnirAuthorise]` check failed (not authenticated) |
| 403 | Forbidden – authenticated but role/policy denied (`[SleipnirAuthorise(Role=…)]` / `[SleipnirAuthorise(Policy=…)]`; distinguished from 401 since Phase 1) |
| 404 | Not Found – controller or method not found |
| 409 | Conflict – duplicate / stale state (business) |
| 413 | Request Entity Too Large – cardinality cap (`MaxParameterArrayLength`, `MaxResultElementCount`, `MaximumBatchSize`) or message-size limit exceeded |
| 429 | Too Many Requests – rate limit exceeded (transport-level; mapped to the `ResourceExhausted` category) |
| 499 | Client Closed Request – client cancelled before the method returned (transport-level; `OperationCanceledException`) |
| 500 | Internal Server Error – method threw an exception. The generic `message` never leaks the exception; `error.details` carries the stack trace only when detailed errors are enabled (Development / `EnableDetailedErrors`). |
| 503 | Service Unavailable – reserved (rate-limiter currently produces 429 at the transport layer; `Unavailable` category) |

> `SleipnirError.requestId` is populated from the originating request `id` on every
> non-2xx response, so clients can correlate failures even in batch calls.

> **Semantic category.** Alongside the numeric `code`, every non-2xx response carries an
> `error.category` (string, `SleipnirErrorCategory`: `InvalidArgument` / `Unauthenticated` /
> `PermissionDenied` / `NotFound` / `Conflict` / `FailedPrecondition` / `ResourceExhausted` /
> `Internal` / `Unavailable` / `Cancelled`, default `None`). The category is a transport-uniform
> semantic layer mapped to gRPC equivalents and is **additive** — existing 1.0.0 clients ignore
> it. The authoritative catalog (numeric code → category → transport/business/auth kind) is
> [`ERROR_CATALOG.md`](ERROR_CATALOG.md); the stable logical-code set is declared in
> [`STABILITY.md`](STABILITY.md) §1.4.

### Returning Errors from a Controller Method

A controller method has two ways to signal a non-success outcome, and they are
**not** interchangeable.

**Return an `SleipnirResponse` with a non-2xx `Code` — recommended for business /
domain errors.** The invoker passes a returned `SleipnirResponse` through verbatim
(`SleipnirInvoker.ReturnResponse`: `if (result is SleipnirResponse) return it;`), so the
exact `Code`, `Data`, and `Error` reach the client. Use the `SleipnirResults` factory
(`SleipnirCommon.Results`) so the human message lands in the structured `SleipnirError`
while `Data` stays `null` (the message does **not** travel in `data`):

```csharp
using SleipnirCommon.Results;

[SleipnirMethod("GetById")]
public SleipnirResponse GetById(int id)
{
    var customer = _repo.Find(id);
    if (customer is null)
        return SleipnirResults.NotFound($"Customer '{id}' not found.");
    return SleipnirResults.Ok(customer);
}
```

`SleipnirResults` API: `Ok(object?)` (result serialized to a raw JSON value in
`data`), `Ok(string jsonData)` (a **pre-serialized JSON string** taken as-is —
requires valid JSON; stored as raw bytes, no re-parse), `Ok(byte[] binary)`
(bytes in `content`, `data` null), `NoContent()` (204, `data` null),
`Error(code, message, details?)`, plus convenience `BadRequest` / `Unauthorized`
/ `NotFound` / `Conflict` / `InternalServerError`, and an RFC-7807
`Error(ProblemDetails)` overload (CamelCase JSON in `data`; `title`/`detail`
mirrored onto `SleipnirError.Message`/`.Details`). The message is **not** gated by
`EnableDetailedErrors` — it is yours and reaches the client in every environment.

**Throw an exception — only for unexpected / internal failures.** Any thrown
exception is mapped to `500` with a **generic** message (`"An internal error
occurred…"`) and never leaks the exception text; the stack trace is placed in
`error.details` only when `EnableDetailedErrors` is on (Development). Throwing is
therefore wrong for validation or "not found": the client would see only the
generic 500 and never your message. Note that throwing `SleipnirException` from a
controller does **not** propagate its `SleipnirError.Code` — the server has no
`catch(SleipnirException)`; every throw becomes a generic 500. To control the code,
return `SleipnirResults.Error(...)`.

**Client side.** The C# `SleipnirClientBase.Call<T>` throws `SleipnirException` on
non-2xx, carrying `SleipnirError` (from `response.Error`, or synthesized from
`response.Code` via `SleipnirError.FromResponse`, which reads
`response.Error?.Message`). The TS `sleipnir-client` `call()` returns the raw
`SleipnirResponse` (`isSuccess:false`, does not throw); `callJson<T>()` /
`callBinary()` throw `SleipnirError`.

---

## Dependency Chaining

### Concept

A request can **expose** values from its response using `dependencyMapping`. The
JSON path is **result-relative** — the root (`$`) is the serialized result (e.g. an
`int`, a `Customer` object, a list), not the response envelope, so there is no
`data` node to traverse:
- `dependencyMapping: { "alias": "$" }` → extracts the whole result (e.g. an `int` id), stores as `alias`
- `dependencyMapping: { "custName": "$.name" }` → extracts a property of an object result. Paths are case-sensitive and match the server's **camelCase** serialization (`name`, not `Name`) — the extraction is evaluated with JsonPath.Net against the structured result, so a PascalCase path like `$.Name` finds nothing.
- `dependencyMapping: { "first": "$[0].id" }` → extracts from the first element of a list result (camelCase, as serialized server-side)
- `dependencyMapping: { "ids": "$[*].id" }` → **multi-match fan-out**: a wildcard/recursive path (`$[*].id`, `$..id`) collects *every* match into a JSON array. The array is injected as a single list-typed parameter value (`List<T>`, `T[]`, `IEnumerable<T>`), so `Search → GetByIds(@ids)` completes in one roundtrip.

A subsequent request can **use** exposed values with `@alias` placeholders:
- `"params": [{ "parameterName": "id", "data": "@orderId" }]`
- The server replaces the `@orderId` string value with the actual value from the previous response

### Resolution Rules

1. **Serial mode**: Responses are processed in order. Each response's `exposedDependencies`
   are merged into a shared context. Subsequent requests can reference any prior alias.
2. **Parallel mode**: All requests execute simultaneously. Dependencies are ignored
   (they can't be resolved if the provider hasn't completed yet).
3. **Auto-detection**: If any request has `dependencyMapping`, the server switches to
   topological batch execution regardless of the specified mode.
4. **Cycle detection**: If a cycle is detected (A depends on B, B depends on A),
   every request in the batch returns `400` with the message
   `"Circular dependency detected in request batch."` and the offending request's `id`
   in `error.requestId`.
5. **Provider failure propagates** (topological path): if a provider is unauthorized,
   errors, or does not expose a consumed alias, its dependents are **not executed** — each
   gets a `400` naming the provider, the alias, and the cause
   (`Dependency '@a' unavailable: provider '<id>' was unauthorized (401).` /
   `… returned HTTP <code>.` / `… did not expose '@a'.` / `… no provider exposes '@a'.`)
   instead of reaching the missing alias at runtime with `Unresolved dependencies`.
   Propagation is transitive — a skipped provider yields no `exposedDependencies`, so its
   own dependents are skipped in turn. See
   [DEPENDENCY_BINDING.md §9](DEPENDENCY_BINDING.md#9-provider-failure--dependent-propagation).
6. **JsonPath**: Values are extracted using [JsonPath](https://goessner.net/articles/JsonPath/)
   expressions against the response `data` value. Because `data` is a structured JSON
   value (not a string blob), the path is evaluated directly against it; `$` is the whole
   result, `$.Property` a field, `$[0].Id` an array element. Extracted values are
   re-encoded as JSON in `exposedDependencies` (so the type is preserved for the `@alias`
   substitution into the next request's parameters).

### Authorization in batches

Authorization is checked **per request**, not per batch. A `401` on one request does not
abort the others — each response is independent (JSON-RPC-conformant), so a batch may mix
unauthenticated reads with `[SleipnirAuthorise]` writes and return a mixed result array. Only
the dependency chain is coupled: a failed provider propagates to its dependents (rule 5).

`HttpContext` is not thread-safe, yet every request in a batch shares the same incoming
context. The server therefore runs the `[SleipnirAuthorise]` check **serially in a pre-pass**
before the parallel fan-out; the parallel execution (`Task.WhenAll`) never touches
`HttpContext`. Authorization is cheap (claims reads), so this does not regress parallel
throughput. **User-code contract:** controllers that obtain the context via
`IHttpContextAccessor`, and overrides of `OnAuthorization`, must treat it as **read-only**
in a parallel batch — the framework's own concurrent access is eliminated by the pre-pass,
but user code is the caller's responsibility. Full specification:
[DEPENDENCY_BINDING.md §9](DEPENDENCY_BINDING.md#9-provider-failure--dependent-propagation).

### Alias Serialization & Type Binding

> Full, dedicated specification: [`DEPENDENCY_BINDING.md`](DEPENDENCY_BINDING.md).
> This section is the protocol-level summary; the binding pipeline, the four outcomes,
> casing, and the subset fan-out pattern are specified precisely there, and codified by
> [`SleipnirTests/Unit/Core/AliasBindingTests.cs`](SleipnirTests/Unit/Core/AliasBindingTests.cs).

`@alias` resolution is a two-step JSON pipeline; the consuming parameter's declared
CLR type is enforced at the second step by `System.Text.Json`:

1. **Extract.** For each `dependencyMapping` entry, `DependencyResolver.ExtractValue`
   evaluates the JsonPath against the prior response's structured `data` (see
   *Casing Contract* below). The match count determines the shape:
   - **0 matches** → `null` (the alias is left unset).
   - **1 match** → the matched node as-is — a scalar, or, if the match itself is an
     array/object, that array/object (exposing `$` over a list returns the whole list).
   - **>1 matches** (`$[*].x`, `$..x`) → a JSON **array** of all matches.
   The extracted value is stored in `exposedDependencies` as its JSON text
   (`alias → extracted.ToJsonString()`), preserving its type for the next step.

2. **Inject.** In the consuming request, an `@alias` placeholder — a `data` field that
   is the **string** `"@alias"` — is replaced by a native `JsonNode` parsed from that
   stored JSON text. The `SleipnirParameter.data` field then carries the native extracted
   value (e.g. `42`, `"alice"`, `[1,2,3]`, `{...}`), not a JSON string.

3. **Bind.** `BuildParameters` deserializes each native `data` value into the method's
   declared parameter type via `JsonSerializer.Deserialize(data, parameterType,
   options)` — the same options used for the rest of the protocol
   (`PropertyNameCaseInsensitive = true`, `PropertyNamingPolicy = CamelCase`, **no**
   `JsonNumberHandling.AllowReadingFromString`). The outcome falls into one of four
   cases:

   | Situation | Server result |
   |---|---|
   | **Cross-kind mismatch** — a JSON number into a `string` parameter, a JSON string into an `int`, a bool into a number, an object into a scalar, an array into a scalar | `400` — `Parameter 'X' cannot be converted to type 'Y'.` `System.Text.Json` throws; the invoker catches and returns `BadRequest`. Because `AllowReadingFromString` is off, `"42"`→`int` and `42`→`string` are **both** rejected — no string/number coercion. |
   | **Unresolved alias** — the JsonPath matched 0 nodes, or no prior request exposed that alias | `400` — `Unresolved dependencies: alias.` (Serial / dangling-alias path). On the **topological** path this is caught *earlier* as provider-failure propagation — `400 dependency '@a' unavailable: …` (see [Resolution Rules §5](#resolution-rules) and [DEPENDENCY_BINDING.md §9](DEPENDENCY_BINDING.md#9-provider-failure--dependent-propagation)). |
   | **`null` extraction** | Reference type / `Nullable<T>` → `null` (no error). Non-nullable value type (`int`, `bool`, `DateTime`, …) → `400` (cannot convert null). |
   | **object → object (duck-typing)** | **No error.** `System.Text.Json` maps properties that match by name (case-insensitively), **ignores extra properties**, and **defaults missing properties silently** — a missing `int` becomes `0`, a missing `bool` becomes `false`, a missing reference becomes `null`. This is the one binding failure the server does *not* raise: a structurally narrower provider object binds without a `400` and yields default-filled fields. The DevUI dependency-builder flags it statically (see below). |

   Accepted silently (no error, genuine conversion): widening within a numeric kind
   when the value fits (`42`→`long`/`double`/`decimal`), and parseable string
   conversions (`"…"`→`Guid`/`DateTime`/`Uri`/`TimeSpan` when the format parses).
   Rejected: narrowing/lossy conversions (`3.5`→`int`, overflow, a mismatched string
   format).

   These binding failures are **returned `SleipnirResponse`s** (`SleipnirResults.BadRequest`),
   not thrown exceptions — so the message reaches the client verbatim and is **not**
   gated by `EnableDetailedErrors`.

   **Subset fan-out (intentional).** The object→object silent-drop direction is a
   feature, not only a hazard: expose one whole object once (`$` → `@customer`) and
   feed the *same* `@customer` into several consumers whose parameter types are each
   shaped to receive only the fields they need — `CustomerId { int Id; }`, `CustomerName
   { string Name; }`, full `Customer`. Each consumer duck-types the overlap; the rest
   drop. The binding rule that makes this work: each consumer **parameter must be an
   object type** (a class declaring the wanted property the same way the provider
   does). A **bare scalar** parameter (`int id`, `string name`) receiving the whole
   `{…}` object is the cross-kind row above → `400`, not a silent drop; for bare
   scalars expose per field (`$.id`, `$.name`) as separate aliases.

   **Binding modes (optional).** `SleipnirOptions.AliasBindingMode` (`Weak` default | `Strict` | `Paranoid`)
   controls how strictly the object→object silent-default row is enforced. Each mode is a
   superset of the previous in strictness:

   - **Weak** — duck-typed; a missing property is silently defaulted (value types → `0`/`false`,
     reference types → `null`) → `2xx`.
   - **Strict** — each `@alias`-sourced parameter must be **fully covered** at the top level:
     every public read-write property the consumer type declares must be present in the fragment
     JSON (matched case-insensitively). A missing property → `400` *Strict alias binding:
     parameter 'P' (Type) requires property 'X, which is absent from the '@alias' fragment.*
     Literals are not re-checked; nested objects are not descended into.
   - **Paranoid** — Strict plus two extensions: it checks **all** parameters (including literals
     the caller sent) and checks **recursively**, descending into nested object properties and
     array elements. A missing property at *any* depth, in *any* parameter → `400` *Paranoid
     binding: parameter 'P' (Type) is not fully covered by its fragment. Missing: 'P.X',
     'P.Address.Zip', …* This is server-side input validation against the declared contract.

   The safe subset direction (consumer ⊆ fragment, the fan-out above) binds in all three modes;
   only the dangerous reverse (consumer ⊋ fragment) is rejected. Cross-kind is `400` in all
   modes; widening (`int`→`long`) is accepted in all modes. Full specification:
   [`DEPENDENCY_BINDING.md`](DEPENDENCY_BINDING.md#7-binding-modes--weak-strict-paranoid).

> **DevUI static check.** The Developer UI's dependency builder runs the same
> schema walk client-side and reports inconsistencies *before* you execute:
> cross-kind mismatches and unresolved/camelCase-wrong paths surface as errors;
> opaque return types and structurally narrower object bindings (missing value-type
> properties → silent default) surface as warnings. It is advisory — *Send anyway*
> stays available, because the runtime shape can differ from the static schema
> (polymorphism, dynamic types). See [README_DETAILS.md](README_DETAILS.md).

### Casing Contract

.NET and JavaScript disagree on casing, and `System.Text.Json` and `JsonPath.Net`
disagree on case sensitivity. Sleipnir bridges them with **three distinct regimes** —
knowing which one applies where is the difference between a working chain and a
mysterious `400`:

| Where | Sensitivity | Rule |
|---|---|---|
| **Parameter *name* binding** (`params[].parameterName` → C# parameter) | **Case-sensitive** (`StringComparer.Ordinal`) | The client must send the parameter name exactly as declared in C# (PascalCase by .NET convention). `parameterName: "id"` does **not** bind to a C# parameter `Id`. |
| **Parameter *value* properties** (the JSON inside `data` → contract type) | **Case-insensitive** (`PropertyNameCaseInsensitive = true`) | The server accepts `{"Id":…}`, `{"id":…}`, `{"ID":…}` alike. It **writes** camelCase (`PropertyNamingPolicy = CamelCase`): a C# `Id` is serialized as `id` on the wire. |
| **JsonPath extraction** (`dependencyMapping` paths over results) | **Case-sensitive** (JsonPath.Net, RFC 9535) | The path must match the server's **camelCase** output. `$.name` matches; `$.Name` matches nothing → *Unresolved*. |

Consequences for cross-language use:

- **JavaScript reading C# values — effortless.** The server normalizes to camelCase
  on the wire, so JS sees `id`, `name`, … and JS property access (case-sensitive)
  matches. The C# PascalCase naming never reaches JS.
- **C# reading JavaScript values — lenient.** The server deserializes
  case-insensitively, so a JS client that accidentally sends PascalCase (`{"Id":…}`)
  still binds; a JS client that sends camelCase (the natural form) also binds.
- **The one strict spot — the JsonPath expression.** Because the result is on the
  wire as camelCase and JsonPath.Net is case-sensitive, the *path* itself must be
  camelCase: `$[0].id`, not `$[0].Id`. This is the asymmetry to remember: every
  other boundary is either case-insensitive on read or normalized on write, but the
  path expression is neither. The DevUI suggests camelCase paths and flags
  PascalCase ones.

"Case-insensitive everywhere" is **not achievable**: JS property access is
case-sensitive by language semantics, and JsonPath paths are case-sensitive by spec
— neither is a Sleipnir decision. The server already reads case-insensitively, so the
only residual strictness is forced by the toolchain, and the DevUI's camelCase
suggestions + static checks make it visible before execution.

### Limits

`@alias` chaining injects **one value per alias — scalar or array — but never expands
into multiple requests**. These are intentional design boundaries, not bugs:

1. **One alias → one value (scalar or array).** Each `dependencyMapping` entry produces
   exactly one injected value. The shape follows the JsonPath **match count**, not the
   value type:
   - **Single match** (e.g. `$`, `$.name`, `$[0].id`) → the match itself, even if that
     match is an array (exposing `$` over a list result hands the whole list through).
   - **Multiple matches** (wildcard `$[*].id`, recursive `$..id`) → a JSON **array** of
     all matches, collected by `DependencyResolver.ExtractValue`. This is the list
     fan-out: `Search → GetByIds(@ids)` in one roundtrip.
   The injected value must match the consuming parameter type — an array into a scalar
   parameter fails to deserialize, and a scalar into a `List<T>` parameter fails too.
2. **No fan-out into N requests.** A batch is a fixed list of requests. The server
   never spawns additional requests from an array result — a list of N ids does **not**
   become N `GetById(id)` calls. Fan-out goes into a single list-typed *parameter*
   (item 1), never into multiple *calls*. Dependency resolution never increases the
   number of requests.
3. **Whole-collection passthrough.** To hand a list to a single call without projecting,
   expose `$` (the whole array, one match) and bind it to a collection-typed parameter
   (`int[]`, `List<T>`). Equivalent to the multi-match array, just sourced from one
   match that is already an array.
4. **Case sensitivity.** Paths are evaluated case-sensitively against the server's
   camelCase output. Use `$[0].id`, not `$[0].Id`. This is one of three casing
   regimes — see [Casing Contract](#casing-contract) for the full picture. (The DevUI
   exposes datalist suggests the correct camelCase form.)
5. **Recommended pattern for "load all by id".** Combine the multi-match fan-out with a
   batch-get endpoint: `Search` exposes `$[*].id` as `ids`, `GetByIds(int[] ids)`
   consumes `@ids` — one roundtrip, one response, cardinality assembled server-side from
   the prior result. Per-id fan-out (N separate `GetById` calls) remains an anti-pattern.
   If a future `Map`/`ForEach` execution mode is added, it must be bounded: a configurable
   `MaxFanOut` cap, bounded concurrency (no unbounded `Task.WhenAll`), per-element
   results with correlation, and a read-only default for safety.
6. **Server-side cardinality caps.** The server protects itself independently of
   client calls via two configurable limits on `SleipnirOptions` (secure-by-default):
   - `MaxParameterArrayLength` — default **1000**, `0` = unlimited. Enforced in the
     invoker before the method call; an oversized array/collection parameter is rejected
     with `400 Bad Request` and a message naming the limit and the actual count.
   - `MaxResultElementCount` — default **10000**, `0` = unlimited. Enforced on
     materialized collection results (`413 Payload Too Large`) and on
     `IAsyncEnumerable` streams (early-stop while consuming, `413`).
   - **Body-size limits do not cover this.** The whole-collection passthrough (item 3)
     builds the array at runtime from a prior result, so the incoming request stays
     tiny while a parameter suddenly carries millions of ids. The caps close that gap
     — the server self-protects regardless of what the client sends.
   - The cap is applied to the **top-level** collection parameter/result only; arrays
     nested inside object properties are not counted (v1).
   - `string` and `byte[]` are exempt (`string` is `IEnumerable<char>`; `byte[]` has its
     own binary-size considerations).

### Binary

`byte[]` parameters (`binaryData`) and `byte[]` results (`content`) travel
**out of band from `data`** — they never compete with structured arguments and
are never duplicated into `data`. Their wire encoding depends on the channel:

- **REST and WebSocket** carry JSON text, so binary is **base64-encoded**,
  bounded by the transport message-size limits (REST 1 MB request body;
  WebSocket 1 MB per message, hardcoded). The WebSocket transport accepts
  **text frames only** — native binary frames are not honored.
- **SignalR (MessagePack)** carries `byte[]` as **native `bin`**, no base64.

`binaryData` is injected into the **first** `byte[]` parameter of the target
method (first-match-only; a method with more than one `byte[]` parameter is
not supported in v1). `byte[]` responses are **buffered** into `content`; a
`ContentStream` field exists on the model but is not wired by any transport in
v1. For large or frequent binary, run a plain REST or WebSocket endpoint
alongside Sleipnir; the v1.x+ binary-transfer plan is in [ROADMAP.md](ROADMAP.md).

> **Media (images / video / downloads) is out of scope for the RPC wire.** The
> Sleipnir wire is `POST` + JSON with a base64 binary envelope — the wrong shape
> for browser media (`<img src>` needs `GET`, raw bytes, `Content-Type`, `Range`,
> `ETag`/`304`, CDN caching). There is **no** `[SleipnirMethod]` raw/`GET` media
> return, no `SleipnirResults.Raw/File/Stream`, and no media route in discovery or
> codegen — a deliberate boundary (second dispatcher verb + transport asymmetry +
> the HTTP-semantics slope). Serve media from a **co-hosted HTTP `GET` endpoint**
> on the same ASP.NET host (same DI, same auth pipeline); Sleipnir acts as the
> *authority* that returns the resource URL and gates permission. See
> [README_DETAILS.md → Serving Media & Non-RPC Resources](README_DETAILS.md#serving-media--non-rpc-resources-images-video-downloads).

---

## Transports

### REST (HTTP/1.1 + JSON)

| Endpoint | Method | Body | Response |
|----------|--------|------|----------|
| `POST /api/sleipnir/json` | POST | SleipnirRequest | SleipnirResponse |
| `POST /api/sleipnir/json/multi` | POST | SleipnirMultiRequest | array of SleipnirResponse |
| `GET /api/sleipnir/discovery` | GET | – | DiscoveryInfo |
| `GET /api/sleipnir/events/{controller}/{method}` | GET | query params | `text/event-stream` (server-push events, opt-in `UseSse`) |
| `GET /api/sleipnir/events/{subscriptionId}` | GET | `Last-Event-Id:` header | `text/event-stream` (resume) |

**Content-Type**: `application/json`

**Example** (single call):
```
POST /api/sleipnir/json HTTP/1.1
Content-Type: application/json

{
  "controller": "Customer",
  "method": "GetById",
  "params": [{ "parameterName": "id", "data": 42 }],
  "id": "Customer.GetById"
}
```

**Response**:
```
HTTP/1.1 200 OK
Content-Type: application/json

{
  "code": 200,
  "data": { "id": 42, "name": "Alice" },
  "content": null,
  "id": "Customer.GetById",
  "exposedDependencies": null,
  "error": null
}
```

### WebSocket (RFC 6455 + JSON text frames)

**URL**: `ws://host/sleipnirws` or `wss://host/sleipnirws`

**Protocol**: JSON text messages (one per request/response).

**Message types** (auto-detected by server):
- If JSON has `requests` and `mode` fields → `SleipnirMultiRequest` → returns array of `SleipnirResponse`
- Otherwise → `SleipnirRequest` → returns single `SleipnirResponse`

**Flow**:
1. Client connects to `ws://host/sleipnirws`
2. Client sends JSON text frame (SleipnirRequest or SleipnirMultiRequest)
3. Server responds with JSON text frame (SleipnirResponse or array)
4. Repeat (connection stays open)

### SignalR (WebSocket + MessagePack)

**Hub endpoint**: `/sleipnirhub`

**Hub methods**:
| Method | Parameters | Returns |
|--------|-----------|---------|
| `DoWork` | `SleipnirRequest request` | `SleipnirResponse?` |
| `DoWorkMany` | `SleipnirMultiRequest request` | `IEnumerable<SleipnirResponse>` |

**Protocol**: MessagePack binary (optional, can be JSON if configured).

---

## Server-Push Events (Phase 3, experimental)

Sleipnir supports server→client push via `IObservable<T>` **event** methods, in addition to
request/response **calls**. An event method is marked with `[SleipnirEvent("name")]` on the
server (not `[SleipnirMethod]`) and returns `IObservable<T>`; a client **subscribes** to it and
receives an unbounded stream of event frames until it unsubscribes or the observable completes.
Events are **not chainable** — they carry no `id`/`exposes`/`@alias` semantics (a stream of push
values has no single result to expose). The subscribe/event/resume wire described below is the
**WebSocket** surface; the same events are also available over **REST via SSE
(`text/event-stream`)** — see [REST Events (SSE)](#rest-events-sse) below. SignalR has no
event surface in v1. See `STABILITY.md` §2 for the experimental-status scope.

> **Status:** experimental in v1. The wire format, subscription lifecycle, and backpressure
> may settle in a minor version. **`Last-Event-Id` resume + a server-side disconnect buffer ship as
> experimental (Phase R)** for opt-in `[SleipnirEvent(Resumable = true)]` events — see
> [Resume (Last-Event-Id)](#resume-last-event-id--resumable-events) below. See
> `docs/design/phase-3-events.md`.

### Routing: the `kind` field

Every inbound WS text frame is a `SleipnirRequest` unless it carries a `kind` field that routes
it elsewhere. The dispatcher (`SleipnirWebSocketMiddleware`) reads `kind` first:

| `kind`            | Meaning                                  | Server entry point          |
|-------------------|------------------------------------------|-----------------------------|
| *(absent)*        | A request/response call (v1.0 behavior)  | `ISleipnirCore.InvokeDi`    |
| `"subscribe"`     | Subscribe to a `[SleipnirEvent]` method  | `SubscribeAsync`            |
| `"unsubscribe"`   | Tear down an active subscription         | `SubscriptionManager`       |

`kind` is matched case-sensitively against the literal strings above. The `method` field of a
subscribe request is the `[SleipnirEvent]` name (analogous to the `[SleipnirMethod]` name for
calls); both share the `{Controller}_{name}` dispatch namespace, so an event name and a call
name on the same controller must not collide (registration throws if they do).

### Subscribe request (client → server)

Same shape as a call request, plus `kind:"subscribe"`:

```json
{
  "kind": "subscribe",
  "controller": "Chat",
  "method": "MessageReceived",
  "params": [
    { "parameterName": "chatId", "data": 42 }
  ],
  "id": "sub-1"
}
```

`params` is the standard `SleipnirParameter[]` (`{parameterName, data}` pairs, matched by name).
Parameters are **first-class subscription parameters** — bound once at subscribe time (e.g.
"all events for chat 42"). `CancellationToken` is injected automatically and not sent.

**Resume fields (optional, Phase R).** A reconnecting client may carry two extra fields to resume
a durable subscription instead of starting fresh:

```json
{
  "kind": "subscribe",
  "controller": "Chat",
  "method": "MessageReceived",
  "subscriptionId": "7a3f9c1e8b4d4e2a9b6f0c1d2e3f4a5b",
  "lastEventId": 42,
  "params": [ { "parameterName": "chatId", "data": 42 } ],
  "id": "sub-resume"
}
```

- `subscriptionId` — the **durable** id returned by the original subscribe (stable across
  reconnects). Present only on a resume; absent on a fresh subscribe.
- `lastEventId` — the highest `eventId` the client has already processed. The server replays
  buffered frames with `eventId > lastEventId`, then continues live.

Both fields are extracted out-of-band from the raw frame so the `SleipnirRequest` wire model is
untouched. A resume for a non-resumable event, an unknown/GC'd id, or an over-cap/TTL-expired
durable subscription **degrades to a fresh subscribe** (new id, `eventId` restarts at 1, no
replay) — the server never errors on a resume it cannot honor. A resume that fails the reconnect
auth re-check returns `401`/`403` and tears down the durable state (see *Delivery semantics*).

### Subscribe response (server → client)

A standard `SleipnirResponse` echoing `id`, with the new `subscriptionId` in `data`:

```json
{ "code": 200, "data": { "subscriptionId": "7a3f9c1e8b4d4e2a9b6f0c1d2e3f4a5b" }, "id": "sub-1" }
```

On a **resume**, the response carries `replayedFrom` — the first replayed `eventId` (absent on a
fresh subscribe or when nothing was buffered):

```json
{ "code": 200, "data": { "subscriptionId": "7a3f9c1e…", "replayedFrom": 43 }, "id": "sub-resume" }
```

The `subscriptionId` is the **same durable id** the client sent when the resume is honored; it is a
**new** id when the server degraded to fresh (the client must re-key its handler and reset its
dedup cursor).

Errors use the normal response error envelope. `code` follows the same semantics as calls:

| `code` | Cause                                                                 |
|--------|-----------------------------------------------------------------------|
| `400`  | Method is not an event (no `[SleipnirEvent]`); binding/param error    |
| `401`  | Authentication required and missing (subscribe-time auth)             |
| `403`  | Authenticated but role/policy denied                                  |
| `404`  | Controller or method name not found                                   |
| `500`  | Internal error (generic message; stack only with `EnableDetailedErrors`) |

**Auth runs at subscribe time**, exactly like a call (through the same auth interceptor and
`[SleipnirAuthorise]` / `[SleipnirAnonymous]` gates). A subscription is long-lived; a **resumable**
subscription also **re-runs the same authorization on reconnect** (Phase R3) — a resume re-checks
the caller against the *original* event route (recorded server-side at create time, not the
client-claimed one), so a role revoked during the disconnect gap cannot silently resume. A 401/403
on resume tears down the durable subscription and returns the error.

### Event frames (server → client)

Once subscribed, the server pushes one text frame per `IObservable<T>` element. Event frames
are a **separate frame type** — they have **no `code` and no `id`** and are correlated by
`subscriptionId`, not by the call `id`:

```json
{ "type": "event", "subscriptionId": "7a3f9c1e…", "eventId": 1, "data": { "from": "alice", "text": "hi" } }
{ "type": "event", "subscriptionId": "7a3f9c1e…", "eventId": 2, "data": { "from": "bob",   "text": "yo" } }
```

- `eventId` is a **monotonically increasing integer per subscription** (starts at 1). For a
  resumable subscription the counter is **stable across reconnects** (a durable subscription keeps
  one counter for its lifetime); the client uses it as a dedup cursor (drop `eventId ≤ lastSeen`)
  and as the `lastEventId` to resume from.
- `data` is the serialized `T` (the observable's element type), using the same JSON options as
  call results (camelCase output, case-insensitive read).
- Frames may interleave with call responses on the same socket; a client distinguishes them by
  presence of `type` (event/complete/error) vs. `code` (call response). A batch call response
  is a JSON array and is never an event frame.

### Completion and error frames (server → client)

When the `IObservable<T>` signals `OnCompleted` or `OnError`, the server sends one terminal
frame and tears down the subscription:

```json
{ "type": "complete", "subscriptionId": "7a3f9c1e…" }
{ "type": "error",     "subscriptionId": "7a3f9c1e…", "message": "Upstream source failed" }
```

After either terminal frame, no further frames are sent for that `subscriptionId`. The
subscription is disposed server-side; the client need not (and cannot) unsubscribe it
afterwards — an unsubscribe for an already-terminated subscription returns `404`.

### Unsubscribe request / response

A client tears down an active subscription early (before completion) with:

```json
{ "kind": "unsubscribe", "subscriptionId": "7a3f9c1e…", "id": "unsub-1" }
```

Response — success (`200`, `id` echoed):

```json
{ "code": 200, "id": "unsub-1" }
```

Or `404` if the `subscriptionId` is unknown (already completed, already unsubscribed, or never
subscribed on this connection):

```json
{ "code": 404, "id": "unsub-1", "error": { "code": 404, "message": "Subscription '…' not found." } }
```

### Subscription lifecycle & delivery semantics

- **`subscriptionId` is per-connection by default.** A new WebSocket connection gets fresh ids;
  ids from a different connection are not valid here. **Resumable** events (Phase R) mint a
  **durable** id that is stable across reconnects — see
  [Resume (Last-Event-Id)](#resume-last-event-id--resumable-events) below.
- **Auto-cleanup on disconnect.** When the socket closes, the server disposes every active
  **ephemeral** subscription on that connection (the observable's subscription is disposed,
  freeing the server-side source). A **durable** subscription only *detaches* its live tap — the
  source + replay buffer persist for resume.
- **Reconnect → re-subscribe (client-side).** A client with auto-reconnect re-issues
  `kind:"subscribe"` for each active subscription with the original parameters after a reconnect,
  obtaining new `subscriptionId`s. The `SleipnirWebSocketClient` does this automatically. The
  resume hook (Phase R2) chooses per subscription **Resume** (send `lastEventId`+durable id),
  **Fresh** (omit them), or **Drop** (don't re-subscribe) — default Fresh preserves the v1
  behavior.
- **Non-resumable: at-most-once-while-disconnected.** For a plain `[SleipnirEvent]` (not
  `Resumable`), events produced while the connection is down are **lost** — there is no
  server-side buffer. This is the documented gap semantic for the non-resumable path.
- **Resumable: at-least-once within the replay window** (Phase R) — see the resume subsection.
- **Backpressure.** Per subscription the server holds a buffer whose capacity and overflow
  strategy are configurable: global defaults `SleipnirOptions.EventBufferCapacity` (fallback 100)
  and `SleipnirOptions.EventBackpressureStrategy` (default `DropOldest`), overridable per event via
  `[SleipnirEvent(BufferCapacity = …, BackpressureStrategy = …)]`. Strategies: `DropOldest`
  (evict oldest — default, DoS-safe, keeps the subscription recent), `DropWrite` (drop newest —
  preserves backlog, loses freshness), `Block` (block the producer until the consumer drains —
  lossless but back-pressures the source), `Unbounded` (no cap, no DoS backstop). `DropOldest`
  evictions and `DropWrite` rejections increment the `sleipnir.event.dropped` counter; `Block` and
  `Unbounded` never drop. The counter is accurate as of 1.2.0 (the earlier `DropOldest`-channel
  path could not detect saturation — `TryWrite` returned `true` unconditionally — so it was dead
  code).
- **Cold vs. hot source.** The framework is a pass-through: at subscribe time it invokes the
  controller method **once** (in a fresh DI scope per subscription) and subscribes to the returned
  `IObservable<T>` once. It does not wrap in `Publish`/`RefCount`/`ReplaySubject`, so cold/hot
  behavior is entirely the producer's. A **cold** source gives each subscriber an independent
  stream from its own start; a **hot** source (e.g. a shared `Subject<T>`) broadcasts to all
  current subscribers and does not replay pre-subscribe events. The at-most-once-while-disconnected
  rule applies to both on the non-resumable path: a hot source keeps producing while you are
  disconnected and those events are lost; a cold source simply restarts on re-subscribe.
  **Resume is meaningful only for a hot/durable source** — see the resume subsection. Build
  shared-broadcast semantics in the producer (a singleton subject) — the framework will not
  infer them.

### Resume (Last-Event-Id) — resumable events

**Phase R (experimental).** A `[SleipnirEvent(Resumable = true)]` declares the event source is a
**long-lived hot/durable observable** whose subscription is meaningful to keep alive across a
disconnect. The server maintains a per-durable-subscription disconnect buffer; on reconnect the
client sends `lastEventId` and the server replays the gap. This is **opt-in on two axes**, both
required for resume:

1. **Server axis:** `[SleipnirEvent(Resumable = true)]` — the server keeps the `IObservable<T>`
   source subscribed across disconnects and buffers events into a bounded replay ring. Non-
   resumable events keep the v1 ephemeral behavior unchanged.
2. **Client axis:** a per-subscription **resume policy** (`SleipnirWebSocketClient` constructor
   `resumePolicy` / per-`SubscribeAsync` override; `onResume` in the TS client) decides per
   subscription **Resume** (send `lastEventId` + the durable id), **Fresh** (omit them), or **Drop**
   (don't re-subscribe). Default is Fresh (preserves v1 behavior). Resume is honored only when the
   event is `Resumable`; a Resume on a non-resumable event degrades to Fresh.

**Delivery semantics — at-least-once within the replay window.** Exactly-once is impossible
without per-event acks (none in v1). The client **dedups by `eventId`**: it tracks the highest
`eventId` seen per subscription and silently drops replayed frames with `eventId ≤ lastSeen`. Net
effect: no gap within the buffer window, no duplicates after dedup. Events beyond the window
(overflow during a long disconnect) are still lost and counted in `sleipnir.event.dropped`.

**Durable subscriptionId.** For a resumable subscription the `subscriptionId` is server-generated
once, returned in the first subscribe response, and **stable across reconnects**. Event frames
carry the same id across reconnects, so the client's per-subscription handler key is stable (no
id-swap churn on resume). When the server cannot honor a resume it returns a **new** id (degrade
to fresh); the client re-keys its handler and resets its dedup cursor.

**Reconnect auth re-check.** A resume re-runs the same authorization a fresh subscribe runs,
against the **original** controller/method recorded server-side at create time (not the
client-claimed route — a caller cannot lie about the route to land a weaker auth check). A role
revoked during the disconnect gap must not silently resume: a 401/403 on resume tears down the
durable subscription and returns the error; a 404 (route vanished) does the same.

**Retention / GC / DoS backstop.** A durable subscription is evicted after a configurable idle
`SleipnirOptions.EventResumeTtl` (fallback 60s) with no attached client, or on explicit
unsubscribe, or when the source completes/errors. A process-wide cap
`SleipnirOptions.EventMaxDurableSubscriptions` (fallback 10 000) rejects over-cap creates with
`503`. The replay ring is capped at `SleipnirOptions.EventReplayBufferCapacity` (fallback 1000),
evicting oldest (each eviction increments `sleipnir.event.dropped`).

**In-process only.** The durable store lives in-process; it does not survive a server restart
(no persistent backend). Cross-process durability is a later extension.

### Discovery

Event methods appear in `DiscoveryInfo` like any other method; their event-ness is expressed on
the return type (there is no method-level `kind` field):

```json
{
  "methodName": "MessageReceived",
  "returnType": { "kind": "event", "element": { "kind": "ref", "ref": "Message" } },
  "parameters": [ { "parameterName": "chatId", "parameterType": { "kind": "scalar", "name": "int" } } ]
}
```

A consumer detects a subscribable method by `returnType.kind == "event"`; the element type is
`returnType.element`. The `[SleipnirEvent]` name is the `methodName`.

### REST Events (SSE)

For clients that can only do REST — corporate proxies/firewalls that block WebSocket
upgrades — the same `[SleipnirEvent]` methods are available over **Server-Sent Events**
(`text/event-stream`), opt-in via `SleipnirOptions.UseSse` (default `true`). SSE reuses the
exact Phase R resume machinery (durable subscriptions, the replay ring, `Last-Event-Id`),
maps each logical event frame onto an SSE block, and shares the process-wide subscription
store with WebSocket — so **a subscription created over WebSocket can be resumed over SSE
and vice-versa** (cross-transport resume).

**Endpoints** (under the existing `/api/sleipnir` group):

| Endpoint | Method | Args | Resume |
|----------|--------|------|--------|
| `/api/sleipnir/events/{controller}/{method}` | GET | method args as **query params** (GET has no body) | fresh subscribe |
| `/api/sleipnir/events/{subscriptionId}` | GET | `Last-Event-Id:` header (and/or `?lastEventId=`) | resume a durable subscription |

**Fresh subscribe.** The transport `RequireAuthentication` gate returns `401` (no stream) for an
unauthenticated request; per-method `[SleipnirAuthorise]` runs at subscribe time via the same
`SubscribeAsync`/`AuthorizeSubscribeAsync` path as WebSocket. Each query parameter is parsed as
JSON when valid (`JsonNode.Parse`), else as a string — so a caller sends `?chatId=42` for an int
or `?name=%22hi%22` for a string. On success the response is `Content-Type: text/event-stream`,
`Cache-Control: no-cache`, `X-Accel-Buffering: no`; the server writes the `ack` event, then live
frames.

**Resume.** `store.Lookup(subscriptionId)` → not found (GC'd / TTL-expired) → **HTTP 410 Gone**
(the client falls back to a fresh subscribe). Found → `AuthorizeSubscribeAsync` re-runs auth
against the **original** event route recorded at create time (a role revoked during the gap does
not silently resume) → 401/403 + `store.Destroy` on failure → `Attach(lastEventId)` → the `ack`
carries `replayedFrom`, then the gap is replayed, then live frames continue.

**Wire mapping** — each logical frame becomes one SSE block (separated by a blank line):

| Phase R (WS, in-frame) | SSE block |
|---|---|
| `eventId` (monotonic) | `id: {eventId}` line (→ `EventSource` auto-sends `Last-Event-Id`) |
| `type: "event"\|"complete"\|"error"` | `event: {type}` line |
| `{type,subscriptionId,eventId[,data][,message]}` | `data: {frame}` (one JSON object per block) |
| ack `{subscriptionId, replayedFrom?}` | first block: `id: 0` / `event: ack` / `data: {…}` |

The `ack` is written **before** any live frame (the same ack-before-first-frame invariant as
the WebSocket race fix), so a client never sees an `eventId` for a `subscriptionId` it has not
yet learned.

**Backpressure** mirrors WebSocket: unbounded durable tap → bounded `EventBuffer` (DropOldest,
drop-counted via `sleipnir_event_dropped`) → `Response.Body`. A dropped live frame is **not
lost** — it remains in the replay ring for resume. No subscription-store change.

**Auth note.** Native `EventSource` **cannot set the `Authorization` header**, so Bearer-auth
hosts need a fetch-based client (the TS `SleipnirSseClient` controls both headers and URL). For
cookie-auth hosts, native `EventSource` against the resume URL reconnects with `Last-Event-Id`
for free. The server reads auth from `HttpContext.User` as on every other path.

---

## Discovery (MEX)

`GET /api/sleipnir/discovery` returns `DiscoveryInfo`. Types are carried as structured,
language-neutral `TypeRef` objects (not .NET type-name strings) — see
[`docs/discovery-schema.md`](docs/discovery-schema.md) for the authoritative type-system
spec, the scalar table, collection-kind semantics, enum members, nullability, and the
additive-only `discoveryVersion` rule.

```json
{
  "discoveryVersion": "1",
  "controllers": [
    {
      "name": "Customer",
      "methods": [
        {
          "methodName": "GetById",
          "returnType": { "kind": "ref", "ref": "Sleipnir.Model.Customer" },
          "parameters": [
            {
              "parameterName": "id",
              "parameterType": { "kind": "scalar", "name": "int" }
            }
          ],
          "documentation": "Returns a customer by ID"
        }
      ]
    }
  ],
  "types": {
    "Sleipnir.Model.Customer": {
      "kind": "object",
      "typeName": "Sleipnir.Model.Customer",
      "properties": [
        { "propertyName": "Id", "propertyType": { "kind": "scalar", "name": "int" } },
        { "propertyName": "Name", "propertyType": { "kind": "scalar", "name": "string" } }
      ],
      "example": { "id": 0, "name": "string" }
    }
  }
}
```

`TypeRef.kind` is one of `scalar | array | set | map | ref | stream | opaque | void`;
`scalar` carries a `name` from the fixed scalar table, `array`/`set`/`stream` carry an
`element`, `map` carries `key`+`value`, `ref` carries a key into `discovery.types`, and
`opaque` carries a diagnostic `nativeName`. Enums register as a `TypeMeta` with
`kind:"enum"` + `members`; a usage site is a `{kind:"ref", ref:"<enumKey>"}` (Sleipnir
serializes enums as their underlying integer, so a ref to an enum reads as a JSON number).

---

## Observability Endpoints (experimental, opt-in)

Sleipnir exposes two optional observability surfaces. Both are **opt-in** and, when the host
runs with `RequireAuthentication = true`, **RequireAuth-gated** like `GET /discovery` — an
unauthenticated caller receives `401`. Per-method `[SleipnirAuthorise]`/`[SleipnirAnonymous]`
auth is the invoker's job and does not apply to these framework endpoints; the gate is the
transport-level `HttpContext.User.Identity.IsAuthenticated` check (populate it upstream via
token middleware / a reverse proxy).

### `GET /api/sleipnir/metrics` — Prometheus text scrape

A pull-model scrape endpoint exposing the Sleipnir `Meter "Sleipnir"` instruments in
[Prometheus text exposition format](https://github.com/prometheus/docs/blob/main/content/docs/instrumenting/exposition_formats.md).
Wire it from `Sleipnir.Telemetry` (a separate opt-in; `Sleipnir.Server` does not reference
the OTel SDK):

```csharp
builder.Services.AddSleipnirPrometheusMetrics();          // subscribe the meter + Prometheus exporter
// …
app.UseSleipnirPrometheusScrapingEndpoint("/api/sleipnir/metrics", requireAuth: true);
```

The path defaults to `/api/sleipnir/metrics`; `requireAuth` defaults to `true`. The auth gate
reads `ISleipnirCore.RequireAuthentication` from request-scoped DI (`ISleipnirCore` lives in
`SleipnirCore`, so no `SleipnirHub`/`SleipnirOptions` dependency is needed). When authed, the
response is `text/plain; version=0.0.4` with one `# HELP` / `# TYPE` pair plus sample lines per
instrument. Instrument names map dots to underscores: `sleipnir.call.duration` →
`sleipnir_call_duration`, `sleipnir.ws.connections` → `sleipnir_ws_connections`, etc.

**Instruments** (tags follow OTel RPC semantic conventions — `rpc.system=sleipnir`,
`rpc.service`, `rpc.method`):

| Instrument | Kind | Unit | Tags |
|---|---|---|---|
| `sleipnir.call.duration` | histogram | `ms` | `rpc.system`, `rpc.service`, `rpc.method`, `sleipnir.error_category`, `sleipnir.success` |
| `sleipnir.call.count` | counter | `{call}` | as above |
| `sleipnir.error.count` | counter | `{call}` | as above (success=false subset) |
| `sleipnir.batch.fan_out` | histogram | `{request}` | `rpc.system`, `sleipnir.batch.mode` |
| `sleipnir.batch.count` | counter | `{batch}` | `rpc.system`, `sleipnir.batch.mode` |
| `sleipnir.event.dropped` | counter | `{event}` | `rpc.system`, `sleipnir.subscription_id` (Phase 3) |
| `sleipnir.ws.connections` | observable gauge | `{connection}` | — (live WebSocket connections) |
| `sleipnir.subscriptions.active` | observable gauge | `{subscription}` | — (live event subscriptions) |

`AddSleipnirPrometheusMetrics` and the push-model `AddSleipnirTelemetry` (OTLP→collector→Grafana)
do not conflict — pull and push can both be wired. **The Prometheus-text `/metrics` interface is
the durable contract**: any scraper (Prometheus, Grafana Agent, VictoriaMetrics, or an embedded
stack) reads it. The OTel exporter behind it is the interim producer and may be replaced without
changing consumers.

### `GET /api/sleipnir/observability` — JSON snapshot (Developer UI)

A small JSON snapshot of live transport/runtime state for the Developer UI Observability panel.
Opt-in via `SleipnirOptions.EnableObservability = true` (default `false`); the endpoint is mapped
only when the flag is on (otherwise `404`). RequireAuth-gated like `/discovery`.

```json
{
  "transports": { "rest": true, "webSocket": true, "signalR": false },
  "activeConnections": 2,
  "activeSubscriptions": 5,
  "eventDroppedTotal": 0,
  "callCount": 142,
  "errorCount": 3,
  "batchCount": 7,
  "uptimeMs": 183402
}
```

| Field | Type | Meaning |
|---|---|---|
| `transports.rest` | bool | REST is on (the endpoint lives in the REST group) |
| `transports.webSocket` | bool | WebSocket channel state from `SleipnirOptions` |
| `transports.signalR` | bool | `SleipnirOptions.UseSignalR` |
| `activeConnections` | int | Live WebSocket connections |
| `activeSubscriptions` | int | Live event subscriptions across all connections |
| `eventDroppedTotal` | long | Cumulative events dropped to backpressure |
| `callCount` | long | Cumulative completed RPC calls (success or error) |
| `errorCount` | long | Cumulative failed RPC calls (non-2xx) |
| `batchCount` | long | Cumulative batches processed |
| `uptimeMs` | long | Milliseconds since the registry was created (≈ host start) |

The snapshot is produced from a process-wide lock-free `SleipnirConnectionRegistry`
(`Interlocked` accumulators), not from the OTel SDK — so the JSON endpoint is readable without a
subscribed `MetricReader`. The same registry backs the `sleipnir.ws.connections` /
`sleipnir.subscriptions.active` gauges scraped at `/metrics`. See
[`README_DETAILS.md`](README_DETAILS.md) → Distributed Tracing.

---

## TypeScript Client Example

> A maintained, isomorphic reference client (REST + WebSocket, fluent + functional
> API, browser + Node.js) ships under [`clients/ts/`](clients/ts/) as the
> `sleipnir-client` npm package. The snippet below illustrates the wire format only;
> for real use prefer `import { createClient } from "sleipnir-client"`.

```typescript
interface SleipnirParameter {
  parameterName: string;
  data: unknown; // native JSON value — number, string, boolean, object, array
  num?: number;
}

interface SleipnirRequest {
  controller: string;
  method: string;
  params?: SleipnirParameter[] | null; // native array (no JSON-string wrapping)
  id: string;
  dependencyMapping?: Record<string, string> | null;
}

interface SleipnirResponse {
  code: number;
  data: unknown | null; // structured JSON value — NOT a string blob, no JSON.parse needed
  content?: string | null; // base64 for byte[] results
  id: string | null;
  error: { code: number; message: string; details?: string; requestId?: string | null } | null;
  exposedDependencies?: Record<string, string> | null;
  isSuccess: boolean; // derived client-side from code (server does not send it)
}

async function sleipnirCall(
  url: string,
  controller: string,
  method: string,
  params: Record<string, any>,
  id?: string
): Promise<SleipnirResponse> {
  const request: SleipnirRequest = {
    controller,
    method,
    params: Object.entries(params).map(([name, value]) => ({
      parameterName: name,
      data: value,
    })),
    id: id ?? `${controller}.${method}`,
  };

  const resp = await fetch(`${url}/api/sleipnir/json`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(request),
  });

  const r = (await resp.json()) as SleipnirResponse;
  r.isSuccess = r.code >= 200 && r.code <= 299; // server omits isSuccess
  return r;
}

// Usage — data is already a structured value (the Customer object), no JSON.parse
const result = await sleipnirCall("https://localhost:5001", "Customer", "GetById", { id: 42 });
const customer = result.data as { id: number; name: string };
console.log(customer.name);
```

## Python Client Example

```python
import requests
from typing import Any

def sleipnir_call(url: str, controller: str, method: str, params: dict[str, Any], id: str = None) -> dict:
    request = {
        "controller": controller,
        "method": method,
        "params": [
            {"parameterName": k, "data": v}
            for k, v in params.items()
        ],
        "id": id or f"{controller}.{method}",
    }
    response = requests.post(f"{url}/api/sleipnir/json", json=request)
    r = response.json()
    r["isSuccess"] = 200 <= r.get("code", 0) <= 299  # server omits isSuccess
    return r

# Usage — data is already a structured value (the Customer object), no json.loads
result = sleipnir_call("https://localhost:5001", "Customer", "GetById", {"id": 42})
customer = result["data"]  # dict, directly usable
print(customer["name"])
```

---

## Design Principles for Cross-Platform Implementations

1. **Wire format is JSON** – no binary dependency for the protocol itself (MessagePack is optional for SignalR only)
2. **Parameter values are native JSON** – `params` is a JSON array of `{parameterName, data}` where `data` is itself a native JSON value (number, string, boolean, object, array). There is no double encoding: a parameter `42` is sent as `42`, not `"42"`. The server deserializes each value directly into the target parameter type.
3. **Discovery enables code generation** – the `/api/sleipnir/discovery` endpoint returns full type metadata, enabling auto-generated clients in any language.
4. **No schema language required** – unlike gRPC (`.proto`) or GraphQL (SDL), the contract is defined in code and discovered at runtime.

---

## JSON-RPC 2.0 Compatibility

Sleipnir ships an **opt-in** JSON-RPC 2.0 adapter (`SleipnirOptions.EnableJsonRpcCompat`,
default off) that maps JSON-RPC requests onto the same `SleipnirInvoker`:

```
POST /api/sleipnir/jsonrpc        # single object or a batch array
```

* `method` is `Controller.Method` (split at the last dot).
* `params` object → named parameters; `params` array → positional (bound by `num`).
* `id` (number/string) echoed with its original type; absent/null → notification (no
  response). A batch of only notifications → `HTTP 204`.
* HTTP envelope is envelope-at-200 like the native REST transport; errors live in
  `error.code` (Sleipnir codes mapped to the JSON-RPC ranges — incl. routing-404 →
  `-32601` vs. business-404 → `-32000`).
* Capability methods `sleipnir.discover` (→ DiscoveryInfo) and `sleipnir.capabilities`
  (→ static strengths manifest) bridge to the native surface.
* **Limitations:** no `@alias` chaining, no execution-mode selection (Parallel only),
  no binary out-of-band, no streaming — graduate to the native wire for those.

Full spec, the Sleipnir-vs-JSON-RPC protocol-differences table, and the implementation
map: see [`JSONRPC_COMPAT.md`](JSONRPC_COMPAT.md).