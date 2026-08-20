// ==============================================================================
// Story.Api — the Sleipnir Guide API server (the backend tier of the 3-tier app).
//
// Three lines of Sleipnir wiring (AddSleipnir -> UseSleipnirTransports -> MapSleipnir)
// serve REST + WebSocket (+ optional SignalR) + the Developer UI. [SleipnirController]
// types in this project are auto-discovered at startup — no manual registration.
//
// Chapter 8 adds standard ASP.NET JWT bearer auth. Sleipnir reads ONLY HttpContext.User;
// ASP.NET auth populates it, and [SleipnirAuthorise] enforces per-method rules on top. The
// ordering is load-bearing: UseAuthentication/UseAuthorization must run BEFORE
// UseSleipnirTransports so HttpContext.User is set when the WS upgrade gate and the
// invoker's CheckAuthorisation read it.
//
//   dotnet run --project guide/server
//     REST          https://localhost:5010/api/sleipnir/json   (+ /multi, /discovery)
//     WebSocket     wss://localhost:5010/sleipnirws
//     Developer UI  https://localhost:5010/Sleipnir
//
// (HTTPS needs a one-time `dotnet dev-certs https --trust`.)
// ==============================================================================

using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Sleipnir.Guide.Api.Services;
using SleipnirHub.Extensions;     // AddSleipnir
using SleipnirServer;            // UseSleipnirTransports, MapSleipnir

var builder = WebApplication.CreateBuilder(args);

// Serve the built Developer UI static assets in Development (production injects them
// automatically via MapSleipnir).
if (builder.Environment.IsDevelopment())
{
    builder.WebHost.UseStaticWebAssets();
}

// CORS open for the browser clients (the Svelte portal on :5173 and the plain HTML page).
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .AllowAnyOrigin()
    .AllowAnyHeader()
    .AllowAnyMethod()));

builder.Services.AddSleipnir(options =>
{
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
    // RequireAuthentication stays false: Market.GetQuote/GetQuotes/Search are public (the
    // portal shows quotes before login). Per-method [SleipnirAuthorise] on Account/Portfolio
    // gates the authed surface instead. (Setting this true would also turn on the
    // connection-level WS/SSE default-deny gate — not what we want for a public Market.)
});

// --- Chapter 8: standard ASP.NET JWT bearer auth -------------------------------------
// Sleipnir does NOT register IHttpContextAccessor; controllers that read the caller's
// claims (Account.Me, Portfolio.GetHoldings/PlaceOrder) need it, so we add it here.
builder.Services.AddHttpContextAccessor();

// The AccountService (issuer) and FeedControlService (the admin-gated feed toggle chapter 9
// reads) are singletons — resolved per-call into controllers via the invoker's DI scope.
builder.Services.AddSingleton<AccountService>();
builder.Services.AddSingleton<FeedControlService>();

// JWT validation. Issuance uses the SAME symmetric key (AccountService.SecurityKey), issuer
// and audience — the two sides must agree. RoleClaimType = ClaimTypes.Role so
// ClaimsPrincipal.IsInRole("Admin") matches the role claim AccountService issues (that is what
// [SleipnirAuthorise(Role = "Admin")] checks).
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = AccountService.Issuer,
            ValidateAudience = true,
            ValidAudience = AccountService.Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = AccountService.SecurityKey,
            RoleClaimType = System.Security.Claims.ClaimTypes.Role,
        };
    });
builder.Services.AddAuthorization();

// Fixed URL so every chapter's snippets (https://localhost:5010 / wss://.../sleipnirws)
// work out of the box.
builder.WebHost.UseUrls("https://localhost:5010");

var app = builder.Build();

app.UseCors();
app.UseRouting();

// Auth BEFORE the Sleipnir transports: populates HttpContext.User for the invoker
// (CheckAuthorisation) and the transport gates (WS upgrade / SSE / discovery).
app.UseAuthentication();
app.UseAuthorization();

// Sleipnir transport middleware (WebSocket primary) + controller registration.
app.UseSleipnirTransports();

// REST (/api/sleipnir) + Developer UI (/Sleipnir) + SignalR hub (/sleipnirhub) when enabled.
app.MapSleipnir();

app.Run();