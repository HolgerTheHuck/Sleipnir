using Microsoft.AspNetCore.Builder;

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
        return app.Map(path, application =>
        {
            application.UseMiddleware<SleipnirWebSocketMiddleware>();
        });
    }
}
