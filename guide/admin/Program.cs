// ==============================================================================
// Story.Admin — the Blazor Server Pflege-Backend (tier 2 of the 3-tier app).
//
// It is a Blazor Web App with Interactive Server render mode (the admin session runs
// server-side, so the admin bearer stays on the server — chapter 8). It talks to the
// Story.Api over the generated typed C# client (SleipnirGeneratedClient, emitted by the
// Sleipnir source generator from the server's contract.sleipnir.json).
//
//   dotnet run --project guide/admin   →  https://localhost:5011
//   (the API at https://localhost:5010 must be running)
// ==============================================================================

using Sleipnir.Generated;
using Sleipnir.Guide.Admin.Components;
using Sleipnir.Guide.Admin.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// One generated client for the whole admin app. The default (string baseUrl) ctor wraps a
// SleipnirTransportRouter (capability "all", auto → WS probed first, REST+SSE fallback). The
// server-side C# WS client CAN set Authorization, so the admin keeps `auto` even for authed
// calls — AdminAuth arms the router's bearer after login. A singleton is fine for a
// Pflege-Backend (single-admin-at-a-time in the guide; a multi-admin setup would use one
// router per session).
builder.Services.AddSingleton(_ => new SleipnirGeneratedClient("https://localhost:5010"));

// The admin's server-side auth session (per-circuit). Holds the bearer + profile; mutates the
// shared client's router on login/logout.
builder.Services.AddScoped<AdminAuth>();

builder.WebHost.UseUrls("https://localhost:5011");

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();