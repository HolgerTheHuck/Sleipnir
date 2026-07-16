namespace TrameHub.Extensions
{
    public class TrameOptions
    {
        public bool EnableDetailedErrors { get; set; }

        public long? MaximumReceiveMessageSize { get; set; }

        public int? StreamBufferCapacity { get; set; }

        public int MaximumParallelInvocationsPerClient { get; set; }

        public bool UseMessagePack { get; set; }

        public bool UseSignalR { get; set; }

        /// <summary>
        /// Maximum number of RPC calls per client per second (0 = unlimited).
        /// </summary>
        public int RateLimitPermitLimit { get; set; } = 0;

        /// <summary>
        /// Time window for rate limiting in seconds.
        /// </summary>
        public int RateLimitWindowSeconds { get; set; } = 10;

        /// <summary>
        /// Wenn <c>true</c>, verlangt das Framework für jeden RPC-Aufruf einen
        /// authentifizierten User (North-Bound-Default-Deny). Eine Methode ohne
        /// <c>[TrameAuthorise]</c> ist dann nur noch erreichbar, wenn der User
        /// authentifiziert ist; eine Methode mit <c>[TrameAnonymous]</c> bleibt
        /// explizit offen (Opt-out, z.B. Health/Ping). <c>[TrameAuthorise]</c>
        /// prüft weiterhin Rolle/Authentication wie bisher. Default <c>false</c>
        /// (South-Bound-Default-Allow — nicht breaking). Wird über <c>AddTrame</c>
        /// an den Invoker durchgereicht und am WebSocket-Upgrade sowie am
        /// Discovery-Endpunkt zusätzlich als Transport-Gate durchgesetzt.
        /// Siehe <c>SECURITY.md</c>.
        /// </summary>
        public bool RequireAuthentication { get; set; } = false;

        /// <summary>
        /// Maximale Anzahl Requests in einem Batch (Default 0 = unbegrenzt, nicht
        /// breaking). Schützt den Server vor Fan-Out-DoS: ein einzelner 1-MB-Body
        /// kann ohne Cap Tausende Requests enthalten, die per Task.WhenAll
        /// gleichzeitig feuern. Enforceiert am Batch-Einstieg des Invokers
        /// (Backstop) und an den Multi-Endpunkten (REST /json/multi, WebSocket,
        /// JSON-RPC-Batch) als frühes 400-Gate. North-Bound empfohlen &gt; 0.
        /// Siehe <c>SECURITY.md</c>.
        /// </summary>
        public int MaximumBatchSize { get; set; } = 0;

        /// <summary>
        /// Maximale Länge eines client-kontrollierten JsonPath in einem
        /// <c>dependencyMapping</c> (Default 256, 0 = unbegrenzt). Der Client
        /// wählt den Pfad und (über die Provider-Wahl) das JSON, gegen das er
        /// ausgewertet wird — ein langer Pfad kann die JsonPath.Net-Evaluation
        /// (insb. <c>$..</c>) zu einem CPU-Stall treiben. Der Cap wird vor dem
        /// Parsen geprüft; ein zu langer Pfad wird verworfen (Alias bleibt
        /// ungesetzt → der Dependente erhält die Propagierungs-400). Siehe
        /// <c>SECURITY.md</c>.
        /// </summary>
        public int MaxDependencyPathLength { get; set; } = 256;

        /// <summary>
        /// Wenn <c>false</c>, werden rekursive-descent-Pfade (<c>$..foo</c>) in
        /// client-kontrollierten <c>dependencyMapping</c>-Pfaden abgelehnt (vor
        /// der teuren Evaluation). Default <c>true</c> (nicht breaking —
        /// <c>$..</c> ist ein legitimes JsonPath-Mittel). North-Bound-Härtung
        /// kann ihn auf <c>false</c> stellen, um den teuersten Pfad-Typ
        /// auszuschließen. Siehe <c>SECURITY.md</c>.
        /// </summary>
        public bool AllowRecursiveDescent { get; set; } = true;

        /// <summary>
        /// Maximale Elementzahl eines Array-/Collection-Parameters (Default 1000, 0 = unbegrenzt).
        /// Schützt vor Kardinalitäts-Sprengung beim @alias-Whole-Collection-Passthrough, wo der
        /// Server das Array aus einem früheren Ergebnis zur Laufzeit erzeugt — Body-Size-Limits
        /// greifen dort nicht, weil die Kardinalität serverseitig entsteht. Enforceiert im
        /// Invoker vor dem Methodenaufruf (Top-Level-Parameter; string/byte[] ausgenommen).
        /// </summary>
        public int MaxParameterArrayLength { get; set; } = 1000;

        /// <summary>
        /// Maximale Elementzahl eines Array-/Collection-Rückgabewerts (Default 10000, 0 = unbegrenzt).
        /// Verhindert, dass ein Einzelergebnis den Server in Memory treibt. Greift auf
        /// materialisierte Collections (List/Array/Dictionary) und IAsyncEnumerable-Streams
        /// (Early-Stop beim Konsumieren). Top-Level-Result; string/byte[] ausgenommen.
        /// </summary>
        public int MaxResultElementCount { get; set; } = 10000;

        /// <summary>
        /// Wie ein extrahiertes @alias-Fragment an den Consumer-Parametertyp gebunden wird
        /// (Default <see cref="TrameCommon.Models.AliasBindingMode.Weak"/>). Weak = STJ-
        /// Duck-Typing mit stillen Defaults (mächtig; Subset-Fan-out funktioniert).
        /// Strict = das Fragment muss den Consumer-Typ vollständig decken (jede public
        /// read-write Eigenschaft muss im Fragment vorhanden sein), sonst 400 — schaltet
        /// nur die object→object-silent-default-Zeile um; cross-kind ist in beiden Modi
        /// 400. Greift nur @alias-sourced Parameter. Siehe DEPENDENCY_BINDING.md.
        /// </summary>
        public AliasBindingMode AliasBindingMode { get; set; } = AliasBindingMode.Weak;

        /// <summary>
        /// Schaltet den JSON-RPC-2.0-Kompatibilitäts-Endpoint (POST /api/trame/jsonrpc)
        /// frei. Default <c>false</c> — Opt-in. Mappt JSON-RPC-Requests auf den
        /// Trame-Invoker (Parallel-Modus, Routing <c>Controller.Method</c>, named und
        /// positional params, Batch, Notifications). Chaining, Ausführungsmodus-Auswahl
        /// und binäres Out-of-Band bleiben dem nativen Trame-Wire vorbehalten. Siehe
        /// <c>JSONRPC_COMPAT.md</c>.
        /// </summary>
        public bool EnableJsonRpcCompat { get; set; } = false;
    }
}
