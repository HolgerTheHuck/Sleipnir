using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
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
/// Reconnect tests for <see cref="TrameWebSocketClient"/>. Because <see cref="ClientWebSocket"/>
/// is sealed, a minimal WebSocket server runs in-process. It is built on <b>Kestrel</b>
/// (the same stack the product ships), not <c>HttpListener</c>: a server-side abort
/// (<c>WebSocket.Dispose()</c>) must reliably surface as a transport error in the client's
/// <c>ReceiveAsync</c> on every platform. On Linux, <c>HttpListener</c>'s WebSocket abort
/// did <i>not</i> unblock a <c>ClientWebSocket</c> (<c>ManagedWebSocket</c>) read, so the
/// client never noticed the drop and reconnect never started. Kestrel aborts the underlying
/// TCP connection, which the client detects promptly on Linux too.
/// </summary>
public class TrameWebSocketClientReconnectTests
{
    /// <summary>Minimal Kestrel WS server: echoes Trame responses, counts accepts, holds the current socket.</summary>
    private sealed class ReconnectWsServer : IAsyncDisposable
    {
        private WebApplication? _app;
        private readonly object _gate = new();
        public string BaseUrl { get; private set; } = "";
        public int AcceptCount;
        public WebSocket? Current;
        public bool EchoEnabled = true;

        public void Start()
        {
            // Reserve a free port via a throwaway TcpListener (port 0 -> OS assigns one),
            // then bind Kestrel to exactly that port. Kestrel does not resolve port 0
            // dynamically the way HttpListener does, so the reservation is needed.
            var tcp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            tcp.Start();
            var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
            tcp.Stop();

            var url = $"http://localhost:{port}";
            var builder = WebApplication.CreateBuilder();
            // Keep test output clean — no console logging from the ephemeral host.
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls(url);
            var app = builder.Build();
            app.UseWebSockets();

            app.Map("/tramews", async context =>
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
                    Current = ws;
                }
                try
                {
                    await EchoLoopAsync(ws);
                }
                finally
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(Current, ws))
                            Current = null;
                    }
                }
            });

            // StartAsync returns once Kestrel is accepting connections — avoids the
            // first-connect race a blocking Run would introduce.
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
                if (!EchoEnabled)
                    continue; // Keep the call open (for the in-flight drop test)

                var text = Encoding.UTF8.GetString(buffer, 0, r.Count);
                var id = ExtractId(text);
                var respJson = $"{{\"code\":200,\"data\":null,\"id\":\"{id}\"}}";
                var bytes = Encoding.UTF8.GetBytes(respJson);
                try { await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None); }
                catch { return; }
            }
        }

        public async Task CloseCurrentAsync()
        {
            WebSocket? ws;
            lock (_gate) ws = Current;
            if (ws != null)
            {
                // Send a close frame (CloseOutputAsync = write direction only; it does NOT
                // wait for the peer's ack, so it cannot conflict with the echo loop's pending
                // ReceiveAsync the way CloseAsync would). The client's read loop receives it
                // as a Close message and treats any server-initiated close as a terminal
                // error → CancelAllPending + StartReconnect. A close frame is data on the
                // wire, so it is delivered reliably on both Windows and Linux — unlike an
                // abort (Dispose), which a Kestrel client's idle ReceiveAsync does not
                // promptly notice on Windows.
                try { await ws.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "drop", CancellationToken.None); }
                catch { /* ignore */ }
            }
        }

        public async ValueTask DisposeAsync()
        {
            var app = _app;
            if (app != null)
            {
                try { await app.StopAsync(); } catch { /* ignore */ }
                try { await app.DisposeAsync(); } catch { /* ignore */ }
            }
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

        // Socket-Factory: der erste Aufruf (Konstruktor) liefert einen echten Socket, der sich
        // gegen den Live-Server verbindet. Jeder weitere Aufruf (Reconnect-Versuche) liefert
        // einen vorab disposeden Socket -> ConnectAsync wirft sofort ObjectDisposedException,
        // deterministisch auf jeder Plattform (~8 ms, kein TCP-RST-Timing, kein HttpListener-
        // Teardown nötig). So erschöpft der Backoff verlässlich -> Disconnected. (Ein mit
        // HTTP 400 abgewiesenes Upgrade hängt auf Linux/ManagedWebSocket in ConnectAsync; ein
        // toter Port braucht auf Windows ~4 s pro Versuch — beides nicht plattformunabhängig.)
        var factoryCalls = 0;
        Func<ClientWebSocket> factory = () =>
        {
            var n = Interlocked.Increment(ref factoryCalls);
            if (n == 1)
                return new ClientWebSocket();
            var dead = new ClientWebSocket();
            dead.Dispose();
            return dead;
        };

        await using var client = new TrameWebSocketClient(server.BaseUrl,
            autoReconnect: true,
            reconnectDelays: new[] { TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20) },
            socketFactory: factory);

        // Connect + Call ok (realer Socket gegen den Live-Server).
        var first = await client.Call(EchoRequest("b1"));
        first!.Code.Should().Be(200);

        // Aktuelle Verbindung droppen -> ReadLoop beendet sich -> Hintergrund-Reconnect startet.
        // Dessen Versuche nutzen den disposeden Socket und schlagen sofort fehl -> Backoff erschöpft.
        await server.CloseCurrentAsync();
        await WaitForStateAsync(client, TrameConnectionState.Disconnected, timeoutMs: 3000);
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

    [Fact]
    public async Task PendingCall_Cancellation_PropagatesFaithfulToken_AndClientStaysUsable()
    {
        // R4: SetCanceled (non-Try) raced the reader thread's TrySetResult and the loser threw
        // an unobserved InvalidOperationException inside a thread-pool cancellation callback —
        // potentially process-terminating. TrySetCanceled(token) no-ops on a completed TCS
        // and keeps OperationCanceledException.CancellationToken faithful. We cancel a pending
        // call (server does not echo) and assert the OCE carries our token, then that the
        // client is still usable — the race must not crash the process.
        await using var server = new ReconnectWsServer { EchoEnabled = false };
        server.Start();

        await using var client = new TrameWebSocketClient(server.BaseUrl,
            autoReconnect: false);

        await client.ConnectAsync();

        using var cts = new CancellationTokenSource();
        // Send the call; the server holds it open (no echo) → call stays pending.
        var callTask = client.Call(EchoRequest("f1"), cts.Token);
        await Task.Delay(120); // let the send complete server-side

        cts.Cancel();

        Func<Task> act = () => callTask;
        var ex = await act.Should().ThrowAsync<OperationCanceledException>();
        ex.Which.CancellationToken.Should().Be(cts.Token,
            "TrySetCanceled(token) must keep the OCE's CancellationToken faithful");

        // The client must still serve a follow-up call — the TCS race must not have crashed.
        server.EchoEnabled = true;
        var followUp = await client.Call(EchoRequest("f2"));
        followUp!.Code.Should().Be(200);

        await client.DisposeAsync();
    }
}