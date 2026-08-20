# Chapter 8 — Auth: JWT Bearer, three tiers, 401 vs 403

> **Goal:** take the Market demo protected. `Account.Login` issues a signed JWT; ASP.NET's
> JwtBearer middleware validates it on the way back in and populates `HttpContext.User`;
> `[SleipnirAuthorise]` enforces per-method rules on top. The 3-tier app becomes explicit:
> the **API** holds the auth surface, the **Blazor admin** keeps an *admin* bearer server-side,
> the **Svelte portal** logs in a *customer* and pins **REST + SSE** for authed calls —
> because a browser WebSocket handshake can't set `Authorization`. "REST best friends" returns.

Chapters 1–6 are anonymous: anyone can call `Market.GetQuote` / `Search` / `GetQuotes`.
Chapter 8 gates a new `Portfolio` behind `[SleipnirAuthorise]` and adds an `Account`
controller that issues JWTs. Two roles — `Customer` (the portal) and `Admin` (the
Pflege-Backend) — and a method-level `[SleipnirAuthorise(Role = "Admin")]` that produces the
one distinction every auth tutorial hand-waves: **401 = unauthenticated, 403 = authenticated
but not allowed**.

## The one ordering rule that matters

Sleipnir reads **only `HttpContext.User`** — it runs no identity-provider logic of its own
(see `stories/04-north-bound-security`). ASP.NET's auth middleware is what populates that
principal, so it must run **before** the Sleipnir transports, or the invoker's
`CheckAuthorisation` and the WebSocket upgrade gate see an unauthenticated context no matter
what token you sent:

```csharp
app.UseRouting();
app.UseAuthentication();      // populates HttpContext.User
app.UseAuthorization();       // (optional policy enforcement)
app.UseSleipnirTransports();  // WS upgrade gate + invoker read HttpContext.User HERE
app.MapSleipnir();
```

Get this order wrong and every authed call returns 401 even with a valid bearer. It is the
single load-bearing line of the chapter.

## The server: JWT issuance + validation

`AccountService` is a minimal, self-contained JWT issuer — two hardcoded users stand in for
a real user store. The signing key is a symmetric `HmacSha256` secret shared between issuance
(here) and validation (`AddJwtBearer` in `Program.cs`). Tutorial-only; production uses an
asymmetric key (RSA/ECDSA) in a vault and the server holds only the public part.

```csharp
public const string Issuer = "Story.Api";
public const string Audience = "Story";
public const string SigningKey = "Story.Api.dev.signing.key.32+chars.long.enough.for.HmacSha256";
public static readonly SymmetricSecurityKey SecurityKey =
    new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey));

public string? TryLogin(string username, string password, out Profile? profile)
{
    // (lookup in the hardcoded user table; null out on bad credentials)
    profile = new Profile { Username = username, Role = entry.Role };
    var claims = new[]
    {
        new Claim(ClaimTypes.Name, username),
        new Claim(ClaimTypes.Role, entry.Role),   // IsInRole("Admin") reads this
    };
    var descriptor = new SecurityTokenDescriptor
    {
        Issuer = Issuer, Audience = Audience,
        Subject = new ClaimsIdentity(claims),
        Expires = DateTime.UtcNow.AddHours(1),
        SigningCredentials = new SigningCredentials(SecurityKey, SecurityAlgorithms.HmacSha256),
    };
    var handler = new JwtSecurityTokenHandler();
    return handler.WriteToken(handler.CreateToken(descriptor));
}
```

The role claim uses `ClaimTypes.Role` deliberately — that is what
`ClaimsPrincipal.IsInRole("Admin")` reads, and `IsInRole` is what
`[SleipnirAuthorise(Role = "Admin")]` checks
(`SleipnirInvoker.CheckAuthorisation` → `SleipnirAuthoriseAttribute.OnAuthorization`).
`AddJwtBearer` sets `RoleClaimType = ClaimTypes.Role` to match.

`Program.cs` wires validation against the **same** key, issuer, and audience — the two sides
must agree:

```csharp
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true, ValidIssuer = AccountService.Issuer,
            ValidateAudience = true, ValidAudience = AccountService.Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true, IssuerSigningKey = AccountService.SecurityKey,
            RoleClaimType = ClaimTypes.Role,
        };
    });
builder.Services.AddAuthorization();
```

`AddSleipnir` is left with `RequireAuthentication = false` on purpose — `Market` stays public
(the portal shows quotes before login). The authed surface is gated **per method** by
`[SleipnirAuthorise]`, not globally. (Setting `RequireAuthentication = true` would also turn
on the connection-level WS/SSE default-deny gate — not what we want for a public Market.)

> Sleipnir does **not** register `IHttpContextAccessor` — controllers that read the caller's
> claims (`Account.Me`, `Portfolio`) add it themselves (`AddHttpContextAccessor()` in
> `Program.cs`). That is the standard ASP.NET pattern; the framework cannot and does not
> prevent it.

## The two attributes, and the 401/403 distinction

`Account` and `Portfolio` demonstrate both shapes of `[SleipnirAuthorise]`:

```csharp
// Class-level: EVERY method here needs a valid bearer (any role). 401 without one.
[SleipnirController("Portfolio")]
[SleipnirAuthorise]
public class PortfolioController
{
    [SleipnirMethod("GetHoldings")]
    public List<Holding> GetHoldings() => SeedHoldings;   // any authed user

    // Method-level override: additionally demands the Admin role.
    // A Customer token is authenticated → not 401, but 403.
    [SleipnirMethod("StartFeed")]
    [SleipnirAuthorise(Role = "Admin")]
    public bool StartFeed() { _feed.IsRunning = true; return _feed.IsRunning; }
}
```

The invoker's `CheckAuthorisation` throws `UnauthorizedAccessException` (→ **401**,
`category: Unauthenticated`) when `HttpContext.User` is unauthenticated, and
`ForbiddenAccessException` (→ **403**, `category: PermissionDenied`) when the user *is*
authenticated but `IsInRole(role)` is false. `Login` itself is anonymous — you need a token
to call anything authed, so the login can't require one. Bad credentials return a business
`401` via `SleipnirResults.Unauthorized("invalid credentials")`, **not** a throw — the client
gets a clear message, not a generic 500.

> **Business errors return `SleipnirResponse`; unexpected failures throw.** `Login`'s
> "invalid credentials" is a business outcome → `SleipnirResults.Unauthorized(...)`. A
> signing-key misconfiguration is unexpected → throws → generic 500. See `CLAUDE.md` →
> "Error Handling".

### The chain, now authed

`Portfolio.PlaceOrder` is the chapter-6 chain provider, now behind the class-level gate: it
exposes `$.id` (camelCase — the wire document is camelCase and JsonPath is case-sensitive) as
`orderId`, and `GetOrder(@orderId)` consumes it. Same Serial batch, one roundtrip — the chain
from chapter 6 works unchanged once you add a bearer.

## The client envelope: HTTP 200, `code: 401`, and the thrown `SleipnirException`

The REST transport always answers **HTTP 200** and carries the Sleipnir status in the
**envelope** `code` (the "envelope-at-200" contract — chapters 1 and 6 show this for success;
it holds for errors too). So an unauthorized `GetHoldings` is:

```
HTTP/1.1 200 OK
{"code":401,"data":null,"error":{"code":401,"message":"Unauthorized.","category":"Unauthenticated"}}
```

The client does **not** key off the HTTP status — it checks the envelope. `SleipnirResponse.IsSuccess`
is `Code is >= 200 and <= 299`, so `Call<T>` sees `code:401`, `IsSuccess` is false, and the
client **throws `SleipnirException`** carrying `Error.Code` / `Error.Message`. That is why the
admin pages `catch (SleipnirException ex)` and read `ex.Error?.Code`:

```csharp
try
{
    holdings = await Sleipnir.Call<List<Holding>>(Sleipnir.Portfolio.GetHoldings()) ?? new();
}
catch (SleipnirException ex)
{
    // 401 = no/invalid bearer; 403 = valid bearer but role denied.
    error = $"HTTP {ex.Error?.Code} — {ex.Error?.Message ?? ex.Message}";
}
```

The TS client, by contrast, does **not** throw — `client.call(...)` resolves to the
`SleipnirResponse` object and the portal reads `res.code` / `res.error` directly (see
`loadHoldings` in `App.svelte`). Both are consistent with their own client's contract; the
difference is API style, not behaviour.

## Tier 2 — the Blazor admin: a server-side admin bearer

The Pflege-Backend's whole point is that the **admin token never reaches the browser**. Blazor
Server runs the admin circuit server-side, so `AdminAuth` (a scoped service, one session per
circuit) holds the bearer in memory and arms the generated client's transport router with it:

```csharp
public async Task LoginAsync(string user, string pass)
{
    var resp = await _client.Call<LoginPayload>(Sleipnir.Account.Login(user, pass));
    Token = resp?.Token ?? throw new UnauthorizedAccessException("login failed");
    Profile = resp.Profile;
    // The generated client is a singleton wrapping a SleipnirTransportRouter. Cast to it
    // and SetBearer — arms every bundled backend (WS + REST + SSE).
    ((SleipnirTransportRouter)_client.Client).SetBearer(Token);
}
```

Crucially, the admin keeps the **`auto`** profile (WebSocket probed first) even for authed
calls — the server-side C# `ClientWebSocket` **can** set the `Authorization` header, unlike a
browser WS handshake. So the admin gets the efficient WS path *and* auth. The contrast with
the browser portal — which cannot — is the chapter's punchline.

`Login.razor` logs in; `Holdings.razor` calls `Portfolio.GetHoldings` (and shows the 401 as a
`SleipnirException` before login); `Feed.razor` calls the admin-only `StartFeed` / `StopFeed`
(a customer token reaches `RunFeedAsync`'s `catch (SleipnirException)` as **403**) and runs the
authed chain `PlaceOrder → GetOrder`.

## Tier 3 — the Svelte portal: a customer bearer, pinned to REST + SSE

The portal logs in a *customer* and calls `Portfolio.GetHoldings`. After a successful `Login`
it does two things — arm the bearer, then **pin REST + SSE**:

```ts
async function login() {
  const res = await client.call(client.account.login(loginUser, loginPass));
  if (res.code !== 200 || !res.data) { loginError = res.error?.message ?? `HTTP ${res.code}`; return; }
  const payload = res.data as { token: string; profile: Profile };
  profile = payload.profile;
  client.setBearer(payload.token);        // arm every bundled backend
  await client.useTransport("rest");       // browser WS can't set Authorization → pin REST+SSE
  transportLabel = "rest+sse (authed)";
  await loadHoldings();
}
```

A browser **cannot** set the `Authorization` header on a `WebSocket` upgrade handshake (the
API is simply not there), and Sleipnir's WS gate authenticates off `HttpContext.User` — so an
authed call over `auto` (WS) would 401. REST + SSE *can* carry the header, so the portal pins
`rest` after login. The anonymous Market board still uses `auto` (WS where available); on
`logout` the portal calls `useTransport("auto")` and re-probes. This is the "REST best
friends" theme made concrete: REST + SSE is the proxy-safe, browser-auth-friendly path the
portal reaches for the moment auth enters the picture.

## Try it

```bash
# terminal 1 — the API (now with Account + Portfolio + JWT)
dotnet run --project guide/server

# terminal 2 — the admin (Blazor Pflege-Backend)
dotnet run --project guide/admin   # → https://localhost:5011  (Login / Holdings / Feed pages)

# terminal 3 — the portal (Svelte)
cd guide/portal && npm run dev     # → http://localhost:5173  (Auth section at the bottom)
```

On the portal: the **Auth** section logs in as `customer` / `customer` → the badge flips to
`rest+sse (authed)` → **Load holdings** returns the two seeded holdings. Log out → the badge
returns to `auto`. On the admin: log in as `admin` / `admin` → **Feed control** → Start/Stop
return `true`/`false`; the authed chain **Place + fetch** places an order and fetches it back
in one roundtrip.

### Verify the wire without a UI

```bash
# 1. Login (anonymous) → JWT
TOK=$(curl -sk -X POST https://localhost:5010/api/sleipnir/json -H "Content-Type: application/json" \
  -d '{"controller":"Account","method":"Login",
       "params":[{"parameterName":"username","data":"customer"},{"parameterName":"password","data":"customer"}]}' \
  | grep -o '"token":"[^"]*"' | sed 's/"token":"//;s/"$//')

# 2. No bearer → 401 (envelope-at-200, category Unauthenticated)
curl -sk -X POST https://localhost:5010/api/sleipnir/json -H "Content-Type: application/json" \
  -d '{"controller":"Portfolio","method":"GetHoldings"}'
# → {"code":401,"error":{"code":401,"message":"Unauthorized.","category":"Unauthenticated"}}

# 3. Customer bearer → 200 + holdings
curl -sk -X POST https://localhost:5010/api/sleipnir/json -H "Authorization: Bearer $TOK" \
  -H "Content-Type: application/json" -d '{"controller":"Portfolio","method":"GetHoldings"}'
# → {"code":200,"data":[{"symbol":"BTC","quantity":0.75,"averagePrice":42000},…]}

# 4. Customer bearer on an Admin-only method → 403 (PermissionDenied, NOT 401)
curl -sk -X POST https://localhost:5010/api/sleipnir/json -H "Authorization: Bearer $TOK" \
  -H "Content-Type: application/json" -d '{"controller":"Portfolio","method":"StartFeed"}'
# → {"code":403,"error":{"code":403,"message":"Forbidden.","category":"PermissionDenied"}}

# 5. Admin bearer → StartFeed succeeds (true), StopFeed succeeds (false)
ATOK=$(curl -sk -X POST https://localhost:5010/api/sleipnir/json -H "Content-Type: application/json" \
  -d '{"controller":"Account","method":"Login",
       "params":[{"parameterName":"username","data":"admin"},{"parameterName":"password","data":"admin"}]}' \
  | grep -o '"token":"[^"]*"' | sed 's/"token":"//;s/"$//')
curl -sk -X POST https://localhost:5010/api/sleipnir/json -H "Authorization: Bearer $ATOK" \
  -H "Content-Type: application/json" -d '{"controller":"Portfolio","method":"StartFeed"}'
# → {"code":200,"data":true}

# 6. The authed chain PlaceOrder → GetOrder (Serial multi, one roundtrip)
curl -sk -X POST https://localhost:5010/api/sleipnir/json/multi -H "Authorization: Bearer $ATOK" \
  -H "Content-Type: application/json" -d '{
    "requests":[
      {"controller":"Portfolio","method":"PlaceOrder",
       "params":[{"parameterName":"symbol","data":"BTC"},{"parameterName":"quantity","data":0.1}],
       "dependencyMapping":{"orderId":"$.id"},"id":"place"},
      {"controller":"Portfolio","method":"GetOrder",
       "params":[{"parameterName":"id","data":"@orderId"}],"id":"order"}],
    "mode":1}'
# → [{"code":200,"data":{"id":2,"symbol":"BTC",…},"id":"place","exposedDependencies":{"orderId":"2"}},
#     {"code":200,"data":{"id":2,"symbol":"BTC",…},"id":"order"}]
```

Step 4 is the whole chapter in one response: the customer is **authenticated** (the bearer
validated, `HttpContext.User` set) but **not Admin**, so the invoker throws
`ForbiddenAccessException` → `403 PermissionDenied`. A missing bearer on the same call is
`401 Unauthenticated`. Same method, different category — that is the distinction.

---

**Next:** [Chapter 9 — Eventing: a live BTC price feed](09-events.md) _(planned)_. The admin's
`StartFeed` / `StopFeed` toggle a `PriceFeedService` that pushes ticks over `[SleipnirEvent]`;
the Svelte portal draws a live chart, the Blazor admin monitors it, and a dropped connection
resumes from `lastEventId`. WebSocket, SSE, and SignalR all carry the stream — and the portal
falls back from WS to SSE transparently.