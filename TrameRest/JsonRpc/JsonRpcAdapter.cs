using System.Text.Json.Nodes;
using System.Text.Json;
using TrameCommon.Models;

namespace TrameRest.JsonRpc;

/// <summary>
/// Reiner bidirektionaler Übersetzer JSON-RPC 2.0 ↔ Trame. Keine Transport- und keine
/// DI-Abhängigkeit — orchestrierungsfrei, sodass die Übersetzung isoliert unit-testbar
/// ist. Der <see cref="JsonRpcDispatcher"/> übernimmt die Orchestrierung (Body lesen,
/// <c>ITrameCore.InvokeDi</c> rufen, Capability-Methoden beantworten).
///
/// <para><b>Fehlercode-Map</b> nach JSON-RPC 2.0: der Reserved-Bereich -32000..-32099
/// deckt server-/anwendungsdefinierte Fehler. Ein <i>Framework</i>-Routing-404
/// (Controller/Methode fehlt, erkennbar am Message-Präfix) wird als -32601
/// (Method not found) gemappt; ein <i>Business</i>-404 (<c>TrameResults.NotFound</c>)
/// bleibt -32000 (Server error). Siehe <c>JSONRPC_COMPAT.md</c>.</para>
/// </summary>
internal static class JsonRpcAdapter
{
    public const string JsonRpcVersion = "2.0";

    /// <summary>
    /// Parst ein JSON-RPC-Item (Objekt) in ein <see cref="ParsedRpcItem"/>. Validiert
    /// jsonrpc/method/params und übersetzt in einen <see cref="TrameRequest"/> (oder
    /// markiert als Capability / Invalid). Notifications (id fehlt/null) werden
    /// ausgeführt, erzeugen aber keine Response — der Dispatcher wertet
    /// <see cref="ParsedRpcItem.IsNotification"/> aus.
    /// </summary>
    public static ParsedRpcItem ParseRequest(JsonElement item)
    {
        var result = new ParsedRpcItem();

        if (item.ValueKind != JsonValueKind.Object)
            return Invalid(result, -32600, "Invalid Request: expected a JSON object.");

        // id (Originaltyp bewahren; fehlt/null → Notification).
        if (item.TryGetProperty("id", out var idEl) && idEl.ValueKind != JsonValueKind.Null)
        {
            result.Id = idEl;
            result.IsNotification = false;
        }

        if (!item.TryGetProperty("jsonrpc", out var vEl)
            || vEl.ValueKind != JsonValueKind.String
            || vEl.GetString() != JsonRpcVersion)
            return Invalid(result, -32600, "Invalid Request: 'jsonrpc' must be \"2.0\".");

        if (!item.TryGetProperty("method", out var mEl)
            || mEl.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(mEl.GetString()))
            return Invalid(result, -32600, "Invalid Request: 'method' must be a non-empty string.");

        var method = mEl.GetString()!;

        // Capability-Methoden (Adoptions-Brücke): nicht übersetzen — der Dispatcher
        // beantwortet sie direkt (Discovery bzw. statisches Manifest).
        if (method == "trame.discover" || method == "trame.capabilities")
        {
            result.IsValid = true;
            result.Capability = method;
            return result;
        }

        // Controller.Method — am letzten Punkt splitten (Controller-Namen dürfen
        // selbst Punkte tragen, z.B. "Customer.Address.Contact").
        var dot = method.LastIndexOf('.');
        if (dot <= 0 || dot >= method.Length - 1)
            return Invalid(result, -32600, "Invalid Request: method must be 'Controller.Method'.");

        var controller = method[..dot];
        var methodName = method[(dot + 1)..];

        // params: Object (named) | Array (positional) | absent/null (keine).
        JsonElement? pEl = item.TryGetProperty("params", out var pe) && pe.ValueKind != JsonValueKind.Null
            ? pe : null;
        if (pEl.HasValue
            && pEl.Value.ValueKind != JsonValueKind.Object
            && pEl.Value.ValueKind != JsonValueKind.Array)
            return Invalid(result, -32600, "Invalid Request: 'params' must be an array or object.");

        result.IsValid = true;
        result.Request = new TrameRequest
        {
            Controller = controller,
            Method = methodName,
            Id = result.Id.HasValue ? IdToString(result.Id.Value) : null,
            Params = TranslateParams(pEl),
        };
        return result;
    }

    /// <summary>
    /// Übersetzt JSON-RPC <c>params</c> in den nativen Trame-<see cref="TrameRequest.Params"/>-
    /// <see cref="JsonNode"/> (ein <see cref="JsonArray"/> von <c>{ parameterName, data, num }</c>).
    /// Named (Object) → <see cref="TrameParameter.ParameterName"/> = Schlüssel; positional (Array)
    /// → <see cref="TrameParameter.Num"/> = Index (der Server fällt bei nicht treffendem Namen
    /// auf den Positional-Index zurück). <see cref="TrameParameter.Data"/> ist der native
    /// JSON-Wert (kein JSON-String mehr) — <c>JsonNode.Parse(GetRawText())</c> überführt das
    /// eingehende <c>JsonElement</c> direkt in einen nativen Knoten ohne Double-Wrapping.
    /// </summary>
    private static JsonNode? TranslateParams(JsonElement? pEl)
    {
        var parameters = new List<TrameParameter>();
        if (pEl.HasValue)
        {
            var p = pEl.Value;
            if (p.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in p.EnumerateObject())
                    parameters.Add(new TrameParameter { ParameterName = prop.Name, Data = JsonNode.Parse(prop.Value.GetRawText()) });
            }
            else if (p.ValueKind == JsonValueKind.Array)
            {
                int i = 0;
                foreach (var el in p.EnumerateArray())
                {
                    parameters.Add(new TrameParameter { Num = i, ParameterName = $"param{i}", Data = JsonNode.Parse(el.GetRawText()) });
                    i++;
                }
            }
        }
        // Default-Serializer (PascalCase) reicht — der Invoker liest case-insensitiv
        // (PropertyNameCaseInsensitive = true). SerializeToNode gibt die Liste direkt als
        // nativen JsonArray-Knoten aus; jedes Data (JsonNode) wird roh eingebettet.
        return JsonSerializer.SerializeToNode(parameters);
    }

    private static string IdToString(JsonElement id) =>
        id.ValueKind == JsonValueKind.String ? (id.GetString() ?? string.Empty) : id.GetRawText();

    private static ParsedRpcItem Invalid(ParsedRpcItem r, int code, string message)
    {
        r.IsValid = false;
        r.ErrorCode = code;
        r.ErrorMessage = message;
        return r;
    }

    /// <summary>
    /// Mappt einen Trame-Status-Code (plus semantische Kategorie, Phase 1) auf einen
    /// JSON-RPC-Fehlercode. Die Kategorie löst die bisherige String-Präfix-Kopplung ab:
    /// statt <c>errorMessage.StartsWith("Controller '…")</c> wird <see cref="TrameErrorCategory.NotFound"/>
    /// mit Routing-Kontext unterschieden — sauberer und nicht an Invoker-Fehlermeldungen gekoppelt.
    /// </summary>
    /// <remarks>
    /// Phase 1 — siehe <c>docs/design/phase-1-interceptor-pipeline.md</c>. Die Category ist
    /// *zusätzlich* zum numerischen Code vorhanden; falls <paramref name="category"/> == None
    /// (ältere Responses ohne Category), fällt die Map auf den numerischen Code zurück (wie v1.0).
    /// </remarks>
    public static int MapErrorCode(int trameCode, TrameCommon.Results.TrameErrorCategory category, string? errorMessage)
    {
        // Routing-404 (Controller/Methode fehlt) → -32601 (Method not found).
        // Ab v1.1 (Phase 1) primär über die Category + Message-Präfix (Fallback für
        // Responses ohne Category): NotFound + "Controller '/Method '"-Präfix = Routing.
        // Business-NotFound (z. B. "Customer '99' not found") fällt durch zu -32000.
        if (trameCode == 404 && errorMessage is not null
            && (errorMessage.StartsWith("Controller '", StringComparison.Ordinal)
                || errorMessage.StartsWith("Method '", StringComparison.Ordinal)))
            return -32601;

        // Phase 1: Category-basierte Map (präziser als numerischer Code allein).
        // None/Default fällt durch zur numerischen switch (unten) — Abwärtskompatibilität.
        if (category != TrameCommon.Results.TrameErrorCategory.None)
        {
            return category switch
            {
                TrameCommon.Results.TrameErrorCategory.InvalidArgument => -32602,   // Invalid params
                TrameCommon.Results.TrameErrorCategory.Unauthenticated => -32001,   // Auth (401)
                TrameCommon.Results.TrameErrorCategory.PermissionDenied => -32001,  // Auth (403)
                TrameCommon.Results.TrameErrorCategory.NotFound => -32000,          // Business-NotFound (catch-all)
                TrameCommon.Results.TrameErrorCategory.Conflict => -32000,
                TrameCommon.Results.TrameErrorCategory.FailedPrecondition => -32602,// Invalid params (Dep-Kette)
                TrameCommon.Results.TrameErrorCategory.ResourceExhausted => -32000,
                TrameCommon.Results.TrameErrorCategory.Internal => -32603,          // Internal error
                TrameCommon.Results.TrameErrorCategory.Unavailable => -32003,       // Server error (overload)
                TrameCommon.Results.TrameErrorCategory.Cancelled => -32000,         // catch-all (499)
                _ => -32000,
            };
        }

        // Fallback: numerischer Code (v1.0-Verhalten für Responses ohne Category).
        return trameCode switch
        {
            400 or 422 => -32602,   // Invalid params (Bindung/Validierung)
            401 or 403 => -32001,    // Server error: Auth
            500 => -32603,           // Internal error
            _ => -32000,             // Server error (catch-all: Business-404, 429, 499, …)
        };
    }

    /// <summary>
    /// Baut ein JSON-RPC-Response-Objekt aus einem <see cref="TrameResponse"/> (Erfolg
    /// oder Fehler). <c>result</c>/<c>error</c> sind mutually exclusive — realisiert über
    /// zwei getrennte <see cref="JsonObject"/>-Pfade. Die id wird mit Originaltyp
    /// (Number/String) zurückechoot.
    /// </summary>
    public static JsonObject MapResponse(TrameResponse trame, JsonElement? id)
    {
        var obj = new JsonObject { ["jsonrpc"] = JsonRpcVersion };
        if (trame.IsSuccess)
        {
            obj["result"] = ResultNode(trame);
        }
        else
        {
            var msg = trame.Error?.Message ?? $"Trame error {trame.Code}.";
            var err = new JsonObject
            {
                ["code"] = MapErrorCode(trame.Code, trame.Error?.Category ?? TrameCommon.Results.TrameErrorCategory.None, trame.Error?.Message),
                ["message"] = msg,
            };
            // error.data: bevorzugt das strukturierte Trame-Data (z.B. ProblemDetails),
            // sonst die Error.Details als String. Beides null → Feld entfällt.
            var dataNode = ErrorDataNode(trame);
            if (dataNode is not null) err["data"] = dataNode;
            obj["error"] = err;
        }
        SetId(obj, id);
        return obj;
    }

    /// <summary>Baut ein Erfolgs-Response für eine Capability-Methode (result vorgegeben).</summary>
    public static JsonObject BuildResult(JsonNode? result, JsonElement? id)
    {
        var obj = new JsonObject { ["jsonrpc"] = JsonRpcVersion, ["result"] = result };
        SetId(obj, id);
        return obj;
    }

    /// <summary>Baut ein JSON-RPC-Fehler-Response (für Invalid Request / Parse error /
    ///  interne Adapterfehler). Für Parse error ist <paramref name="id"/> null.</summary>
    public static JsonObject BuildError(int code, string message, JsonElement? id, JsonNode? data = null)
    {
        var err = new JsonObject { ["code"] = code, ["message"] = message };
        if (data is not null) err["data"] = data;
        var obj = new JsonObject { ["jsonrpc"] = JsonRpcVersion, ["error"] = err };
        SetId(obj, id);
        return obj;
    }

    /// <summary>
    /// Strukturiertes Ergebnis für <c>trame.capabilities</c> — die statische
    /// Adoptions-Brücke: listet die Trame-Stärken auf, die im JSON-RPC-Compat-Modus
    /// nicht erreichbar sind, sodass ein JSON-RPC-Client weiß, wohin er wechseln kann.
    /// </summary>
    public static JsonObject CapabilitiesManifest() => new()
    {
        ["nativeClient"] = true,                 // TrameClient (REST/WS/SignalR) verfügbar
        ["chaining"] = true,                     // @alias-Dependency-Chaining (nur native)
        ["executionModes"] = new JsonArray("Parallel", "Serial"),
        ["binary"] = new JsonObject { ["supported"] = true, ["encoding"] = "base64", ["transport"] = "native" },
        ["bindingModes"] = new JsonArray("Weak", "Strict", "Paranoid"),
        ["transports"] = new JsonArray("rest", "websocket", "signalr"),
        ["compatMode"] = new JsonObject
        {
            ["supported"] = true,
            ["mode"] = "Parallel",
            ["routing"] = "Controller.Method",
            ["limits"] = "no chaining, no execution-mode selection, no binary out-of-band, no streaming"
        },
    };

    private static JsonNode ResultNode(TrameResponse trame)
    {
        // 204 / void → JSON-null (MUSS als Schlüssel vorhanden bleiben — Zuweisung von
        // C#-null an einen JsonObject-Indexer würde den Schlüssel entfernen, was JSON-RPC
        // verletzt). Binär (Content, gepuffert) → Base64-String. Sonst strukturiertes
        // Data (lazy aus DataBytes materialisiert).
        if (trame.Data.HasValue)
            return JsonNode.Parse(trame.Data.Value.GetRawText());
        if (trame.Content is not null)
            return Convert.ToBase64String(trame.Content);
        return JsonNull;
    }

    /// <summary>Ein JSON-null als nicht-nuller JsonNode (JsonValue), damit die
    ///  Zuweisung an einen JsonObject-Indexer den Schlüssel erhält (C#-null würde ihn
    ///  entfernen — JSON-RPC verlangt result/id als null, nicht abwesend). Erzeugt
    ///  über einen null-kind JsonElement, weil <c>JsonNode.Parse("null")</c> selbst
    ///  C#-null liefert.</summary>
    private static readonly JsonNode JsonNull = JsonValue.Create(CreateNullElement());

    private static JsonElement CreateNullElement()
    {
        using var doc = JsonDocument.Parse("null");
        return doc.RootElement.Clone(); // Clone überlebt die Dispose des Dokuments.
    }

    private static JsonNode? ErrorDataNode(TrameResponse trame)
    {
        if (trame.Data.HasValue)
            return JsonNode.Parse(trame.Data.Value.GetRawText());
        if (!string.IsNullOrEmpty(trame.Error?.Details))
            return trame.Error!.Details;
        return null;
    }

    private static void SetId(JsonObject obj, JsonElement? id)
    {
        // JSON-RPC verlangt id IMMER (als null, wenn nicht bestimmbar). C#-null würde
        // den Schlüssel entfernen → explizit ein JSON-null setzen (JsonNull).
        JsonNode idNode;
        if (!id.HasValue)
        {
            idNode = JsonNull;
        }
        else
        {
            var k = id.Value.ValueKind;
            idNode = k == JsonValueKind.String ? (JsonNode)id.Value.GetString()!
                : k == JsonValueKind.Number ? JsonNode.Parse(id.Value.GetRawText())
                : JsonNull; // Spec: id soll String/Number sein.
        }
        obj["id"] = idNode;
    }
}