using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using SleipnirClient.Sleipnir;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Xunit;

namespace SleipnirTests.Unit.Client;

/// <summary>
/// Phase R resume tests for <see cref="SleipnirWebSocketClient"/>: the per-subscription
/// reconnect decision hook (Fresh / Resume / Drop), client-side <c>eventId</c> dedup, and the
/// degrade-to-fresh path when the server cannot honor a Resume. Uses the same in-process Kestrel
/// WebSocket server pattern as <see cref="SleipnirWebSocketClientReconnectTests"/> (Kestrel, not
/// HttpListener, so a server abort reliably surfaces as a transport error cross-platform).
/// </summary>
public class SleipnirWebSocketClientResumeTests
{
    /// <summary>
    /// Minimal Kestrel WS server that speaks just enough of the Sleipnir event wire to drive the
    /// client: it answers <c>subscribe</c> frames, captures every subscribe (so a test can assert
    /// the resume fields the client sent on reconnect), and exposes <see cref="SendEventAsync"/>
    /// to push <c>{type:"event",subscriptionId,eventId,data}</c> frames.
    /// </summary>
    private sealed class ResumeWsServer : IAsyncDisposable
    {
        private WebApplication? _app;
        private readonly object _gate = new();
        public string BaseUrl { get; private set; } = "";
        public int AcceptCount;
        private WebSocket? _current;

        /// <summary>Whether a Resume (client-sent subscriptionId) is honored (same id returned)
        /// or degraded (a fresh id returned, simulating TTL expiry / non-resumable). Default true.</summary>
        public bool HonorResume = true;

        private int _subSeq;
        public readonly Channel<SubscribeCapture> Subscribes = Channel.CreateUnbounded<SubscribeCapture>();

        public sealed class SubscribeCapture
        {
            public string? SentSubscriptionId;  // resume id the client sent (null on fresh)
            public long? SentLastEventId;        // lastEventId the client sent (null if absent)
            public string? Controller;
            public string? Method;
            public string? ResponseSubscriptionId; // the subscriptionId the server returned
        }

        public void Start()
        {
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
                lock (_gate)
                {
                    Interlocked.Increment(ref AcceptCount);
                    _current = ws;
                }
                try { await HandleAsync(ws); }
                finally
                {
                    lock (_gate) { if (ReferenceEquals(_current, ws)) _current = null; }
                }
            });

            app.StartAsync().GetAwaiter().GetResult();
            _app = app;
            BaseUrl = url;
        }

        private async Task HandleAsync(WebSocket ws)
        {
            var buffer = new byte[8192];
            while (ws.State == WebSocketState.Open)
            {
                using var msg = new MemoryStream();
                WebSocketReceiveResult r;
                try
                {
                    do
                    {
                        r = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                        if (r.MessageType == WebSocketMessageType.Close) return;
                        if (r.MessageType == WebSocketMessageType.Text && r.Count > 0)
                            msg.Write(buffer, 0, r.Count);
                    }
                    while (!r.EndOfMessage);
                }
                catch { return; }

                if (msg.Length == 0) continue;
                var text = Encoding.UTF8.GetString(msg.GetBuffer(), 0, (int)msg.Length);
                if (!TryParseSubscribe(text, out var cap, out var reqId))
                {
                    // Non-subscribe frames (e.g. unsubscribe) — best-effort ack with a null-data 200.
                    await SendAsync(ws, $"{{\"code\":200,\"data\":null,\"id\":\"{reqId ?? ""}\"}}");
                    continue;
                }

                // Decide the subscriptionId to return: honor the resume id, or generate a fresh one.
                string responseSubId;
                if (!string.IsNullOrEmpty(cap.SentSubscriptionId) && HonorResume)
                    responseSubId = cap.SentSubscriptionId!;
                else
                    responseSubId = "s" + Interlocked.Increment(ref _subSeq);
                cap.ResponseSubscriptionId = responseSubId;
                Subscribes.Writer.TryWrite(cap);

                await SendAsync(ws, $"{{\"code\":200,\"data\":{{\"subscriptionId\":\"{responseSubId}\"}},\"id\":\"{reqId}\"}}");
            }
        }

        /// <summary>Push an event frame to the current socket.</summary>
        public async Task SendEventAsync(string subscriptionId, long eventId, string payload)
        {
            WebSocket? ws;
            lock (_gate) ws = _current;
            if (ws == null) return;
            // data is a JSON string value — the client reads GetRawText() and deserializes to T.
            var frame = JsonSerializer.Serialize(new
            {
                type = "event",
                subscriptionId,
                eventId,
                data = payload,
            }, JsonOpts);
            await SendAsync(ws, frame);
        }

        public async Task CloseCurrentAsync()
        {
            WebSocket? ws;
            lock (_gate) ws = _current;
            if (ws != null)
            {
                try { await ws.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "drop", CancellationToken.None); }
                catch { /* ignore */ }
            }
        }

        private static async Task SendAsync(WebSocket ws, string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            try { await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None); }
            catch { /* ignore */ }
        }

        private static bool TryParseSubscribe(string json, out SubscribeCapture cap, out string? reqId)
        {
            cap = new SubscribeCapture();
            reqId = null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return false;
                if (!root.TryGetProperty("kind", out var kind) || kind.GetString() != "subscribe") return false;
                if (root.TryGetProperty("controller", out var c)) cap.Controller = c.GetString();
                if (root.TryGetProperty("method", out var m)) cap.Method = m.GetString();
                if (root.TryGetProperty("id", out var id)) reqId = id.GetString();
                if (root.TryGetProperty("subscriptionId", out var sid) && sid.ValueKind == JsonValueKind.String)
                    cap.SentSubscriptionId = sid.GetString();
                if (root.TryGetProperty("lastEventId", out var leid) && leid.ValueKind == JsonValueKind.Number)
                    cap.SentLastEventId = leid.GetInt64();
                return true;
            }
            catch { return false; }
        }

        public async ValueTask DisposeAsync()
        {
            Subscribes.Writer.TryComplete();
            if (_app != null)
            {
                try { await _app.StopAsync(); } catch { /* ignore */ }
                try { await _app.DisposeAsync(); } catch { /* ignore */ }
            }
        }

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        };
    }

    /// <summary>Counts OnNext / OnCompleted for an event subscription.</summary>
    private sealed class CountingObserver<T> : IObserver<T>
    {
        public int NextCount;
        public int CompletedCount;
        public readonly List<T> Values = new();
        public void OnNext(T value) { NextCount++; Values.Add(value); }
        public void OnCompleted() => Interlocked.Increment(ref CompletedCount);
        public void OnError(Exception error) { }
    }

    private static async Task WaitForStateAsync(SleipnirWebSocketClient client, SleipnirConnectionState expected, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (client.State == expected) return;
            await Task.Delay(20);
        }
        client.State.Should().Be(expected, $"client should reach {expected} (was {client.State})");
    }

    [Fact]
    public async Task Dedup_DropsReplayedEventId_ForwardFreshOnly()
    {
        await using var server = new ResumeWsServer();
        server.Start();

        await using var client = new SleipnirWebSocketClient(server.BaseUrl,
            autoReconnect: false);
        await client.ConnectAsync();

        var sub = await client.SubscribeAsync<string>("C", "Tick");
        var first = await server.Subscribes.Reader.ReadAsync();
        var subId = first.ResponseSubscriptionId!;
        var obs = new CountingObserver<string>();
        sub.Subscribe(obs);

        // eventId 1 → forwarded; replayed eventId 1 → dropped; eventId 2 → forwarded.
        await server.SendEventAsync(subId, 1, "a");
        await server.SendEventAsync(subId, 1, "a-again"); // duplicate replay
        await server.SendEventAsync(subId, 2, "b");
        await WaitUntilAsync(() => obs.NextCount >= 2, 2000);

        obs.NextCount.Should().Be(2, "the replayed eventId 1 must be deduped");
        obs.Values.Should().Equal(["a", "b"]);

        sub.Dispose();
    }

    [Fact]
    public async Task Reconnect_DefaultPolicy_Fresh_OmitsResumeFields()
    {
        await using var server = new ResumeWsServer();
        server.Start();

        await using var client = new SleipnirWebSocketClient(server.BaseUrl,
            autoReconnect: true,
            reconnectDelays: new[] { TimeSpan.FromMilliseconds(40) });
        await client.ConnectAsync();

        var sub = await client.SubscribeAsync<string>("C", "Tick");
        var first = await server.Subscribes.Reader.ReadAsync();
        first.SentSubscriptionId.Should().BeNull("initial subscribe is fresh");
        first.SentLastEventId.Should().BeNull();

        // Drop → reconnect → ResubscribeAllAsync consults the (null) policy → Fresh.
        await server.CloseCurrentAsync();
        await WaitForStateAsync(client, SleipnirConnectionState.Reconnecting, 2000);
        await WaitForStateAsync(client, SleipnirConnectionState.Connected);

        var second = await server.Subscribes.Reader.ReadAsync();
        second.SentSubscriptionId.Should().BeNull("Fresh re-subscribe sends no durable subscriptionId");
        second.SentLastEventId.Should().BeNull("Fresh re-subscribe sends no lastEventId");

        sub.Dispose();
    }

    [Fact]
    public async Task Reconnect_ResumePolicy_SendsSubscriptionIdAndLastEventId()
    {
        await using var server = new ResumeWsServer();
        server.Start();

        // Client-wide policy: always Resume.
        ResumePolicy policy = _ => ResumeDecision.Resume;
        await using var client = new SleipnirWebSocketClient(server.BaseUrl,
            autoReconnect: true,
            reconnectDelays: new[] { TimeSpan.FromMilliseconds(40) },
            resumePolicy: policy);
        await client.ConnectAsync();

        var sub = await client.SubscribeAsync<string>("C", "Tick");
        var first = await server.Subscribes.Reader.ReadAsync();
        var durableId = first.ResponseSubscriptionId!;
        var obs = new CountingObserver<string>();
        sub.Subscribe(obs);

        // Process one event so the client's LastEventId becomes 1.
        await server.SendEventAsync(durableId, 1, "a");
        await WaitUntilAsync(() => obs.NextCount >= 1, 2000);
        obs.NextCount.Should().Be(1);

        // Drop → reconnect → Resume sends the durable id + lastEventId=1.
        await server.CloseCurrentAsync();
        await WaitForStateAsync(client, SleipnirConnectionState.Reconnecting, 2000);
        await WaitForStateAsync(client, SleipnirConnectionState.Connected);

        var second = await server.Subscribes.Reader.ReadAsync();
        second.SentSubscriptionId.Should().Be(durableId, "Resume re-subscribe carries the durable subscriptionId");
        second.SentLastEventId.Should().Be(1, "Resume re-subscribe carries the last processed eventId");

        sub.Dispose();
    }

    [Fact]
    public async Task Reconnect_DropPolicy_DoesNotResubscribe_AndCompletesConsumer()
    {
        await using var server = new ResumeWsServer();
        server.Start();

        ResumePolicy policy = _ => ResumeDecision.Drop;
        await using var client = new SleipnirWebSocketClient(server.BaseUrl,
            autoReconnect: true,
            reconnectDelays: new[] { TimeSpan.FromMilliseconds(40) },
            resumePolicy: policy);
        await client.ConnectAsync();

        var sub = await client.SubscribeAsync<string>("C", "Tick");
        var first = await server.Subscribes.Reader.ReadAsync();
        var obs = new CountingObserver<string>();
        sub.Subscribe(obs);

        // Drop → reconnect → Drop decision: no re-subscribe, consumer's IObservable completes.
        await server.CloseCurrentAsync();
        await WaitForStateAsync(client, SleipnirConnectionState.Connected, 3000);

        // No second subscribe should arrive. Drain any stray frame briefly, then assert the channel
        // is empty (no re-subscribe) and the observer was completed.
        await Task.Delay(150);
        server.Subscribes.Reader.TryRead(out _).Should().BeFalse("Drop must not re-subscribe");
        await WaitUntilAsync(() => obs.CompletedCount >= 1, 2000);
        obs.CompletedCount.Should().BeGreaterOrEqualTo(1, "Drop completes the consumer's IObservable");

        sub.Dispose();
    }

    [Fact]
    public async Task Reconnect_ResumeNotHonored_DegradesToFresh_UnderNewId()
    {
        await using var server = new ResumeWsServer { HonorResume = false };
        server.Start();

        ResumePolicy policy = _ => ResumeDecision.Resume;
        await using var client = new SleipnirWebSocketClient(server.BaseUrl,
            autoReconnect: true,
            reconnectDelays: new[] { TimeSpan.FromMilliseconds(40) },
            resumePolicy: policy);
        await client.ConnectAsync();

        var sub = await client.SubscribeAsync<string>("C", "Tick");
        var first = await server.Subscribes.Reader.ReadAsync();
        var originalId = first.ResponseSubscriptionId!;
        var obs = new CountingObserver<string>();
        sub.Subscribe(obs);

        await server.SendEventAsync(originalId, 1, "a");
        await WaitUntilAsync(() => obs.NextCount >= 1, 2000);

        // Drop → reconnect. Client sends Resume (subscriptionId=originalId, lastEventId=1), but the
        // server ignores it (HonorResume=false → fresh id). The client must re-key under the new id
        // and keep delivering events on the same consumer subject.
        await server.CloseCurrentAsync();
        await WaitForStateAsync(client, SleipnirConnectionState.Reconnecting, 2000);
        await WaitForStateAsync(client, SleipnirConnectionState.Connected);

        var second = await server.Subscribes.Reader.ReadAsync();
        second.SentSubscriptionId.Should().Be(originalId, "client still requests Resume");
        second.ResponseSubscriptionId.Should().NotBe(originalId, "server degraded to a fresh id");

        // An event on the new id must reach the same observer (handler re-keyed under the new id).
        await server.SendEventAsync(second.ResponseSubscriptionId!, 1, "b");
        await WaitUntilAsync(() => obs.NextCount >= 2, 3000);
        obs.Values.Should().Contain("b");

        sub.Dispose();
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(20);
        }
        predicate().Should().BeTrue("condition should hold within the timeout");
    }
}