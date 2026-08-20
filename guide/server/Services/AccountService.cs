using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Sleipnir.Guide.Api.Domain;

namespace Sleipnir.Guide.Api.Services;

// The chapter 8 identity provider — a minimal, self-contained JWT issuer. Two hardcoded
// users stand in for a real user store:
//   customer / customer  → role "Customer" (the Svelte portal tier)
//   admin    / admin     → role "Admin"    (the Blazor Pflege-Backend tier)
//
// Sleipnir itself reads ONLY HttpContext.User; it runs no IdP logic of its own (see
// stories/04-north-bound-security). So this service issues a JWT, ASP.NET's JwtBearer
// middleware validates it on the way back in and populates HttpContext.User, and
// [SleipnirAuthorise] enforces per-method rules on top. The role claim uses ClaimTypes.Role
// so ClaimsPrincipal.IsInRole("Admin") matches — that is what [SleipnirAuthorise(Role=...)]
// checks (SleipnirInvoker.CheckAuthorisation -> SleipnirAuthoriseAttribute.OnAuthorization).
public class AccountService
{
    // A single symmetric signing key shared between issuance (here) and validation
    // (AddJwtBearer in Program.cs). Tutorial-only — production uses an asymmetric key
    // (RSA/ECDSA) kept in a vault, and the server only holds the public part.
    public const string Issuer = "Story.Api";
    public const string Audience = "Story";
    public const string SigningKey = "Story.Api.dev.signing.key.32+chars.long.enough.for.HmacSha256";

    public static readonly SymmetricSecurityKey SecurityKey =
        new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey));

    private static readonly SigningCredentials SigningCreds =
        new SigningCredentials(SecurityKey, SecurityAlgorithms.HmacSha256);

    // The in-memory user store. (username, password) → role.
    private static readonly Dictionary<string, (string Password, string Role)> Users = new(StringComparer.OrdinalIgnoreCase)
    {
        ["customer"] = ("customer", "Customer"),
        ["admin"] = ("admin", "Admin"),
    };

    // Validate credentials and issue a JWT. Returns null on bad credentials (the controller
    // turns that into a 401 business response via SleipnirResults).
    public string? TryLogin(string username, string password, out Profile? profile)
    {
        profile = null;
        if (!Users.TryGetValue(username, out var entry) ||
            !string.Equals(entry.Password, password, StringComparison.Ordinal))
        {
            return null;
        }

        profile = new Profile { Username = username, Role = entry.Role };

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, username),
            new Claim(ClaimTypes.Role, entry.Role),   // IsInRole("Admin") reads this
        };

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddHours(1),
            SigningCredentials = SigningCreds,
        };

        var handler = new JwtSecurityTokenHandler();
        var token = handler.CreateToken(descriptor);
        return handler.WriteToken(token);
    }
}