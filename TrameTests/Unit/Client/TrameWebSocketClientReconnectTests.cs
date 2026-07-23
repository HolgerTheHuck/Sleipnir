using FluentAssertions;
using TrameClient.Trame;
using TrameCommon.Exceptions;
using TrameCommon.Models;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace TrameTests.Unit.Client;

/// <summary>
/// Reconnect-Tests für <see cref="TrameWebSocketClient"/>. Da <see cref="ClientWebSocket"/>
/// sealed ist, läuft ein minimaler <see cref="HttpListener"/>-WS-Server, der Trame-Responses
/// echoed und den serverseitigen Close (Drop) steuern kann.
/// </summary>
public class TrameWebSocketClientReconnectTests
{
    /// <summary>Minimaler WS-Server: echoed Trame-Responses, zählt Accepts, hält den aktuellen Socket.</summary>
    private sealed class ReconnectWsServer : IAsyncDisposable
    {
        private HttpListener? _listener;
        private CancellationTokenSource _cts = new();
        private Task? _acceptLoop;
        private readonly object _gate = new();
        public string BaseUrl { get; private set; } = "";
        public int AcceptCount;
        public WebSocket? Current;
        public bool EchoEnabled = true;
        public bool RejectUpgrade = false;

        public void Start()
        {
            // Freien Port über TcpListener reservieren (Port 0 -> OS vergibt einen).
            var tcp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            tcp.Start();
            var port = ((System.Net.IPEndPoint)tcp.LocalEndpoint).Port;
            tcp.Stop();

            var prefix = $"http://localhost:{port}/tramews/";
            _listener = new HttpListener();
            _listener.Prefixes.Add(prefix);
            _listener.Start();
            BaseUrl = $"http://localhost:{port}";

            _acceptLoop = Task.Run(AcceptLoopAsync);
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener!.GetContextAsync(); }
                catch { return; }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (RejectUpgrade)
                        {
                            // WS-Upgrade ablehnen -> ClientWebSocket.ConnectAsync schlägt schnell fehl
                            // (für die Backoff-Erschöpfung, ohne auf TCP-Refused warten zu müssen).
                            ctx.Response.StatusCode = 400;
                            ctx.Response.Close();
                            return;
                        }
                        var wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: null);
                        using var ws = wsCtx.WebSocket;
                        lock (_gate)
                        {
                            Interlocked.Increment(ref AcceptCount);
                            Current = ws;
                        }
                        await EchoLoopAsync(ws);
                        lock (_gate)
                        {
                            if (Current == ws) Current = null;
                        }
                    }
                    catch { /* ignore */ }
                });
            }
        }

        private async Task EchoLoopAsync(WebSocket ws)
        {
            var buffer = new byte[8192];
            while (ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                WebSocketReceiveResult r;
                try { r = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token); }
                catch { return; }
                if (r.MessageType == WebSocketMessageType.Close)
                    return;
                if (!EchoEnabled)
                    continue; // Call offen halten (für In-Flight-Drop-Test)

                var text = Encoding.UTF8.GetString(buffer, 0, r.Count);
                var id = ExtractId(text);
                var respJson = $"{{\"code\":200,\"data\":null,\"id\":\"{id}\"}}";
                var bytes = Encoding.UTF8.GetBytes(respJson);
                try { await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts.Token); }
                catch { return; }
            }
        }

        public async Task CloseCurrentAsync()
        {
            WebSocket? ws;
            lock (_gate) ws = Current;
            if (ws != null)
            {
                // Abort (Dispose) statt sauberem CloseAsync: ein gleichzeitiges
                // ReceiveAsync im EchoLoop würde sonst den Close-Handshake blockieren.
                // Dispose löst beim Client einen Transportfehler aus (unerwarteter Drop).
                try { ws.Dispose(); }
                catch { /* ignore */ }
                await Task.CompletedTask;
            }
        }

        /// <summary>
        /// Stoppt den Listener, ohne den gesamten Server zu disposen. Danach verweigert der
        /// Port neue Verbindungen (TCP-refused) — <c>ClientWebSocket.ConnectAsync</c> schlägt
        /// auf jeder Plattform schnell fehl, im Gegensatz zu einem mit HTTP 400 abgewiesenen
        /// WS-Upgrade, das auf Linux/ManagedWebSocket in <c>ConnectAsync</c> hängen bleibt.
        /// </summary>
        public void StopListener()
        {
            try { _listener?.Stop(); } catch { /* ignore */ }
            try { _listener?.Close(); } catch { /* ignore */ }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { _listener?.Stop(); } catch { /* ignore */ }
            try { _listener?.Close(); } catch { /* ignore */ }
            if (_acceptLoop != null) { try { await _acceptLoop; } catch { /* ignore */ } }
            _cts.Dispose();
        }

        private static string ExtractId(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        if (string.Equals(prop.Name, "id", StringComparison.OrdinalIgnoreCase))
                            return prop.Value.GetString() ?? "";
                    }
                }
            }
            catch { /* ignore */ }
            return "";
        }
    }

    private static TrameRequest EchoRequest(string id) => new()
    {
        Controller = "C",
        Method = "M",
        Params = JsonNode.Parse("[]"),
        Id = id,
    };

    private static async Task WaitForStateAsync(TrameWebSocketClient client, TrameConnectionState expected, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (client.State == expected)
                return;
            await Task.Delay(20);
        }
        client.State.Should().Be(expected, $"Zustand sollte {expected} erreichen (war {client.State})");
    }

    [Fact]
    public async Task Reconnect_NachServerClose_StelltVerbindungWiederHerUndCallGeht()
    {
        await using var server = new ReconnectWsServer();
        server.Start();

        await using var client = new TrameWebSocketClient(server.BaseUrl,
            autoReconnect: true,
            reconnectDelays: new[] { TimeSpan.FromMilliseconds(40) });

        // Erster Connect + Call (echo) klappt.
        var first = await client.Call(EchoRequest("a1"));
        first!.Code.Should().Be(200);
        client.State.Should().Be(TrameConnectionState.Connected);
        var acceptAfterFirst = server.AcceptCount;

        // Serverseitiger Drop -> Client reconnectet im Hintergrund.
        await server.CloseCurrentAsync();
        await WaitForStateAsync(client, TrameConnectionState.Reconnecting, timeoutMs: 2000);
        await WaitForStateAsync(client, TrameConnectionState.Connected);
        server.AcceptCount.Should().BeGreaterThan(acceptAfterFirst, "ein Reconnect sollte einen neuen Accept auslösen");

        // Nachfolgender Call geht über die reconnected Verbindung.
        var second = await client.Call(EchoRequest("a2"));
        second!.Code.Should().Be(200);
        client.State.Should().Be(TrameConnectionState.Connected);

        await client.DisposeAsync();
    }

    [Fact]
    public async Task Reconnect_BackoffErschoepft_GehtInDisconnected()
    {
        await using var server = new ReconnectWsServer();
        server.Start();

        await using var client = new TrameWebSocketClient(server.BaseUrl,
            autoReconnect: true,
            reconnectDelays: new[] { TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20) });

        // Connect + Call ok.
        var first = await client.Call(EchoRequest("b1"));
        first!.Code.Should().Be(200);

        // Listener stoppen (Port verweigert neue Verbindungen -> TCP-refused) und aktuelle
        // Verbindung droppen. Jeder Reconnect-Versuch schlägt auf jeder Plattform zuverlässig
        // fehl: ein mit HTTP 400 abgewiesenes WS-Upgrade bleibt auf Linux/ManagedWebSocket in
        // ConnectAsync hängen (auf Windows hingegen schlägt es in ~30 ms fehl) — der tote Port
        // (TCP-RST) scheitert auf beiden Plattformen, nur auf Windows langsamer (~4 s pro
        // Versuch, WinHTTP-Connect-Timeout). Daher zwei Versuche und ein großzügiger Wait-Timeout.
        server.StopListener();
        await server.CloseCurrentAsync();
        await WaitForStateAsync(client, TrameConnectionState.Disconnected, timeoutMs: 20000);
        client.State.Should().Be(TrameConnectionState.Disconnected);

        await client.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_BrichtReconnectAb_KeinWeitererConnect()
    {
        await using var server = new ReconnectWsServer();
        server.Start();

        await using var client = new TrameWebSocketClient(server.BaseUrl,
            autoReconnect: true,
            reconnectDelays: new[] { TimeSpan.FromMilliseconds(50) });

        await client.Call(EchoRequest("c1"));
        await server.CloseCurrentAsync();
        await WaitForStateAsync(client, TrameConnectionState.Reconnecting, timeoutMs: 2000);

        var acceptBeforeDispose = server.AcceptCount;
        await client.DisposeAsync(); // terminal — kein weiterer Reconnect.

        // Etwas warten und prüfen, dass keine neuen Accepts durch den Reconnect entstanden.
        await Task.Delay(250);
        client.State.Should().Be(TrameConnectionState.Disconnected);
        (server.AcceptCount - acceptBeforeDispose).Should().BeLessOrEqualTo(1,
            "Dispose sollte den Hintergrund-Reconnect abbrechen (höchstens ein Versuch in Flight)");

        var act = async () => await client.Call(EchoRequest("c2"));
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task NebenlaufenderCall_WaehrendReconnect_WartetUndGelingt()
    {
        await using var server = new ReconnectWsServer();
        server.Start();

        await using var client = new TrameWebSocketClient(server.BaseUrl,
            autoReconnect: true,
            reconnectDelays: new[] { TimeSpan.FromMilliseconds(40) });

        await client.Call(EchoRequest("d0"));

        // Drop auslösen -> Reconnect startet (Hintergrund).
        await server.CloseCurrentAsync();
        await WaitForStateAsync(client, TrameConnectionState.Reconnecting, timeoutMs: 2000);

        // Call WÄHREND des laufenden Reconnects: muss auf den in-flight Reconnect
        // warten (nicht selbst verbinden) und danach gelingen.
        var callTask = client.Call(EchoRequest("d1"));
        var second = await callTask;
        second!.Code.Should().Be(200);
        client.State.Should().Be(TrameConnectionState.Connected);

        await client.DisposeAsync();
    }

    [Fact]
    public async Task InFlightCall_BeiDrop_WirftTrameException()
    {
        await using var server = new ReconnectWsServer { EchoEnabled = false };
        server.Start();

        await using var client = new TrameWebSocketClient(server.BaseUrl,
            autoReconnect: false); // In-Flight-Verhalten isoliert testen

        await client.ConnectAsync();
        client.State.Should().Be(TrameConnectionState.Connected);

        // Call absenden — Server hält offen (kein Echo) -> Call pending.
        var callTask = client.Call(EchoRequest("e1"));

        // Drop -> pending Call wird abgelehnt (Spiegel SignalR).
        await server.CloseCurrentAsync();
        Func<Task> act = async () => await callTask;
        await act.Should().ThrowAsync<TrameException>();

        await client.DisposeAsync();
    }
}