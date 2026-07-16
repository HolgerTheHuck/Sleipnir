using Microsoft.AspNetCore.Builder;

namespace TrameWebSocket;

/// <summary>
/// Extension-Methoden, um den schlanken Trame-WebSocket-Transport einfach einzubinden.
/// </summary>
public static class TrameWebSocketExtensions
{
    /// <summary>
    /// Fügt die Trame-WebSocket-Middleware der Pipeline hinzu.
    /// Verwenden Sie vorher app.UseWebSockets().
    /// </summary>
    public static IApplicationBuilder UseTrameWebSocket(this IApplicationBuilder app, string path = "/tramews")
    {
        return app.Map(path, application =>
        {
            application.UseMiddleware<TrameWebSocketMiddleware>();
        });
    }
}
