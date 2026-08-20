using SleipnirCommon.Attribute;     // [SleipnirDocumentation]
using SleipnirCommon.Models;       // SleipnirResponse
using SleipnirCommon.Results;       // SleipnirResults
using SleipnirCore.Attributes;     // [SleipnirController], [SleipnirMethod], [SleipnirAuthorise]
using Sleipnir.Guide.Api.Domain;
using Sleipnir.Guide.Api.Services;

namespace Sleipnir.Guide.Api.Controllers;

// The chapter 8 identity surface. Login is anonymous (you need a token to call anything
// authed, so the login itself can't require one). Me is the first authed call — it proves
// the bearer works by echoing the caller's own identity back from HttpContext.User.
//
// Controllers are resolved per-call in a DI scope, so constructor injection of AccountService
// (singleton) and IHttpContextAccessor (added in Program.cs — Sleipnir does NOT register it)
// is the standard ASP.NET pattern. Sleipnir only checks [SleipnirAuthorise] for you; reading
// the actual claims (who is this customer?) is your job via the accessor.
[SleipnirController("Account")]
public class AccountController
{
    private readonly AccountService _account;
    private readonly IHttpContextAccessor _http;

    public AccountController(AccountService account, IHttpContextAccessor http)
    {
        _account = account;
        _http = http;
    }

    // Anonymous: exchange credentials for a signed JWT. Bad credentials → a business 401
    // (SleipnirResults.Unauthorized), NOT a throw — the client gets a clear error message,
    // not a generic 500. See the "Return SleipnirResponse for business errors" rule.
    [SleipnirMethod("Login")]
    [SleipnirDocumentation("Exchange username + password for a JWT bearer token. Try customer/customer or admin/admin. The token is sent back as Authorization: Bearer on subsequent calls.")]
    public SleipnirResponse Login(string username, string password)
    {
        var token = _account.TryLogin(username, password, out var profile);
        if (token is null || profile is null)
            return SleipnirResults.Unauthorized("invalid credentials");

        return SleipnirResults.Ok(new LoginResult { Token = token, Profile = profile });
    }

    // The first authed call: echo the caller's identity from HttpContext.User. Requires a
    // valid bearer (any role). 401 without one (the invoker's CheckAuthorisation throws
    // UnauthorizedAccessException before this body runs).
    [SleipnirMethod("Me")]
    [SleipnirAuthorise]
    [SleipnirDocumentation("Return the caller's profile from the bearer token. Requires authentication (any role).")]
    public Profile Me()
    {
        var user = _http.HttpContext?.User;
        var username = user?.Identity?.Name ?? "unknown";
        var role = user?.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value ?? "unknown";
        return new Profile { Username = username, Role = role };
    }
}