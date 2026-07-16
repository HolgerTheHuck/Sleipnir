// =============================================================================
//  Story 02 — One Button, Seven Commands  (standalone solution, F5 → DevUI)
//
//    One "Place order" click fans out to seven downstream writes. One of them
//    refuses (customer over credit limit). The REST way aborts the loop on the
//    first failure and never contacts the rest. The Trame way runs all seven
//    in one batch with per-command isolation — every command reports its own
//    outcome, the unrelated ones still ran.
//
//    In Visual Studio: open Story02.sln, press F5 → browser lands in the DevUI
//    at /Trame with the seven command controllers. See README.md for the
//    narrative and the one-batch call that replaces the sequential loop.
// =============================================================================

using TrameHub.Extensions;
using TrameServer;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseStaticWebAssets();

builder.Services.AddTrame(new TrameOptions
{
    EnableDetailedErrors = builder.Environment.IsDevelopment(),
    RateLimitPermitLimit = 0, // Demo ohne Rate-Limit — South-Bound-Story
});

var app = builder.Build();

app.UseStaticFiles();
app.UseRouting();
app.UseTrameTransports();
app.MapTrame();

app.MapGet("/", ctx => { ctx.Response.Redirect("/Trame"); return Task.CompletedTask; });

app.Run();