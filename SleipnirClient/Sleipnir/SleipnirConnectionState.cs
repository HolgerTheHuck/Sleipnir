namespace SleipnirClient.Sleipnir;

/// <summary>
/// Lebenszyklus-Zustände des WebSocket-Clients (spiegelt SignalRs
/// <c>HubConnectionState</c> nach, ohne eine SignalR-Abhängigkeit einzuführen).
/// </summary>
public enum SleipnirConnectionState
{
    /// <summary>Keine aktive Verbindung (vor dem ersten Connect oder nach erschöpftem Reconnect).</summary>
    Disconnected = 0,

    /// <summary>Verbindungsaufbau läuft.</summary>
    Connecting = 1,

    /// <summary>Verbindung steht; Calls können gesendet werden.</summary>
    Connected = 2,

    /// <summary>Unerwarteter Disconnect; Hintergrund-Reconnect mit Backoff läuft.</summary>
    Reconnecting = 3,
}