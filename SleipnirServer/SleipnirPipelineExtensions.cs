using SleipnirHub.Extensions;
using SleipnirDeveloperUi;
using SleipnirRest;
using SleipnirWebSocket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace SleipnirServer;

/// <summary>
/// Vereinheitlichte Pipeline-Extensions für das Sleipnir-Server-Setup.
/// Ergänzt <c>AddSleipnir</c> (Services-Stage) um die Transport-Middleware und
/// die Endpoint-Mappings in jeweils einem Aufruf — das v1.0-Server-Setup
/// reduziert sich auf drei Zeilen:
/// <code>
/// builder.Services.AddSleipnir(o =&gt; { /* … */ });
/// app.UseSleipnirTransports();   // WebSocket (primär) + Controller-Registrierung
/// app.MapSleipnir();             // REST + Developer-UI + optional SignalR-Hub
/// </code>
/// </summary>
public static class SleipnirPipelineExtensions
{
    /// <summary>
    /// Aktiviert die Sleipnir-Transport-Middleware in einem Aufruf:
    /// <c>UseWebSockets</c> + WebSocket-Transport (primärer Kanal) +
    /// Controller-Registrierung via <c>UseSleipnir</c>.
    /// SignalR ist kein Middleware-Transport — sein Hub wird über
    /// <see cref="MapSleipnir"/> gemappt, sobald <see cref="SleipnirOptions.UseSignalR"/>
    /// aktiv ist.
    /// </summary>
    /// <param name="app">Die Application-Builder-Pipeline.</param>
    /// <param name="webSocketPath">Pfad des WebSocket-Transports (Default <c>/sleipnirws</c>).</param>
    public static IApplicationBuilder UseSleipnirTransports(this IApplicationBuilder app,
        string webSocketPath = "/sleipnirws")
    {
        app.UseWebSockets();
        app.UseSleipnirWebSocket(webSocketPath);
        app.UseSleipnir();
        return app;
    }

    /// <summary>
    /// Mappt alle Sleipnir-Endpunkte in einem Aufruf: REST (<paramref name="restPrefix"/>)
    /// + Developer-UI (<paramref name="developerUiPath"/>) + optional den SignalR-Hub
    /// unter <paramref name="hubPath"/>, falls <see cref="SleipnirOptions.UseSignalR"/>
    /// in der Services-Konfiguration aktiviert wurde (die Options werden aus DI
    /// gelesen, wenn <c>AddSleipnir</c> sie registriert hat).
    /// </summary>
    public static IEndpointRouteBuilder MapSleipnir(this IEndpointRouteBuilder endpoints,
        string restPrefix = "/api/sleipnir",
        string developerUiPath = "/Sleipnir",
        string hubPath = "/sleipnirhub")
    {
        var options = endpoints.ServiceProvider.GetService<SleipnirOptions>();

        // Rate Limiting nur durchreichen, wenn der Host die "sleipnir"-Policy registriert hat.
        // JSON-RPC-Compat nur auf ausdrücklichen Wunsch (SleipnirOptions.EnableJsonRpcCompat).
        endpoints.MapSleipnirEndpoints(restPrefix,
            enableRateLimiting: options?.RateLimitPermitLimit > 0,
            enableJsonRpcCompat: options?.EnableJsonRpcCompat == true);
        endpoints.MapSleipnirDeveloperUi(developerUiPath);

        if (options?.UseSignalR == true)
        {
            // Vollqualifiziert, um den Namen vom gleichnamigen Namespace zu trennen.
            // North-Bound-Härtung: RequireAuthorization wenn RequireAuthentication an
            // (F9.2 — Verbindungs-Gate; der Invoker kann pro-Method-Auth nicht mehr
            // retten, wenn die Verbindung schon steht). Rate-Limiting auf den Hub-
            // Endpoint, wenn die "sleipnir"-Policy registriert ist (F5.2).
            var hub = endpoints.MapHub<SleipnirHub.Hub.SleipnirHub>(hubPath);
            if (options?.RequireAuthentication == true)
                hub.RequireAuthorization();
            if (options?.RateLimitPermitLimit > 0)
                hub.RequireRateLimiting("sleipnir");
        }

        return endpoints;
    }
}