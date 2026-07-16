// ==============================================================================
// Trame Beispiel-Server — ausführbares Server-Setup (Program.cs).
//
// Minimaler Einstieg: drei Zeilen Trame-Wiring (AddTrame → UseTrameTransports →
// MapTrame) reichen, um REST + WebSocket (+ optional SignalR) + Developer-UI
// bereitzustellen. Die [TrameController]-Typen aus SampleServer.cs werden per
// Attribut-Scan automatisch gefunden und beim UseTrame-Aufruf registriert —
// keine manuelle Registrierung.
//
// Start:  dotnet run --project samples/server/SampleServer.csproj
//         (HTTPS braucht `dotnet dev-certs https --trust` einmalig pro Maschine)
// Endpunkte:
//   • REST          https://localhost:5001/api/trame/json  (+ /multi, /discovery)
//   • WebSocket     wss://localhost:5001/tramews
//   • Developer-UI  https://localhost:5001/Trame
// ==============================================================================

using TrameHub.Extensions;
using TrameServer;                       // UseTrameTransports, MapTrame
using TrameTelemetry;                    // AddTrameTelemetry (optionales OTel-SDK)

var builder = WebApplication.CreateBuilder(args);

// In Development die gebauten Developer-UI-Static-Assets aus dem
// Trame.DeveloperUi-Paket einblenden (Produktion injiziert sie automatisch).
if (builder.Environment.IsDevelopment())
{
    builder.WebHost.UseStaticWebAssets();
}

// CORS für die Browser-basierten TS-Client-Samples (samples/typescript/*).
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy => policy
        .AllowAnyOrigin()
        .AllowAnyHeader()
        .AllowAnyMethod());
});

builder.Services.AddTrame(o =>
{
    o.EnableDetailedErrors = builder.Environment.IsDevelopment();
    // Rate-Limit nur in Produktion; in der Demo keine Drosselung.
    o.RateLimitPermitLimit = builder.Environment.IsProduction() ? 50 : 0;
});

// Optional: OpenTelemetry-SDK booten und den „Trame"-ActivitySource abonnieren.
// Console-Exporter schreibt jeden TrameCall-/TrameBatch-Span auf die Konsole —
// für die Demo am einfachsten zu beobachten (Production: OTLP an einen Collector).
builder.Services.AddTrameTelemetry(o =>
{
    o.ServiceName = "Trame.SampleServer";
    o.Exporter = TrameExporter.Console;
});

// Fixe URL, damit die Client-Snippets (https://localhost:5001 / wss://…/tramews)
// out-of-the-box passen.
builder.WebHost.UseUrls("https://localhost:5001");

var app = builder.Build();

app.UseCors();
app.UseRouting();
app.UseRateLimiter();

// Trame-Transport-Middleware (WebSocket primär) + Controller-Registrierung.
app.UseTrameTransports();

// REST (/api/trame) + Developer-UI (/Trame) + ggf. SignalR-Hub (/tramehub).
app.MapTrame();

app.Run();