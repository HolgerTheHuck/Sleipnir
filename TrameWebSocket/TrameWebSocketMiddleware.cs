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

    /// <summary>
    /// Builds a correlated, JSON-RPC-free error frame as a real <see cref="TrameResponse"/>
    /// (Code + <see cref="TrameError"/> + Id) instead of an anonymous <c>{ code, data }</c>.
    /// R3: the previous anonymous frames carried the message in <c>data</c> and omitted
    /// <c>id</c>/<c>error</c>, so a C# client could not correlate them (strict dispatcher
    /// dropped the response → hang) and never surfaced the message as a <see cref="TrameException"/>.
    /// The <see cref="TrameResponseJsonConverter"/> serializes this to
    /// <c>{"code":...,"id":"...","error":{"code":...,"message":"..."}}</c> in one pass.
    /// </summary>
    private static TrameResponse BuildErrorFrame(int code, string message, string? id) => new()
    {
        Code = code,
        Id = id ?? string.Empty,
        Error = new TrameError { Code = code, Message = message, RequestId = id },
    };

    /// <summary>
    /// Extracts the correlation id up-front from an already-parsed request document. For a
    /// single request / subscribe / unsubscribe this is the top-level <c>id</c>; for a multi
    /// request it is the first element's <c>id</c> (the client correlates the batch response
    /// array on its first element). Case-insensitive (a C# PascalCase client sends
    /// <c>Id</c>/<c>Requests</c>, a JS/TS client <c>id</c>/<c>requests</c>). Returns
    /// <c>null</c> when no id is present (e.g. a malformed/unparseable request).
    /// </summary>
    private static string? ExtractCorrelationId(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;

        string? topId = null;
        bool hasRequests = false;
        JsonElement requestsEl = default;
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Name.Equals("id", StringComparison.OrdinalIgnoreCase) && prop.Value.ValueKind == JsonValueKind.String)
                topId = prop.Value.GetString();
            else if (prop.Name.Equals("requests", StringComparison.OrdinalIgnoreCase))
            {
                hasRequests = true;
                requestsEl = prop.Value;
            }
        }

        if (!string.IsNullOrEmpty(topId)) return topId;

        // Multi: fall back to the first request's id.
        if (hasRequests && requestsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var first in requestsEl.EnumerateArray())
            {
                foreach (var prop in first.EnumerateObject())
                {
                    if (prop.Name.Equals("id", StringComparison.OrdinalIgnoreCase) && prop.Value.ValueKind == JsonValueKind.String)
                        return prop.Value.GetString();
                }
                break; // only the first element
            }
        }
        return null;
    }

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
                            await SendErrorAsync(subscriptions, 400, "Message too large.", null);
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
        // R3: extract the correlation id once, up-front, so every downstream error frame
        // (validation, batch-cap, catch-all) can carry it back to the awaiting caller.
        // Stays null when the JSON is unparseable — the catch-all then sends id="" (an
        // uncorrelated error, unavoidable for a malformed request).
        string? id = null;
        try
        {
            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;
            id = ExtractCorrelationId(root);

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
                if (request == null) { await SendErrorAsync(subscriptions, 400, "Invalid subscribe request.", id); return; }
                var response = await subscriptions.HandleSubscribeAsync(request, context, context.RequestAborted);
                if (response != null)
                {
                    if (string.IsNullOrEmpty(response.Id)) response.Id = request.Id ?? id ?? string.Empty;
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
                if (string.IsNullOrEmpty(subId)) { await SendErrorAsync(subscriptions, 400, "unsubscribe requires subscriptionId.", reqId ?? id); return; }
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
                    await SendErrorAsync(subscriptions, 400, "Invalid multi request.", id);
                    return;
                }

                // Batch-Cap-Gate (North-Bound-Härtung F4.1): frühes 400 statt Fan-Out-DoS.
                // Quelle ist ITrameCore (TrameOptions → Invoker → Interface → Transporte).
                if (_trameCore.MaximumBatchSize > 0 && multiRequest.Requests.Count > _trameCore.MaximumBatchSize)
                {
                    await SendErrorAsync(subscriptions, 400, $"Batch exceeds MaximumBatchSize ({_trameCore.MaximumBatchSize}).", id);
                    return;
                }

                response2 = await _trameCore.InvokeDi(multiRequest.Requests, context, multiRequest.Mode, context.RequestAborted);
            }
            else
            {
                var request = JsonSerializer.Deserialize<TrameRequest>(message, _jsonOptions);
                if (request == null)
                {
                    await SendErrorAsync(subscriptions, 400, "Invalid request.", id);
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
            // id is null here — a malformed request cannot be correlated. Send the frame so a
            // non-C# client (which manages its own matching) still gets a structured error.
            await SendErrorAsync(subscriptions, 400, "Invalid JSON in request.", id);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error processing WebSocket message.");
            // 500 (was 400): an unexpected/internal failure is a server error per the stable
            // error catalog, not a client bad-request. Keep the message generic — no leak.
            await SendErrorAsync(subscriptions, 500, "Internal server error.", id);
        }
    }

    /// <summary>
    /// Serializes a <see cref="BuildErrorFrame"/> via the shared send channel. Thin wrapper
    /// so every error site serializes identically (one pass, TrameResponseJsonConverter).
    /// </summary>
    private async Task SendErrorAsync(TrameSubscriptionManager subscriptions, int code, string message, string? id)
        => await subscriptions.EnqueueSendAsync(JsonSerializer.Serialize(BuildErrorFrame(code, message, id), _jsonOptions));
}
