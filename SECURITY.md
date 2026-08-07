# Security Policy

## Supported Versions

| Version | Supported | Notes |
|---------|-----------|-------|
| 1.1.x   | ✅ Active | Current release — security fixes applied. |
| 1.0.x   | ⚠️ Maintenance | Critical fixes only; upgrade to 1.1.x recommended. |
| < 1.0   | ❌ Unsupported | Pre-release / internal. |

## Reporting a Vulnerability

**Do not open a public GitHub issue for security vulnerabilities.**

Instead, report vulnerabilities privately:

1. Use [GitHub's private vulnerability reporting](https://github.com/HolgerTheHuck/Trame/security/advisories/new) (preferred), or
2. Email the maintainer directly.

Please include:

- A clear description of the vulnerability and its impact.
- The affected transport (REST, WebSocket, SignalR, JSON-RPC).
- Reproduction steps or a proof-of-concept.
- If known, the audit finding code (F1–F12 from [`SECURITY_AUDIT.md`](SECURITY_AUDIT.md)).

**Response timeline:**

- **Acknowledgement:** within 48 hours.
- **Initial assessment:** within 7 days.
- **Fix or mitigation:** target 30 days (severity-dependent); a security advisory is published when the fix is released.

We follow [coordinated disclosure](https://security.github.com/web/best-practices/communicate-possible-security-issue): we'll work with you on a fix and a public advisory before disclosure.

## Security Posture

Trame is designed as a **code-first RPC framework** — your C# classes are the contract, and the framework dispatches calls to them. The security model is **defense in depth**, with the trust boundary at the transport layer:

### Authentication & Authorization

- **Trame does not run its own identity provider.** It reads `HttpContext.User` — configure your auth scheme (JWT bearer, cookies, mTLS via reverse proxy) *before* the Trame transport, so `HttpContext.User` is populated.
- **`RequireAuthentication`** (default `false` for south-bound, non-breaking): when `true`, every method requires an authenticated user unless it explicitly opts out with `[TrameAnonymous]`.
- **`[TrameAuthorise]`** — require authentication; with `Role = "Admin"`, require that role (`IsInRole`).
- **`[TrameAuthorise(Policy = "...")]`** (1.1.0) — evaluate an ASP.NET Core authorization policy via `IAuthorizationService`.
- **`[TrameAnonymous]`** — deliberate opt-out for health checks, public handshakes, etc.
- **403 vs 401** (1.1.0) — `ForbiddenAccessException` distinguishes "authenticated but not permitted" (403, `PermissionDenied`) from "not authenticated" (401, `Unauthenticated`).

See [`SECURITY_GUIDE.md`](SECURITY_GUIDE.md) for the full posture matrix and deployment checklist.

### Size & Cost Controls (DoS Surface)

- **`MaximumBatchSize`** — caps the number of requests in a batch (default 0 = unlimited; set > 0 for north-bound).
- **`MaxParameterArrayLength`** / **`MaxResultElementCount`** — cardinality caps for array parameters and results (default 1000 / 10000).
- **`MaxDependencyPathLength`** / **`AllowRecursiveDescent`** — limit client-controlled JsonPath evaluation (default 256 / `true`).
- **`RateLimitPermitLimit`** — fixed-window rate limiting on REST endpoints and SignalR hub (default 0 = off; set > 0 for north-bound).
- **Body caps** — REST 1 MB (`RequestSizeLimitAttribute`), WebSocket 1 MB message (`MaxMessageSize`).

### Transport Gates

- **WebSocket upgrade** — rejected with 401 before `AcceptWebSocketAsync` when `RequireAuthentication` and unauthenticated.
- **SignalR hub** — `.RequireAuthorization()` applied when `RequireAuthentication`.
- **Discovery** — `GET /api/trame/discovery` and `trame.discover` gated behind auth when `RequireAuthentication` (attack-surface oracle).

### Structurally Safe

- **No code injection** — `Expression.Compile` builds delegates from server-controlled `MethodInfo`; client input flows as parameter values, never as code.
- **No path traversal** — dispatch is by name (`{Controller}_{Method}` dictionary lookup), not by path.
- **No polymorphic deserialization gadget** — `System.Text.Json` with server-side types from the method signature; no client-controlled `TypeNameHandling`.
- **Batch / WS / JSON-RPC do not bypass `[TrameAuthorise]`** — all paths run through `ResolveAndAuthorizeAsync`.

## North-Bound Deployment Checklist

Before exposing Trame to untrusted clients:

1. **Configure authentication** (JWT/Cookie/mTLS) that populates `HttpContext.User`.
2. **Set `TrameOptions`**: `RequireAuthentication = true`, `RateLimitPermitLimit > 0`, `MaximumBatchSize > 0`.
3. **Set `ASPNETCORE_ENVIRONMENT = Production`** (no stack-trace leaks via `EnableDetailedErrors`).
4. **Do not ship the DevUI** (`MapTrameDeveloperUi`) to untrusted clients — omit it or put it behind auth.
5. **Set Kestrel limits** (`MaxConcurrentConnections`, `MaxRequestBodySize`).
6. **Put a reverse proxy in front** — TLS termination, connection-rate limiting (especially for WebSocket), header filtering.
7. **Discovery is behind auth** (automatic with `RequireAuthentication`).
8. **Smoke test**: unauthenticated call → rejected; authenticated → 200; `[TrameAnonymous]` method → 200 unauth; `[TrameAuthorise("admin")]` without role → 401; batch > `MaximumBatchSize` → 400.

Full audit (findings F1–F12, roadmap, compensation guidance): [`SECURITY_AUDIT.md`](SECURITY_AUDIT.md).
Operational guide: [`SECURITY_GUIDE.md`](SECURITY_GUIDE.md).