// ==============================================================================
// Sleipnir Beispiel-Server — ausführbares Server-Setup (Program.cs).
//
// Minimaler Einstieg: drei Zeilen Sleipnir-Wiring (AddSleipnir → UseSleipnirTransports →
// MapSleipnir) reichen, um REST + WebSocket (+ optional SignalR) + Developer-UI
// bereitzustellen. Die [SleipnirController]-Typen aus SampleServer.cs werden per
// Attribut-Scan automatisch gefunden und beim UseSleipnir-Aufruf registriert —
// keine manuelle Registrierung.
//
// Start:  dotnet run --project samples/server/SampleServer.csproj
//         (HTTPS braucht `dotnet dev-certs https --trust` einmalig pro Maschine)
// Endpunkte:
//   • REST          https://localhost:5001/api/sleipnir/json  (+ /multi, /discovery)
//   • WebSocket     wss://localhost:5001/sleipnirws
//   • Developer-UI  https://localhost:5001/Sleipnir
// ==============================================================================

using SleipnirHub.Extensions;
using SleipnirServer;                       // UseSleipnirTransports, MapSleipnir
using SleipnirTelemetry;                    // AddSleipnirTelemetry (optionales OTel-SDK)

var builder = WebApplication.CreateBuilder(args);

// In Development die gebauten Developer-UI-Static-Assets aus dem
// Sleipnir.DeveloperUi-Paket einblenden (Produktion injiziert sie automatisch).
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

builder.Services.AddSleipnir(o =>
{
    o.EnableDetailedErrors = builder.Environment.IsDevelopment();
    // Rate-Limit nur in Produktion; in der Demo keine Drosselung.
    o.RateLimitPermitLimit = builder.Environment.IsProduction() ? 50 : 0;
});

// Optional: OpenTelemetry-SDK booten und den „Sleipnir"-ActivitySource abonnieren.
// Console-Exporter schreibt jeden SleipnirCall-/SleipnirBatch-Span auf die Konsole —
// für die Demo am einfachsten zu beobachten (Production: OTLP an einen Collector).
builder.Services.AddSleipnirTelemetry(o =>
{
    o.ServiceName = "Sleipnir.SampleServer";
    o.Exporter = SleipnirExporter.Console;
});

// Fixe URL, damit die Client-Snippets (https://localhost:5001 / wss://…/sleipnirws)
// out-of-the-box passen.
builder.WebHost.UseUrls("https://localhost:5001");

var app = builder.Build();

app.UseCors();
app.UseRouting();
app.UseRateLimiter();

// Sleipnir-Transport-Middleware (WebSocket primär) + Controller-Registrierung.
app.UseSleipnirTransports();

// REST (/api/sleipnir) + Developer-UI (/Sleipnir) + ggf. SignalR-Hub (/sleipnirhub).
app.MapSleipnir();

app.Run();