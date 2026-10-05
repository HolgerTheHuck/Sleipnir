using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using SleipnirCore.Services;

namespace SleipnirWebSocket;

/// <summary>
/// Extension-Methoden, um den schlanken Sleipnir-WebSocket-Transport einfach einzubinden.
/// </summary>
public static class SleipnirWebSocketExtensions
{
    /// <summary>
    /// Fügt die Sleipnir-WebSocket-Middleware der Pipeline hinzu.
    /// Verwenden Sie vorher app.UseWebSockets().
    /// </summary>
    public static IApplicationBuilder UseSleipnirWebSocket(this IApplicationBuilder app, string path = "/sleipnirws")
    {
        // The opt-in ?access_token= fallback (SleipnirOptions.AcceptAccessTokenQuery) is honored
        // only on the WebSocket upgrade of this path — register it (no-op without AddSleipnir).
        app.ApplicationServices.GetService<SleipnirAccessTokenQueryPaths>()?.AddWebSocketPath(path);

        return app.Map(path, application =>
        {
            application.UseMiddleware<SleipnirWebSocketMiddleware>();
        });
    }
}
