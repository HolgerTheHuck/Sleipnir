using TrameHub.Extensions;
using TrameDeveloperUi;
using TrameRest;
using TrameWebSocket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace TrameServer;

/// <summary>
/// Vereinheitlichte Pipeline-Extensions für das Trame-Server-Setup.
/// Ergänzt <c>AddTrame</c> (Services-Stage) um die Transport-Middleware und
/// die Endpoint-Mappings in jeweils einem Aufruf — das v1.0-Server-Setup
/// reduziert sich auf drei Zeilen:
/// <code>
/// builder.Services.AddTrame(o =&gt; { /* … */ });
/// app.UseTrameTransports();   // WebSocket (primär) + Controller-Registrierung
/// app.MapTrame();             // REST + Developer-UI + optional SignalR-Hub
/// </code>
/// </summary>
public static class TramePipelineExtensions
{
    /// <summary>
    /// Aktiviert die Trame-Transport-Middleware in einem Aufruf:
    /// <c>UseWebSockets</c> + WebSocket-Transport (primärer Kanal) +
    /// Controller-Registrierung via <c>UseTrame</c>.
    /// SignalR ist kein Middleware-Transport — sein Hub wird über
    /// <see cref="MapTrame"/> gemappt, sobald <see cref="TrameOptions.UseSignalR"/>
    /// aktiv ist.
    /// </summary>
    /// <param name="app">Die Application-Builder-Pipeline.</param>
    /// <param name="webSocketPath">Pfad des WebSocket-Transports (Default <c>/tramews</c>).</param>
    public static IApplicationBuilder UseTrameTransports(this IApplicationBuilder app,
        string webSocketPath = "/tramews")
    {
        app.UseWebSockets();
        app.UseTrameWebSocket(webSocketPath);
        app.UseTrame();
        return app;
    }

    /// <summary>
    /// Mappt alle Trame-Endpunkte in einem Aufruf: REST (<paramref name="restPrefix"/>)
    /// + Developer-UI (<paramref name="developerUiPath"/>) + optional den SignalR-Hub
    /// unter <paramref name="hubPath"/>, falls <see cref="TrameOptions.UseSignalR"/>
    /// in der Services-Konfiguration aktiviert wurde (die Options werden aus DI
    /// gelesen, wenn <c>AddTrame</c> sie registriert hat).
    /// </summary>
    public static IEndpointRouteBuilder MapTrame(this IEndpointRouteBuilder endpoints,
        string restPrefix = "/api/trame",
        string developerUiPath = "/Trame",
        string hubPath = "/tramehub")
    {
        var options = endpoints.ServiceProvider.GetService<TrameOptions>();

        // Rate Limiting nur durchreichen, wenn der Host die "trame"-Policy registriert hat.
        // JSON-RPC-Compat nur auf ausdrücklichen Wunsch (TrameOptions.EnableJsonRpcCompat).
        endpoints.MapTrameEndpoints(restPrefix,
            enableRateLimiting: options?.RateLimitPermitLimit > 0,
            enableJsonRpcCompat: options?.EnableJsonRpcCompat == true);
        endpoints.MapTrameDeveloperUi(developerUiPath);

        if (options?.UseSignalR == true)
        {
            // Vollqualifiziert, um den Namen vom gleichnamigen Namespace zu trennen.
            // North-Bound-Härtung: RequireAuthorization wenn RequireAuthentication an
            // (F9.2 — Verbindungs-Gate; der Invoker kann pro-Method-Auth nicht mehr
            // retten, wenn die Verbindung schon steht). Rate-Limiting auf den Hub-
            // Endpoint, wenn die "trame"-Policy registriert ist (F5.2).
            var hub = endpoints.MapHub<TrameHub.Hub.TrameHub>(hubPath);
            if (options?.RequireAuthentication == true)
                hub.RequireAuthorization();
            if (options?.RateLimitPermitLimit > 0)
                hub.RequireRateLimiting("trame");
        }

        return endpoints;
    }
}