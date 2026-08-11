# Sleipnir — Security Guide

**Sleipnir is built to be exposed to untrusted clients over the public transports (REST, WebSocket,
SignalR). This guide shows how to configure it for a north-bound deployment — every request
authenticated, authorized, rate-limited, and size-capped before a controller runs.**

Sleipnir itself runs **no identity-provider logic**. It reads `HttpContext.User`, which your
authentication scheme (JWT, cookies, mTLS via reverse proxy, …) populates *before* the Sleipnir
transport runs. Sleipnir's job is to enforce, on top of that identity: who may call which method,
how fast, how big, and what they may learn about the contract.

> For the full feature tour see [README.md](README.md); wire format [PROTOCOL.md](PROTOCOL.md);
> step-by-step setup [GETTING_STARTED.md](GETTING_STARTED.md); broader engineering guidance
> [BEST_PRACTICES.md](BEST_PRACTICES.md).

---

## 1. The auth posture: default-deny, opt-out per method

The central switch is `SleipnirOptions.RequireAuthentication`. When `true`, the invoker applies a
**default-deny** rule: every method requires an authenticated `HttpContext.User` unless it
explicitly opts out. The decision is made **in the invoker** (`SleipnirInvoker.CheckAuthorisation`),
not in the transport endpoint — this is what keeps a per-method opt-out working.

| Method decorated with                | unauth          | auth (no role) | auth (Admin) |
|--------------------------------------|-----------------|----------------|--------------|
| — (undecorated)                      | **401** (deny)  | 200            | 200          |
| `[SleipnirAuthorise]`                   | 401             | 200            | 200          |
| `[SleipnirAuthorise(Role = "Admin")]`   | 401             | 401            | 200          |
| `[SleipnirAnonymous]`                   | **200** (opt-out) | 200          | 200          |
| class `[SleipnirAuthorise]` + undecorated method | 401      | 200            | 200          |
| class `[SleipnirAuthorise]` + method `[SleipnirAnonymous]` | **200** (method wins) | 200 | 200 |

- **`[SleipnirAuthorise]`** — require authentication; with `Role = "Admin"`, require that role
  (`IsInRole`). Applies to a method or to a whole controller (class-level is the default for every
  method on the class; a method-level attribute wins).
- **`[SleipnirAnonymous]`** — the deliberate hole in default-deny. Use it for health/readiness
  probes, the public handshake, anything that must answer without a token. Method-level only.

> **Batch behavior.** Authorization is checked **per request** in a batch — a `401` on one request
> does not abort the others (JSON-RPC-conformant). The check runs in a **serial pre-pass** before
> the parallel fan-out, so parallel execution never touches the shared, non-thread-safe
> `HttpContext`.

### The minimal north-bound options

```csharp
builder.Services.AddSleipnir(new SleipnirOptions
{
    RequireAuthentication  = true,   // default-deny on
    RateLimitPermitLimit   = 20,     // 0 = off; pick a number for your load
    MaximumBatchSize       = 16,     // 0 = unbounded; cap the fan-out
    MaxDependencyPathLength = 128,   // default 256; tighter for untrusted input
    AllowRecursiveDescent  = false,  // default true; $.. is the costliest path
    EnableDetailedErrors  = builder.Environment.IsDevelopment(),
});
```

Every hardening option defaults to **off / permissive** so existing deployments never break.
North-bound is a deployment choice you make explicitly.

---

## 2. Defense in depth — three layers, one boundary each

Authentication is not one gate; it is three, and each protects a different boundary.

1. **The invoker gate (per request).** `CheckAuthorisation` runs in the serial auth pre-pass,
   before any controller executes. This is where `[SleipnirAuthorise]`, the role check, and the
   default-deny rule live. On REST it is per-request, which is why `[SleipnirAnonymous]` works
   per-method.
2. **The transport gate (per connection).** A WebSocket / SignalR connection is established
   *before* any request runs, so a per-method decision in the invoker is too late — once the
   socket is open, the client has the channel. The WebSocket middleware therefore rejects the
   upgrade and the SignalR hub requires authorization **before** the connection exists. These
   gates have **no per-method opt-out**: the connection is the trust boundary. Authenticate via
   the upgrade (token in the query string, subprotocol, or cookies) and let your scheme populate
   `HttpContext.User`.
3. **The discovery gate.** `/api/sleipnir/discovery` and the JSON-RPC `sleipnir.discover` capability
   expose the full contract — every controller, parameter type, example JSON. That is an
   attack-surface oracle for an untrusted caller. With `RequireAuthentication`, both are gated;
   `sleipnir.capabilities` (a static manifest with no type introspection) stays public.

---

## 3. Size and cost controls (DoS surface)

Untrusted input controls the request body, the JSON structure, the `@alias` JsonPath, the batch
size, and the connection rate. Sleipnir bounds each:

| Control                    | Default | What it bounds |
|----------------------------|---------|----------------|
| `MaximumBatchSize`         | 0 (off) | Number of requests in one batch. Enforced early: REST `/json/multi` → 400, JSON-RPC → `-32600`, WS multi → 400 error frame, invoker backstop throws. |
| `MaxDependencyPathLength`  | 256     | Length of a client-controlled `@alias` JsonPath. Validated **before** `JsonPath.Parse` — a malicious path never reaches the parser; the alias is left unset and the dependent gets a clean `400`, never a `500` and never a CPU stall. |
| `AllowRecursiveDescent`    | true    | Permits `$..` paths. Set `false` for untrusted input — recursive descent is the costliest evaluation path over a large result graph. |
| `RateLimitPermitLimit`     | 0 (off) | Fixed-window rate limit. REST endpoints and the SignalR hub get `RequireRateLimiting("sleipnir")` when > 0. (WebSocket connection-rate limiting is the reverse proxy's job — see §5.) |
| `MaxParameterArrayLength`  | 1000    | Array / collection parameter size (the injected `@alias` array). |
| `MaxResultElementCount`    | 10000   | Streamed / produced result element count (`IAsyncEnumerable<T>` materialization, fan-out source). |
| REST request body          | 1 MB    | Hard cap on the REST route group. |
| WS message                 | 1 MB    | Hard cap per WebSocket message in the middleware. |

Pick finite values for `MaximumBatchSize`, `RateLimitPermitLimit`, and (for untrusted input)
`MaxDependencyPathLength` / `AllowRecursiveDescent`. The defaults are permissive — that is a
non-breaking choice, not a secure one.

---

## 4. What is structurally safe (not just gated)

Hardening adds gates; several defenses are already structural and need no configuration:

- **No code injection.** Each method is compiled once at registration into an Expression Tree
  delegate built **only** from server-controlled `MethodInfo` (reflection over the registered
  controller assembly). Client input flows in as parameter **values**, never as code. There is no
  `Compile`/`Emit` path that turns client strings into IL. Controller and method are chosen by
  **name** — a dictionary lookup on `"{Controller}_{Method}"` — not by path, so there is no
  traversal and no late binding into arbitrary types.
- **JSON-RPC reaches only registered controllers.** The compat adapter translates `method` →
  `Controller.Method` (split at the last dot) and runs through the same dictionary lookup. An
  unknown name is a routing `404` → `-32601`, never an invoke.
- **All transports route through authorization.** Batch, WebSocket, and JSON-RPC cannot bypass
  `[SleipnirAuthorise]` — the serial auth pre-pass covers every path before `ExecuteAuthorized` runs.
- **User interceptors do not run on batch elements (1.1.x limitation).** `ISleipnirInterceptor`
  instances registered via `SleipnirOptions.Interceptors` (and all `ISleipnirBatchInterceptor`
  instances) currently run **only on the single-call path** (`InvokeDi(SleipnirRequest)`) — they
  do not run on the per-request elements of a batch (`/json/multi`, WebSocket multi,
  JSON-RPC batch), and there is no batch-level pipeline consumer yet. Authorization is **not**
  affected (it is enforced structurally by the pre-pass, not by user interceptors), but any
  *custom* logic you put behind `ISleipnirInterceptor` — tenant isolation, request validation,
  rate limiting, audit logging — is silently bypassed on every batch call. Do not build a
  security control on the interceptor seam in 1.1.x; use `[SleipnirAuthorise]`/policies and the
  framework-level gates instead. Routing the batch path through the interceptor pipeline is
  tracked for 1.2 (`ROADMAP.md` R7). The single-call-only scope is logged as a warning once at
  startup when `options.Interceptors` is non-empty.
- **Parameter binding is `System.Text.Json` with server-fixed types.** Types come from the method
  signature, never from the client. Sleipnir does not use `TypeNameHandling`, so the known
  polymorphic-deserialization gadget vectors do not apply.
- **`byte[]` parameters arrive as raw bytes** from `SleipnirRequest.BinaryData`, not as a JSON string
  — no Base64 parse failure vector in the binding path.

---

## 5. The wire shape you must understand

Native Sleipnir REST is **envelope-at-200**: `POST /api/sleipnir/json` and `/json/multi` always return
**HTTP 200** with the per-call result in the body, where the `code` field carries the real status.
A per-method auth failure is therefore **HTTP 200 with `"code":401` in the body**, not an HTTP 401.
This is deliberate — the batch path cannot be HTTP 401 just because *one* request in N failed, so
the single-call path matches it for consistency.

The **framework-level gates** — discovery, the batch cap, the WebSocket upgrade — reject *before*
the invoker and therefore return **real HTTP 401/400**. Two status regimes, one consistent rule:
the framework gates you out at the HTTP layer; the invoker gates you out in the body.

`SleipnirClient` users do not notice the split: `SleipnirClientBase` throws `SleipnirException` on a non-2xx
body `code` regardless of the HTTP status.

---

## 6. Deployment checklist

Before exposing Sleipnir to untrusted clients:

1. **Configure an auth scheme** (JWT / cookies / mTLS via reverse proxy) that populates
   `HttpContext.User` **before** the Sleipnir transport runs. Sleipnir does not provide one.
2. **Set the north-bound options:** `RequireAuthentication = true`, `RateLimitPermitLimit > 0`,
   `MaximumBatchSize > 0`. For untrusted `@alias` input, tighten `MaxDependencyPathLength` and
   set `AllowRecursiveDescent = false`.
3. **Run in Production** (`ASPNETCORE_ENVIRONMENT = Production`). `EnableDetailedErrors` (stack
   traces in `error.details`) is Development-bound; in Production, unexpected failures are a
   generic `500` with no message leak. Business errors you return via `SleipnirResults.*` are *not*
   gated by this — their messages reach the client by design, so keep them free of sensitive
   context.
4. **Do not expose the Developer UI publicly.** `MapSleipnir()` always maps the Developer UI at
   `/Sleipnir`, so the only way to omit it is to stop using `MapSleipnir()` and wire the production
   endpoints manually:
   ```csharp
   app.UseSleipnirTransports();
   app.MapSleipnirEndpoints("/api/sleipnir");   // REST only
   // map the SignalR hub yourself if you need it
   ```
   If you keep `MapSleipnir()`, put `/Sleipnir` behind auth at the reverse proxy or middleware layer.
   The DevUI is a dev tool, not a production surface.
5. **Set Kestrel limits** — `MaxConcurrentConnections`, `MaxConcurrentUpgradedConnections`,
   `MaxRequestBodySize`. Sleipnir's framework caps (1 MB REST body, 1 MB WS message) sit on top of
   these; the global limits are the host's responsibility:
   ```csharp
   builder.WebHost.ConfigureKestrel(k =>
   {
       k.Limits.MaxConcurrentConnections = 200;
       k.Limits.MaxConcurrentUpgradedConnections = 100;
       k.Limits.MaxRequestBodySize = 2_000_000; // 2 MB
   });
   ```
6. **Put a reverse proxy in front** — TLS termination, header filtering, and **connection-rate
   limiting for WebSocket** (the WS transport is branch middleware, so per-connection rate
   limiting is the proxy's job; e.g. nginx `limit_conn`).
7. **Configure CORS** if browser clients call you cross-origin. Sleipnir does not set a CORS policy;
   add a named policy with `app.UseCors("…")` and scope it tightly (avoid `*`).
8. **Smoke-test the posture:**
   - unauth call to an undecorated method → body `code` 401 (HTTP 200);
   - `[SleipnirAnonymous]` method → 200 without a token;
   - `[SleipnirAuthorise(Role="Admin")]` without the role → 401;
   - `GET /api/sleipnir/discovery` unauth → HTTP 401, with token → 200;
   - batch with more than `MaximumBatchSize` requests → HTTP 400;
   - WebSocket upgrade without credentials → HTTP 401.

---

## 7. Reporting a vulnerability

Report security vulnerabilities **privately** to the maintainer, not as a public issue. Include
reproduction steps, the affected transport, and the impact. Do not include exploit payloads that
could harm deployed instances in a public channel.