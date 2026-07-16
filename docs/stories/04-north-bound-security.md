# Story 04 — North-bound Security

> **Trame was a trusted south-bound caller. This is it facing the open internet. Every
> request must be authenticated, authorized, rate-limited, and size-capped before a
> controller runs — and the hardening is opt-in, so the trusted-caller defaults don't break.**

## The problem

Stories 01–03 assumed the caller was one of *us*: a backend service, an internal tool, a trusted
client on the inside of the trust boundary. Trame's defaults reflect that — no authentication
required, unbounded batches, every controller reachable, the discovery endpoint open. That is
ergonomic and correct for south-bound.

North-bound is a different posture. The caller is now an **external, untrusted client** with
network access to the transport endpoints. It controls the request body, the JSON structure,
the `@alias` JsonPath, the batch size, the headers, the connection rate. It does not control the
server code or the `TrameOptions`. In this posture the south-bound defaults are a vulnerability
surface, not a convenience.

This story shows the hardened server — what flips, what stays, and what was *already* safe.

## The posture: default-deny, opt-out per method

The core change is one toggle: `RequireAuthentication`. When it is `true`, the invoker applies a
**default-deny** rule — every method requires an authenticated `HttpContext.User` *unless* it
explicitly opts out. The decision falls in the invoker (`TrameInvoker.CheckAuthorisation`), not in
the transport endpoint, which keeps the per-method opt-out intact.

| Method decorated with      | unauth | auth (no role) | auth (Admin) |
|----------------------------|--------|----------------|--------------|
| — (undecorated)            | **401** (deny) | 200 | 200 |
| `[TrameAuthorise]`         | 401    | 200            | 200          |
| `[TrameAuthorise(Role="Admin")]` | 401 | 401       | 200          |
| `[TrameAnonymous]`         | **200** (opt-out) | 200 | 200 |
| class `[TrameAuthorise]` + method undecorated | 401 | 200 | 200 |
| class `[TrameAuthorise]` + method `[TrameAnonymous]` | **200** (method wins) | 200 | 200 |

`[TrameAnonymous]` is the deliberate hole in default-deny: Health/Ping, the readiness probe,
the public handshake — these stay reachable without a token. Class-level `[TrameAuthorise]` sets a
default for every method on the controller; a method-level `[TrameAnonymous]` overrides it.

## Defense in depth — three layers, one boundary each

Auth is not one gate; it is three, and they protect different boundaries:

1. **The invoker gate (per request).** `CheckAuthorisation` runs in the serial auth pre-pass
   before any controller executes. This is where `[TrameAuthorise]`, the role check, and the
   default-deny rule live. On REST it is per-request, so `[TrameAnonymous]` works per-method.
2. **The transport gate (per connection).** A WebSocket/SignalR connection is established *before*
   any request runs, so a per-method decision in the invoker is too late — once the socket is open
   the client has the channel. The WS middleware therefore rejects the upgrade and the SignalR hub
   requires authorization **before** the connection exists. These gates have no per-method
   opt-out: the connection is the trust boundary.
3. **The discovery gate.** `/api/trame/discovery` and the JSON-RPC `trame.discover` capability
   expose the full contract — every controller, parameter type, example JSON. That is an attack-
   surface oracle. With `RequireAuthentication`, both are gated.

## What was already safe (structure, not gating)

Hardening adds gates; it does not have to add defenses that were already structural:

- **No code injection via Expression Trees.** The compiled delegate is built *only* from server-
  controlled `MethodInfo` (reflection over the registered controller assembly). Client input flows
  in as parameter *values*, never as code. Controller and method are chosen by *name* (a dictionary
  lookup on `"{Controller}_{Method}"`), not by path — no traversal, no late binding into arbitrary
  types. JSON-RPC translates `method` → `Controller.Method` and runs through the same lookup; an
  unknown name is a routing 404, never an invoke.
- **All transports route through `CheckAuthorisation`.** Batch, WebSocket, and JSON-RPC cannot
  bypass `[TrameAuthorise]` — the serial auth pre-pass covers every path before `ExecuteAuthorized`
  runs.
- **Body / message / cardinality caps.** REST bodies are capped at 1 MB, WS messages at 1 MB,
  array parameters at `MaxParameterArrayLength` (1000), streamed results at `MaxResultElementCount`
  (10000). Parameter binding uses `System.Text.Json` with server-fixed types — no
  client-controlled `TypeNameHandling`, no polymorphic-deserialization gadget vector.

## The north-bound knobs (all opt-in, all non-breaking)

Every hardening option defaults to the south-bound value — flipping them is a deployment choice,
not a behavior change for existing callers.

| `TrameOptions`            | North-bound | Default | Why the default is off |
|---------------------------|-------------|---------|-------------------------|
| `RequireAuthentication`   | **true**    | false   | trusted caller; default-deny would break it |
| `RateLimitPermitLimit`    | **20**      | 0       | no limit needed internally |
| `MaximumBatchSize`        | **16**      | 0       | unbounded is fine for trusted callers |
| `MaxDependencyPathLength` | **128**     | 256     | tighter for untrusted input |
| `AllowRecursiveDescent`   | **false**   | true    | `$..` is the costliest path; deny for untrusted |

`MaximumBatchSize` caps the fan-out DoS vector: a client sending N requests triggers N parallel
controller scopes, N parameter bindings, N delegate invocations. The cap is enforced early — REST
`/json/multi` returns 400, JSON-RPC returns `-32600`, WS sends a 400 error frame, and the invoker
has a backstop for direct in-process callers.

`MaxDependencyPathLength` and `AllowRecursiveDescent` bound the `@alias` JsonPath — which is
client-controlled. A very long path or a recursive-descent `$..` over a large result graph is the
costliest evaluation path. Both are validated *before* `JsonPath.Parse`, so a malicious path never
reaches the parser: the alias is left unset and the dependent gets a clean `400`, never a `500`
and never a CPU stall.

## The wire shape you must understand

Native Trame REST is **envelope-at-200**: `POST /api/trame/json` and `/json/multi` always return
HTTP 200 with the per-call result in the body, where the `code` field carries the real status. A
per-method auth failure is therefore **HTTP 200 with `"code":401` in the body**, not an HTTP 401.
This is deliberate — the batch path cannot be HTTP 401 just because *one* request in N failed, so
the single-call path matches for consistency.

The **framework-level gates** — discovery, the batch cap, the WebSocket upgrade — reject *before*
the invoker and therefore return real HTTP 401/400. Two status regimes, one consistent rule: the
framework gates you out at the HTTP layer; the invoker gates you out in the body. (`TrameClient`
users do not notice: `TrameClientBase` throws `TrameException` on a non-2xx body `code`
regardless of the HTTP status.)

## Try it

**Standalone solution — open in Visual Studio, press F5:**

```
stories/04-north-bound-security/Story04.sln
```

Boots the hardened server (port 5004) with `RequireAuthentication=true`, rate limiting, and a
batch cap. A demo auth middleware stands in for your real identity provider (JWT/Cookie/mTLS) —
Trame itself reads only `HttpContext.User`; it runs no identity-provider logic. The demo token
goes in `Authorization: Bearer …`, `X-Trame-Token: …`, or `?token=…`.

The browser lands in the DevUI at `/Trame`, and its discovery call returns **401** — *that is the
lesson*: `RequireAuthentication` gates the framework's own discovery endpoint. Open the DevUI Auth
panel, set the demo token `trame-demo`, and discovery loads. The curl matrix in the story README
walks the full auth posture (per-method 401/200 in the body, framework gates as real HTTP 401/400).

Full audit catalog, roadmap (the Medium/Low findings deliberately left as compensation guidance),
and the north-bound deployment checklist: **`../../SECURITY.md`**. Source:
`stories/04-north-bound-security/Program.cs`, `DemoAuthMiddleware.cs`, `Domain.cs`.