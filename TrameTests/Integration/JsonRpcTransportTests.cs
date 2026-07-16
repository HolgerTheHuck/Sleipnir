using FluentAssertions;
using System.Net;
using System.Text;
using System.Text.Json;
using TrameHub.Extensions;
using TrameRest;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace TrameTests.Integration;

/// <summary>
/// Integrationstests für den JSON-RPC-2.0-Kompatibilitäts-Endpoint
/// <c>POST /api/trame/jsonrpc</c>. Echter Kestrel-Host (wie <c>TransportTestFixture</c>),
/// <c>EnableJsonRpcCompat = true</c>, Auto-Discovery registriert TestInvokerController +
/// DependencyChainController. Prüft die Orchestrierung: Einzel/Batch/Notification,
/// Parse-/Invalid-Fehler in der 200er-Hülle, Fehlercode-Map (-32601 Routing vs. -32000
/// Business), Capability-Methoden, id-Echo mit Originaltyp.
/// </summary>
public class JsonRpcTransportTests : IClassFixture<JsonRpcFixture>
{
    private readonly HttpClient _client;

    public JsonRpcTransportTests(JsonRpcFixture fx)
        => _client = new HttpClient { BaseAddress = new Uri(fx.BaseUrl) };

    /// <summary>Liefert (HTTP-Status, Body). 204 → Body ist <c>default(JsonElement)</c>
    ///  (ValueKind Undefined); sonst ein geparstes <c>JsonElement</c>. JsonElement (nicht
    ///  JsonNode), weil ein vorhandenes JSON-null als ValueKind.Null greifbar ist —
    ///  JsonNode.Parse macht daraus C#-null und verliert die Unterscheidung.</summary>
    private async Task<(int Status, JsonElement Body)> PostAsync(string body)
    {
        var content = new StringContent(body, Encoding.UTF8, "application/json");
        var resp = await _client.PostAsync("/api/trame/jsonrpc", content);
        var text = resp.StatusCode == HttpStatusCode.NoContent ? null : await resp.Content.ReadAsStringAsync();
        return ((int)resp.StatusCode, text is null ? default : JsonDocument.Parse(text).RootElement);
    }

    // === Erfolgreiche Einzel-Requests ======================================

    [Fact]
    public async Task Single_NamedParams_ReturnsResultWithId()
    {
        var (status, b) = await PostAsync("""{"jsonrpc":"2.0","method":"TestInvoker.Add","params":{"a":3,"b":4},"id":1}""");
        status.Should().Be(200);
        b.GetProperty("jsonrpc").GetString().Should().Be("2.0");
        b.GetProperty("result").GetInt32().Should().Be(7);
        b.GetProperty("id").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Single_PositionalParams_BindsByIndex()
    {
        var (_, b) = await PostAsync("""{"jsonrpc":"2.0","method":"TestInvoker.Add","params":[20,22],"id":2}""");
        b.GetProperty("result").GetInt32().Should().Be(42);
    }

    [Fact]
    public async Task Single_StringId_EchoedAsString()
    {
        var (_, b) = await PostAsync("""{"jsonrpc":"2.0","method":"TestInvoker.NoParams","id":"req-xyz"}""");
        b.GetProperty("result").GetString().Should().Be("Hello World");
        b.GetProperty("id").GetString().Should().Be("req-xyz");
    }

    [Fact]
    public async Task Single_VoidMethod_ResultIsPresentNull()
    {
        var (_, b) = await PostAsync("""{"jsonrpc":"2.0","method":"TestInvoker.VoidMethod","params":{"data":"x"},"id":3}""");
        // result MUSS vorhanden sein (JSON-RPC) und JSON-null sein — nicht abwesend.
        b.TryGetProperty("result", out var r).Should().BeTrue();
        r.ValueKind.Should().Be(JsonValueKind.Null);
        b.TryGetProperty("error", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Single_BinaryResult_Base64String()
    {
        var (_, b) = await PostAsync("""{"jsonrpc":"2.0","method":"TestInvoker.DownloadBlob","params":{"name":"a"},"id":4}""");
        var b64 = b.GetProperty("result").GetString()!;
        Convert.FromBase64String(b64).Length.Should().BeGreaterThan(0);
    }

    // === Batch ==============================================================

    [Fact]
    public async Task Batch_ReturnsArrayInOrder()
    {
        var (_, b) = await PostAsync(
            """[{"jsonrpc":"2.0","method":"TestInvoker.Add","params":[1,1],"id":1},"""
            + """{"jsonrpc":"2.0","method":"TestInvoker.NoParams","id":2}]""");
        var arr = b.EnumerateArray().ToArray();
        arr.Should().HaveCount(2);
        arr[0].GetProperty("result").GetInt32().Should().Be(2);
        arr[1].GetProperty("result").GetString().Should().Be("Hello World");
    }

    [Fact]
    public async Task Batch_MixedNotificationAndRequest_OmitsNotification()
    {
        // Notification (kein id) + Request → nur EINE Response im Array.
        var (_, b) = await PostAsync(
            """[{"jsonrpc":"2.0","method":"TestInvoker.NoParams"},"""
            + """{"jsonrpc":"2.0","method":"TestInvoker.Add","params":[1,2],"id":9}]""");
        var arr = b.EnumerateArray().ToArray();
        arr.Should().HaveCount(1);
        arr[0].GetProperty("id").GetInt32().Should().Be(9);
    }

    [Fact]
    public async Task Batch_AllNotifications_Returns204()
    {
        var (status, b) = await PostAsync("""[{"jsonrpc":"2.0","method":"TestInvoker.NoParams"},{"jsonrpc":"2.0","method":"TestInvoker.Add","params":[1,1]}]""");
        status.Should().Be(204);
        b.ValueKind.Should().Be(JsonValueKind.Undefined);
    }

    [Fact]
    public async Task Single_Notification_Returns204()
    {
        var (status, b) = await PostAsync("""{"jsonrpc":"2.0","method":"TestInvoker.NoParams"}""");
        status.Should().Be(204);
        b.ValueKind.Should().Be(JsonValueKind.Undefined);
    }

    [Fact]
    public async Task EmptyBatch_IsInvalidRequest()
    {
        var (status, b) = await PostAsync("[]");
        status.Should().Be(200);
        b.GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32600);
    }

    // === Fehlercode-Map ====================================================

    [Fact]
    public async Task ParseError_Returns32700()
    {
        var (status, b) = await PostAsync("{not json");
        status.Should().Be(200);
        b.GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32700);
        b.GetProperty("id").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task InvalidRequest_MissingJsonrpc_Returns32600()
    {
        var (_, b) = await PostAsync("""{"method":"TestInvoker.NoParams","id":1}""");
        b.GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32600);
        b.GetProperty("id").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task RoutingNotFound_Returns32601()
    {
        var (_, b) = await PostAsync("""{"jsonrpc":"2.0","method":"Nope.NoMethod","id":1}""");
        b.GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32601); // Controller/Methode fehlt
    }

    [Fact]
    public async Task BusinessNotFound_Returns32000()
    {
        // GetOr404(99) → TrameResults.NotFound → Business-404, NICHT Routing → -32000.
        var (_, b) = await PostAsync("""{"jsonrpc":"2.0","method":"TestInvoker.GetOr404","params":{"id":99},"id":1}""");
        b.GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32000);
        b.GetProperty("error").GetProperty("message").GetString().Should().Contain("not found");
    }

    // === Capability-Methoden ===============================================

    [Fact]
    public async Task TrameDiscover_ReturnsDiscoveryInfo()
    {
        var (_, b) = await PostAsync("""{"jsonrpc":"2.0","method":"trame.discover","id":1}""");
        b.GetProperty("result").ValueKind.Should().Be(JsonValueKind.Object);
        b.GetProperty("id").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task TrameCapabilities_ReturnsManifest()
    {
        var (_, b) = await PostAsync("""{"jsonrpc":"2.0","method":"trame.capabilities","id":2}""");
        b.GetProperty("result").GetProperty("nativeClient").GetBoolean().Should().BeTrue();
        b.GetProperty("result").GetProperty("compatMode").GetProperty("supported").GetBoolean().Should().BeTrue();
    }
}

/// <summary>
/// Echter Kestrel-Host mit <c>EnableJsonRpcCompat = true</c>. Auto-Discovery
/// registriert die Test-Controller (Test-Assembly ist in-Prozess geladen).
/// </summary>
public class JsonRpcFixture : IAsyncLifetime
{
    private Microsoft.AspNetCore.Builder.WebApplication _app = null!;
    public string BaseUrl { get; private set; } = "";

    public async Task InitializeAsync()
    {
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddTrame(new TrameOptions
        {
            EnableDetailedErrors = true,
            EnableJsonRpcCompat = true,
            RateLimitPermitLimit = 0,
        });

        var app = builder.Build();
        app.UseRouting();
        app.UseTrame();
        app.MapTrameEndpoints("/api/trame", enableJsonRpcCompat: true);

        await app.StartAsync();
        _app = app;
        BaseUrl = app.Urls.First().TrimEnd('/') + "/";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}