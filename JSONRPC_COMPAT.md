# JSON-RPC 2.0 Compatibility

Trame ships an **opt-in** JSON-RPC 2.0 adapter so any existing JSON-RPC client
ecosystem can drive a Trame server as-is. The adapter is an adoption lure: callers
start with the JSON-RPC tooling they already have and, once they want chaining,
batch execution modes, or binary out-of-band, graduate to the native Trame wire.

The adapter maps JSON-RPC requests onto the **same** `TrameInvoker` that the REST,
WebSocket, and SignalR transports use — there is one engine, one authorization path,
one discovery surface. It is a translation layer, not a second protocol.

---

## Enable

```csharp
builder.Services.AddTrame(new TrameOptions
{
    EnableJsonRpcCompat = true,   // opt-in, default false
});
app.MapTrame();                   // or app.MapTrameEndpoints("/api/trame", enableJsonRpcCompat: true);
```

This registers a single endpoint:

```
POST /api/trame/jsonrpc          (single object or a batch array)
```

`MapTrame()` reads `TrameOptions.EnableJsonRpcCompat` and forwards it; if you call
`MapTrameEndpoints` directly, pass `enableJsonRpcCompat: true`.

---

## Wire shape

The adapter follows [JSON-RPC 2.0](https://www.jsonrpc.org/specification). A request
is an object (or an array of objects for a batch):

```json
{
  "jsonrpc": "2.0",
  "method": "TestInvoker.Add",
  "params": { "a": 3, "b": 4 },
  "id": 1
}
```

A success response:

```json
{ "jsonrpc": "2.0", "result": 7, "id": 1 }
```

An error response (note: `result` and `error` are mutually exclusive, and `id` is
**always** present — `null` when it cannot be determined, e.g. a parse error):

```json
{ "jsonrpc": "2.0", "error": { "code": -32601, "message": "Controller 'Nope' not found." }, "id": 1 }
```

The HTTP envelope is **envelope-at-200** like the native Trame REST transport: every
JSON-RPC response (including errors) is `HTTP 200` with the JSON-RPC object in the
body. The only non-`200` status is `HTTP 204`, returned when **every** request in the
call was a notification (see below).

---

## Routing — `method` is `Controller.Method`

The JSON-RPC `method` string is split at the **last** dot:

| `method`                         | Controller                  | Method   |
|----------------------------------|-----------------------------|----------|
| `TestInvoker.Add`                | `TestInvoker`               | `Add`    |
| `Customer.Address.Contact.Add`   | `Customer.Address.Contact`  | `Add`    |

A `method` with no dot is `-32600 Invalid Request`. Trame dispatches by name only
(`{Controller}_{Method}`, no parameter-based overload resolution), so each JSON-RPC
`method` maps to exactly one Trame method.

## Parameters — named and positional

* **Object** `params` → named parameters. Each key binds to the C# parameter of the
  same name (case-sensitive, like the native wire).
* **Array** `params` → positional. Each element is bound by its index (`num`), the
  same positional fallback the native fluent client uses.
* Absent / `null` `params` → no parameters.

```json
{ "jsonrpc": "2.0", "method": "TestInvoker.Add", "params": [20, 22], "id": 2 }
```

`byte[]` parameters are **not** bound positionally (a documented Trame constraint); use
named params and the native `binaryData` channel for binary, or encode as base64 via a
string parameter (see *Limitations*).

## `id` — type preserved and echoed

The `id` may be a **number** or a **string** (or null/absent for a notification). The
adapter echoes it back with the **original type**: a number id is echoed as a number,
a string id as a string. Internally, the id is also stringified for Trame's correlation
and tracing (`trame.request_id`).

## Notifications

A request **without** an `id` (or with `id: null`) is a notification: the server
executes it but emits **no** response. A batch consisting entirely of notifications
returns `HTTP 204` (no body). A batch with a mix returns an array containing only the
responses for the non-notification items.

## Batches

A JSON-RPC batch (top-level JSON array) is dispatched to the invoker in **Parallel**
mode. Responses come back in the **same order** as the requests. Notifications are
executed but omitted from the response array. An empty array is `-32600 Invalid Request`.

---

## Error-code map

Trame's logical body codes are mapped onto the JSON-RPC error ranges:

| Trame code | JSON-RPC code | Meaning                          |
|------------|---------------|----------------------------------|
| 400, 422   | `-32602`      | Invalid params (binding/validation) |
| 404 (routing — Controller/Method not found) | `-32601` | Method not found |
| 404 (business — `TrameResults.NotFound`)    | `-32000` | Server error |
| 401, 403   | `-32001`      | Server error (auth)              |
| 429, 499   | `-32000`      | Server error                     |
| 500        | `-32603`      | Internal error                   |
| parse      | `-32700`      | Parse error                      |
| invalid request | `-32600` | Invalid Request                  |
| any other non-2xx | `-32000` | Server error (catch-all)     |

The **Routing vs. Business 404** distinction is the one subtlety. Trame's 404 is
ambiguous: it is produced both when a controller/method does not exist (framework, in
the authorization pre-pass) and when a controller returns `TrameResults.NotFound`
(business). The adapter distinguishes them by the error **message prefix** — a
framework routing failure (`"Controller 'X' not found."` / `"Method 'X' not found on
controller 'Y'."`) maps to `-32601` so a generic JSON-RPC client surfaces "method not
found"; a business 404 maps to `-32000` with the domain message intact in `error.message`.

### `error.data`

When the Trame error carries a structured payload (e.g. a `ProblemDetails` body from
`TrameResults.Error(ProblemDetails)`), that payload becomes `error.data`. Otherwise,
`TrameError.details` (a diagnostic string, present in Development) is used. If neither
exists, `data` is omitted.

---

## Capability methods — `trame.*`

Two reserved methods bridge to Trame's discovery and advertise the strengths that the
compat mode does **not** expose:

### `trame.discover`

Returns the full Trame discovery metadata (the same object as
`GET /api/trame/discovery`) as the JSON-RPC `result`. Types are structured,
language-neutral `TypeRef` objects (`kind` ∈ `scalar | array | set | map | ref |
stream | opaque | void`), versioned by an additive-only `discoveryVersion`
field — see [`docs/discovery-schema.md`](docs/discovery-schema.md) for the
authoritative type-system spec:

```json
{ "jsonrpc": "2.0", "method": "trame.discover", "id": 1 }
→ { "jsonrpc": "2.0", "result": { /* DiscoveryInfo */ }, "id": 1 }
```

### `trame.capabilities`

Returns a static manifest of the native Trame features, so a JSON-RPC client knows
what it can graduate to:

```json
{
  "nativeClient": true,
  "chaining": true,
  "executionModes": ["Parallel", "Serial"],
  "binary": { "supported": true, "encoding": "base64", "transport": "native" },
  "bindingModes": ["Weak", "Strict", "Paranoid"],
  "transports": ["rest", "websocket", "signalr"],
  "compatMode": {
    "supported": true,
    "mode": "Parallel",
    "routing": "Controller.Method",
    "limits": "no chaining, no execution-mode selection, no binary out-of-band, no streaming"
  }
}
```

---

## Protocol differences: Trame wire vs. JSON-RPC 2.0

The two share a common basis — **transmit parameters as JSON, write a result (or error)
back** — but Trame's wire is richer because it targets a single, code-first .NET engine.
JSON-RPC 2.0 is the *lowest common denominator*; the differences are logical, not
arbitrary:

| Aspect | JSON-RPC 2.0 | Native Trame wire | Why Trame differs |
|--------|--------------|-------------------|-------------------|
| **Parameters** | `params` is a structured JSON value (object/array) the server consumes natively. | `params` is a native JSON array of `[{parameterName, data, num}]` where each `data` is itself a native JSON value (number, string, object, …). | Same shape as JSON-RPC at the value level: Trame adopted native `params` pre-1.0 (the earlier double-encoded `stringData` was removed). The engine binds each `data` to its C# type directly and rewrites the array in place for `@alias` substitution. The compat adapter is therefore identity at the `params` level — it only reshapes `Controller.Method` routing and the envelope. |
| **Batch shape** | A plain top-level JSON array; responses in a plain array. | `TrameMultiRequest` envelope `{requests:[…], mode}`. | Trame carries the **execution mode** (Parallel / Serial / auto-detect topological) in the envelope so the server picks the batch strategy per call. |
| **Dependency chaining** | None — each call is independent. | `dependencyMapping` + `@alias` placeholders, resolved server-side via JsonPath. | Eliminates N+1 roundtrips: one request can declare "use the `id` from call A as the input to call B", and the server resolves and binds it. |
| **Result vs. error** | `result`/`error` mutually exclusive; `id` always present (null if unknown). | `TrameResponse` always carries a logical `code` (HTTP-like) with `data` and optional `error`; `isSuccess` is derived. | Trame unifies success and *business* error into one envelope so a domain `NotFound` (404) is a real payload, not a transport failure — and so dependency propagation can name *why* a provider failed. |
| **Status surface** | Error lives in `error.code` (reserved ranges); transport is 200. | Same envelope-at-200 idea, but the logical `code` is an explicit body field used for routing/propagation. | Lets the engine distinguish routing 404 from business 404, 401 from 403, etc., and forward those into dependent calls. |
| **Binary** | None first-class (base64 inside a JSON value if you encode it yourself). | `binaryData` (request) and `content` (response) out-of-band, plus `ContentStream` for large payloads. | Avoids base64 tax and lets large results stream without buffering into the JSON tree. |
| **Discovery / MEX** | Not part of the spec. | First-class `GET /api/trame/discovery` returning full type metadata inferred from signatures. | Code-first means the contract *is* the C# classes; discovery makes it consumable for codegen and the built-in DevUI without an IDL. |
| **Streaming** | Not part of the spec (would need an extension). | `IAsyncEnumerable<T>` results consumed server-side. | Lets a method be a true async stream over the transports that support it. |
| **Transport** | Unspecified (often HTTP or WebSocket, implementer's choice). | REST, WebSocket (RFC 6455), and SignalR (MessagePack) share one engine. | Same contract, three wire formats; the invoker and authorization are transport-agnostic. |

**Lesson applied pre-1.0:** JSON-RPC's native `params` (a structured value consumed
directly) is simpler than the double-encoded `stringData` Trame originally shipped
(`stringData` was a JSON *string* of `[{parameterName, data}]`, and each `data` was
itself a JSON string — triple-escaping on the wire). That double encoding existed for
lazy deserialization and in-place `@alias` rewriting, but the `@alias` substitution
works on a `JsonNode` tree encoding-agnostically, so the wrapping was never required.
Trame removed it before 1.0: `params` is now a native JSON array with native `data`
values, identical to JSON-RPC at the value level. The compat adapter is now identity
at the `params` level — it reads native `params` and passes them straight through,
reshaping only the `Controller.Method` routing and the response envelope.

---

## Limitations (what the compat mode does **not** expose)

These are deliberately native-only — graduate to the Trame wire to use them:

* **No `@alias` chaining / dependency mapping.** JSON-RPC has no place to declare a
  JsonPath extraction; each call is independent.
* **No execution-mode selection.** Batches always run in Parallel. There is no Serial
  or topological (auto-detect) mode, and no inter-call dependencies.
* **No binary out-of-band.** `binaryData` / `content` are not exposed on the JSON-RPC
  endpoint. A `byte[]` result is returned as a base64 `result` string; a `byte[]`
  *parameter* must arrive as a base64 string bound to a `string` parameter (positional
  `byte[]` binding is not supported, matching the native constraint).
* **No streaming.** `IAsyncEnumerable<T>` results are materialized server-side into a
  JSON array `result`, exactly as the native REST path does.
* **No `exposedDependencies` on the wire.** The chaining surface is omitted.
* **Method must be `Controller.Method`.** JSON-RPC method names without a dot are
  `-32600`. Trame's dotted controller namespaces are supported (split at the last dot).

## Graduating to native

Switching a caller from the compat endpoint to the native wire (`POST /api/trame/json`,
`POST /api/trame/json/multi`, or the WebSocket/SignalR transports) unlocks chaining,
mode selection, binary, and the full discovery-driven client (`TrameCall`, the TS/JS
client). The discovery metadata from `trame.discover` is identical to
`GET /api/trame/discovery`, so codegen against either produces the same client surface.

---

## Implementation map

* `TrameRest/JsonRpc/JsonRpcAdapter.cs` — pure bidirectional translator (request →
  `TrameRequest`, `TrameResponse` → JSON-RPC, error-code map, `trame.capabilities`).
  No transport or DI dependency; unit-tested in isolation.
* `TrameRest/JsonRpc/JsonRpcDispatcher.cs` — orchestration: reads the body, dispatches
  capability methods, calls `ITrameCore.InvokeDi` (Parallel), assembles single/batch
  responses, applies the 200/204 envelope rules.
* `TrameRest/JsonRpc/JsonRpcModels.cs` — `JsonRpcRequest` DTO + `ParsedRpcItem`
  working struct.
* `TrameHub/Extensions/TrameOptions.cs` — `EnableJsonRpcCompat` option (default false).
* `TrameRest/TrameEndpointExtensions.cs` — `enableJsonRpcCompat` param + `POST /jsonrpc`.
* Tests: `TrameTests/Unit/Rest/JsonRpcAdapterTests.cs` (translation),
  `TrameTests/Integration/JsonRpcTransportTests.cs` (end-to-end over Kestrel).