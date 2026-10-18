// Contract compatibility check: classifies every difference between a baseline contract (what
// clients in the field were built against) and the current contract (what the server serves)
// as breaking / soft / safe for the wire. Spec: CONTRACT_EVOLUTION.md. Executable spec, shared
// with the TS port (clients/codegen/src/core/compat.ts): clients/codegen/test/fixtures/compat/.
//
// Every check is "does the ACCEPTOR take everything the PRODUCER may send?". Inputs (parameters,
// request DTOs) are produced by the client and accepted by the server; outputs (returns, event
// payloads) the other way round. The rollback direction (new client against the old server) is
// the same pass with the roles of the two contracts swapped. Types compare structurally, never by
// registry key: a renamed or moved C# type is identical on the wire. Output is ASCII-only so it
// survives MSBuild/console code pages.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sleipnir.Codegen.Core;

/// <summary>One classified difference. <see cref="Position"/> is null for controller/method-level changes.</summary>
public sealed class ContractChange
{
    public string Id { get; set; } = "";
    /// <summary><c>breaking</c> | <c>soft</c> | <c>safe</c>.</summary>
    public string Severity { get; set; } = "";
    public string Site { get; set; } = "";
    /// <summary><c>input</c> | <c>output</c> | null.</summary>
    public string? Position { get; set; }
    /// <summary><c>old-clients</c> (baseline clients vs. current server) | <c>rollback</c> (current clients vs. baseline server).</summary>
    public string Direction { get; set; } = "";
    public string Message { get; set; } = "";
}

public sealed class ContractCompatibilityReport
{
    public IReadOnlyList<ContractChange> Changes { get; }
    public int Breaking => Changes.Count(c => c.Severity == ContractCompatibility.Breaking);
    public int Soft => Changes.Count(c => c.Severity == ContractCompatibility.Soft);
    public int Safe => Changes.Count(c => c.Severity == ContractCompatibility.Safe);

    internal ContractCompatibilityReport(IReadOnlyList<ContractChange> changes) => Changes = changes;

    /// <summary>The machine-readable report (same shape as <c>sleipnir-gen diff --json</c>).</summary>
    public string ToJson()
    {
        var changes = new JsonArray();
        foreach (var c in Changes)
        {
            changes.Add(new JsonObject
            {
                ["id"] = c.Id,
                ["severity"] = c.Severity,
                ["site"] = c.Site,
                ["position"] = c.Position,
                ["direction"] = c.Direction,
                ["message"] = c.Message,
            });
        }
        var root = new JsonObject
        {
            ["summary"] = new JsonObject { ["breaking"] = Breaking, ["soft"] = Soft, ["safe"] = Safe },
            ["changes"] = changes,
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Human-readable report, one line per change, most severe first.</summary>
    public string ToText()
    {
        var sb = new StringBuilder();
        sb.Append("Contract compatibility: ").Append(Breaking).Append(" breaking, ")
          .Append(Soft).Append(" soft, ").Append(Safe).Append(" safe").Append('\n');
        foreach (var c in Changes)
        {
            sb.Append("  ").Append(c.Severity.ToUpperInvariant().PadRight(8)).Append(' ')
              .Append(c.Site).Append("  [").Append(c.Id)
              .Append(c.Direction == ContractCompatibility.Rollback ? ", rollback" : "")
              .Append("]  ").Append(c.Message).Append('\n');
        }
        return sb.ToString();
    }
}

public static class ContractCompatibility
{
    public const string Breaking = "breaking";
    public const string Soft = "soft";
    public const string Safe = "safe";
    public const string OldClients = "old-clients";
    public const string Rollback = "rollback";

    private const string Input = "input";
    private const string Output = "output";

    /// <summary>
    /// Compare <paramref name="baselineJson"/> (the contract deployed clients were built against)
    /// with <paramref name="currentJson"/> (the contract the server now serves). With
    /// <paramref name="includeRollback"/> the reverse pass (current clients against the baseline
    /// server) is added; its safe changes are omitted.
    /// </summary>
    public static ContractCompatibilityReport Compare(string baselineJson, string currentJson, bool includeRollback = false)
    {
        var baseline = new Side(DiscoveryShape.Assert(JsonNode.Parse(baselineJson)));
        var current = new Side(DiscoveryShape.Assert(JsonNode.Parse(currentJson)));

        var changes = new List<ContractChange>();
        new Pass(client: baseline, server: current, OldClients, changes).Run();
        if (includeRollback)
            new Pass(client: current, server: baseline, Rollback, changes).Run();

        var ordered = changes
            .OrderBy(c => SeverityRank(c.Severity))
            .ThenBy(c => c.Site, StringComparer.Ordinal)
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .ThenBy(c => c.Direction, StringComparer.Ordinal)
            .ToList();
        return new ContractCompatibilityReport(ordered);
    }

    private static int SeverityRank(string severity) => severity switch
    {
        Breaking => 0,
        Soft => 1,
        _ => 2,
    };

    // --- One contract ---------------------------------------------------------------------

    private sealed class Side
    {
        public readonly List<(string Name, Dictionary<string, JsonObject> Methods, List<string> Order)> Controllers = new();
        public readonly Dictionary<string, JsonObject> Types = new(StringComparer.OrdinalIgnoreCase);

        public Side(JsonObject discovery)
        {
            foreach (var c in discovery["controllers"]!.AsArray().OfType<JsonObject>())
            {
                var methods = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
                var order = new List<string>();
                foreach (var m in c["methods"]!.AsArray().OfType<JsonObject>())
                {
                    var name = m["methodName"]!.GetValue<string>();
                    methods[name] = m;
                    order.Add(name);
                }
                Controllers.Add((c["name"]!.GetValue<string>(), methods, order));
            }
            if (discovery["types"] is JsonObject types)
            {
                foreach (var kv in types)
                    if (kv.Value is JsonObject meta)
                        Types[kv.Key] = meta;
            }
        }

        public Dictionary<string, JsonObject>? FindController(string name)
            => Controllers.Where(c => c.Name == name).Select(c => c.Methods).FirstOrDefault();
    }

    // --- One direction --------------------------------------------------------------------

    private sealed class Pass
    {
        private readonly Side _client;
        private readonly Side _server;
        private readonly string _direction;
        private readonly List<ContractChange> _out;
        // Ref pairs on the current recursion path — guards cycles only, so a type reached via two
        // properties (Billing/Shipping: Address) reports both sites.
        private readonly HashSet<string> _visiting = new(StringComparer.Ordinal);

        public Pass(Side client, Side server, string direction, List<ContractChange> output)
        {
            _client = client;
            _server = server;
            _direction = direction;
            _out = output;
        }

        private void Add(string id, string severity, string site, string? position, string message)
        {
            // The rollback pass reports only what can go wrong.
            if (_direction == Rollback && severity == Safe)
                return;
            _out.Add(new ContractChange
            {
                Id = id,
                Severity = severity,
                Site = site,
                Position = position,
                Direction = _direction,
                Message = message,
            });
        }

        public void Run()
        {
            foreach (var (name, methods, order) in _client.Controllers)
            {
                var serverMethods = _server.FindController(name);
                if (serverMethods == null)
                {
                    Add("controller-removed", Breaking, name, null,
                        $"Controller '{name}' does not exist on the server - every call to it fails (404).");
                    continue;
                }
                foreach (var methodName in order)
                {
                    var route = name + "." + methodName;
                    if (!serverMethods.TryGetValue(methodName, out var serverMethod))
                    {
                        Add("method-removed", Breaking, route, null,
                            $"Method '{route}' does not exist on the server - calls fail with 400 (method not found).");
                        continue;
                    }
                    CompareMethod(route, methods[methodName], serverMethod);
                }
                foreach (var serverMethodName in _server.Controllers.First(c => c.Name == name).Order)
                {
                    if (!methods.ContainsKey(serverMethodName))
                        Add("method-added", Safe, name + "." + serverMethodName, null,
                            $"Method '{name}.{serverMethodName}' is new on the server.");
                }
            }
            foreach (var (name, _, _) in _server.Controllers)
            {
                if (_client.FindController(name) == null)
                    Add("controller-added", Safe, name, null, $"Controller '{name}' is new on the server.");
            }
        }

        private void CompareMethod(string route, JsonObject client, JsonObject server)
        {
            var clientReturn = (JsonObject)client["returnType"]!;
            var serverReturn = (JsonObject)server["returnType"]!;
            bool clientEvent = Kind(clientReturn) == "event";
            bool serverEvent = Kind(serverReturn) == "event";
            if (clientEvent != serverEvent)
            {
                Add("method-kind-changed", Breaking, route, null,
                    $"'{route}' is {(clientEvent ? "an event" : "a call")} for the client but {(serverEvent ? "an event" : "a call")} on the server.");
                return;
            }

            // Newly deprecated on the server: nothing breaks, but it is the first step of a removal.
            var serverDeprecated = server["deprecated"] is JsonValue sd && sd.TryGetValue<string>(out var sdText) ? sdText : null;
            if (serverDeprecated != null && client["deprecated"] is null)
            {
                Add("method-deprecated", Safe, route, null,
                    $"Method '{route}' is deprecated on the server: {serverDeprecated}");
            }

            CompareParameters(route, Params(client), Params(server));

            if (serverEvent)
            {
                Accepts(clientReturn["element"] as JsonObject, serverReturn["element"] as JsonObject, Output,
                    route + " -> event", skipTopNullability: false);
            }
            else
            {
                Accepts(clientReturn, serverReturn, Output, route + " -> return", skipTopNullability: false);
            }
        }

        private sealed class Param
        {
            public string Name = "";
            public int Index;
            public JsonObject Type = new();
            public JsonNode? Default;
            public bool Optional;
        }

        private static List<Param> Params(JsonObject method)
        {
            var list = new List<Param>();
            if (method["parameters"] is not JsonArray arr)
                return list;
            int i = 0;
            foreach (var p in arr.OfType<JsonObject>())
            {
                var type = (JsonObject)p["parameterType"]!;
                var def = p["defaultValue"];
                list.Add(new Param
                {
                    Name = p["parameterName"]!.GetValue<string>(),
                    Index = i++,
                    Type = type,
                    Default = def,
                    // Optional = may be omitted: nullable, a C# default, or byte[] (from binaryData).
                    Optional = IsNullable(type) || def != null || (Kind(type) == "scalar" && ScalarName(type) == "bytes"),
                });
            }
            return list;
        }

        private void CompareParameters(string route, List<Param> client, List<Param> server)
        {
            var clientByName = client.ToDictionary(p => p.Name, StringComparer.Ordinal);
            var serverByName = server.ToDictionary(p => p.Name, StringComparer.Ordinal);
            var clientOnly = client.Where(p => !serverByName.ContainsKey(p.Name)).ToList();
            var serverOnly = server.Where(p => !clientByName.ContainsKey(p.Name)).ToList();

            foreach (var s in serverOnly)
            {
                var site = $"{route}({s.Name})";
                if (s.Optional)
                {
                    Add("optional-parameter-added", Safe, site, Input,
                        $"The server accepts the optional parameter '{s.Name}' ({Describe(s.Type, _server)}), which the client does not send - it binds its default.");
                    continue;
                }
                // Rename heuristic: a client-only parameter at the same position whose type the
                // server parameter accepts (a removed bool next to an added string is no rename).
                var renamed = clientOnly.FirstOrDefault(c => c.Index == s.Index && SameShape(s.Type, c.Type));
                if (renamed != null)
                {
                    clientOnly.Remove(renamed);
                    Add("parameter-renamed", Breaking, site, Input,
                        $"The client sends '{renamed.Name}' where the server expects '{s.Name}' (same position) - the name no longer binds: 400 Missing required parameter.");
                    continue;
                }
                Add("required-parameter-not-sent", Breaking, site, Input,
                    $"The server requires parameter '{s.Name}' ({Describe(s.Type, _server)}), which the client does not send - 400 Missing required parameter.");
            }

            foreach (var c in clientOnly)
            {
                Add("parameter-not-accepted", Soft, $"{route}({c.Name})", Input,
                    $"The client sends '{c.Name}', which the server does not have - the value is silently ignored.");
            }

            foreach (var c in client)
            {
                if (!serverByName.TryGetValue(c.Name, out var s))
                    continue;
                var site = $"{route}({c.Name})";
                if (c.Optional && !s.Optional)
                {
                    Add("parameter-may-be-omitted", Breaking, site, Input,
                        $"The client may omit '{c.Name}' (optional in its contract), but the server requires it - 400 Missing required parameter when omitted.");
                }
                else if (c.Optional && s.Optional && JsonText(c.Default) != JsonText(s.Default))
                {
                    Add("parameter-default-changed", Soft, site, Input,
                        $"The default of '{c.Name}' differs (client contract: {JsonText(c.Default)}, server: {JsonText(s.Default)}) - callers that omit it get the server's default.");
                }
                // Top-level nullability of a parameter is its optionality, handled above.
                Accepts(s.Type, c.Type, Input, site, skipTopNullability: true);
            }

            var commonClientOrder = client.Where(p => serverByName.ContainsKey(p.Name)).Select(p => p.Name).ToList();
            var commonServerOrder = server.Where(p => clientByName.ContainsKey(p.Name)).Select(p => p.Name).ToList();
            if (!commonClientOrder.SequenceEqual(commonServerOrder, StringComparer.Ordinal))
            {
                Add("parameter-order-changed", Soft, route, Input,
                    $"The parameter order differs (client: {string.Join(", ", commonClientOrder)}; server: {string.Join(", ", commonServerOrder)}) - positional callers bind values to the wrong parameters; named callers are unaffected.");
            }
        }

        // --- Type acceptance --------------------------------------------------------------

        // The side whose type registry an acceptor/producer ref resolves in.
        private Side AcceptorSide(string position) => position == Input ? _server : _client;
        private Side ProducerSide(string position) => position == Input ? _client : _server;

        private void Accepts(JsonObject? acceptor, JsonObject? producer, string position, string site, bool skipTopNullability)
        {
            if (acceptor == null || producer == null)
                return;

            var aKind = NormalizeKind(Kind(acceptor));
            var pKind = NormalizeKind(Kind(producer));

            // The client ignores the data of a call it treats as void.
            if (aKind == "void")
                return;

            if (!skipTopNullability && IsNullable(producer) && !IsNullable(acceptor))
            {
                Add("null-not-accepted", Breaking, site, position, position == Input
                    ? "The client may send null, but the server does not accept null - 400 for a value type, an unexpected null otherwise."
                    : "The server may return null, but the client does not expect null.");
            }

            bool aAny = aKind == "scalar" && ScalarName(acceptor) == "any";
            bool pAny = pKind == "scalar" && ScalarName(producer) == "any";
            if (aAny || aKind == "opaque")
                return; // accepts anything
            if (pAny || pKind == "opaque")
            {
                var p = Describe(producer, ProducerSide(position));
                var a = Describe(acceptor, AcceptorSide(position));
                Add("opaque-changed", Soft, site, position, position == Input
                    ? $"The client sends {p} (not modelled), the server expects {a} - compatibility cannot be verified."
                    : $"The server returns {p} (not modelled), the client expects {a} - compatibility cannot be verified.");
                return;
            }

            if (aKind != pKind)
            {
                TypeIncompatible(acceptor, producer, position, site);
                return;
            }

            switch (aKind)
            {
                case "scalar":
                    var a = ScalarName(acceptor);
                    var p = ScalarName(producer);
                    if (a != p && !Widens(p, a))
                        TypeIncompatible(acceptor, producer, position, site);
                    return;
                case "array":
                    Accepts(acceptor["element"] as JsonObject, producer["element"] as JsonObject, position, site + "[]", skipTopNullability: false);
                    return;
                case "map":
                    Accepts(acceptor["key"] as JsonObject, producer["key"] as JsonObject, position, site + "{key}", skipTopNullability: false);
                    Accepts(acceptor["value"] as JsonObject, producer["value"] as JsonObject, position, site + "{value}", skipTopNullability: false);
                    return;
                case "ref":
                    AcceptsRef(acceptor, producer, position, site);
                    return;
            }
        }

        private void TypeIncompatible(JsonObject acceptor, JsonObject producer, string position, string site)
        {
            var a = Describe(acceptor, AcceptorSide(position));
            var p = Describe(producer, ProducerSide(position));
            Add("type-incompatible", Breaking, site, position, position == Input
                ? $"The client sends {p}, the server expects {a} - 400."
                : $"The server returns {p}, the client expects {a} - deserialization fails.");
        }

        private void AcceptsRef(JsonObject acceptor, JsonObject producer, string position, string site)
        {
            var aKey = acceptor["ref"]!.GetValue<string>();
            var pKey = producer["ref"]!.GetValue<string>();
            if (!AcceptorSide(position).Types.TryGetValue(aKey, out var aMeta)
                || !ProducerSide(position).Types.TryGetValue(pKey, out var pMeta))
                return;

            var aTypeKind = aMeta["kind"]?.GetValue<string>();
            var pTypeKind = pMeta["kind"]?.GetValue<string>();
            if (aTypeKind != pTypeKind)
            {
                TypeIncompatible(acceptor, producer, position, site);
                return;
            }

            var pathKey = aKey + "|" + pKey + "|" + position;
            if (!_visiting.Add(pathKey))
                return; // recursive type: this pair is already being compared further up
            try
            {
                if (aTypeKind == "enum")
                    AcceptsEnum(aMeta, pMeta, position, site);
                else
                    AcceptsObject(aMeta, pMeta, position, site);
            }
            finally
            {
                _visiting.Remove(pathKey);
            }
        }

        private void AcceptsObject(JsonObject aMeta, JsonObject pMeta, string position, string site)
        {
            var aProps = Props(aMeta);
            var pProps = Props(pMeta);
            var pByName = pProps.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
            var aByName = aProps.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);

            foreach (var (name, type) in aProps)
            {
                var propSite = site + "." + name;
                if (pByName.TryGetValue(name, out var produced))
                {
                    Accepts(type, produced.Type, position, propSite, skipTopNullability: false);
                    continue;
                }
                bool required = !IsNullable(type);
                if (position == Output)
                {
                    Add("property-not-sent", required ? Breaking : Soft, propSite, position, required
                        ? $"The server does not send '{name}', which the client requires."
                        : $"The server does not send '{name}' (optional for the client) - the client sees it as absent.");
                }
                else
                {
                    Add("property-not-sent", required ? Soft : Safe, propSite, position, required
                        ? $"The server requires property '{name}', which the client does not send - it binds a silent default (400 under AliasBindingMode Strict/Paranoid)."
                        : $"The server has the optional property '{name}', which the client does not send - it binds null.");
                }
            }

            foreach (var (name, _) in pProps)
            {
                if (aByName.ContainsKey(name))
                    continue;
                var propSite = site + "." + name;
                if (position == Output)
                    Add("property-not-accepted", Safe, propSite, position,
                        $"The server sends '{name}', which the client does not know - ignored.");
                else
                    Add("property-not-accepted", Soft, propSite, position,
                        $"The client sends '{name}', which the server does not have - the value is silently ignored.");
            }
        }

        private void AcceptsEnum(JsonObject aMeta, JsonObject pMeta, string position, string site)
        {
            var aMembers = Members(aMeta);
            var pMembers = Members(pMeta);
            var aByName = aMembers.ToDictionary(m => m.Name, StringComparer.Ordinal);
            var aValues = new HashSet<string>(aMembers.Select(m => m.Value), StringComparer.Ordinal);

            foreach (var (name, value) in pMembers)
            {
                var memberSite = site + "::" + name;
                if (aByName.TryGetValue(name, out var same) && same.Value != value)
                {
                    Add("enum-value-changed", Breaking, memberSite, position,
                        $"Enum member '{name}' is {same.Value} for the {Party(position, acceptor: true)} but {value} for the {Party(position, acceptor: false)} - the numeric wire value changed meaning.");
                    continue;
                }
                if (aValues.Contains(value))
                    continue;
                if (position == Output)
                    Add("enum-value-not-accepted", Soft, memberSite, position,
                        $"The server may return '{name}' ({value}), which the client's enum does not know.");
                else
                    Add("enum-value-not-accepted", Breaking, memberSite, position,
                        $"The client may send '{name}' ({value}), which the server's enum does not have - the server binds an undefined enum value.");
            }
        }

        private static string Party(string position, bool acceptor)
            => (position == Input) == acceptor ? "server" : "client";

        private static List<(string Name, JsonObject Type)> Props(JsonObject meta)
            => (meta["properties"] as JsonArray ?? new JsonArray())
                .OfType<JsonObject>()
                .Select(p => (p["propertyName"]!.GetValue<string>(), (JsonObject)p["propertyType"]!))
                .ToList();

        private static List<(string Name, string Value)> Members(JsonObject meta)
            => (meta["members"] as JsonArray ?? new JsonArray())
                .OfType<JsonObject>()
                .Select(m => (m["name"]!.GetValue<string>(), m["value"] is JsonNode v ? JsonText(v) : m["name"]!.GetValue<string>()))
                .ToList();
    }

    // --- Shared helpers -------------------------------------------------------------------

    private static string Kind(JsonObject typeRef) => typeRef["kind"]?.GetValue<string>() ?? "opaque";

    private static string? ScalarName(JsonObject typeRef) => typeRef["name"]?.GetValue<string>();

    private static bool IsNullable(JsonObject typeRef)
        => typeRef["nullable"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    // set/stream are JSON arrays on the wire.
    private static string NormalizeKind(string kind) => kind is "set" or "stream" ? "array" : kind;

    // Relaxed escaping = what JSON.stringify writes, so both engines print a default like "a+b" alike.
    private static readonly JsonSerializerOptions TextOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string JsonText(JsonNode? node) => node == null ? "none" : node.ToJsonString(TextOptions);

    // Lossless JSON widening (producer -> acceptor): the acceptor parses every value the producer writes.
    private static readonly HashSet<string> Widenings = new(StringComparer.Ordinal)
    {
        "int>long", "int>float", "int>double", "int>decimal",
        "long>double", "long>decimal", "float>double", "char>string",
    };

    private static bool Widens(string? from, string? to) => Widenings.Contains(from + ">" + to);

    // Rename candidate: same wire kind, and for scalars the same or a widened scalar.
    private static bool SameShape(JsonObject acceptor, JsonObject producer)
    {
        var a = NormalizeKind(Kind(acceptor));
        if (a != NormalizeKind(Kind(producer)))
            return false;
        if (a != "scalar")
            return true;
        var an = ScalarName(acceptor);
        var pn = ScalarName(producer);
        return an == pn || Widens(pn, an);
    }

    private static string Describe(JsonObject? typeRef, Side side)
    {
        if (typeRef == null)
            return "?";
        switch (Kind(typeRef))
        {
            case "scalar": return ScalarName(typeRef) ?? "?";
            case "array":
            case "set":
            case "stream":
            case "event": return Describe(typeRef["element"] as JsonObject, side) + "[]";
            case "map": return "map<" + Describe(typeRef["key"] as JsonObject, side) + ", " + Describe(typeRef["value"] as JsonObject, side) + ">";
            case "ref":
                var key = typeRef["ref"]!.GetValue<string>();
                var cut = Math.Max(key.LastIndexOf('.'), key.LastIndexOf('+'));
                return cut >= 0 ? key.Substring(cut + 1) : key;
            case "opaque": return typeRef["nativeName"]?.GetValue<string>() ?? "opaque";
            default: return Kind(typeRef);
        }
    }
}
