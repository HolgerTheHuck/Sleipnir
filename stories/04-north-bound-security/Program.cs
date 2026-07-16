// =============================================================================
//  Story 04 — North-bound Security  (standalone solution, F5 → DevUI behind auth)
//
//    The hardened server. Trame was south-bound (trusted caller); this is it
//    going north-bound — untrusted external clients over REST/WebSocket/SignalR.
//    RequireAuthentication is ON (default-deny), RateLimitPermitLimit and
//    MaximumBatchSize are set, Discovery is behind auth. A demo auth middleware
//    populates HttpContext.User (standing in for your real JWT/Cookie/mTLS).
//
//    In Visual Studio: open Story04.sln, press F5 → browser lands on /Trame. The
//    DevUI loads but its discovery call 401s — that IS the lesson. Enter the demo
//    token in the DevUI Auth panel (trame-demo) and the contract appears. The
//    README walks the curl matrix: unauth → 401, token → 200, anonymous → 200,
//    admin-only without role → 401, batch over cap → 400.
//
//    See SECURITY.md at the repo root for the full audit (F1–F12) + roadmap.
// =============================================================================

using TrameHub.Extensions;
using TrameServer;
using TrameStories.Story04;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseStaticWebAssets();

builder.Services.AddTrame(new TrameOptions
{
    EnableDetailedErrors = builder.Environment.IsDevelopment(),
    // === North-Bound-Härtung (Defaults sind south-bound/off; hier opt-in) ===
    RequireAuthentication = true,     // Default-Deny: unbestückte Methoden verlangen Auth
    RateLimitPermitLimit = 20,        // Fixed-Window-Rate-Limit (REST + SignalR-Hub)
    RateLimitWindowSeconds = 10,
    MaximumBatchSize = 16,            // Fan-Out-DoS-Cap auf /json/multi, JSON-RPC, WS-multi
    MaxDependencyPathLength = 128,    // client-kontrollierter JsonPath-DoS-Cap
    AllowRecursiveDescent = false,    // $.. verboten (teuerster Pfad-Typ)
    UseSignalR = true,                // dritter Wire — auch gehärtet (Hub.RequireAuthorization)
    UseMessagePack = true,
});

var app = builder.Build();

// Demo-Auth-Middleware VOR den Trame-Transporten: belegt HttpContext.User, den
// der Invoker (CheckAuthorisation) und die Transporte (WS-Upgrade/Hub-Endpoint)
// lesen. Produktion: hier steht echtes JWT/Cookie/mTLS — Trame selbst misst nichts.
app.UseMiddleware<DemoAuthMiddleware>();

app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();        // aktiviert die "trame"-Policy (RateLimitPermitLimit>0)
app.UseTrameTransports();    // WS-Upgrade gate-t auf RequireAuthentication; REST via Invoker
app.MapTrame();              // REST-Discovery gate-t; SignalR-Hub .RequireAuthorization()

app.MapGet("/", ctx => { ctx.Response.Redirect("/Trame"); return Task.CompletedTask; });

app.Run();