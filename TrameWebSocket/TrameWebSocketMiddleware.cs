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
/// Middleware for a lightweight, custom WebSocket transport for Trame.
/// Uses only standard WebSockets (RFC 6455) and JSON, so clients in
/// Java, JavaScript, Python or other languages can integrate easily.
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
            // Relaxed encoder: Data (JsonElement) is serialized raw — no
            // double-wrapping; UnsafeRelaxed also prevents `"`-escaping
            // in the envelope (ExposedDependencies strings, error messages).
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            // Write-only converter: DataBytes written raw to the wire via WriteRawValue →
            // no JsonDocument tree on the server (single-pass optimization).
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

        // North-bound default-deny (security audit F9.1): reject the upgrade before the
        // socket is created when RequireAuthentication is on and the caller is
        // unauthenticated. WS has no per-method opt-out ([TrameAnonymous] only takes effect
        // in the invoker gate on REST); here the connection is the trust boundary.
        // Authentication must have populated HttpContext.User upstream
        // (reverse proxy / token middleware).
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
        // Phase 3: per-connection subscription manager for events.
        var subscriptions = new TrameSubscriptionManager(webSocket, _trameCore, _logger);
        try
        {
            var buffer = new byte[1024 * 4];

            while (webSocket.State == WebSocketState.Open)
            {
                // Accumulate bytes (do not decode per chunk) — otherwise multi-byte
                // characters get corrupted at chunk boundaries (A2).
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
            // Auto-cleanup: dispose all subscriptions on disconnect.
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

            // Phase 3: subscribe/unsubscribe detection (kind field). Without kind → call (v1.0 behavior).
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

            // Calls (without kind field) — existing v1.0 behavior.
            object? response2;

            // Detect multi- vs. single-request. JsonElement.TryGetProperty is
            // case-sensitive — a C# client without a camelCase policy sends PascalCase
            // ("Requests"/"Mode"), a JS/TS client camelCase ("requests"/"mode").
            // Detect case-insensitively, otherwise every batch is treated as a single
            // request (Controller null → 404 with empty Id) and the client cannot
            // correlate the response (→ endless wait, see TrameWebSocketClient timeout).
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

                // Batch-cap gate (north-bound hardening F4.1): early 400 instead of fan-out DoS.
                // Source is ITrameCore (TrameOptions → Invoker → Interface → transports).
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

            // Hotfix 1.1.1: route all sends through the subscription manager's shared send
            // channel — prevents concurrent WebSocket.SendAsync calls between
            // call responses (middleware thread) and event frames (pump tasks).
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
