using System.Linq;
using System.Security.Claims;
using System.Text.Encodings.Web;
using SleipnirClient.Sleipnir;
using SleipnirHub.Extensions;
using SleipnirRest;
using SleipnirWebSocket;
using SleipnirTests.Fixtures;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Xunit;

namespace SleipnirTests.Integration;

/// <summary>
/// Echter, in-Prozess lauschender Kestrel-Host mit allen Sleipnir-Transports
/// (REST/WS/SignalR) auf einem Zufallsport. <c>ClientWebSocket</c> und
/// <c>HubConnection</c> können nicht gegen einen In-Memory-TestServer sprechen,
/// daher ein realer Lauscher. <c>TestInvokerController</c> wird per Auto-Discovery
/// registriert (die Test-Assembly ist im Test-Prozess geladen).
/// </summary>
public class TransportTestFixture : IAsyncLifetime
{
    private WebApplication _app = null!;
    public string BaseUrl { get; private set; } = "";

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Services.AddSleipnir(new SleipnirOptions
        {
            EnableDetailedErrors = true,
            UseSignalR = true,
            UseMessagePack = true,
            MaximumParallelInvocationsPerClient = 100,
            RateLimitPermitLimit = 0 // aus (dev) -> keine Rate-Limit-Policy nötig
        });

        // Test-only Auth für den Bearer/JWT-Nachweis (A4).
        builder.Services.AddAuthentication("Test")
            .AddScheme<TestAuthOptions, TestAuthHandler>("Test", _ => { });
        builder.Services.AddAuthorization();

        var app = builder.Build();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseSleipnir();
        app.MapSleipnirEndpoints("/api/sleipnir");
        app.UseWebSockets();
        app.UseSleipnirWebSocket("/sleipnirws");
        app.MapHub<global::SleipnirHub.Hub.SleipnirHub>("/sleipnirhub");

        await app.StartAsync();
        _app = app;
        BaseUrl = app.Urls.First().TrimEnd('/') + "/";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    public SleipnirRestJsonClient CreateRestClient() => new(BaseUrl);
    public SleipnirWebSocketClient CreateWsClient() => new(BaseUrl);
    public SleipnirSignalrClient CreateSignalrClient(string? bearer = null, bool useMessagePack = true)
        => new(BaseUrl, bearer, useMessagePack: useMessagePack);
}

/// <summary>
/// Minimale Test-Auth-Scheme: validiert einen Bearer-Token gegen
/// <see cref="ValidToken"/> und setzt einen authentifizierten Principal mit Rolle
/// "Admin". Nur für die Bearer-Übermittlung im Integrationstest (A4).
/// </summary>
public class TestAuthOptions : AuthenticationSchemeOptions { }

public class TestAuthHandler : AuthenticationHandler<TestAuthOptions>
{
    public const string ValidToken = "valid-token";

    public TestAuthHandler(IOptionsMonitor<TestAuthOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : base(options, logger, encoder) { }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var auth = Request.Headers[HeaderNames.Authorization].ToString();
        if (string.IsNullOrEmpty(auth) || !auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(AuthenticateResult.NoResult());

        var token = auth["Bearer ".Length..].Trim();
        if (token != ValidToken)
            return Task.FromResult(AuthenticateResult.Fail("Invalid token."));

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, "test"),
            new Claim(ClaimTypes.Role, "Admin")
        };
        var identity = new ClaimsIdentity(claims, "Test");
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, "Test");
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}