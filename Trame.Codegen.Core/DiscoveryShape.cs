// Runtime shape guard + no-drift ingress gate. Ported from
// clients/codegen/src/core/discovery.ts (assertDiscoveryShape). Enforces
// discoveryVersion (additive-only: accept known, reject unknown loudly) and
// validates every TypeRef (kind ∈ enum, scalar name ∈ table, array/set/stream has
// element, map has key+value, ref resolves into types). Throws
// DiscoveryShapeException (English) with a precise reason. Refusing malformed
// input early prevents emitting broken stubs from a structurally-wrong payload.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Trame.Codegen.Core;

public sealed class DiscoveryShapeException : Exception
{
    public DiscoveryShapeException(string message) : base(message) { }
}

internal static class DiscoveryShape
{
    /// <summary>Known discoveryVersion values (additive-only — see docs/discovery-schema.md §11).</summary>
    private static readonly HashSet<string> KnownDiscoveryVersions = new() { "1" };

    private static readonly HashSet<string> ValidKinds = new()
    {
        "scalar", "array", "set", "map", "ref", "stream", "opaque", "void",
    };

    private static readonly HashSet<string> ScalarNames = new()
    {
        "string", "char", "bool", "int", "long", "float", "double", "decimal",
        "datetime", "datetimeoffset", "dateonly", "timeonly", "timespan", "guid",
        "uri", "version", "bytes", "any",
    };

    /// <summary>
    /// Validate a discovery payload in place. Throws <see cref="DiscoveryShapeException"/> on the
    /// first structural violation. Returns the (validated) root node for downstream consumption.
    /// </summary>
    public static JsonObject Assert(JsonNode? node)
    {
        if (node is not JsonObject o)
            throw new DiscoveryShapeException("Discovery payload is not a JSON object.");

        // discoveryVersion — additive-only gate.
        var version = o["discoveryVersion"]?.GetValue<string>();
        if (string.IsNullOrEmpty(version))
            throw new DiscoveryShapeException(
                "Discovery payload is missing a string \"discoveryVersion\" (expected { discoveryVersion: \"1\", ... }).");
        if (!KnownDiscoveryVersions.Contains(version!))
            throw new DiscoveryShapeException(
                $"Unsupported discoveryVersion \"{version}\" — known versions: {string.Join(", ", KnownDiscoveryVersions)}. " +
                "Upgrade the codegen (additive-only, see docs/discovery-schema.md §11).");

        if (o["controllers"] is not JsonArray controllers)
            throw new DiscoveryShapeException(
                "Discovery payload is missing a \"controllers\" array (expected { controllers: [...], types: {...} }).");
        if (o["types"] is not JsonObject types)
            throw new DiscoveryShapeException(
                "Discovery payload is missing a \"types\" object (expected { controllers: [...], types: {...} }).");

        // Validate every TypeRef reachable from controllers.
        foreach (var c in controllers)
        {
            if (c is not JsonObject ctrl)
                throw new DiscoveryShapeException("A \"controllers\" entry is not an object.");
            var name = ctrl["name"]?.GetValue<string>();
            if (string.IsNullOrEmpty(name))
                throw new DiscoveryShapeException("A controller is missing a string \"name\".");
            if (ctrl["methods"] is not JsonArray methods)
                throw new DiscoveryShapeException($"Controller \"{name}\" is missing a \"methods\" array.");
            foreach (var m in methods)
            {
                if (m is not JsonObject mm)
                    throw new DiscoveryShapeException($"Controller \"{name}\" has a non-object method.");
                var methodName = mm["methodName"]?.GetValue<string>();
                if (string.IsNullOrEmpty(methodName))
                    throw new DiscoveryShapeException($"A method in controller \"{name}\" is missing a string \"methodName\".");
                AssertTypeRef(mm["returnType"], $"controller \"{name}\" method \"{methodName}\" returnType", types);
                if (mm["parameters"] is { } p)
                {
                    if (p is not JsonArray pa)
                        throw new DiscoveryShapeException($"Method \"{methodName}\" parameters is not an array.");
                    foreach (var item in pa)
                    {
                        var pp = item as JsonObject ?? new JsonObject();
                        var pName = pp["parameterName"]?.GetValue<string>() ?? "?";
                        AssertTypeRef(pp["parameterType"], $"method \"{methodName}\" parameter \"{pName}\"", types);
                    }
                }
            }
        }

        // Validate the types registry entries themselves.
        foreach (var kv in types)
        {
            var key = kv.Key;
            if (kv.Value is not JsonObject meta)
                throw new DiscoveryShapeException($"Type registry entry \"{key}\" is not an object.");
            var kind = meta["kind"]?.GetValue<string>();
            if (kind != "object" && kind != "enum")
                throw new DiscoveryShapeException($"Type registry entry \"{key}\" has invalid kind \"{kind ?? "null"}\" (expected \"object\" or \"enum\").");
            if (kind == "object" && meta["properties"] is not JsonArray)
                throw new DiscoveryShapeException($"Object type \"{key}\" is missing a \"properties\" array.");
            if (kind == "enum")
            {
                if (meta["members"] is not JsonArray members || members.Count == 0)
                    throw new DiscoveryShapeException($"Enum type \"{key}\" must have a non-empty \"members\" array.");
            }
            if (meta["properties"] is JsonArray props)
            {
                foreach (var item in props)
                {
                    var pp = item as JsonObject ?? new JsonObject();
                    var pName = pp["propertyName"]?.GetValue<string>() ?? "?";
                    AssertTypeRef(pp["propertyType"], $"type \"{key}\" property \"{pName}\"", types);
                }
            }
        }

        return o;
    }

    /// <summary>Validate one TypeRef recursively; refs must resolve into the types registry.</summary>
    private static void AssertTypeRef(JsonNode? value, string where, JsonObject types)
    {
        if (value is not JsonObject refObj)
            throw new DiscoveryShapeException($"{where} is not a TypeRef object.");
        var kind = refObj["kind"]?.GetValue<string>();
        if (kind is null || !ValidKinds.Contains(kind))
            throw new DiscoveryShapeException($"{where} has invalid kind \"{kind ?? "null"}\".");
        switch (kind)
        {
            case "scalar":
                var scalarName = refObj["name"]?.GetValue<string>();
                if (scalarName is null || !ScalarNames.Contains(scalarName))
                    throw new DiscoveryShapeException($"{where} has invalid scalar name \"{scalarName ?? "null"}\".");
                return;
            case "array":
            case "set":
            case "stream":
                if (refObj["element"] is null)
                    throw new DiscoveryShapeException($"{where} (kind \"{kind}\") is missing \"element\".");
                AssertTypeRef(refObj["element"], $"{where} element", types);
                return;
            case "map":
                if (refObj["key"] is null || refObj["value"] is null)
                    throw new DiscoveryShapeException($"{where} (map) is missing \"key\" or \"value\".");
                AssertTypeRef(refObj["key"], $"{where} key", types);
                AssertTypeRef(refObj["value"], $"{where} value", types);
                return;
            case "ref":
                var r = refObj["ref"]?.GetValue<string>();
                if (string.IsNullOrEmpty(r))
                    throw new DiscoveryShapeException($"{where} (ref) is missing a \"ref\" string.");
                if (!types.ContainsKey(r!))
                    throw new DiscoveryShapeException($"{where} (ref) \"{r}\" does not resolve into the types registry.");
                return;
                // opaque | void: no further constraints.
        }
    }
}