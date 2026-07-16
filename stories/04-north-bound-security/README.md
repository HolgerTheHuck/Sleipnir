# Story 04 — North-bound Security

> **Trame was south-bound (trusted caller). This is it going north-bound — untrusted external
> clients over REST/WebSocket/SignalR. `RequireAuthentication` is ON (default-deny), rate limits
> and batch caps are set, Discovery is behind auth. Every request is authenticated, authorized,
> rate-limited, and size-capped before a controller runs.**

The hardened server. It demonstrates the audit fixes from **`SECURITY.md`** at the repo root
(F1–F12). A demo auth middleware stands in for your real identity provider (JWT/Cookie/mTLS) —
Trame itself reads only `HttpContext.User`; it runs no identity-provider logic.

## Run it (F5 → DevUI behind auth)

1. Open **`Story04.sln`** in Visual Studio (or `dotnet build && dotnet run --project Story04.csproj`).
2. Press **F5**. The browser opens at **`http://localhost:5004/Trame`** — the DevUI loads its
   shell, but its discovery call returns **401**. *That is the lesson:* `RequireAuthentication`
   gates the framework's own discovery endpoint.
3. In the DevUI, open the **Auth** panel (top-right), enter the demo token **`trame-demo`****, and
   set it. Discovery now loads — the contract (`Health`, `Echo`, `SecuredAll`) appears.

## The curl matrix

Run these against the running server (`http://localhost:5004`). The demo middleware accepts the
token as `Authorization: Bearer …`, `X-Trame-Token: …`, or `?token=…`.

> **Wire shape — read this first.** Native Trame REST is **envelope-at-200** (like the JSON-RPC
> compat layer): `POST /api/trame/json` and `/json/multi` always return **HTTP 200** with the
> per-call result in the body, where the Trame `code` field carries the real status. A per-method
> auth failure is therefore **HTTP 200 with `"code":401` in the body**, not an HTTP 401. The
> **framework-level gates** below — discovery, the batch-cap, the WebSocket upgrade — reject
> *before* the invoker and therefore return real HTTP 401/400. (`TrameClient` users don't notice:
> `TrameClientBase` throws `TrameException` on a non-2xx body `code` regardless of the HTTP status.)

### Per-method auth (F1.1 / F1.2) — check the body `code`, not the HTTP status

```bash
# 1) Undecorated method, unauthenticated → body code 401 (Default-Deny)
curl -s -X POST http://localhost:5004/api/trame/json -H 'Content-Type: application/json' \
  -d '{"controller":"Echo","method":"PublicEcho","params":[{"parameterName":"text","data":"hi"}],"id":"q1"}'
# → {"code":401,"data":null,"error":{"code":401,"message":"Unauthorized."}, ...}

# 2) Same method, with demo token → body code 200
curl -s -X POST http://localhost:5004/api/trame/json -H 'Content-Type: application/json' \
  -H 'X-Trame-Token: trame-demo' \
  -d '{"controller":"Echo","method":"PublicEcho","params":[{"parameterName":"text","data":"hi"}],"id":"q1"}'
# → {"code":200,"data":{"text":"hi","caller":"public"}, ...}

# 3) [TrameAnonymous] Health.Ping → body code 200 WITHOUT a token (opt-out works)
curl -s -X POST http://localhost:5004/api/trame/json -H 'Content-Type: application/json' \
  -d '{"controller":"Health","method":"Ping","params":[],"id":"q1"}'
# → {"code":200,"data":"pong", ...}

# 4) [TrameAuthorise] SecretEcho with token → body code 200 (authenticated, no role required)
curl -s -X POST http://localhost:5004/api/trame/json -H 'Content-Type: application/json' \
  -H 'X-Trame-Token: trame-demo' \
  -d '{"controller":"Echo","method":"SecretEcho","params":[{"parameterName":"text","data":"hi"}],"id":"q1"}'
# → {"code":200,"data":{"text":"hi","caller":"secret"}, ...}

# 5) [TrameAuthorise(Role="Admin")] AdminEcho with trame-demo (no Admin role) → body code 401
curl -s -X POST http://localhost:5004/api/trame/json -H 'Content-Type: application/json' \
  -H 'X-Trame-Token: trame-demo' \
  -d '{"controller":"Echo","method":"AdminEcho","params":[{"parameterName":"text","data":"hi"}],"id":"q1"}'
# → {"code":401, ...}

# 6) AdminEcho with trame-admin (Admin role) → body code 200
curl -s -X POST http://localhost:5004/api/trame/json -H 'Content-Type: application/json' \
  -H 'X-Trame-Token: trame-admin' \
  -d '{"controller":"Echo","method":"AdminEcho","params":[{"parameterName":"text","data":"hi"}],"id":"q1"}'
# → {"code":200,"data":{"text":"hi","caller":"admin"}, ...}

# 7) Class-level [TrameAuthorise], method-level [TrameAnonymous] wins → body code 200 unauth
curl -s -X POST http://localhost:5004/api/trame/json -H 'Content-Type: application/json' \
  -d '{"controller":"SecuredAll","method":"Opened","params":[],"id":"q1"}'
# → {"code":200,"data":"anyone", ...}

# 8) Class-level [TrameAuthorise] Locked, unauth → body code 401
curl -s -X POST http://localhost:5004/api/trame/json -H 'Content-Type: application/json' \
  -d '{"controller":"SecuredAll","method":"Locked","params":[],"id":"q1"}'
# → {"code":401, ...}
```

### Framework-level gates (F7.3 / F4.1 / F9.1) — real HTTP status

```bash
# 9) Discovery unauth → HTTP 401 (the framework's own endpoint is gated, F7.3)
curl -s -o /dev/null -w "%{http_code}\n" http://localhost:5004/api/trame/discovery
# → 401
curl -s -o /dev/null -w "%{http_code}\n" -H 'X-Trame-Token: trame-demo' http://localhost:5004/api/trame/discovery
# → 200

# 10) 17 requests in one batch, MaximumBatchSize=16 → HTTP 400 (early, before fan-out, F4.1)
curl -s -o /dev/null -w "%{http_code}\n" -X POST http://localhost:5004/api/trame/json/multi \
  -H 'Content-Type: application/json' -H 'X-Trame-Token: trame-demo' \
  -d '{"mode":0,"requests":['$(for i in $(seq 1 17); do echo -n "{\"controller\":\"Health\",\"method\":\"Ping\",\"params\":[],\"id\":\"p$i\"},"; done | sed 's/,$//')']}'
# → 400

# 11) Unauthenticated WebSocket upgrade → rejected before the socket exists (HTTP 401, F9.1)
curl -s -o /dev/null -w "%{http_code}\n" http://localhost:5004/tramews \
  -H 'Connection: Upgrade' -H 'Upgrade: websocket' \
  -H 'Sec-WebSocket-Version: 13' -H 'Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ=='
# → 401

# With token → 101 Switching Protocols (the upgrade proceeds)
curl -s -o /dev/null -w "%{http_code}\n" 'http://localhost:5004/tramews?token=trame-demo' \
  -H 'Connection: Upgrade' -H 'Upgrade: websocket' \
  -H 'Sec-WebSocket-Version: 13' -H 'Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ=='
# → 101
```

## What is on, and why each default is off

| `TrameOptions`            | This story | Default | Why default is off (south-bound) |
|---------------------------|-----------|---------|----------------------------------|
| `RequireAuthentication`   | **true**  | false   | trusted caller; default-deny would break it |
| `RateLimitPermitLimit`   | **20**    | 0       | no limit needed internally        |
| `MaximumBatchSize`       | **16**    | 0       | unbounded is fine for trusted callers |
| `MaxDependencyPathLength`| **128**   | 256     | tighter for untrusted input       |
| `AllowRecursiveDescent`  | **false** | true    | `$..` is the costliest path; deny for untrusted |

The hardening is **opt-in and non-breaking** — south-bound deployments keep their defaults and
their behavior. North-bound is a deployment choice, surfaced in `SECURITY.md`'s checklist.

## What is structurally safe (not just gated)

- **No code injection.** The compiled delegate is built only from server-controlled `MethodInfo`;
  client input is parameter *values*, never code. Controller/method is a name → dictionary lookup,
  not a path → no traversal, no late binding into arbitrary types.
- **All transports route through `CheckAuthorisation`.** Batch/WS/JSON-RPC cannot bypass
  `[TrameAuthorise]` — the serial auth pre-pass covers every path.
- **Body/message caps.** REST 1 MB request body; WS 1 MB per message. Cardinality caps
  (`MaxParameterArrayLength`, `MaxResultElementCount`) bound arrays and streamed results.

Full catalog, roadmap, and deployment checklist: **`../../SECURITY.md`**.

## Files

- `Program.cs` — hardened `TrameOptions` + demo auth middleware before the transports.
- `DemoAuthMiddleware.cs` — stands in for your real identity provider.
- `Domain.cs` — the auth-posture-matrix controllers (`[TrameAnonymous]`, `[TrameAuthorise]`,
  `[TrameAuthorise(Role="Admin")]`, class-level `[TrameAuthorise]`).