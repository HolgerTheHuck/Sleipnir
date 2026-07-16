# Trame Roadmap

> Post-v1, forward-looking. This file lists features that deliberately did *not* make the first
> public release — either because they would touch the v1 contract, or because they are optional
> and must not change the code-first default model. As of: 2026-07-08.
>
> **Shipped with v1:** an isomorphic JS/TypeScript client (REST + WebSocket) at
> [`clients/ts/`](clients/ts/) (`npm i trame-client`). SignalR for JS/TS and
> discovery → typed client codegen remain open (see "Later").
>
> **In progress — client stub generators:** typed client stubs (TS/JS/C#/Python) generated from
> runtime discovery, plus the v1.1 source-generator endgame. Tracked in
> [`CLIENT_GENERATION.md`](CLIENT_GENERATION.md) (Increment 1: TS + JS + `trame-gen` CLI).

---

## Motivation: what batching + dependency resolution deliver in practice

Trame's core promise is not "a pretty client API" but **eliminating N+1 and roundtrip cost without
the server building special-purpose methods.** An observed example from a real application:

> A page that originally needed **over 5 seconds** to load dropped to **around 100 ms** with
> targeted Trame batching — **with no server-side change**, i.e. no extra method built just for
> this one case. The customer list, prefetching the first two, and the order lines of the first
> customer run in a single roundtrip, as parallel as the dependencies allow.

That is the potential the architecture carries: the client orchestrates, the server resolves and
parallelizes. The features documented below exist to make this model *safer* at scale — not to
replace it.

---

## v1.1 — Versioning & build-time contract

### Problem

Versioning is the one problem that **catches up with every RPC generation** (CORBA → COM → SOAP →
gRPC). And the one measure that reliably works is **build-time stub generation**: interface drift
that fails at compile time instead of only in production traffic. That was `wsdl.exe`/`svcutil`
back then, protobuf today — and it is missing from Trame so far.

v1 solves versioning purely as a **convention** (`[TrameController("Customer.v1")]`, dotted
namespace, see README *Known Limitations*). This allows v1/v2 coexistence but offers no build-time
guarantee: a client compiled against an old shape notices a server-side interface break only at
runtime.

### Goal

An **optional, opt-in** second model alongside the code-first default:

- **Default stays:** runtime discovery, no code generation, no IDL — as in v1.
- **Opt-in:** a source generator produces typed client stubs + version constants from a committed
  contract snapshot. Interface break → build fails.

### Architecture (two pieces — like wsdl.exe, but .NET-native)

A Roslyn source generator cannot fetch `/api/trame/discovery` at build time (no network, no
running server). Hence two pieces:

1. **Contract export (server side).** An MSBuild target or CLI tool produces the discovery JSON —
   the same one `/api/trame/discovery` already returns — and writes it to a file committed to the
   repo (e.g. `contract.trame.json`).
2. **Source generator (client side).** An `IIncrementalGenerator` reads the committed file via
   `AdditionalFiles` and produces, per controller, a partial class `CustomerClient` with strongly
   typed methods (`Task<Customer?> GetById(int id, CancellationToken ct)`) plus a
   `TrameControllerVersion` constant. Internally the stubs still build on
   `TrameCall`/`ITrameClient` — they are a typed wrapper, not a second protocol.
3. **Drift check (mandatory component).** An MSBuild target on the server project regenerates the
   discovery at build time and **fails the build** if the committed JSON differs. *Without this
   check it is exactly the wsdl trap:* someone changes the server, forgets to regenerate, and the
   client build is still green. The drift check is the part that would have made `wsdl.exe` better
   back then.

> **Pulled forward, and Node-free.** This was originally the v1.1 endgame; it is now the next C#
> increment (A-prio), because the discovery JSON has been elevated to the standard — if the JSON is
> the contract, C# needs a producer of it in the .NET edge, not the Node edge. Node is not a
> build-time dependency .NET shops can be assumed to have, so the generator **ports the C# emission
> logic to C#** rather than subprocessing the TS `--lang cs` emitter (subprocessing would put Node
> back in the .NET build). The TS `--lang cs` emitter stays for the DevUI C# tab and the CI
> drift-check. Two C# emitters means a **parity gate**: the Roslyn generator and the TS emitter run
> on the shared golden `contract.trame.json` and their C# output is asserted equivalent (same
> pattern as `DiscoveryContractTests`, one level down). See
> [`CLIENT_GENERATION.md`](CLIENT_GENERATION.md) → *Build-chain fit*.

### Versioning model: routing key, not compatibility gate

- **Routing key (recommended):** `Customer.v1` and `Customer.v2` are two entries in the controller
  dictionary, both registered; the request selects via the `controller` field. Builds on what
  exists, no new wire field needed. The generator burns the version as a constant into the stub;
  the server can hard-fail with `409`/`400` "Version mismatch" on mismatch — explicit instead of
  silent.
- **Compatibility gate (alternative):** a typed `version` field in `TrameRequest`, one controller,
  server rejects non-matching versions. More metadata, larger protocol change. Choose only if the
  routing key is not enough.

### Deliberately accepted trade-offs

- **Runtime type safety precedes compile-time safety where generation is not used.** Anyone using
  the default model still has no build-time guarantee on chained values (`@alias`) — a typo fails
  only at runtime as a `400`. This is a deliberate sacrifice for the flexibility of the code-first
  model; the generator is the counterpart for teams that need build-time safety.
- **A second sales model.** Trame's pitch so far: "code-first, no IDL, no code generation." That
  stays true as the default. The generator must be positioned clearly as *opt-in*, not as a
  replacement — otherwise it contradicts the core position.
- **The contract snapshot is drift-prone by nature.** Hence the drift check as a mandatory
  component, not a nice-to-have.

### Open design questions (decide at implementation time)

- Concrete form of the contract file (plain discovery JSON vs. a dedicated contract schema file
  with version metadata).
- Whether the generator produces only stubs or also request/response DTOs (`[TrameDataContract]`)
  as C# types — and how it handles types that already exist client-side (name collisions).
- Whether `version` is a wire field (gate) or only a generator constant (routing key).
- NuGet packaging: a separate generator analyzer vs. part of `Trame.Client`.

### Relationship to v1

v1 stays unchanged. The `Customer.v1` convention from *Known Limitations* is the v1 story; this
feature turns it into a build-time guarantee. No protocol change to the existing contract (except
in the optional gate path), no incompatibility for existing clients.

### Proof of concept: `spikes/LinqProvider/` (as of 2026-07-08)

An Expression-Tree-typed RPC proxy spike validates the feasibility of the typed client model
end-to-end against the sample app:

- `client.Build((ICustomerService c) => c.GetCustomerById(id))` → correct `TrameRequest` from the
  `MethodCallExpression` (controller/method from contract attributes, parameters from the
  signature).
- **Type-safe dependency wiring** via `Dep<T>` marker + `Arg<T>` wrapper with implicit
  conversions: a `Dep<string>` at an `Arg<int>` position is a compile error, not a runtime 500.
- `Expose(x => x.Name)` → result-relative JsonPath `$.Name`.
- `ContractGenerator` produces C# contracts from `discovery.json` (model "c").

**Verdict: technically feasible, but not built out in v1.** The spike has indirectly already
justified its existence — the typed wiring is what made the three server bugs in dependency
chaining (the `$.data` convention, type-faithful `@alias` substitution, the non-resolving
topological batch path) visible in the first place, because it executes a numeric chain
end-to-end. Assessed honestly, though, the ROI is limited: the untyped `TrameCall` builder works,
method/parameter errors surface immediately at runtime during development anyway, and the biggest
gain (typed dependency wiring) covers only the narrowest case (batch chains). The codegen +
`Arg<T>` tax is justified only as an opt-in.

**Points to resolve from the spike for a v1.x:**

- **No `IQueryable` needed.** The spike is a typed proxy, not a query provider — the IQueryable
  monad problem does not apply. When designing, take care not to inherit unnecessary complexity
  from the LINQ template.
- **Alias lexicon is restrictive server-side.** `DependencyGraphBuilder.ExtractAliases` breaks on
  `.`/`#` and similar (only `[A-Za-z0-9_]`). The spike sanitizes generated aliases client-side;
  cleaner would be to let the server extract more freely — evaluate as a server change before the
  generator release.
- **`Arg<T>` only where needed.** The spike wraps every parameter in `Arg<T>`; a product would do
  that only at dependency-receiving positions, to avoid overloading the API.
- **Constant folding + caching** of argument evaluation (`Expression.Lambda(arg).Compile().DynamicInvoke()`
  per call is too expensive for hot loops).
- **Pass-through** of cancellation, `IAsyncEnumerable`, `byte[]` — the untyped `ITrameClient` can
  already do this; the typed layer must mirror it.

Details and the full decision in the spike at
[`spikes/LinqProvider/README.md`](spikes/LinqProvider/README.md). The spike is deliberately *not*
part of `Trame.sln` — it runs isolated:
`dotnet test spikes/LinqProvider/Trame.Spike.LinqProvider.csproj`.

---

## Binary transfer (v1.x+)

### Status quo (v1)

`byte[]` parameters (`TrameRequest.binaryData`) and `byte[]` returns (`TrameResponse.content`)
are carried out of band from the JSON `data` field — they do not compete with structured arguments
and are not double-encoded in `data`. The wire encoding is transport-dependent:

- **REST and WebSocket (JSON):** base64-in-JSON (~33% overhead), bounded by the message-size caps
  (REST 1 MB body, WebSocket 1 MB/message, hardcoded). WebSocket deliberately accepts text frames
  only — native binary frames are not accepted.
- **SignalR (MessagePack):** native `bin` encoding, no base64.

Open gaps deliberately unsolved in v1 (see README *Known Limitations* — Binary):

1. **C# fluent builder without `WithBinary`** — binary upload from C# only via
   `request.BinaryData = …`. Helper missing (TS has `withBinary`).
2. **First-match-only for `byte[]` parameters** — a method with several `byte[]` parameters gets
   the payload only in the first.
3. **No streaming** — `byte[]` responses buffer in `content`; `TrameResponse.ContentStream` is
   declared on the model but not wired up by any transport.
4. **No multipart/chunking** for large REST uploads (only base64-in-JSON, 1 MB cap).

### Goal (v1.x+)

Build out binary transfer as an equal, non-base64-laden path — without giving up the JSON text
wire for REST/WebSocket (language neutrality remains the criterion).

- **WebSocket native binary frames** optionally accepted (auto-detection: text frame = JSON
  request as today; binary frame = raw payload for a `byte[]` method plus a thin header for
  controller/method/id). The base64 path stays compatible.
- **Wire up `ContentStream`** — stream large `byte[]` responses instead of buffering (REST:
  chunked transfer; WebSocket/SignalR: sequential frames).
- **Add `WithBinary` to the C# builder**, symmetric with the TS client.
- **Name binding for `byte[]` parameters** instead of first-match-only, as soon as a second
  binary field per request is needed — or multiple named binary slots in `TrameRequest`.
- **Configurable message-size caps** via `TrameOptions` (WebSocket is hardcoded to 1 MB today).

### Deliberately accepted trade-off

Binary is in v1 worth a **second-class-citizen warning**, not a selling point: anyone who needs
large or frequent binary streams is better off running them over a plain REST or WebSocket
endpoint alongside Trame today. The RPC model carries the command and chaining load, not bulk
transfer. The v1.x+ plan raises binary to that level without breaking the JSON wire.

### Open design questions (decide at implementation time)

- WebSocket binary-frame header format (minimal JSON prefix vs. a dedicated frame sub-type) and
  correlation with `id`.
- Whether `ContentStream` runs per transport or generically through a transport adapter.
- Whether multiple `byte[]` parameters are solved via multiple named slots in `TrameRequest` or
  via a binary multipart frame.

---

## Later (v1.x+, unsorted)

- **SignalR client for JS/TS.** The `clients/ts/` client (v1) covers REST + WebSocket. A SignalR
  transport (`/tramehub` hub, MessagePack) for browser/Node follows in v1.1 — deliberately not in
  v1, to keep the heavy `@microsoft/signalr` dependency + MessagePack out of the default build.
  REST + WebSocket already cover browser RPC.
- **Discovery → typed client codegen.** `/api/trame/discovery` delivers full type metadata.
  Multi-language stub generators (TS/JS/C#/Python) from a single TS core, with dependency chaining
  as a first-class compile-checked surface, are in progress — see
  [`CLIENT_GENERATION.md`](CLIENT_GENERATION.md). This is the JS/Python equivalent of the .NET
  source generator described above and shares its drift-check requirement.
- **Policy-based authorization** for `[TrameAuthorise]` via `IAuthorizationHandler`, so that
  `403` (authenticated but not permitted) can be distinguished from `401` (not authenticated) —
  currently a roadmap item in RELEASE-PLAN Phase 3.1.
- **Direct/fluent handler registration** without `[TrameController]`/`[TrameMethod]` — for
  scenarios that do not want attribute-scan registration.
- **Input validation** of parameters (DataAnnotations / FluentValidation) in the interceptor
  pipeline.
- **True REST streams** instead of materialization to a JSON array (currently a limitation;
  WebSocket/SignalR offer streaming semantics).