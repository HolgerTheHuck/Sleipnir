// ==============================================================================
// Story.Api — the Sleipnir Guide API server (the backend tier of the 3-tier app).
//
// Three lines of Sleipnir wiring (AddSleipnir -> UseSleipnirTransports -> MapSleipnir)
// serve REST + WebSocket (+ optional SignalR) + the Developer UI. [SleipnirController]
// types in this project are auto-discovered at startup — no manual registration.
//
//   dotnet run --project guide/server
//     REST          https://localhost:5010/api/sleipnir/json   (+ /multi, /discovery)
//     WebSocket     wss://localhost:5010/sleipnirws
//     Developer UI  https://localhost:5010/Sleipnir
//
// (HTTPS needs a one-time `dotnet dev-certs https --trust`.)
// ==============================================================================

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
});

// Fixed URL so every chapter's snippets (https://localhost:5010 / wss://.../sleipnirws)
// work out of the box.
builder.WebHost.UseUrls("https://localhost:5010");

var app = builder.Build();

app.UseCors();
app.UseRouting();

// Sleipnir transport middleware (WebSocket primary) + controller registration.
app.UseSleipnirTransports();

// REST (/api/sleipnir) + Developer UI (/Sleipnir) + SignalR hub (/sleipnirhub) when enabled.
app.MapSleipnir();

app.Run();