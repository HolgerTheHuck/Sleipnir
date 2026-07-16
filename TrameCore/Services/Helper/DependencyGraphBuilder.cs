using TrameCommon.Models;
using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace TrameCore.Services.Helper;

/// <summary>
/// Builds a dependency graph from TrameRequests and performs topological sorting.
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
    public static List<List<TrameRequest>> SortByDependencyBatches(List<TrameRequest> requests)
    {
        if (requests == null || requests.Count == 0)
            return new List<List<TrameRequest>>();

        // Build dependency map: requestId -> set of alias names it depends on
        var dependencies = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        // Build provider map: alias -> requestId that provides it (via DependencyMapping)
        var providers = new Dictionary<string, string>(StringComparer.Ordinal);
        // Index requests by ID
        var requestById = new Dictionary<string, TrameRequest>(StringComparer.Ordinal);

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
        var result = new List<List<TrameRequest>>();
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
                    $"Zyklus in Abhängigkeiten erkannt. Beteiligte Requests: {string.Join(", ", cycleMembers)}");
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
    /// Extrahiert alle @alias-Platzhalter aus dem nativen <see cref="TrameRequest.Params"/>-
    /// <see cref="JsonNode"/>. Durchläuft den Knotenbaum und sammelt String-Werte, die mit
    /// <c>@</c> beginnen (der Alias-Name ist der alphanumerische+_--Anteil danach). Wird für
    /// die statische Verfügbarkeits-Propagierung genutzt, wo die Abhängigkeiten noch nicht
    /// aufgelöst sind; die präzise Match-Logik liegt im Invoker (ReplaceDependencyByAlias).
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
        if (node is JsonValue v && v.TryGetValue<string>(out var s) && s.StartsWith("@"))
        {
            // Alias-Name: alphanumerisch + _ (wie früherer Token-Scan).
            var sb = new System.Text.StringBuilder();
            for (int j = 1; j < s.Length; j++)
            {
                char c = s[j];
                if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c); else break;
            }
            if (sb.Length > 0)
                aliases.Add(sb.ToString());
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