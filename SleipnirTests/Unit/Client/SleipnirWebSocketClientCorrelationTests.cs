using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using SleipnirClient.Sleipnir;
using SleipnirCommon.Models;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace SleipnirTests.Unit.Client;

/// <summary>
/// Correlation regression for <see cref="SleipnirWebSocketClient"/> (parity with the TS client
/// websocket.ts): the library default request id <c>"Controller.Method"</c> (what
/// <c>SleipnirCall.ToRequest()</c> emits) is identical for every call to the same method. Two
/// concurrent identical calls over ONE connection used to key the same entry in
/// <c>_pendingRequests</c> — the second write dropped the first holder, so the first caller
/// waited until the call timeout while the second received the first's answer (and the second
/// response frame, whose id no longer had a pending request, was dropped). The client must
/// replace that dead id with a unique one (`NextId()`), exactly like absent ids.
/// </summary>
public class SleipnirWebSocketClientCorrelationTests
{
    /// <summary>
    /// Minimal Kestrel WS echo server: answers each request with
    /// <c>{"code":200,"data":"call-N","id":"&lt;request id&gt;"}</c> (N increments per answered
    /// request). Batch (<c>{requests:[...]}</c>) frames are answered with one element per member,
    /// correlated by the members' ids — the shape the strict dispatcher expects.
    /// </summary>
    private sealed class CorrelationWsServer : IAsyncDisposable
    {
        private WebApplication? _app;
        private int _dataSeq;

        public string BaseUrl { get; private set; } = "";

        public void Start()
        {
            // Reserve a free port (Kestrel does not resolve port 0 the way HttpListener does).
            var tcp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            tcp.Start();
            var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
            tcp.Stop();

            var url = $"http://localhost:{port}";
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls(url);
            var app = builder.Build();
            app.UseWebSockets();

            app.Map("/sleipnirws", async context =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }
                using var ws = await context.WebSockets.AcceptWebSocketAsync();
                await EchoLoopAsync(ws);
            });

            app.StartAsync().GetAwaiter().GetResult();
            _app = app;
            BaseUrl = url;
        }

        private async Task EchoLoopAsync(WebSocket ws)
        {
            var buffer = new byte[8192];
            while (ws.State == WebSocketState.Open)
            {
                WebSocketReceiveResult r;
                try { r = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None); }
                catch { return; }
                if (r.MessageType == WebSocketMessageType.Close)
                    return;

                var text = Encoding.UTF8.GetString(buffer, 0, r.Count);
                string? batchFirstId;
                var responses = ParseAndBuildResponses(text, out batchFirstId);
                var respJson = batchFirstId != null
                    ? JsonSerializer.Serialize(responses)
                    : JsonSerializer.Serialize(responses[0]);
                var bytes = Encoding.UTF8.GetBytes(respJson);
                try { await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None); }
                catch { return; }
            }
        }

        private List<Dictionary<string, object?>> ParseAndBuildResponses(string json, out string? batchFirstId)
        {
            batchFirstId = null;
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var requests = new List<(string? id, string controller, string method)>();
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("requests", out var members))
            {
                foreach (var m in members.EnumerateArray())
                    requests.Add((GetString(m, "id"), GetString(m, "controller"), GetString(m, "method")));
                batchFirstId = requests.FirstOrDefault().id;
            }
            else
            {
                requests.Add((GetString(root, "id"), GetString(root, "controller"), GetString(root, "method")));
            }

            var list = new List<Dictionary<string, object?>>();
            foreach (var (id, _, _) in requests)
            {
                var n = Interlocked.Increment(ref _dataSeq);
                list.Add(new Dictionary<string, object?>
                {
                    ["code"] = 200,
                    ["data"] = $"call-{n}",
                    ["id"] = id ?? "",
                    ["isSuccess"] = true,
                });
            }
            return list;
        }

        private static string GetString(JsonElement obj, string name)
            => obj.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";

        public async ValueTask DisposeAsync()
        {
            var app = _app;
            if (app != null)
            {
                try { await app.StopAsync(); } catch { /* ignore */ }
                try { await app.DisposeAsync(); } catch { /* ignore */ }
            }
        }
    }

    /// <summary>A request carrying exactly the <c>SleipnirCall.ToRequest()</c> default id — the dead
    /// correlation id this suite is about (identical for every call to the same method).</summary>
    private static SleipnirRequest BuilderDefaultRequest() => new()
    {
        Controller = "Projects",
        Method = "ListProjects",
        Params = JsonNode.Parse("[]"),
        Id = "Projects.ListProjects", // SleipnirCall.ToRequest() default (Named("name") unset)
    };

    private static SleipnirRequest NoIdRequest() => new()
    {
        Controller = "Projects",
        Method = "ListProjects",
        Params = JsonNode.Parse("[]"),
    };

    [Fact]
    public async Task TwoConcurrentIdenticalBuilderDefaultIds_EachCallerReceivesItsOwnResponse()
    {
        await using var server = new CorrelationWsServer();
        server.Start();
        await using var client = new SleipnirWebSocketClient(server.BaseUrl,
            callTimeout: TimeSpan.FromSeconds(5));

        // Pre-fix: the shared id "Projects.ListProjects" keyed one _pendingRequests entry — call
        // a hung until timeout (SleipnirException here), call b resolved with a's echo.
        var taskA = client.Call(BuilderDefaultRequest());
        var taskB = client.Call(BuilderDefaultRequest());
        var responses = await Task.WhenAll(taskA, taskB);

        responses.Should().NotContainNulls("both calls must complete");
        // Unique data values ("call-N") prove each caller received its own echo — not the twin's.
        responses[0]!.Data!.Value.GetString().Should().NotBe(responses[1]!.Data!.Value.GetString());
        // The wire ids (mutated in place by the client) were replaced by unique ones before send.
        responses[0]!.Id!.Should().NotBe("Projects.ListProjects",
            "the builder default id must be replaced by a unique correlation id");
        responses[0]!.Id!.Should().StartWith("ws-"); // the C# NextId() shape
        responses[1]!.Id!.Should().NotBe(responses[0]!.Id);
    }

    [Fact]
    public async Task TwoConcurrentNoIdCalls_EachCallerReceivesItsOwnResponse()
    {
        await using var server = new CorrelationWsServer();
        server.Start();
        await using var client = new SleipnirWebSocketClient(server.BaseUrl,
            callTimeout: TimeSpan.FromSeconds(5));

        var taskA = client.Call(NoIdRequest());
        var taskB = client.Call(NoIdRequest());
        var responses = await Task.WhenAll(taskA, taskB);

        responses.Should().NotContainNulls("both calls must complete");
        responses[0]!.Data!.Value.GetString().Should().NotBe(responses[1]!.Data!.Value.GetString());
        responses[0]!.Id!.Should().StartWith("ws-"); // NextId() shape
        responses[0]!.Id!.Should().NotBe(responses[1]!.Id);
    }

    [Fact]
    public async Task TwoConcurrentIdenticalBatches_EachBatchReceivesItsOwnResponseArray()
    {
        await using var server = new CorrelationWsServer();
        server.Start();
        await using var client = new SleipnirWebSocketClient(server.BaseUrl,
            callTimeout: TimeSpan.FromSeconds(5));

        // The batch correlates via the FIRST member's id — two identical batches with builder
        // default / no ids must not share that key.
        var t1 = client.Call(new SleipnirMultiRequest
        {
            Requests = new List<SleipnirRequest> { BuilderDefaultRequest(), NoIdRequest() },
        });
        var t2 = client.Call(new SleipnirMultiRequest
        {
            Requests = new List<SleipnirRequest> { BuilderDefaultRequest(), NoIdRequest() },
        });
        var batches = await Task.WhenAll(t1, t2);

        batches.Should().NotContainNulls("both batches must complete");
        batches[0]!.First()!.Id.Should().NotBe(batches[1]!.First()!.Id);
        // The responses' ids tie each batch to its own echo set — unique data values prove no
        // cross-talk (batch a resolving with batch b's array would show the twin's ids).
        var ids1 = batches[0]!.Select(r => r!.Id).ToList();
        var ids2 = batches[1]!.Select(r => r!.Id).ToList();
        ids1.Intersect(ids2).Should().BeEmpty("every echoed request id is unique per connection");
    }

    [Fact]
    public async Task CallerChosenExplicitId_IsKeptVerbatim()
    {
        await using var server = new CorrelationWsServer();
        server.Start();
        await using var client = new SleipnirWebSocketClient(server.BaseUrl,
            callTimeout: TimeSpan.FromSeconds(5));

        var request = new SleipnirRequest
        {
            Controller = "Projects",
            Method = "ListProjects",
            Params = JsonNode.Parse("[]"),
            Id = "my-call-42", // caller's correlation handle — must not be rewritten
        };
        var response = await client.Call(request);

        response.Should().NotBeNull();
        response!.Code.Should().Be(200);
        response.Id.Should().Be("my-call-42",
            "a caller-chosen id is the caller's correlation handle (JSON-RPC semantics)");
    }
}
