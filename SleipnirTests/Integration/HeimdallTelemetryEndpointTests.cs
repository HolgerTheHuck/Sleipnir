using System.Net;
using System.Text.Json;
using FluentAssertions;
using SleipnirHub.Extensions;
using SleipnirTelemetryHeimdall;
using Xunit;

namespace SleipnirTests.Integration;

/// <summary>
/// Integration tests for the built-in Heimdall telemetry backend
/// (<c>Sleipnir.Telemetry.Heimdall</c>). Verifies that <c>AddSleipnirHeimdallTelemetry</c> +
/// <c>MapSleipnirHeimdall</c> bring up an embedded Heimdall SQLite sink with the Blazor
/// dashboard and the PromQL HTTP API live under <c>/otel</c>, and that the Sleipnir Meter is
/// subscribed (a PromQL instant query returns a Prometheus <c>success</c> envelope).
/// </summary>
/// <remarks>
/// Each test builds a real in-process Kestrel host with a unique temp SQLite path so parallel
/// runs do not contend on the DB file. Heimdall endpoints are unauthenticated by default, so no
/// test-auth scheme is wired. The span→metric end-to-end query (make a Sleipnir call, then query
/// Heimdall for the <c>SleipnirCall</c> span) is deliberately out of scope here — the assertions
/// prove the wiring is live, not that a specific call produced a specific span.
/// </remarks>
public class HeimdallTelemetryEndpointTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Builds an in-process Kestrel host with Sleipnir + the Heimdall telemetry backend,
    /// using <paramref name="dataPath"/> as the SQLite sink path.
    /// </summary>
    private static async Task<(WebApplication app, HttpClient client)> BuildHostAsync(string dataPath)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Environment.EnvironmentName = "Development"; // detailed error pages for diagnosis
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Services.AddSleipnir(new SleipnirOptions());
        builder.Services.AddSleipnirHeimdallTelemetry(o =>
        {
            o.DataPath = dataPath;
            o.ServiceName = "SleipnirHeimdallTest";
        });

        var app = builder.Build();
        app.UseStaticFiles();
        app.UseRouting();
        app.UseSleipnir();
        app.MapSleipnirHeimdall("/otel");

        await app.StartAsync();
        var baseUrl = app.Urls.First().TrimEnd('/') + "/";
        var client = new HttpClient { BaseAddress = new Uri(baseUrl) };
        return (app, client);
    }

    [Fact]
    public async Task Heimdall_Prometheus_BuildInfoEndpoint_Responds()
    {
        var dataPath = TempDbPath();
        var (app, client) = await BuildHostAsync(dataPath);
        await using var _ = app;
        using var __ = client;
        using var ___ = TempDbCleanup(dataPath);

        // The Prometheus HTTP API buildinfo endpoint proves the Heimdall PromQL engine + its
        // endpoint mapping are live under /otel/api/v1/*.
        var res = await client.GetAsync("/otel/api/v1/status/buildinfo");
        var body = await res.Content.ReadAsStringAsync();
        if (res.StatusCode != HttpStatusCode.OK)
            Assert.Fail($"buildinfo {res.StatusCode}: {body}");
        res.Content.Headers.ContentType!.MediaType.Should().Contain("json");

        var doc = JsonSerializer.Deserialize<JsonElement>(body, JsonOpts);
        // Prometheus HTTP API envelope: { "status": "success", "data": { ... } }.
        doc.GetProperty("status").GetString().Should().Be("success");
    }

    [Fact]
    public async Task Heimdall_PromQuery_ReturnsSuccessEnvelope()
    {
        var dataPath = TempDbPath();
        var (app, client) = await BuildHostAsync(dataPath);
        await using var _ = app;
        using var __ = client;
        using var ___ = TempDbCleanup(dataPath);

        // An instant query for a Sleipnir metric returns a Prometheus success envelope (empty
        // result vector is still success), proving the Sleipnir Meter is subscribed to the
        // Heimdall metric source and the PromQL engine serves queries.
        var res = await client.GetAsync("/otel/api/v1/query?query=sleipnir_call_count");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadAsStringAsync();
        var doc = JsonSerializer.Deserialize<JsonElement>(body, JsonOpts);
        doc.GetProperty("status").GetString().Should().Be("success");
    }

    [Fact]
    public async Task Heimdall_Dashboard_RespondsUnderOtelPrefix()
    {
        var dataPath = TempDbPath();
        var (app, client) = await BuildHostAsync(dataPath);
        await using var _ = app;
        using var __ = client;
        using var ___ = TempDbCleanup(dataPath);

        // The Blazor SSR dashboard is mapped at /otel. The root route renders the index page
        // (HTML), independent of static-asset serving, so it must respond 200 (not 404).
        var res = await client.GetAsync("/otel");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static string TempDbPath() =>
        Path.Combine(Path.GetTempPath(), $"heimdall-test-{Guid.NewGuid():N}.db");

    /// <summary>A disposable that deletes the temp SQLite file (best-effort) on disposal.</summary>
    private static IDisposable TempDbCleanup(string path) => new TempFileDeleter(path);

    private sealed class TempFileDeleter : IDisposable
    {
        private readonly string _path;
        public TempFileDeleter(string path) => _path = path;
        public void Dispose()
        {
            try { if (File.Exists(_path)) File.Delete(_path); }
            catch { /* best-effort; the sink is disposed on host shutdown */ }
        }
    }
}