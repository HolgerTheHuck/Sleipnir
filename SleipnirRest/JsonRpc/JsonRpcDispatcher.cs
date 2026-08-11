using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using SleipnirCommon.Models;
using SleipnirCore.Model.Messages.Mex;
using SleipnirCore.Services;

namespace SleipnirRest.JsonRpc;

/// <summary>
/// Orchestriert einen JSON-RPC-2.0-Request über den Sleipnir-Invoker. Liest den Body,
/// parst die Items, dispatched die Capability-Methoden (<c>sleipnir.discover</c> /
/// <c>sleipnir.capabilities</c>) direkt, ruft <c>ISleipnirCore.InvokeDi</c> im Parallel-Modus
/// für den RESTEN auf und baut die JSON-RPC-Response(s) auf. Die reine Übersetzung
/// steckt in <see cref="JsonRpcAdapter"/>; diese Klasse kümmert sich um Reihenfolge,
/// Notifications, Batch und die 200/204-Hüllen-Regeln (envelope-at-200 wie der
/// Sleipnir-REST-Pfad — Fehler liegen immer in der 200er-Hülle, nicht im HTTP-Status).
/// </summary>
internal static class JsonRpcDispatcher
{
    // The discovery wire uses the deterministic options (docs/discovery-schema.md §11) so the
    // JSON-RPC sleipnir.discover payload is byte-identical to GET /api/sleipnir/discovery.
    private static readonly JsonSerializerOptions DiscoveryOptions = DiscoverySerialization.Options;

    /// <summary>
    /// Liefert (statusCode, bodyJson). <c>bodyJson == null</c> → 204 (alle Items waren
    /// Notifications). Sonst immer 200 mit JSON-RPC-Envelope im Body; auch Fehler
    /// liegen in der Hülle (JSON-RPC-konform).
    /// </summary>
    public static async Task<(int StatusCode, JsonNode? Body)> DispatchAsync(
        ISleipnirCore core, HttpContext? ctx, Stream body, CancellationToken ct)
    {
        string bodyText;
        using (var reader = new StreamReader(body, Encoding.UTF8, leaveOpen: true))
            bodyText = await reader.ReadToEndAsync(ct);

        if (string.IsNullOrWhiteSpace(bodyText))
            return (200, JsonRpcAdapter.BuildError(-32700, "Parse error: empty request body.", null));

        JsonDocument doc;
        try { doc = JsonDocument.Parse(bodyText); }
        catch (JsonException) { return (200, JsonRpcAdapter.BuildError(-32700, "Parse error: malformed JSON.", null)); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                // Einzel-Request als 1-Element-Batch behandeln — Notifications werden
                // so ebenfalls ausgeführt (fire-and-forget), auch wenn nichts zurückkommt.
                var arr = await HandleBatchAsync(core, ctx, new[] { root }, ct);
                if (arr is null) return (204, null);
                if (arr.Count == 1)
                {
                    var n = arr[0];
                    arr.RemoveAt(0);
                    return (200, n);
                }
                return (200, arr);
            }
            if (root.ValueKind == JsonValueKind.Array)
            {
                if (root.GetArrayLength() == 0)
                    return (200, JsonRpcAdapter.BuildError(-32600, "Invalid Request: batch array must not be empty.", null));
                // Batch-Cap-Gate (North-Bound-Härtung): früher Fehler statt Fan-Out-DoS.
                // Quelle ist ISleipnirCore (SleipnirOptions → Invoker → Interface → Transporte).
                if (core.MaximumBatchSize > 0 && root.GetArrayLength() > core.MaximumBatchSize)
                    return (200, JsonRpcAdapter.BuildError(-32600,
                        $"Invalid Request: batch exceeds MaximumBatchSize ({core.MaximumBatchSize}).", null));
                var items = root.EnumerateArray().ToArray();
                var arr = await HandleBatchAsync(core, ctx, items, ct);
                return arr is null ? (204, null) : (200, arr);
            }
            return (200, JsonRpcAdapter.BuildError(-32600, "Invalid Request: expected object or array.", null));
        }
    }

    private static async Task<JsonArray?> HandleBatchAsync(ISleipnirCore core,
        HttpContext? ctx, JsonElement[] items, CancellationToken ct)
    {
        var parsed = items.Select(JsonRpcAdapter.ParseRequest).ToArray();

        // Invoke-Liste (valid, non-capability) — Notifications AUCH ausführen
        // (fire-and-forget), aber in der Response-Liste übersprungen.
        var invokeList = new List<SleipnirRequest>();
        foreach (var p in parsed)
        {
            if (p.IsValid && p.Capability is null && p.Request is not null)
            {
                p.InvokeIndex = invokeList.Count;
                invokeList.Add(p.Request);
            }
        }

        SleipnirResponse?[] responses = Array.Empty<SleipnirResponse?>();
        if (invokeList.Count > 0)
            responses = (await core.InvokeDi(invokeList, ctx, ExecutionMode.Parallel, ct)).ToArray();

        var outArr = new JsonArray();
        for (int i = 0; i < parsed.Length; i++)
        {
            var node = BuildItemResponse(core, ctx, parsed[i], responses);
            if (node is not null) outArr.Add(node);
        }
        return outArr.Count == 0 ? null : outArr;
    }

    private static JsonNode? BuildItemResponse(ISleipnirCore core,
        HttpContext? ctx, ParsedRpcItem p, SleipnirResponse?[] responses)
    {
        if (!p.IsValid)
            return JsonRpcAdapter.BuildError(p.ErrorCode, p.ErrorMessage!, p.Id);

        if (p.Capability is not null)
            return BuildCapabilityResult(core, ctx, p.Capability, p.Id);

        // Valid & non-capability → invoked (Notifications emitieren keine Response).
        if (p.IsNotification)
            return null;

        if (p.InvokeIndex >= 0 && p.InvokeIndex < responses.Length)
        {
            var resp = responses[p.InvokeIndex];
            return resp is null
                ? JsonRpcAdapter.BuildError(-32603, "Internal error: no response from invoker.", p.Id)
                : JsonRpcAdapter.MapResponse(resp, p.Id);
        }
        return JsonRpcAdapter.BuildError(-32603, "Internal error: request was not invoked.", p.Id);
    }

    private static JsonNode? BuildCapabilityResult(ISleipnirCore core,
        HttpContext? ctx, string capability, JsonElement? id)
    {
        // sleipnir.discover ist ein Angriffsflächen-Orakel — im RequireAuthentication-Modus
        // hinter Auth legen (Security-Audit F7.3). sleipnir.capabilities bleibt öffentlich
        // (statisches Manifest ohne Typ-Introspektion).
        if (capability == "sleipnir.discover" && core.RequireAuthentication
            && !(ctx?.User?.Identity?.IsAuthenticated ?? false))
            return JsonRpcAdapter.BuildError(-32001, "Unauthorized: sleipnir.discover requires authentication.", id);

        try
        {
            JsonNode? result = capability switch
            {
                "sleipnir.discover" => JsonNode.Parse(JsonSerializer.Serialize(core.GetDiscoveryInfo(), DiscoveryOptions)),
                "sleipnir.capabilities" => JsonRpcAdapter.CapabilitiesManifest(),
                _ => null,
            };
            return JsonRpcAdapter.BuildResult(result, id);
        }
        catch (Exception)
        {
            return JsonRpcAdapter.BuildError(-32603, "Internal error: capability failed.", id);
        }
    }
}