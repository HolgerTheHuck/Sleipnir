using System.Collections.Concurrent;
using System.Text.Json;
using SleipnirCommon.Models;

namespace SleipnirClient.Sleipnir;

/// <summary>
/// In-memory test double for <see cref="ISleipnirClient"/> (Phase 3, step 5 — client test doubles).
/// Lets consumers unit-test their Sleipnir client code without a running server. Handlers are
/// registered per <c>Controller.Method</c>; a <see cref="Call"/> invokes the handler synchronously
/// and returns the response. <see cref="CallBinary"/> and event subscriptions are not supported
/// (throw <see cref="NotSupportedException"/>). See <c>docs/design/phase-3-events.md</c> step 5.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not for production</b> — no real connection, no serialization, no transports. Unit tests
/// only: the consumer registers handlers (e.g. <c>mock.On("Customer","GetById",
/// req => SleipnirResults.Ok(new Customer{...}))</c>) and tests the client code that calls
/// <c>ISleipnirClient.Call</c> against those handlers.
/// </para>
/// <para>
/// For typed generated clients: the generated client builds on <c>ISleipnirClient</c> (or an
/// <c>ISleipnirClient</c> mock). A test <c>ISleipnirClient</c> instance (this class or a Moq setup)
/// suffices to test generated client methods.
/// </para>
/// <para>
/// <b>Batch semantics diverge from every real transport (audit F6):</b> <c>SleipnirMultiRequest.Mode</c>
/// is ignored (requests always run sequentially in order), and <c>@alias</c> placeholders are not
/// resolved. A real transport honors <c>Mode</c> and resolves aliases server-side.
/// </para>
/// </remarks>
public sealed class SleipnirInMemoryClient : ISleipnirClient
{
    private readonly ConcurrentDictionary<string, Func<SleipnirRequest, CancellationToken, SleipnirResponse?>> _handlers = new();

    /// <summary>
    /// Registers a handler for <c>Controller.Method</c>. The handler receives the request and the
    /// cancellation token and returns a <see cref="SleipnirResponse"/>.
    /// </summary>
    public SleipnirInMemoryClient On(string controller, string method,
        Func<SleipnirRequest, CancellationToken, SleipnirResponse?> handler)
    {
        _handlers[$"{controller}.{method}"] = handler;
        return this;
    }

    /// <summary>Convenience: a handler that returns a result object (200 OK).</summary>
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

    /// <summary>Convenience: a handler that returns an error.</summary>
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

    public async Task<IEnumerable<SleipnirResponse?>?> Call(SleipnirMultiRequest? request, CancellationToken ct = default)
    {
        if (request?.Requests == null) return [];
        // Limitation (audit F6 — documented, not fixed): batch semantics diverge from any real
        // transport. `Mode` is IGNORED (requests always run sequentially in order, Serial-like),
        // and `@alias` placeholders are NOT resolved (no dependency chaining). If a handler
        // needs alias-driven arguments, register the handler against the resolved request.
        var results = new List<SleipnirResponse?>();
        foreach (var req in request.Requests)
            results.Add(await Call(req, ct));
        return results;
    }

    public Task<byte[]?> CallBinary(SleipnirRequest? request, CancellationToken ct = default)
        => throw new NotSupportedException("SleipnirInMemoryClient does not support CallBinary — use a real transport for binary tests.");

    public Task<SleipnirSubscription<T>> SubscribeAsync<T>(SleipnirRequest? request, ResumePolicy? resumePolicy = null, CancellationToken ct = default)
        => throw new NotSupportedException("SleipnirInMemoryClient does not support event subscriptions — use a real transport (WebSocket / SSE / SignalR) for event tests.");

    public Task<SleipnirSubscription<T>> ResumeAsync<T>(string subscriptionId, long lastEventId, ResumePolicy? resumePolicy = null, CancellationToken ct = default)
        => throw new NotSupportedException("SleipnirInMemoryClient does not support event subscriptions — use a real transport (WebSocket / SSE / SignalR) for event tests.");
}