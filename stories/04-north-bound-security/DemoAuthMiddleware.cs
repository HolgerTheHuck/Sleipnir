using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace TrameStories.Story04;

/// <summary>
/// DEMO-Auth-Middleware für die North-Bound-Security-Story. KEIN Produktions-Auth —
/// sie steht für eine echte Identity-Prvider-Lauf (JWT/Cookie/mTLS), die upstream
/// <c>HttpContext.User</c> belegt. Trame selbst liest nur <c>HttpContext.User</c>;
/// es führt keine eigene Identity-Prvider-Logik.
///
/// Akzeptiert einen Demo-Token über drei Kanäle (damit der Curl-Walkthrough, die
/// Browser-URL und die DevUI-Bearer-Eingabe alle funktionieren):
///   - <c>Authorization: Bearer trame-demo</c> / <c>trame-admin</c>  (DevUI Auth-Panel)
///   - <c>X-Trame-Token: trame-demo</c> / <c>trame-admin</c>        (curl-Komfort)
///   - <c>?token=trame-demo</c> / <c>?token=trame-admin</c>        (Browser-URL/WS-Upgrade)
///
/// <c>trame-demo</c>  → authentifiziert als "demo" (keine Rolle).
/// <c>trame-admin</c> → authentifiziert als "demo" + Rolle "Admin".
/// Ohne/ungültigen Token → <c>HttpContext.User</c> bleibt unauthentifiziert; die Trame-
/// Transporte/der Invoker liefern dann 401 (RequireAuthentication-Default-Deny).
/// </summary>
public class DemoAuthMiddleware
{
    private readonly RequestDelegate _next;
    public DemoAuthMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var token = ExtractToken(context);
        if (token is not null && TryBuildPrincipal(token, out var principal))
            context.User = principal;

        await _next(context);
    }

    private static string? ExtractToken(HttpContext context)
    {
        // 1) Authorization: Bearer <token>
        var auth = context.Request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return auth["Bearer ".Length..].Trim();

        // 2) X-Trame-Token header
        var header = context.Request.Headers["X-Trame-Token"].ToString();
        if (!string.IsNullOrWhiteSpace(header)) return header.Trim();

        // 3) ?token= query (Browser-URL, WS-Upgrade)
        var query = context.Request.Query["token"].ToString();
        if (!string.IsNullOrWhiteSpace(query)) return query.Trim();

        return null;
    }

    private static bool TryBuildPrincipal(string token, out ClaimsPrincipal principal)
    {
        principal = null!;
        bool admin = false;
        if (token.Equals("trame-demo", StringComparison.OrdinalIgnoreCase)) { /* normal user */ }
        else if (token.Equals("trame-admin", StringComparison.OrdinalIgnoreCase)) { admin = true; }
        else return false; // ungültiger Token → unauthentifiziert (Default-Deny greift)

        var claims = new List<Claim> { new(ClaimTypes.Name, "demo") };
        if (admin) claims.Add(new Claim(ClaimTypes.Role, "Admin"));
        principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "DemoAuth"));
        return true;
    }
}