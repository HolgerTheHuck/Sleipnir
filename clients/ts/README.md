# sleipnir-client

Isomorphic JavaScript/TypeScript client for [Sleipnir](../../README.md) — the code-first,
multi-transport framework for command-oriented web APIs on .NET 8+. Runs in the
**browser** and **Node.js** with the same API.

- **Transports:** REST (`fetch`) + WebSocket. (SignalR is a roadmap item — see
  [ROADMAP.md](../../ROADMAP.md).)
- **API:** fluent `SleipnirCall` builder **and** functional `client.call("C","M",{...})`.
- **Batching + dependency chaining** in a single roundtrip (resolved server-side).
- **Binary:** `byte[]` parameters and binary results, base64 over the wire.
- **Cancellation & timeout** via `AbortSignal` → `CancelledError` (not wrapped as
  `SleipnirError`).
- **Discovery:** `client.discover()` returns the full API metadata from
  `GET /api/sleipnir/discovery`.

The wire format is specified in [PROTOCOL.md](../../PROTOCOL.md).

## Install

```bash
npm i sleipnir-client
```

In Node, the WebSocket transport uses the optional [`ws`](https://www.npmjs.com/package/ws)
package (declared as `optionalDependencies`, installed by default). In the browser, the
native `WebSocket` is used — no extra dependency.

## Quick start

```ts
import { createClient, SleipnirCall, ExecutionMode } from "sleipnir-client";

const { rest, ws } = createClient("https://localhost:5001", { bearer: token });

// --- REST, functional ---
const customer = await rest.callJson<Customer>("Customer", "GetById", { id: 42 });

// --- REST, fluent ---
const req = SleipnirCall.init("Customer", "Create")
  .with({ name: "Alice" })
  .named("step1")
  .exposes("$", "newId")
  .toRequest();
const created = await rest.call(req);

// --- Batch with dependency chaining (single roundtrip) ---
const batch = SleipnirCall.batch(
  [
    SleipnirCall.init("Customer", "Create").with({ name: "Alice" }).named("step1").exposes("$", "newId").toRequest(),
    SleipnirCall.init("Customer", "GetById").withAlias("@newId").named("step2").toRequest(),
  ],
  ExecutionMode.Serial, // Serial => @alias resolution between calls
);
const [first, second] = await rest.callBatch(batch.requests, batch.mode);

// --- List fan-out: Search → GetByIds in one roundtrip ---
// A wildcard path ($[*].id) matches every node; Sleipnir collects all matches into a
// JSON array injected as one list-typed parameter (not N separate calls).
const fanOut = SleipnirCall.batch(
  [
    SleipnirCall.init("Customer", "Search").with({ country: "France" }).named("search")
      .exposes("$[*].id", "customerIds").toRequest(),
    SleipnirCall.init("Customer", "GetByIds").withAlias("@customerIds").named("load").toRequest(),
  ],
  ExecutionMode.Serial,
);
const [_, loaded] = await rest.callBatch(fanOut.requests, fanOut.mode);

// --- WebSocket (persistent connection) ---
await ws.connect();
const live = await ws.callJson<Customer>(SleipnirCall.init("Customer", "GetById").with({ id: 42 }).toRequest());
ws.close();
```

`exposes(jsonPath, alias)` is **result-relative** — `$` is the serialized result (an
`int`, a `Customer`, a list), `$.name` a field, `$[0].id` one element, `$[*].id` **every**
element (collected into an array). There is no `$.data` envelope level. A multi-match
path produces an array, injected into a list-typed parameter (`Array<T>`,
`T[]`); the consuming method's parameter name must equal the alias (`withAlias`
derives it from `@customerIds` → `customerIds`).

```ts
import { SleipnirRestClient } from "sleipnir-client";

const client = new SleipnirRestClient("https://localhost:5001", {
  bearer: "eyJ...",
  callTimeout: 10_000,   // ms; AbortController pro Call
  headers: { "X-Trace": "abc" }, // Default-Header
  apiPath: "api/sleipnir",   // Default; Slashes werden abgeschnitten
  fetch,                 // injizierbar (Tests / älteres Node)
});

// Rohe Response (wirft nur bei Transport-/Abbruchfehlern; logische Nicht-2xx zurückgegeben)
const r = await client.call("Customer", "GetById", { id: 42 });
if (!r.isSuccess) console.warn(r.error);

// Getypt & werfend bei logischem Nicht-2xx
const c = await client.callJson<Customer>("Customer", "GetById", { id: 42 });

// Binary (byte[]-Resultat) — base64 wird dekodiert
const bytes = await client.callBinary("Document", "Download", { id: 7 }); // Uint8Array | null

// Batch
const arr = await client.callBatch([req1, req2], ExecutionMode.Parallel);

// Discovery
const meta = await client.discover();
```

Overloads: every `call*` accepts either a pre-built `SleipnirRequest` or
`(controller, method, params)`.

## WebSocket client

```ts
import { SleipnirWebSocketClient, SleipnirCall } from "sleipnir-client";

const ws = new SleipnirWebSocketClient("https://localhost:5001", {
  bearer: "eyJ...",
  wsPath: "sleipnirws",     // Default
  callTimeout: 10_000,
  connectTimeout: 15_000,
});

await ws.connect();     // wird von call() implizit aufgerufen; concurrent-safe (B1)
const r = await ws.call(SleipnirCall.init("Customer", "GetById").with({ id: 42 }).toRequest());
const batch = await ws.callBatch([req1, req2], ExecutionMode.Parallel);
ws.close();             // lehnt alle pending Calls mit SleipnirError ab
```

Correlation: each response is matched by `id` (single) or `requests[0].id` (batch).
Responses with no matching pending call are warned + dropped (no last-resort misdelivery).

## Errors

```ts
import { SleipnirError, CancelledError, isCancelled } from "sleipnir-client";

try {
  await rest.callJson("Customer", "GetById", { id: 42 });
} catch (e) {
  if (isCancelled(e)) {
    // Abbruch oder Timeout -> CancelledError (KEIN SleipnirError)
    // (e as CancelledError).timedOut === true bei Timeout
  } else if (e instanceof SleipnirError) {
    console.error(e.code, e.message, e.details, e.requestId);
  }
}
```

- **`SleipnirError`**: transport/logical failures (`code`, `message`, `details?`, `requestId?`).
- **`CancelledError`**: caller abort or timeout — propagated **unwrapped** (consistent
  with the C# client). `timedOut` distinguishes a timeout from a caller abort.

## Cancellation & timeout

```ts
const ac = new AbortController();
setTimeout(() => ac.abort(), 50);
await rest.call("C", "M", { id: 1 }, { signal: ac.signal, timeout: 5_000 }); // -> CancelledError
```

`callTimeout` (client) / `timeout` (per-call) are linked with the caller's `signal`.

## Isomorphic notes

- **Browser:** uses global `fetch`, `WebSocket`, `btoa`/`atob`. No polyfills needed.
- **Node 18+:** global `fetch` and `Buffer` are used directly; WebSocket requires `ws`
  (optional dependency). The `ws` factory is resolved lazily on first `connect()`.
- Binary payloads (`binaryData` / `content`) are base64 strings on the wire, decoded to
  `Uint8Array` on the client.

## Known limitations

- **Browser WebSocket auth:** the Sleipnir server authenticates the WS upgrade **only** via the
  HTTP `Authorization` header, which the browser WebSocket API cannot set. The client
  falls back to `?access_token=` in the URL for browser WS, which the server does not yet
  accept — so authenticated browser-WS calls need REST (or server-side `?access_token=`
  support, tracked in ROADMAP). Node (`ws`) sends the header correctly. See PROTOCOL.md.
- **Batch `id` collisions:** concurrent batches whose first request shares the same `id`
  (default `Controller.Method`) collide on correlation. Set explicit, unique `id`s via
  `.named(...)` for concurrent batches (same constraint as the C# client).

## Development

```bash
npm install
npm run build        # tsc -> dist/
npm run typecheck    # src + test, 0 Fehler
npm test             # vitest unit tests
SLEIPNIR_E2E=1 npm run test:e2e   # optional, gegen laufende Sample-App
```

## License

MIT.