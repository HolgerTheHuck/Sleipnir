using System.Collections.Concurrent;
using System.Text.Json;
using TrameCommon.Models;

namespace TrameClient.Trame;

/// <summary>
/// In-Memory-Test-Double für <see cref="ITrameClient"/> (Phase 3, Schritt 5 — Client-Test-Doubles).
/// Erlaubt Konsumenten, ihren Trame-Client-Code zu unit-testen, ohne einen laufenden Server.
/// Registriert Handler-Delegates pro <c>Controller.Method</c>; ein <see cref="Call"/> ruft den
/// Handler synchron auf und gibt die Response zurück. <see cref="CallBinary"/> wird nicht
/// unterstützt (wirft <see cref="NotSupportedException"/>). Siehe
/// <c>docs/design/phase-3-events.md</c> Schritt 5.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nicht für Produktion</b> — keine echte Verbindung, keine Serialisierung, keine Transports.
/// Nur für Unit-Tests: der Konsument registriert Handler (z. B. <c>mock.On("Customer","GetById",
/// req => TrameResults.Ok(new Customer{...}))</c>), und sein Client-Code, der
/// <c>ITrameClient.Call</c> aufruft, wird gegen diese Handler getestet.
/// </para>
/// <para>
/// Für typisierte generierte Clients: der generierte Client baut auf <c>ITrameClient</c> auf
/// (oder einem <c>ITrameClient</c>-Mock). Eine Test-Instanz von <c>ITrameClient</c> (z. B. diese
/// Klasse oder ein Moq-Setup) reicht, um generierte Client-Methoden zu testen.
/// </para>
/// </remarks>
public sealed class TrameInMemoryClient : ITrameClient
{
    private readonly ConcurrentDictionary<string, Func<TrameRequest, CancellationToken, TrameResponse?>> _handlers = new();

    /// <summary>
    /// Registriert einen Handler für <c>Controller.Method</c>. Der Handler bekommt den Request
    /// und das CancellationToken und gibt eine <see cref="TrameResponse"/> zurück.
    /// </summary>
    public TrameInMemoryClient On(string controller, string method,
        Func<TrameRequest, CancellationToken, TrameResponse?> handler)
    {
        _handlers[$"{controller}.{method}"] = handler;
        return this;
    }

    /// <summary>Convenience: Handler, der ein Ergebnis-Objekt zurückgibt (200 OK).</summary>
    public TrameInMemoryClient On<T>(string controller, string method, Func<TrameRequest, CancellationToken, T> handler)
    {
        _handlers[$"{controller}.{method}"] = (req, ct) =>
        {
            var result = handler(req, ct);
            return new TrameResponse
            {
                Code = 200,
                DataBytes = JsonSerializer.SerializeToUtf8Bytes(result),
                Id = req.Id,
            };
        };
        return this;
    }

    /// <summary>Convenience: Handler, der einen Fehler zurückgibt.</summary>
    public TrameInMemoryClient OnError(string controller, string method, int code, string message)
    {
        _handlers[$"{controller}.{method}"] = (req, ct) => new TrameResponse
        {
            Code = code,
            Error = new TrameError { Code = code, Message = message },
            Id = req.Id,
        };
        return this;
    }

    public Task<TrameResponse?> Call(TrameRequest request, CancellationToken ct = default)
    {
        var key = $"{request.Controller}.{request.Method}";
        if (!_handlers.TryGetValue(key, out var handler))
            return Task.FromResult<TrameResponse?>(new TrameResponse
            {
                Code = 404,
                Error = new TrameError { Code = 404, Message = $"No handler registered for '{key}'." },
                Id = request.Id,
            });

        return Task.FromResult(handler(request, ct));
    }

    public async Task<T?> Call<T>(TrameRequest? request, CancellationToken ct = default)
    {
        if (request == null) return default;
        var response = await Call(request, ct);
        if (response == null || !response.IsSuccess) return default;
        if (response.DataBytes == null) return default;
        return JsonSerializer.Deserialize<T>(response.DataBytes);
    }

    public Task<IEnumerable<TrameResponse?>?> Call(TrameMultiRequest? request, CancellationToken ct = default)
    {
        if (request?.Requests == null) return Task.FromResult<IEnumerable<TrameResponse?>?>([]);
        var results = new List<TrameResponse?>();
        foreach (var req in request.Requests)
            results.Add(Call(req, ct).Result);
        return Task.FromResult<IEnumerable<TrameResponse?>?>(results);
    }

    public Task<byte[]?> CallBinary(TrameRequest? request, CancellationToken ct = default)
        => throw new NotSupportedException("TrameInMemoryClient does not support CallBinary — use a real transport for binary tests.");
}