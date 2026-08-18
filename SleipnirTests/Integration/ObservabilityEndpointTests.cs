using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using SleipnirHub.Extensions;
using SleipnirRest;
using SleipnirTelemetry;
using SleipnirWebSocket;
using Xunit;

namespace SleipnirTests.Integration;

/// <summary>
/// Integrationstests für die Observability-Endpunkte:
/// <list type="bullet">
/// <item><c>GET /api/sleipnir/observability</c> — JSON-Snapshot (opt-in via
/// <see cref="SleipnirOptions.EnableObservability"/>, RequireAuth-gated wie /discovery).</item>
/// <item><c>GET /api/sleipnir/metrics</c> — Prometheus-Text-Scrape (opt-in via
/// <c>AddSleipnirPrometheusMetrics</c> + <c>UseSleipnirPrometheusScrapingEndpoint</c>,
/// RequireAuth-gated).</item>
/// </list>
/// Baut pro Test einen echten in-prozess Kestrel-Host (wie <see cref="TransportTestFixture"/>),
/// damit die Pipeline-Extensions und das Test-Auth-Schema voll zusammenwirken. Das
/// <c>TestAuthHandler</c>-Scheme validiert den Bearer <c>valid-token</c>.
/// </summary>
public class ObservabilityEndpointTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Baut einen in-prozess Kestrel-Host mit den übergebenen Options. <paramref name="wirePrometheus"/>
    /// schaltet <c>AddSleipnirPrometheusMetrics</c>/<c>UseSleipnirPrometheusScrapingEndpoint</c> dazu.
    /// </summary>
    private static async Task<(WebApplication app, HttpClient client)> BuildHostAsync(
        SleipnirOptions options, bool wirePrometheus)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Services.AddSleipnir(options);

        // Test-only Auth: Bearer "valid-token" → authentifizierter Principal (Rolle Admin).
        builder.Services.AddAuthentication("Test")
            .AddScheme<TestAuthOptions, TestAuthHandler>("Test", _ => { });
        builder.Services.AddAuthorization();

        if (wirePrometheus)
            builder.Services.AddSleipnirPrometheusMetrics();

        var app = builder.Build();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        if (wirePrometheus)
            app.UseSleipnirPrometheusScrapingEndpoint("/api/sleipnir/metrics", requireAuth: true);
        app.UseSleipnir();
        app.UseWebSockets();
        app.UseSleipnirWebSocket("/sleipnirws");
        // Low-level endpoint mapping (not MapSleipnir, which also maps the Developer UI).
        app.MapSleipnirEndpoints("/api/sleipnir",
            enableObservability: options.EnableObservability,
            signalREnabled: options.UseSignalR);

        await app.StartAsync();
        var baseUrl = app.Urls.First().TrimEnd('/') + "/";
        var client = new HttpClient { BaseAddress = new Uri(baseUrl) };
        return (app, client);
    }

    private static HttpRequestMessage Authed(HttpMethod method, string uri) => new(method, uri)
    {
        Headers = { Authorization = new AuthenticationHeaderValue("Bearer", TestAuthHandler.ValidToken) }
    };

    // ─── /observability ───────────────────────────────────────────────────────

    [Fact]
    public async Task Observability_RequireAuth_Unauthenticated_Returns401()
    {
        var (app, client) = await BuildHostAsync(new SleipnirOptions
        {
            EnableObservability = true,
            RequireAuthentication = true,
        }, wirePrometheus: false);
        await using var _ = app;
        using var __ = client;

        var res = await client.GetAsync("/api/sleipnir/observability");
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Observability_RequireAuth_Authenticated_Returns200_AndSnapshotDto()
    {
        var (app, client) = await BuildHostAsync(new SleipnirOptions
        {
            EnableObservability = true,
            RequireAuthentication = true,
        }, wirePrometheus: false);
        await using var _ = app;
        using var __ = client;

        var res = await client.SendAsync(Authed(HttpMethod.Get, "/api/sleipnir/observability"));
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        res.Content.Headers.ContentType!.MediaType.Should().Contain("json");

        var body = await res.Content.ReadAsStringAsync();
        var snap = JsonSerializer.Deserialize<JsonElement>(body, JsonOpts);

        snap.GetProperty("transports").GetProperty("rest").GetBoolean().Should().BeTrue();
        snap.GetProperty("transports").GetProperty("webSocket").GetBoolean().Should().BeTrue();
        // SignalR is off in this host (UseSignalR not set) → false.
        snap.GetProperty("transports").GetProperty("signalR").GetBoolean().Should().BeFalse();

        snap.GetProperty("activeConnections").GetInt32().Should().BeGreaterOrEqualTo(0);
        snap.GetProperty("activeSubscriptions").GetInt32().Should().BeGreaterOrEqualTo(0);
        snap.GetProperty("eventDroppedTotal").GetInt64().Should().BeGreaterOrEqualTo(0);
        snap.GetProperty("callCount").GetInt64().Should().BeGreaterOrEqualTo(0);
        snap.GetProperty("errorCount").GetInt64().Should().BeGreaterOrEqualTo(0);
        snap.GetProperty("batchCount").GetInt64().Should().BeGreaterOrEqualTo(0);
        snap.GetProperty("uptimeMs").GetInt64().Should().BeGreaterOrEqualTo(0);
    }

    [Fact]
    public async Task Observability_Disabled_NotMapped_Returns404()
    {
        // EnableObservability=false (default) → /observability is not registered.
        var (app, client) = await BuildHostAsync(new SleipnirOptions
        {
            EnableObservability = false,
            RequireAuthentication = true,
        }, wirePrometheus: false);
        await using var _ = app;
        using var __ = client;

        var res = await client.SendAsync(Authed(HttpMethod.Get, "/api/sleipnir/observability"));
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Observability_NoRequireAuth_OpenWithoutToken_Returns200()
    {
        // RequireAuthentication=false → the transport-level gate is open; the snapshot is
        // reachable without a bearer (per-method auth remains the invoker's job).
        var (app, client) = await BuildHostAsync(new SleipnirOptions
        {
            EnableObservability = true,
            RequireAuthentication = false,
        }, wirePrometheus: false);
        await using var _ = app;
        using var __ = client;

        var res = await client.GetAsync("/api/sleipnir/observability");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ─── /metrics (Prometheus scrape) ─────────────────────────────────────────

    [Fact]
    public async Task Metrics_RequireAuth_Unauthenticated_Returns401()
    {
        var (app, client) = await BuildHostAsync(new SleipnirOptions
        {
            EnableObservability = true,
            RequireAuthentication = true,
        }, wirePrometheus: true);
        await using var _ = app;
        using var __ = client;

        var res = await client.GetAsync("/api/sleipnir/metrics");
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Metrics_RequireAuth_Authenticated_ReturnsPrometheusText()
    {
        var (app, client) = await BuildHostAsync(new SleipnirOptions
        {
            EnableObservability = true,
            RequireAuthentication = true,
        }, wirePrometheus: true);
        await using var _ = app;
        using var __ = client;

        var res = await client.SendAsync(Authed(HttpMethod.Get, "/api/sleipnir/metrics"));
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadAsStringAsync();
        // Prometheus text exposition format exposes instruments as sleipnir_* (dots → underscores).
        body.Should().Contain("sleipnir_");
        // The connection/subscription gauges are part of the meter and must appear once a
        // scrape reads them (their names map to sleipnir_ws_connections / sleipnir_subscriptions_active).
        body.Should().Contain("sleipnir_ws_connections");
        body.Should().Contain("sleipnir_subscriptions_active");
    }
}