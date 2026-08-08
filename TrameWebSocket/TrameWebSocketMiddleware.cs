using System.Net.WebSockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using TrameCommon;
using TrameCore.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace TrameWebSocket;

/// <summary>
/// Middleware für einen schlanken, eigenen WebSocket-Transport für Trame.
/// Nutzt ausschließlich Standard-WebSockets (RFC 6455) und JSON, damit Clients in
/// Java, JavaScript, Python oder anderen Sprachen einfach integrierbar sind.
/// </summary>
public class TrameWebSocketMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ITrameCore _trameCore;
    private readonly ILogger<TrameWebSocketMiddleware>? _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    public TrameWebSocketMiddleware(
        RequestDelegate next,
        ITrameCore trameCore,
        ILogger<TrameWebSocketMiddleware>? logger = null)
    {
        _next = next;
        _trameCore = trameCore;
        _logger = logger;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            // Relaxed Encoder: Data (JsonElement) wird roh serialisiert — kein
            // Double-Wrapping; UnsafeRelaxed verhindert zusätzlich `"`-Escaping
            // im Mantel (ExposedDependencies-Strings, Fehlermeldungen).
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            // Write-Only-Converter: DataBytes via WriteRawValue roh in den Wire →
            // kein JsonDocument-Baum auf dem Server (Single-Pass-Optimierung).
            Converters = { new TrameResponseJsonConverter() }
        };
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            await _next(context);
            return;
        }

        // North-Bound-Default-Deny (Security-Audit F9.1): Upgrade ablehnen, bevor der
        // Socket entsteht, wenn RequireAuthentication an und der Caller unauthentifiziert
        // ist. WS hat keinen pro-Method-Opt-out ([TrameAnonymous] wirkt nur im Invoker-
        // Gate auf REST); hier ist die Verbindung die Vertrauensgrenze. Authentifizierung
        // muss upstream (Reverse-Proxy/Token-Middleware) HttpContext.User belegt haben.
        if (_trameCore.RequireAuthentication && !(context.User?.Identity?.IsAuthenticated ?? false))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
        _logger?.LogInformation("WebSocket connection established: {ConnectionId}", context.Connection.Id);

        await HandleConnectionAsync(context, webSocket);
    }

    private const int MaxMessageSize = 1_048_576; // 1 MB

    private async Task HandleConnectionAsync(HttpContext context, WebSocket webSocket)
    {
        // Phase 3: pro-Connection Subscription-Manager für Events.
        var subscriptions = new TrameSubscriptionManager(webSocket, _trameCore, _logger);
        try
        {
            var buffer = new byte[1024 * 4];

            while (webSocket.State == WebSocketState.Open)
            {
                // Bytes sammeln (nicht pro Chunk dekodieren) — sonst korruptieren
                // Multi-Byte-Zeichen an Chunk-Grenzen (A2).
                using var messageStream = new MemoryStream();
                WebSocketReceiveResult result;

                do
                {
                    result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), context.RequestAborted);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                        return;
                    }

                    if (result.MessageType == WebSocketMessageType.Text && result.Count > 0)
                    {
                        messageStream.Write(buffer, 0, result.Count);

                        if (messageStream.Length > MaxMessageSize)
                        {
                            await subscriptions.EnqueueSendAsync(JsonSerializer.Serialize(new { code = 400, data = "Message too large." }, _jsonOptions));
                            return;
                        }
                    }
                }
                while (!result.EndOfMessage);

                if (messageStream.Length == 0)
                    continue;

                var message = Encoding.UTF8.GetString(messageStream.GetBuffer(), 0, (int)messageStream.Length);
                if (string.IsNullOrWhiteSpace(message))
                    continue;

                await ProcessMessageAsync(context, webSocket, message, subscriptions);
            }
        }
        finally
        {
            // Auto-Cleanup: alle Subscriptions disposed beim Disconnect.
            await subscriptions.DisposeAsync();
        }
    }

    private async Task ProcessMessageAsync(HttpContext context, WebSocket webSocket, string message, TrameSubscriptionManager subscriptions)
    {
        try
        {
            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;

            // Phase 3: Subscribe/Unsubscribe-Erkennung (kind-Feld). Ohne kind → Call (v1.0-Verhalten).
            string? kind = null;
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.Name.Equals("kind", StringComparison.OrdinalIgnoreCase))
                    {
                        kind = prop.Value.GetString();
                        break;
                    }
                }
            }

            if (kind == "subscribe")
            {
                var request = JsonSerializer.Deserialize<TrameRequest>(message, _jsonOptions);
                if (request == null) { await subscriptions.EnqueueSendAsync(JsonSerializer.Serialize(new { code = 400, data = "Invalid subscribe request." }, _jsonOptions)); return; }
                var response = await subscriptions.HandleSubscribeAsync(request, context, context.RequestAborted);
                if (response != null)
                {
                    if (string.IsNullOrEmpty(response.Id)) response.Id = request.Id ?? string.Empty;
                    await subscriptions.EnqueueSendAsync(JsonSerializer.Serialize(response, _jsonOptions));
                }
                return;
            }

            if (kind == "unsubscribe")
            {
                string? subId = null, reqId = null;
                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.Name.Equals("subscriptionId", StringComparison.OrdinalIgnoreCase)) subId = prop.Value.GetString();
                    else if (prop.Name.Equals("id", StringComparison.OrdinalIgnoreCase)) reqId = prop.Value.GetString();
                }
                if (string.IsNullOrEmpty(subId)) { await subscriptions.EnqueueSendAsync(JsonSerializer.Serialize(new { code = 400, data = "unsubscribe requires subscriptionId." }, _jsonOptions)); return; }
                var response = await subscriptions.HandleUnsubscribeAsync(subId!, reqId, context.RequestAborted);
                if (response != null)
                {
                    if (string.IsNullOrEmpty(response.Id)) response.Id = reqId ?? string.Empty;
                    await subscriptions.EnqueueSendAsync(JsonSerializer.Serialize(response, _jsonOptions));
                }
                return;
            }

            // Calls (ohne kind-Feld) — bestehendes v1.0-Verhalten.
            object? response2;

            // Multi- vs. Single-Request erkennen. JsonElement.TryGetProperty ist
            // case-sensitiv — ein C#-Client ohne CamelCase-Policy schickt PascalCase
            // ("Requests"/"Mode"), ein JS/TS-Client camelCase ("requests"/"mode").
            // Case-insensitiv erkennen, sonst wird jeder Batch als Single (Controller
            // null → 404 mit leerer Id) behandelt und der Client kann die Antwort nicht
            // korrelieren (→ Endlos-Warte, s. TrameWebSocketClient-Timeout).
            bool hasRequests = false, hasMode = false;
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.Name.Equals("requests", StringComparison.OrdinalIgnoreCase))
                        hasRequests = true;
                    else if (prop.Name.Equals("mode", StringComparison.OrdinalIgnoreCase))
                        hasMode = true;
                }
            }

            if (hasRequests && hasMode)
            {
                var multiRequest = JsonSerializer.Deserialize<TrameMultiRequest>(message, _jsonOptions);
                if (multiRequest?.Requests == null)
                {
                    await subscriptions.EnqueueSendAsync(JsonSerializer.Serialize(new { code = 400, data = "Invalid multi request." }, _jsonOptions));
                    return;
                }

                // Batch-Cap-Gate (North-Bound-Härtung F4.1): frühes 400 statt Fan-Out-DoS.
                // Quelle ist ITrameCore (TrameOptions → Invoker → Interface → Transporte).
                if (_trameCore.MaximumBatchSize > 0 && multiRequest.Requests.Count > _trameCore.MaximumBatchSize)
                {
                    await subscriptions.EnqueueSendAsync(JsonSerializer.Serialize(new { code = 400, data = $"Batch exceeds MaximumBatchSize ({_trameCore.MaximumBatchSize})." }, _jsonOptions));
                    return;
                }

                response2 = await _trameCore.InvokeDi(multiRequest.Requests, context, multiRequest.Mode, context.RequestAborted);
            }
            else
            {
                var request = JsonSerializer.Deserialize<TrameRequest>(message, _jsonOptions);
                if (request == null)
                {
                    await subscriptions.EnqueueSendAsync(JsonSerializer.Serialize(new { code = 400, data = "Invalid request." }, _jsonOptions));
                    return;
                }

                response2 = await _trameCore.InvokeDi(request, context, context.RequestAborted);
            }

            // Hotfix 1.1.1: Alle Sends über den gemeinsamen Send-Channel des SubscriptionManagers
            // leiten — verhindert konkurrierende WebSocket.SendAsync-Aufrufe zwischen
            // Call-Responses (Middleware-Thread) und Event-Frames (Pump-Tasks).
            var json = JsonSerializer.Serialize(response2, _jsonOptions);
            await subscriptions.EnqueueSendAsync(json);
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "Failed to parse WebSocket message as JSON.");
            await subscriptions.EnqueueSendAsync(JsonSerializer.Serialize(new { code = 400, data = "Invalid JSON in request." }, _jsonOptions));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error processing WebSocket message.");
            await subscriptions.EnqueueSendAsync(JsonSerializer.Serialize(new { code = 400, data = "Internal server error." }, _jsonOptions));
        }
    }
}
