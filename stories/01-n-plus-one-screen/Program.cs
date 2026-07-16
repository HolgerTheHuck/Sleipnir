// =============================================================================
//  Story 01 — The N+1 Screen  (standalone solution, F5 → the N+1 screen)
//
//    One Order detail page, six dependent reads, five services.
//    The Trame thesis: the client declares WHAT depends on WHAT;
//    the server resolves the graph in one roundtrip.
//
//    In Visual Studio: open Story01.sln, press F5 → browser lands on the N+1
//    screen at /story01/ (same origin as /api/trame/*, so no CORS). The screen
//    links to the DevUI at /Trame, where the six Story-01 controllers (Order,
//    Customer, OrderLine, Article, Address, Stock) are ready to call. The web
//    bundle (web/dist) is served by this API; build it once with
//    `npm run build` in the web/ folder. See README.md for the narrative.
// =============================================================================

using Microsoft.Extensions.FileProviders;
using TrameHub.Extensions;
using TrameServer;

var builder = WebApplication.CreateBuilder(args);

// DevUI-Bundles aus dem benachbarten TrameDeveloperUi-Projekt im Dev-Modus ausliefern.
// (Production/Publish braucht stattdessen das StaticWebAssets-Manifest + UseStaticFiles.)
builder.WebHost.UseStaticWebAssets();

builder.Services.AddTrame(new TrameOptions
{
    EnableDetailedErrors = builder.Environment.IsDevelopment(),
    RateLimitPermitLimit = 0, // Demo ohne Rate-Limit — South-Bound-Story
});

var app = builder.Build();

// Static-Web-Assets (Dev) stellt die DevUI-Bundles aus dem benachbarten
// TrameDeveloperUi-Projekt bereit; UseStaticFiles dient sie tatsächlich aus.
app.UseStaticFiles();

// Story-01-Web-Beispiel (der N+1-Screen) aus dem gebauten web/dist ausliefern —
// same-origin unter /story01, also KEINE CORS-Hürde für den ersten Walkthrough.
// Das UI nutzt new TrameClient("/") und macht damit relative /api/trame/json-Calls
// auf denselben Origin. Dev-Modus liest aus dem Quellbaum (ContentRoot/web/dist);
// für Publish kopiert die csproj web/dist in die Ausgabe (siehe Story01.csproj).
var webDist = Path.Combine(builder.Environment.ContentRootPath, "web", "dist");
if (Directory.Exists(webDist))
{
    var webDistProvider = new PhysicalFileProvider(webDist);
    // UseDefaultFiles dient index.html für den Verzeichnis-Request /story01/ (SPA-Landing),
    // UseStaticFiles dient /story01/assets/* und die index.html selbst. Reihenfolge matters:
    // DefaultFiles muss VOR StaticFiles stehen.
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = webDistProvider, RequestPath = "/story01" });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = webDistProvider, RequestPath = "/story01" });
}

app.UseRouting();

// WebSocket (Default-Kanal) + Controller-Registrierung (Auto-Discovery übernimmt
// die Story-Controller aus Domain.cs) in einem Aufruf.
app.UseTrameTransports();

// REST + Developer-UI in einem Aufruf. (SignalR ist hier off — Story 03 zeigt den
// dritten Wire; Story 01 braucht nur REST + WS, beide über UseTrameTransports/MapTrame.)
app.MapTrame();

// N+1-Screen als Endpunkt (wie /Trame die DevUI). Dient index.html für /story01 und
// /story01/ — die Asset-Requests /story01/assets/* bedient die StaticFile-Middleware oben.
string screenIndex = Path.Combine(webDist, "index.html");
async Task ServeScreen(HttpContext ctx)
{
    if (File.Exists(screenIndex))
    {
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.SendFileAsync(screenIndex);
        return;
    }
    ctx.Response.StatusCode = StatusCodes.Status404NotFound;
    await ctx.Response.WriteAsync("Story-01 web bundle not found. Run `npm run build` in stories/01-n-plus-one-screen/web.");
}
// Bare /story01 (ohne trailing slash) — der Redirect aus "/" und der DevUI-Link nutzen
// diese Form. /story01/ (mit Slash) wird bereits von UseDefaultFiles oben bedient. Eine
// zweite MapGet("/story01/") würde mit der DefaultFiles-Middleware bzw. dieser Route
// eine AmbiguousMatchException auslösen, daher nur der bare-Pfad als Endpoint.
app.MapGet("/story01", ServeScreen);

// F5-Komfort: Browser-Start auf / landet direkt im Web-Beispiel (erster Walkthrough,
// keine Hürden). Direkt auf /story01/ (mit Slash), weil UseDefaultFiles den bare-Pfad
// sonst mit einem weiteren 301 auf /story01/ weiterleiten würde — so ist es ein Hop.
// Die DevUI bleibt unter /Trame erreichbar — der Screen verlinkt dorthin.
app.MapGet("/", ctx => { ctx.Response.Redirect("/story01/"); return Task.CompletedTask; });

app.Run();