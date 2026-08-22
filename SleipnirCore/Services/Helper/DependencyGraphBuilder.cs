using SleipnirCommon.Models;
using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace SleipnirCore.Services.Helper;

/// <summary>
/// Builds a dependency graph from SleipnirRequests and performs topological sorting.
/// Groups independent requests into parallel batches (level-based execution).
/// Detects cycles and throws InvalidOperationException with cycle details.
/// </summary>
public static class DependencyGraphBuilder
{
    /// <summary>
    /// Sorts requests into execution batches based on their dependency mappings.
    /// Requests without dependencies are grouped into the first batch.
    /// Requests depending on batch N results are in batch N+1.
    /// </summary>
    /// <param name="requests">The list of requests to sort.</param>
    /// <returns>Ordered list of batches (each batch can execute in parallel).</returns>
    /// <exception cref="InvalidOperationException">Thrown when a cycle is detected.</exception>
    public static List<List<SleipnirRequest>> SortByDependencyBatches(List<SleipnirRequest> requests)
    {
        if (requests == null || requests.Count == 0)
            return new List<List<SleipnirRequest>>();

        // Build dependency map: requestId -> set of alias names it depends on
        var dependencies = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        // Build provider map: alias -> requestId that provides it (via DependencyMapping)
        var providers = new Dictionary<string, string>(StringComparer.Ordinal);
        // Index requests by ID
        var requestById = new Dictionary<string, SleipnirRequest>(StringComparer.Ordinal);

        foreach (var request in requests)
        {
            var id = request.Id ?? string.Empty;
            if (string.IsNullOrEmpty(id))
                id = $"{request.Controller}.{request.Method}";
            requestById[id] = request;
            dependencies[id] = new HashSet<string>(StringComparer.Ordinal);

            // Register what this request provides
            if (request.DependencyMapping != null)
            {
                foreach (var kvp in request.DependencyMapping)
                {
                    // kvp.Key = alias, kvp.Value = jsonPath
                    providers[kvp.Key] = id;
                }
            }
        }

        // Build dependency edges: for each request, find which aliases it uses (via @alias in Params)
        foreach (var request in requests)
        {
            var id = request.Id ?? string.Empty;
            if (string.IsNullOrEmpty(id))
                id = $"{request.Controller}.{request.Method}";

            if (request.Params == null)
                continue;

            // Find all @alias placeholders in the native Params-JsonNode
            var usedAliases = ExtractAliases(request.Params);
            foreach (var alias in usedAliases)
            {
                if (providers.TryGetValue(alias, out var providerId))
                {
                    if (providerId != id) // Don't depend on self
                        dependencies[id].Add(providerId);
                }
            }
        }

        // Topological sort with cycle detection (Kahn's algorithm, batch-based)
        var result = new List<List<SleipnirRequest>>();
        var completed = new HashSet<string>(StringComparer.Ordinal);
        var remaining = new HashSet<string>(dependencies.Keys, StringComparer.Ordinal);

        while (remaining.Count > 0)
        {
            // Find all requests whose dependencies are fully satisfied
            var batch = remaining
                .Where(id => dependencies[id].All(dep => completed.Contains(dep)))
                .ToList();

            if (batch.Count == 0)
            {
                // Cycle detected: remaining items all have unsatisfied dependencies
                var cycleMembers = remaining.ToList();
                throw new InvalidOperationException(
                    $"Cycle detected in dependencies. Involved requests: {string.Join(", ", cycleMembers)}");
            }

            var batchRequests = batch.Select(id => requestById[id]).ToList();
            result.Add(batchRequests);

            foreach (var id in batch)
            {
                completed.Add(id);
                remaining.Remove(id);
            }
        }

        return result;
    }

    /// <summary>
    /// Extrahiert alle @alias-Platzhalter aus dem nativen <see cref="SleipnirRequest.Params"/>-
    /// <see cref="JsonNode"/>. Durchläuft den Knotenbaum und sammelt String-Werte, die unter
    /// der gemeinsamen Alias-Grammatik (<see cref="AliasGrammar"/>) eine Alias-Referenz sind —
    /// trim-frei, mit @@-Escape (escaped Literale erzeugen keine Kante). Wird für die statische
    /// Verfügbarkeits-Propagierung genutzt, wo die Abhängigkeiten noch nicht aufgelöst sind;
    /// die präzise Match-Logik liegt im Invoker (ReplaceDependencyByAlias).
    /// </summary>
    internal static HashSet<string> ExtractAliases(JsonNode? paramsNode)
    {
        var aliases = new HashSet<string>(StringComparer.Ordinal);
        CollectAliases(paramsNode, aliases);
        return aliases;
    }

    private static void CollectAliases(JsonNode? node, HashSet<string> aliases)
    {
        if (node == null) return;
        if (node is JsonValue v && v.TryGetValue<string>(out var s))
        {
            // Gemeinsame Grammatik: nur echte Alias-Referenzen erzeugen eine Kante
            // ("@a.b" → Kante zu "a"); "@@x" ist ein escaped Literal und keine Kante.
            if (AliasGrammar.Classify(s, out var aliasName) == AliasKind.AliasReference)
                aliases.Add(aliasName);
        }
        else if (node is JsonObject obj)
        {
            foreach (var kvp in obj) CollectAliases(kvp.Value, aliases);
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr) CollectAliases(item, aliases);
        }
    }
}