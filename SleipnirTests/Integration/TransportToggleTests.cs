using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SleipnirHub.Extensions;
using SleipnirServer;
using Xunit;

namespace SleipnirTests.Integration;

/// <summary>
/// Task #11: <see cref="SleipnirOptions.UseRest"/> / <see cref="SleipnirOptions.UseWebSocket"/>
/// gate the unified <c>UseSleipnirTransports</c>/<c>MapSleipnir</c> pipeline, and
/// <c>UseSleipnirTransports</c> emits one startup transport-introspection log. These tests boot a
/// real in-proc Kestrel host on a random port with the <b>unified</b> pipeline (the existing
/// <see cref="TransportTestFixture"/> uses the low-level extensions, which bypass the toggles and
/// so cannot exercise them) and a capturing logger provider.
/// </summary>
/// <remarks>
/// Each <c>ToggleHost</c> calls <c>AddSleipnir</c>, which eagerly creates a
/// <c>SleipnirConnectionRegistry</c> and assigns the process-global
/// <c>SleipnirConnectionRegistry.Current</c>. Running parallel with the telemetry gauge tests
/// (which assert on <c>Current</c>) flips <c>Current</c> mid-test and breaks
/// <c>Gauges_Read_Current_Registry_Values</c>. Same collection as the telemetry/tracing tests
/// serializes against that process-global state (see <c>SleipnirTracingTests</c> collection def).
/// </remarks>
[Collection("sleipnir-tracing")]
public class TransportToggleTests
{
    /// <summary>
    /// Boots a real Kestrel host on a random port with the unified Sleipnir pipeline
    /// (<c>UseSleipnirTransports</c> + <c>MapSleipnir</c>) and captures the startup
    /// transport-introspection log. Mirrors <see cref="TransportTestFixture"/>'s auth setup
    /// (<c>TestAuthHandler</c>, <c>Bearer valid-token</c>) and the capturing-logger pattern from
    /// <c>SleipnirInterceptorBypassWarningTests</c>.
    /// </summary>
    private sealed class ToggleHost : IAsyncDisposable
    {
        public string BaseUrl { get; }
        public List<(string Category, LogLevel Level, string Message)> Logs { get; } = new();
        private readonly WebApplication _app;

        private ToggleHost(WebApplication app, string baseUrl, List<(string, LogLevel, string)> logs)
        {
            _app = app;
            BaseUrl = baseUrl;
            Logs = logs;
        }

        public static async Task<ToggleHost> StartAsync(SleipnirOptions options)
        {
            var provider = new CaptureLoggerProvider();
            var logs = provider.Logs;
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.AddProvider(provider);
            builder.Logging.SetMinimumLevel(LogLevel.Trace);

            builder.Services.AddSleipnir(options);
            // Test-only auth (same scheme as TransportTestFixture).
            builder.Services.AddAuthentication("Test")
                .AddScheme<TestAuthOptions, TestAuthHandler>("Test", _ => { });
            builder.Services.AddAuthorization();

            var app = builder.Build();
            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseSleipnirTransports();   // unified pipeline — the toggle entry point
            app.MapSleipnir();             // unified endpoint mapping

            await app.StartAsync();
            return new ToggleHost(app, app.Urls.First().TrimEnd('/') + "/", logs);
        }

        public HttpClient CreateClient() => new() { BaseAddress = new Uri(BaseUrl) };

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private sealed class CaptureLoggerProvider : ILoggerProvider
    {
        public List<(string Category, LogLevel Level, string Message)> Logs { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(categoryName, Logs);
        public void Dispose() { }
    }

    private sealed class CaptureLogger(string category, List<(string, LogLevel, string)> logs) : ILogger
    {
        private sealed class NullScope : IDisposable { public void Dispose() { } }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => new NullScope();
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => logs.Add((category, logLevel, formatter(state, exception)));
    }

    [Fact]
    public void Defaults_RestAndWebSocket_AreTrue()
    {
        var options = new SleipnirOptions();
        options.UseRest.Should().BeTrue("REST is default-on (non-breaking)");
        options.UseWebSocket.Should().BeTrue("WebSocket is default-on (non-breaking)");
    }

    [Fact]
    public async Task UseRestFalse_Discovery_Returns404()
    {
        await using var host = await ToggleHost.StartAsync(new SleipnirOptions { UseRest = false });
        using var client = host.CreateClient();

        var response = await client.GetAsync("api/sleipnir/discovery");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "UseRest=false must not map the REST endpoint group, so /discovery is absent (404)");
    }

    [Fact]
    public async Task UseRestDefault_Discovery_Returns200()
    {
        await using var host = await ToggleHost.StartAsync(new SleipnirOptions());
        using var client = host.CreateClient();

        var response = await client.GetAsync("api/sleipnir/discovery");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "default options map the REST endpoint group, so /discovery returns 200 (RequireAuthentication is off by default)");
    }

    [Fact]
    public async Task StartupLog_ReflectsDefaultTransports()
    {
        await using var host = await ToggleHost.StartAsync(new SleipnirOptions());

        host.Logs.Should().Contain(l =>
            l.Level == LogLevel.Information &&
            l.Message.Contains("Sleipnir transports:", StringComparison.OrdinalIgnoreCase) &&
            l.Message.Contains("REST=True", StringComparison.OrdinalIgnoreCase) &&
            l.Message.Contains("WebSocket=True", StringComparison.OrdinalIgnoreCase) &&
            l.Message.Contains("SignalR=False", StringComparison.OrdinalIgnoreCase),
            "the startup introspection log must name all three transports with their configured flags");
    }

    [Fact]
    public async Task StartupLog_ReflectsDisabledTransports()
    {
        await using var host = await ToggleHost.StartAsync(new SleipnirOptions
        {
            UseRest = false,
            UseWebSocket = false,
            UseSignalR = true
        });

        host.Logs.Should().Contain(l =>
            l.Level == LogLevel.Information &&
            l.Message.Contains("REST=False", StringComparison.OrdinalIgnoreCase) &&
            l.Message.Contains("WebSocket=False", StringComparison.OrdinalIgnoreCase) &&
            l.Message.Contains("SignalR=True", StringComparison.OrdinalIgnoreCase),
            "the startup log must reflect UseRest/UseWebSocket toggled off and UseSignalR on");
    }
}