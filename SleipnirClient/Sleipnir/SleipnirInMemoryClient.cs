using System.Collections.Concurrent;
using System.Text.Json;
using SleipnirCommon.Models;

namespace SleipnirClient.Sleipnir;

/// <summary>
/// In-Memory-Test-Double für <see cref="ISleipnirClient"/> (Phase 3, Schritt 5 — Client-Test-Doubles).
/// Erlaubt Konsumenten, ihren Sleipnir-Client-Code zu unit-testen, ohne einen laufenden Server.
/// Registriert Handler-Delegates pro <c>Controller.Method</c>; ein <see cref="Call"/> ruft den
/// Handler synchron auf und gibt die Response zurück. <see cref="CallBinary"/> wird nicht
/// unterstützt (wirft <see cref="NotSupportedException"/>). Siehe
/// <c>docs/design/phase-3-events.md</c> Schritt 5.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nicht für Produktion</b> — keine echte Verbindung, keine Serialisierung, keine Transports.
/// Nur für Unit-Tests: der Konsument registriert Handler (z. B. <c>mock.On("Customer","GetById",
/// req => SleipnirResults.Ok(new Customer{...}))</c>), und sein Client-Code, der
/// <c>ISleipnirClient.Call</c> aufruft, wird gegen diese Handler getestet.
/// </para>
/// <para>
/// Für typisierte generierte Clients: der generierte Client baut auf <c>ISleipnirClient</c> auf
/// (oder einem <c>ISleipnirClient</c>-Mock). Eine Test-Instanz von <c>ISleipnirClient</c> (z. B. diese
/// Klasse oder ein Moq-Setup) reicht, um generierte Client-Methoden zu testen.
/// </para>
/// </remarks>
public sealed class SleipnirInMemoryClient : ISleipnirClient
{
    private readonly ConcurrentDictionary<string, Func<SleipnirRequest, CancellationToken, SleipnirResponse?>> _handlers = new();

    /// <summary>
    /// Registriert einen Handler für <c>Controller.Method</c>. Der Handler bekommt den Request
    /// und das CancellationToken und gibt eine <see cref="SleipnirResponse"/> zurück.
    /// </summary>
    public SleipnirInMemoryClient On(string controller, string method,
        Func<SleipnirRequest, CancellationToken, SleipnirResponse?> handler)
    {
        _handlers[$"{controller}.{method}"] = handler;
        return this;
    }

    /// <summary>Convenience: Handler, der ein Ergebnis-Objekt zurückgibt (200 OK).</summary>
    public SleipnirInMemoryClient On<T>(string controller, string method, Func<SleipnirRequest, CancellationToken, T> handler)
    {
        _handlers[$"{controller}.{method}"] = (req, ct) =>
        {
            var result = handler(req, ct);
            return new SleipnirResponse
            {
                Code = 200,
                DataBytes = JsonSerializer.SerializeToUtf8Bytes(result),
                Id = req.Id,
            };
        };
        return this;
    }

    /// <summary>Convenience: Handler, der einen Fehler zurückgibt.</summary>
    public SleipnirInMemoryClient OnError(string controller, string method, int code, string message)
    {
        _handlers[$"{controller}.{method}"] = (req, ct) => new SleipnirResponse
        {
            Code = code,
            Error = new SleipnirError { Code = code, Message = message },
            Id = req.Id,
        };
        return this;
    }

    public Task<SleipnirResponse?> Call(SleipnirRequest request, CancellationToken ct = default)
    {
        var key = $"{request.Controller}.{request.Method}";
        if (!_handlers.TryGetValue(key, out var handler))
            return Task.FromResult<SleipnirResponse?>(new SleipnirResponse
            {
                Code = 404,
                Error = new SleipnirError { Code = 404, Message = $"No handler registered for '{key}'." },
                Id = request.Id,
            });

        return Task.FromResult(handler(request, ct));
    }

    public async Task<T?> Call<T>(SleipnirRequest? request, CancellationToken ct = default)
    {
        if (request == null) return default;
        var response = await Call(request, ct);
        if (response == null || !response.IsSuccess) return default;
        if (response.DataBytes == null) return default;
        return JsonSerializer.Deserialize<T>(response.DataBytes);
    }

    public Task<IEnumerable<SleipnirResponse?>?> Call(SleipnirMultiRequest? request, CancellationToken ct = default)
    {
        if (request?.Requests == null) return Task.FromResult<IEnumerable<SleipnirResponse?>?>([]);
        var results = new List<SleipnirResponse?>();
        foreach (var req in request.Requests)
            results.Add(Call(req, ct).Result);
        return Task.FromResult<IEnumerable<SleipnirResponse?>?>(results);
    }

    public Task<byte[]?> CallBinary(SleipnirRequest? request, CancellationToken ct = default)
        => throw new NotSupportedException("SleipnirInMemoryClient does not support CallBinary — use a real transport for binary tests.");

    public Task<SleipnirSubscription<T>> SubscribeAsync<T>(SleipnirRequest? request, ResumePolicy? resumePolicy = null, CancellationToken ct = default)
        => throw new NotSupportedException("SleipnirInMemoryClient does not support event subscriptions — use a real transport (WebSocket / SSE / SignalR) for event tests.");

    public Task<SleipnirSubscription<T>> ResumeAsync<T>(string subscriptionId, long lastEventId, ResumePolicy? resumePolicy = null, CancellationToken ct = default)
        => throw new NotSupportedException("SleipnirInMemoryClient does not support event subscriptions — use a real transport (WebSocket / SSE / SignalR) for event tests.");
}