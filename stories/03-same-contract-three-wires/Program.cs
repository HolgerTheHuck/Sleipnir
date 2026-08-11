// =============================================================================
//  Story 03 — The Same Contract, Three Wires  (standalone solution, F5 → DevUI)
//
//    One code-first domain. Three transports — REST, WebSocket, SignalR —
//    expose the SAME controllers simultaneously. Identical call, identical
//    result, three wires. The contract is the C# classes; the transport is a
//    deployment detail, not a design decision.
//
//    In Visual Studio: open Story03.sln, press F5 → browser lands in the DevUI
//    at /Sleipnir (REST wire). The README shows the identical call over WebSocket
//    and SignalR against the same running server.
// =============================================================================

using SleipnirHub.Extensions;
using SleipnirServer;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseStaticWebAssets();

builder.Services.AddSleipnir(new SleipnirOptions
{
    EnableDetailedErrors = builder.Environment.IsDevelopment(),
    UseSignalR = true,            // dritter Wire — REST + WS sind immer an, SignalR opt-in
    UseMessagePack = true,        // SignalR binary (MsgPack) — der dritte Wire im eigenen Format
    RateLimitPermitLimit = 0,
});

var app = builder.Build();

app.UseStaticFiles();
app.UseRouting();
app.UseSleipnirTransports();   // REST + WS + Controller-Registrierung
app.MapSleipnir();              // REST-Endpunkte + DevUI + SignalR-Hub (UseSignalR=true)

app.MapGet("/", ctx => { ctx.Response.Redirect("/Sleipnir"); return Task.CompletedTask; });

app.Run();