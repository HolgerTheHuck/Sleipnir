using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SleipnirHub.Auth;
using SleipnirHub.Extensions;
using SleipnirServer;
using Xunit;

namespace SleipnirTests.Integration;

/// <summary>
/// U4 / decision 6.3: the opt-in <c>?access_token=</c> fallback
/// (<see cref="SleipnirOptions.AcceptAccessTokenQuery"/>) is honored on the WebSocket upgrade and
/// the SSE event endpoints only — never on ordinary REST requests — an existing
/// <c>Authorization</c> header wins, and the token is stripped from the query string so downstream
/// logging never sees it. Boots a real Kestrel host with the unified pipeline and the test bearer
/// scheme (<see cref="TestAuthHandler"/>: <c>Bearer valid-token</c>).
/// </summary>
/// <remarks>
/// Same collection as <see cref="TransportToggleTests"/>: <c>AddSleipnir</c> swaps the
/// process-global <c>SleipnirConnectionRegistry.Current</c>, which the telemetry gauge tests read.
/// </remarks>
[Collection("sleipnir-tracing")]
public class AccessTokenQueryTests
{
    private const string Token = TestAuthHandler.ValidToken;

    private sealed class Host : IAsyncDisposable
    {
        public string BaseUrl { get; }
        public List<(string Category, string Message)> Logs { get; }
        private readonly WebApplication _app;

        private Host(WebApplication app, string baseUrl, List<(string, string)> logs)
        {
            _app = app;
            BaseUrl = baseUrl;
            Logs = logs;
        }

        public static async Task<Host> StartAsync(bool acceptAccessTokenQuery, bool requireAuthentication = false)
        {
            var logs = new List<(string, string)>();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(new CaptureLoggerProvider(logs));
            builder.Logging.SetMinimumLevel(LogLevel.Trace);
            // Provider-specific rule: beats any category rule from configuration, so the
            // hosting diagnostics ("Request starting/finished") reach the capture.
            builder.Logging.AddFilter<CaptureLoggerProvider>(null, LogLevel.Trace);

            builder.Services.AddSleipnir(new SleipnirOptions
            {
                AcceptAccessTokenQuery = acceptAccessTokenQuery,
                RequireAuthentication = requireAuthentication,
            });
            builder.Services.AddAuthentication("Test")
                .AddScheme<TestAuthOptions, TestAuthHandler>("Test", _ => { });
            builder.Services.AddAuthorization();

            var app = builder.Build();
            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseSleipnirTransports();
            app.MapSleipnir();

            await app.StartAsync();
            return new Host(app, app.Urls.First().TrimEnd('/') + "/", logs);
        }

        public HttpClient CreateClient() => new() { BaseAddress = new Uri(BaseUrl) };

        public Uri WsUri(string? query = null)
            => new(BaseUrl.Replace("http://", "ws://") + "sleipnirws" + (query is null ? "" : "?" + query));

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private sealed class CaptureLoggerProvider(List<(string, string)> logs) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(categoryName, logs);
        public void Dispose() { }
    }

    private sealed class CaptureLogger(string category, List<(string, string)> logs) : ILogger
    {
        private sealed class NullScope : IDisposable { public void Dispose() { } }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => new NullScope();
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (logs) logs.Add((category, formatter(state, exception)));
        }
    }

    // ── WebSocket upgrade ────────────────────────────────────────────────────────

    [Fact]
    public async Task WebSocket_WithAccessTokenQuery_AuthenticatesTheConnection()
    {
        await using var host = await Host.StartAsync(acceptAccessTokenQuery: true);
        var code = await CallSecuredOverWebSocketAsync(host.WsUri($"access_token={Token}"));
        code.Should().Be(200, "the query token is promoted to the bearer on the WS upgrade");
    }

    [Fact]
    public async Task WebSocket_WithoutToken_Is401()
    {
        await using var host = await Host.StartAsync(acceptAccessTokenQuery: true);
        var code = await CallSecuredOverWebSocketAsync(host.WsUri());
        code.Should().Be(401);
    }

    [Fact]
    public async Task WebSocket_WithAccessTokenQuery_OptionOff_IsIgnored()
    {
        await using var host = await Host.StartAsync(acceptAccessTokenQuery: false);
        var code = await CallSecuredOverWebSocketAsync(host.WsUri($"access_token={Token}"));
        code.Should().Be(401, "the fallback is opt-in (AcceptAccessTokenQuery defaults to false)");
    }

    [Fact]
    public async Task WebSocket_RequireAuthentication_UpgradeGateAcceptsQueryToken()
    {
        await using var host = await Host.StartAsync(acceptAccessTokenQuery: true, requireAuthentication: true);
        var code = await CallSecuredOverWebSocketAsync(host.WsUri($"access_token={Token}"));
        code.Should().Be(200, "the connection-level default-deny gate sees the authenticated user");
    }

    // ── SSE ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sse_WithAccessTokenQuery_StreamsAck()
    {
        await using var host = await Host.StartAsync(acceptAccessTokenQuery: true);
        using var http = host.CreateClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var resp = await http.GetAsync(
            $"api/sleipnir/events/AuthedResumableEvent/SecureTick?access_token={Token}",
            HttpCompletionOption.ResponseHeadersRead, cts.Token);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
    }

    [Fact]
    public async Task Sse_WithAccessTokenQuery_OptionOff_Is401()
    {
        await using var host = await Host.StartAsync(acceptAccessTokenQuery: false);
        using var http = host.CreateClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var resp = await http.GetAsync(
            $"api/sleipnir/events/AuthedResumableEvent/SecureTick?access_token={Token}",
            HttpCompletionOption.ResponseHeadersRead, cts.Token);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Sse_HeaderWinsOverQuery()
    {
        await using var host = await Host.StartAsync(acceptAccessTokenQuery: true);
        using var http = host.CreateClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var req = new HttpRequestMessage(HttpMethod.Get,
            "api/sleipnir/events/AuthedResumableEvent/SecureTick?access_token=wrong-token");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, "an existing Authorization header is never overwritten");
    }

    [Fact]
    public async Task Sse_TokenIsStrippedFromQuery_NotBoundAsParameter_NotLoggedDownstream()
    {
        await using var host = await Host.StartAsync(acceptAccessTokenQuery: true);
        using var http = host.CreateClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        // Anonymous cold stream with a real parameter: count=2 must still bind; access_token must
        // not reach the parameter binder (it is removed from the query string up front).
        using (var resp = await http.GetAsync(
            $"api/sleipnir/events/TestInvoker/ObservableStrings?count=2&access_token={Token}",
            HttpCompletionOption.ResponseHeadersRead, cts.Token))
        {
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await resp.Content.ReadAsStringAsync(cts.Token);
            body.Should().Contain("evt-0").And.Contain("evt-1").And.Contain("event: complete");
        }

        // Every log line written after the middleware ran is token-free. The hosting "Request
        // starting" line is emitted before any middleware can act (documented limitation).
        // "Request finished" is written after the pipeline completes, which can trail the client
        // seeing EOF — poll briefly for it.
        List<(string Category, string Message)> snapshot;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            lock (host.Logs) snapshot = host.Logs.ToList();
            if (snapshot.Any(l => l.Message.StartsWith("Request finished", StringComparison.Ordinal))
                || DateTime.UtcNow > deadline)
                break;
            await Task.Delay(50);
        }
        snapshot.Should().Contain(l => l.Message.StartsWith("Request finished", StringComparison.Ordinal)
                                       && l.Message.Contains("ObservableStrings?count=2"),
            "the hosting 'Request finished' log reflects the rewritten (token-free) query string");
        snapshot.Where(l => l.Message.Contains("access_token", StringComparison.OrdinalIgnoreCase)
                            && !l.Message.StartsWith("Request starting", StringComparison.Ordinal))
            .Should().BeEmpty();
    }

    // ── REST: never honored ──────────────────────────────────────────────────────

    [Fact]
    public async Task Rest_WithAccessTokenQuery_IsNeverHonored()
    {
        await using var host = await Host.StartAsync(acceptAccessTokenQuery: true);
        using var http = host.CreateClient();
        var body = new StringContent(
            """{"controller":"TestInvoker","method":"Secured","params":[{"parameterName":"data","data":"x"}],"id":"r1"}""",
            Encoding.UTF8, "application/json");
        using var resp = await http.PostAsync($"api/sleipnir/json?access_token={Token}", body);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("code").GetInt32().Should().Be(401,
            "the query token is accepted on the WS upgrade and SSE only — never on ordinary requests");
    }

    [Fact]
    public async Task Rest_Discovery_WithAccessTokenQuery_StaysGated()
    {
        await using var host = await Host.StartAsync(acceptAccessTokenQuery: true, requireAuthentication: true);
        using var http = host.CreateClient();
        using var resp = await http.GetAsync($"api/sleipnir/discovery?access_token={Token}");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "GET outside the SSE prefix is not eligible");
    }

    // ── unit: query rewriting ────────────────────────────────────────────────────

    [Theory]
    [InlineData("?access_token=abc", "")]
    [InlineData("?count=2&access_token=abc", "?count=2")]
    [InlineData("?access_token=abc&count=2&msg=%22hi%22", "?count=2&msg=%22hi%22")]
    [InlineData("?a=1&ACCESS_TOKEN=x&b=2&access_token=y", "?a=1&b=2")]
    [InlineData("?access%5Ftoken=abc&a=1", "?a=1")]
    [InlineData("?a=1", "?a=1")]
    public void RemoveParameter_DropsOnlyTheToken(string input, string expected)
    {
        var result = SleipnirHub.Auth.SleipnirAccessTokenQueryMiddleware.RemoveParameter(
            new Microsoft.AspNetCore.Http.QueryString(input), "access_token");
        (result.Value ?? "").Should().Be(expected);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    /// <summary>Opens a raw WS, calls <c>TestInvoker.Secured</c>, returns the envelope code
    /// (or the HTTP status when the upgrade itself is rejected).</summary>
    private static async Task<int> CallSecuredOverWebSocketAsync(Uri uri)
    {
        using var ws = new ClientWebSocket();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        ws.Options.CollectHttpResponseDetails = true;
        try
        {
            await ws.ConnectAsync(uri, cts.Token);
        }
        catch (WebSocketException)
        {
            return (int)ws.HttpStatusCode;
        }

        var request = """{"controller":"TestInvoker","method":"Secured","params":[{"parameterName":"data","data":"x"}],"id":"ws1"}""";
        await ws.SendAsync(Encoding.UTF8.GetBytes(request), WebSocketMessageType.Text, true, cts.Token);

        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buffer, cts.Token);
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        using var doc = JsonDocument.Parse(ms.ToArray());
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        return doc.RootElement.GetProperty("code").GetInt32();
    }
}
