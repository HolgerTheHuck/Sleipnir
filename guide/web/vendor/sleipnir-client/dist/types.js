// Kanonische Wire-Typen des Sleipnir-Protokolls (camelCase, siehe PROTOCOL.md).
// Port aus SleipnirDeveloperUi/src/lib/types/discovery.ts; Binary-Felder sind hier
// korrekt als base64-String getypt (System.Text.Json serialisiert byte[] als
// base64), nicht als number[].
/** Ausführungsmodus für Batch-Requests (SleipnirMultiRequest.mode). */
export var ExecutionMode;
(function (ExecutionMode) {
    /** 0 — alle Requests parallel (Dependencies werden ignoriert). */
    ExecutionMode[ExecutionMode["Parallel"] = 0] = "Parallel";
    /** 1 — seriell, mit @alias-Abhängigkeitsauflösung (topologisch). */
    ExecutionMode[ExecutionMode["Serial"] = 1] = "Serial";
})(ExecutionMode || (ExecutionMode = {}));
/** Lebenszyklus-Zustand des WebSocket-Clients (Spiegel von C# SleipnirConnectionState). */
export var SleipnirConnectionState;
(function (SleipnirConnectionState) {
    /** 0 — keine aktive Verbindung (vor dem ersten Connect oder nach erschöpftem Reconnect). */
    SleipnirConnectionState[SleipnirConnectionState["Disconnected"] = 0] = "Disconnected";
    /** 1 — Verbindungsaufbau läuft. */
    SleipnirConnectionState[SleipnirConnectionState["Connecting"] = 1] = "Connecting";
    /** 2 — Verbindung steht; Calls können gesendet werden. */
    SleipnirConnectionState[SleipnirConnectionState["Connected"] = 2] = "Connected";
    /** 3 — unerwarteter Disconnect; Hintergrund-Reconnect mit Backoff läuft. */
    SleipnirConnectionState[SleipnirConnectionState["Reconnecting"] = 3] = "Reconnecting";
})(SleipnirConnectionState || (SleipnirConnectionState = {}));
//# sourceMappingURL=types.js.map