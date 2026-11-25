# Sleipnir — Guide for AI Coding Agents

The 5-minute map for an agent that builds an application **with** Sleipnir: a .NET RPC
framework that is code-first (the C# controller classes *are* the contract, no `.proto`) and
serves every method over REST, WebSocket, SSE (events) and SignalR. This file is short on
purpose; the linked references stay the source of truth. Read the one you need, not all of them.

(Working **on** the Sleipnir repository itself? Read `CLAUDE.md` instead.)

---

## 1. Packages and versions

| Package | Channel | What it is |
|---------|---------|------------|
| `Sleipnir.Server` | NuGet | Server, all transports + DevUI. The only server package you need. |
| `Sleipnir.Server.Codegen` | NuGet | Build-time contract export + drift gate (`contract.sleipnir.json`). |
| `Sleipnir.Client` | NuGet | C# client (REST / WebSocket / SSE / SignalR, `SleipnirCall` builder). |
| `Sleipnir.Generator` | NuGet | Roslyn source generator: typed C# client from `contract.sleipnir.json`. |
| `sleipnir-client` | npm | TS/JS runtime client (browser + Node). |
| `sleipnir-codegen` | npm | `sleipnir-gen` CLI: typed TS/JS/C#/Python clients from the contract. |

Current versions — copy these pins:

```xml
<PackageReference Include="Sleipnir.Server" Version="1.10.0" />
<PackageReference Include="Sleipnir.Server.Codegen" Version="1.10.0" />
```

```json
"sleipnir-client": "^1.10.0",
"sleipnir-codegen": "^1.10.0"
```

**Compatibility rule:** NuGet and npm share major and minor (`1.9.x` with `1.9.x`); patch
versions move per channel. Whether a client matches a server is decided by the contract hash,
not by package versions (`STABILITY.md` §3). Libraries ship `net8.0` and `net10.0`; .NET 11 runs
the `net10.0` build.

---

## 2. Minimal server

```csharp
using SleipnirHub.Extensions;
using SleipnirServer;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSleipnir(o =>
{
    o.EnableDetailedErrors = builder.Environment.IsDevelopment();
    // o.UseSignalR = true;          // opt-in; REST, WebSocket and SSE are on by default
    // o.ValidateParameters = true;  // DataAnnotations on parameters → 400 with field errors
});

var app = builder.Build();
app.UseRouting();
app.UseSleipnirTransports();   // WebSocket + controller registration (auto-discovery)
app.MapSleipnir();             // REST /api/sleipnir, SSE events, DevUI /Sleipnir, SignalR hub
app.Run();
```

Controllers are discovered automatically and resolved per call from DI (scoped), so constructor
injection works.

```csharp
using SleipnirCore.Attributes;

[SleipnirController("Orders")]
public class OrdersController(IOrderStore store)
{
    [SleipnirMethod("GetById")]
    public async Task<Order?> GetById(int id, CancellationToken ct) => await store.Find(id, ct);

    [SleipnirMethod("Create")]
    [SleipnirAuthorise(Role = "Admin")]          // or Policy = "…"; [SleipnirAnonymous] opts out
    public Task<Order> Create(NewOrder order) => store.Add(order);
}
```

- Parameters bind **by name, case-sensitively**. `CancellationToken` is injected. A parameter
  with a C# default or a nullable type is optional; anything else is required (`400` if absent).
- Return `T`, `Task<T>`, `void`/`Task` (→ `204`) or `IAsyncEnumerable<T>` (sent as a JSON array).
- Put DTOs in the API project. Types from other assemblies appear as opaque `unknown` in
  generated clients (startup log and `sleipnir-gen` warn about this).

Try it without a client: the DevUI at `/Sleipnir`, or

```bash
curl -s -X POST http://localhost:5000/api/sleipnir/json -H 'Content-Type: application/json' \
  -d '{"controller":"Orders","method":"GetById","params":[{"parameterName":"id","data":1}],"id":"q1"}'
```

The HTTP status is `200` for every call the server could read; the real status is the body's `code`.

---

## 3. Errors

| Situation | Do this | Client sees |
|-----------|---------|-------------|
| Domain error (not found, conflict, …) | `return SleipnirResults.NotFound("Order 7 not found.")` (also `BadRequest`, `Conflict`, `Forbidden`, `Error(code, message, category)`) | `code` + `error.message` + `error.category` |
| Domain error, typed success value | return `SleipnirResponse<Order>`: `SleipnirResponse<Order>.Ok(o)` / `.NotFound("…")` | typed `data`, same error shape |
| Field validation | `return SleipnirResults.ValidationProblem(errors)` — or opt in to `ValidateParameters` | `400`, `error.errors: { "order.lines[1].qty": ["…"] }` |
| Unexpected failure | `throw` | generic `500`, no message leak (details only with `EnableDetailedErrors`) |

Rules: a method that returns `SleipnirResponse` is passed through verbatim. **Never throw to signal
a domain error** — it becomes a generic `500`, also when you throw `SleipnirException`. Clients
should branch on `error.category` (`InvalidArgument`, `NotFound`, `Unauthenticated`,
`PermissionDenied`, …), not on the message text. Validation keys are camelCase wire paths rooted at
the parameter name.

Namespaces: `SleipnirResults` in `SleipnirCommon.Results`, `SleipnirResponse<T>` in
`SleipnirCommon.Models`. Full catalog: `ERROR_CATALOG.md`.

---

## 4. Contract and typed clients

The recommended loop — the client is generated from a committed file, never from a running server:

```bash
# server project, once:
dotnet add package Sleipnir.Server.Codegen
SLEIPNIR_REGEN_GOLDEN=1 dotnet build     # PowerShell: $env:SLEIPNIR_REGEN_GOLDEN='1'; dotnet build
git add contract.sleipnir.json
```

From then on **every server build fails if the API no longer matches `contract.sleipnir.json`**.
After an intended API change: rebuild with `SLEIPNIR_REGEN_GOLDEN=1`, commit the contract,
regenerate the clients.

```bash
# TypeScript client from the contract (add --selfcheck in CI; pass the same --transport to both)
npx sleipnir-gen --lang ts --discovery ../Api/contract.sleipnir.json --out src   # writes src/api/*.ts
```

```ts
import { SleipnirClient } from "./api/index.js";

const client = new SleipnirClient("https://api.example.com", { bearer: () => token });

const res = await client.call(client.orders.getById(7));   // TS builder is synchronous
if (res.isSuccess) show(res.data);                           // res.data: Order | null
else if (res.error?.category === "NotFound") showMissing();
else showFieldErrors(res.error?.errors);                     // validation: field path → messages

client.connection.subscribe((s) => setOnline(s === "open")); // connecting | open | reconnecting | closed
```

The client picks the transport itself (WebSocket, falling back to REST + SSE). C# clients:
`Sleipnir.Generator` reads the same contract at compile time. Details: `CODEGEN_ONBOARDING.md`
(C# + loop), `CLIENT_GENERATION.md` (TS/JS/Python), `CONTRACT_EVOLUTION.md` (breaking-change
gate against a released baseline).

---

## 5. Events (server push)

```csharp
[SleipnirEvent("PriceChanged")]
public IObservable<PriceTick> PriceChanged(string symbol) => _feed.For(symbol);   // hot observable
```

```ts
const sub = await client.market.priceChanged("BTC", {
  onNext: (tick) => render(tick),
  onComplete: () => {},
  onError: (err) => showError(err),
});
await sub.unsubscribe();
```

- Return `IObservable<T>` **directly** (not `Task<…>`). Events are not chainable.
- **Ephemeral (default):** events sent while a client is disconnected are lost; the client
  re-subscribes automatically with the same parameters.
- **Resumable** (`[SleipnirEvent("X", Resumable = true)]`): the server buffers what one dropped
  subscription missed (ring of 1000, idle TTL 60 s). The method must return one long-lived hot
  observable.
- **Send buffer:** per subscription, default 100 events, `DropOldest` (drops are counted in
  `sleipnir.event.dropped`). Override per event: `BufferCapacity = …`, `BackpressureStrategy = …`.
- **Late subscribers / job progress (history + live):** use `SleipnirReplayStream<T>` /
  `SleipnirReplayStreams<TKey, T>` (`SleipnirCore.Events`; added after 1.9.1, see `CHANGELOG.md`):

```csharp
[SleipnirEvent("Progress", BufferCapacity = MaxHistory + 100)]   // buffer ≥ history, or the replay drops events
public IObservable<ProgressEvent> Progress(string runId, long afterSeq = 0) => _runs.Observe(runId, afterSeq);

// producer: _runs.GetOrCreate(runId).Publish(seq => new ProgressEvent(seq, step)); … .Complete();
```

The client keeps `lastSeq`, drops events with `seq <= lastSeq` and passes `lastSeq` when it
re-subscribes. Recipe: `EVENTS_REFERENCE.md` §15. Everything else about events: `EVENTS_REFERENCE.md`.

---

## 6. Batches and chaining (one roundtrip)

Several calls in one request, in parallel or serial. A call can expose a value from its result
(`Exposes("$.id", "newId")`, a JsonPath **relative to the result**, camelCase) and a later call
uses it as a parameter (`"@newId"`). The server orders dependent calls itself. Read
`DEPENDENCY_BINDING.md` before relying on object-shaped aliases (missing properties bind as
defaults unless `AliasBindingMode` is `Strict`/`Paranoid`).

---

## 7. Pitfalls

1. **Throwing for a domain error** → generic `500`. Return `SleipnirResults.*` instead.
2. **Overloads do not exist.** Dispatch is by `[SleipnirMethod]` name; two methods with the same
   name, or two controllers with the same name, fail at startup.
3. **REST status is in the body.** HTTP `200` does not mean success; check `code` / `isSuccess`.
4. **JsonPath is camelCase and result-relative:** `$.id`, not `$.Id` and not `$.data.id`.
5. **Types outside the API assembly are opaque** in clients — map them to DTOs in the API project.
6. **Contract drift fails the build** — regenerate with `SLEIPNIR_REGEN_GOLDEN=1`, commit, then
   regenerate clients. Do not switch the gate off to get a green build.
7. **Events:** `IObservable<T>` only; default buffer 100 with `DropOldest`; a history replay needs
   `BufferCapacity` ≥ history; ephemeral events are lost while disconnected.
8. **Batches share one `HttpContext`** — treat it as read-only in controller code. Custom
   `ISleipnirInterceptor`s run on single calls only, not on batch elements.
9. **Parameter names are case-sensitive** on the wire; generated clients get them right, hand-written
   JSON often does not.
10. **`sleipnir-gen --selfcheck` must use the same `--transport`** as the generation run.

---

## 8. Which reference for what

| Question | Read |
|----------|------|
| First steps, by hand | `GETTING_STARTED.md` |
| A full multi-tier app, chapter by chapter | `guide/README.md` |
| Every feature with examples | `README_DETAILS.md` |
| Errors, codes, categories, validation | `ERROR_CATALOG.md` |
| Contract file, drift gate, C# generator | `CODEGEN_ONBOARDING.md`, `CODEGEN_REFERENCE.md` |
| TS / JS / Python client generation | `CLIENT_GENERATION.md` |
| Breaking-change gate against a release | `CONTRACT_EVOLUTION.md` |
| Events, buffers, resume, history + live | `EVENTS_REFERENCE.md` |
| Transports, reconnect, client options | `TRANSPORT_REFERENCE.md` |
| `@alias` binding, casing, binding modes | `DEPENDENCY_BINDING.md` |
| Auth, rate limits, production settings | `SECURITY_GUIDE.md`, `BEST_PRACTICES.md` |
| Telemetry and tracing | `OBSERVABILITY_REFERENCE.md` |
| Wire format (only for a custom client) | `PROTOCOL.md` |
| What is stable vs. experimental | `STABILITY.md` |
| JSON-RPC 2.0 endpoint | `JSONRPC_COMPAT.md` |

All files are in the repository root of https://github.com/HolgerTheHuck/Sleipnir. This guide also
ships inside the `Sleipnir.Server` NuGet package (`docs/AGENT_GUIDE.md`) and the `sleipnir-client`
npm package (`AGENT_GUIDE.md`).
