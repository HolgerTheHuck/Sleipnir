using MessagePack;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
// SleipnirException is provided via global using alias from GlobalUsings.cs

namespace SleipnirClient.Sleipnir;

/// <summary>
/// Ein Sleipnir-Client, der über SignalR mit dem Server kommuniziert.
/// Unterstützt automatische Wiederverbindung (SignalR-eigen) und asynchrone
/// Einzel- und Multi-Requests.
/// </summary>
public class SleipnirSignalrClient : SleipnirClientBase, ISleipnirClient, IAsyncDisposable
{
    private readonly HubConnection _connection;
    private bool _disposed;
    private string _jwtToken = string.Empty;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    // Repräsentiert einen laufenden Verbindungsversuch; konkurrierende Caller
    // erwarten dieselbe Task statt selbst StartAsync aufzurufen oder abzuweisen (B1).
    private Task<bool>? _connectingTask;

    /// <summary>
    /// Erstellt einen neuen SignalR-Client. Optionaler <paramref name="bearer"/>
    /// (JWT) an zweiter Stelle, damit <c>new SleipnirSignalrClient(url, "token")</c>
    /// eindeutig den Bearer setzt (A4) und nicht mit <paramref name="hubPath"/> kollidiert.
    /// </summary>
    public SleipnirSignalrClient(string server,
        string? bearer = null,
        string? hubPath = "sleipnirhub", bool useMessagePack = true,
        TimeSpan? handshakeTimeout = null, TimeSpan? serverTimeout = null,
        TimeSpan? keepAliveInterval = null)
    {
        _jwtToken = bearer ?? string.Empty;

        var baseUrl = server.TrimEnd('/');
        if (!baseUrl.EndsWith('/'))
            baseUrl += "/";

        var builder = new HubConnectionBuilder()
            .WithUrl($"{baseUrl}{hubPath ?? "sleipnirhub"}", options =>
            {
                // AccessTokenProvider IMMER registrieren (A4): der Provider liest
                // das Token lazy zur Call-Zeit. _jwtToken ist oben bereits gesetzt.
                options.AccessTokenProvider = () =>
                    Task.FromResult<string?>(string.IsNullOrEmpty(_jwtToken) ? null : _jwtToken);
            });

        if (useMessagePack)
        {
            // Custom Resolver (server-seitig gespiegelt): JsonElement (SleipnirResponse.Data
            // seit dem Single-Pass-Fix) wird als native MessagePack-Tokens serialisiert —
            // keine Double-Wrapping-Tax. Gleicher Source wie SleipnirHub (je eigene MP-Version).
            builder.AddMessagePackProtocol(o =>
                o.SerializerOptions = MessagePackSerializerOptions.Standard
                    .WithResolver(SleipnirCommon.MessagePack.JsonElementResolver.Instance));
        }

        builder.WithAutomaticReconnect(new[]
        {
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)
        });

        _connection = builder.Build();

        if (handshakeTimeout.HasValue)
            _connection.HandshakeTimeout = handshakeTimeout.Value;
        if (serverTimeout.HasValue)
            _connection.ServerTimeout = serverTimeout.Value;
        if (keepAliveInterval.HasValue)
            _connection.KeepAliveInterval = keepAliveInterval.Value;
    }

    /// <summary>
    /// Sendet einen einzelnen SleipnirRequest an den Server und gibt die Antwort zurück.
    /// </summary>
    public override async Task<SleipnirResponse?> Call(SleipnirRequest? request, CancellationToken ct = default)
    {
        if (request == null)
            return null;

        if (!await Connect())
            throw new SleipnirException("Not connected to server.");

        try
        {
            if (await _connection.InvokeCoreAsync(
                    "DoWork",
                    typeof(SleipnirResponse),
                    new object[] { request },
                    ct
                ) is SleipnirResponse r)
            {
                return r;
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new SleipnirException("Error from server.", ex);
        }
    }

    /// <summary>
    /// Sendet mehrere SleipnirRequests (Batch) an den Server und gibt die Antworten zurück.
    /// </summary>
    public override async Task<IEnumerable<SleipnirResponse?>?> Call(SleipnirMultiRequest? request, CancellationToken ct = default)
    {
        if (request == null)
            return null;

        if (!await Connect())
            throw new SleipnirException("Not connected to server.");

        try
        {
            if (await _connection.InvokeCoreAsync(
                    "DoWorkMany",
                    typeof(IEnumerable<SleipnirResponse?>),
                    new object[] { request },
                    ct
                ) is IEnumerable<SleipnirResponse?> r)
            {
                return r;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new SleipnirException("Error from server.", ex);
        }

        return new List<SleipnirResponse?>();
    }

    /// <summary>
    /// Baut die Verbindung zum Server auf. Ein laufender Verbindungsversuch wird
    /// von konkurrierenden Callern **gemeinsam erwartet** (B1) statt abgewiesen —
    /// parallele erste Calls werfen also nicht "Not connected". SignalRs
    /// <c>WithAutomaticReconnect</c> kümmert sich um Wiederverbindung; <c>.State</c>
    /// ist autoritativ, daher keine eigenen Lifecycle-Handler nötig.
    /// </summary>
    private Task<bool> Connect()
    {
        // Fast path: bereits verbunden.
        if (_connection.State == HubConnectionState.Connected)
            return Task.FromResult(true);

        // Ein Connect/Reconnect läuft bereits -> dieselbe Task mitwarten (B1),
        // statt sofort "Not connected" zu werfen.
        if (_connection.State is HubConnectionState.Connecting or HubConnectionState.Reconnecting
            && _connectingTask is not null)
        {
            return _connectingTask;
        }

        return ConnectSlowPathAsync();
    }

    private async Task<bool> ConnectSlowPathAsync()
    {
        await _connectLock.WaitAsync();
        try
        {
            // Double-Check nach Lock-Erwerb: ein anderer Caller hat evtl. schon
            // verbunden oder einen Versuch gestartet.
            if (_connection.State == HubConnectionState.Connected)
                return true;
            if (_connection.State is HubConnectionState.Connecting or HubConnectionState.Reconnecting
                && _connectingTask is not null)
            {
                return await _connectingTask;
            }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _connectingTask = tcs.Task;
            try
            {
                await _connection.StartAsync();
                tcs.TrySetResult(true);
                return true;
            }
            catch (HttpRequestException)
            {
                // Transienter Netzwerkfehler -> Caller kann es erneut versuchen.
                tcs.TrySetResult(false);
                return false;
            }
            catch (Exception ex)
            {
                var sleipnirEx = new SleipnirException("Error connecting to server via SignalR", ex);
                tcs.TrySetException(sleipnirEx);
                throw sleipnirEx;
            }
            finally
            {
                // Versuch beendet -> Feld freigeben, damit ein späterer Retry
                // (z.B. nach Closed) einen neuen Versuch starten kann.
                _connectingTask = null;
            }
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_connection.State == HubConnectionState.Connected)
        {
            await _connection.StopAsync();
        }

        await _connection.DisposeAsync();
        _connectLock.Dispose();
    }
}