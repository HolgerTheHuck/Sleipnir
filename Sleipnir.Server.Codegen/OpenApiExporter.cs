// Spike (P3.1, product-direction-work-items.md): DiscoveryInfo → OpenAPI 3.1 document.
//
// A pure, side-effect-free mapper. Each [SleipnirMethod] becomes its own pseudo-operation
// (POST {BasePath}/{Controller}/{Method} with a flat {param: value} body and a typed response
// schema) — a DESCRIPTION device for tools like Postman/Insomnia/Swagger UI, not an alternate
// wire. All real calls share the ONE canonical endpoint with the params envelope; that single
// truth is recorded under the x-sleipnir extension so the document never lies about itself.
// Contract types expand to components/schemas straight from the DiscoveryInfo.Types registry
// (the Weg-C inference already computed these) with wire (camelCase) property names, so
// structured request/response editing matches what the server actually sends.
//
// Known ceiling (see spike notes): generated foreign clients and Postman's "Send" only execute
// against these paths if the REST transport additionally accepts the flat path convention
// (work item 3.3). Until then the paths document and structure requests, but a sent request
// must go to the canonical endpoint.
using System.Text.Json;
using System.Text.Json.Nodes;
using SleipnirCore.Model.Messages.Mex;

namespace Sleipnir.Server.Codegen;

internal static class OpenApiExporter
{
    internal sealed record Options
    {
        public string Title { get; init; } = "Sleipnir API";
        /// <summary>Defaults to the discovery schema version so the doc is reproducible from the contract.</summary>
        public string? Version { get; init; }
        /// <summary>Optional server base URL (e.g. "https://localhost:5001"). Omitted when null.</summary>
        public string? ServerUrl { get; init; }
        /// <summary>REST ingress base path of the canonical JSON endpoint.</summary>
        public string BasePath { get; init; } = "/api/sleipnir/json";
    }

    private static readonly JsonSerializerOptions DefaultSerialization = new() { WriteIndented = true };

    public static string Export(DiscoveryInfo discovery, Options opts)
    {
        var root = new JsonObject
        {
            ["openapi"] = "3.1.0",
            ["info"] = new JsonObject
            {
                ["title"] = opts.Title,
                ["version"] = opts.Version ?? discovery.DiscoveryVersion,
                ["description"] = "Exported from Sleipnir code-first discovery (" +
                    "contract.sleipnir.json / GET /api/sleipnir/discovery). One pseudo-operation per " +
                    "[SleipnirMethod].",
            },
            // The document's single-wire truth: per-method paths below are a description device;
            // every real call goes through the canonical envelope.
            ["x-sleipnir"] = new JsonObject
            {
                ["canonicalEndpoint"] = new JsonObject
                {
                    ["method"] = "post",
                    ["path"] = opts.BasePath,
                    ["body"] = new JsonObject
                    {
                        ["controller"] = "{Controller}",
                        ["method"] = "{Method}",
                        ["parameters"] = new JsonArray(
                            new JsonObject { ["name"] = "{paramName}", ["data"] = "{value}" }),
                    },
                },
                ["note"] = "Each path below is a pseudo-operation for tooling (typed editing, collection " +
                    "import, single-call mocks). The canonical wire is the single endpoint above; sending " +
                    "to the per-method path requires the flat path convention (work item 3.3).",
            },
        };

        if (!string.IsNullOrWhiteSpace(opts.ServerUrl))
            root["servers"] = new JsonArray(new JsonObject { ["url"] = opts.ServerUrl });

        var paths = new JsonObject();
        foreach (var controller in discovery.Controllers)
        {
            foreach (var method in controller.Methods)
            {
                var path = $"{opts.BasePath}/{controller.Name}/{method.MethodName}";
                if (paths.ContainsKey(path))
                    continue; // name-uniqueness makes this unreachable; keep the guard cheap anyway
                paths[path] = new JsonObject { ["post"] = OperationFor(controller.Name, method) };
            }
        }
        root["paths"] = paths;

        var schemas = new JsonObject();
        foreach (var (key, type) in discovery.Types.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
            schemas[key] = SchemaForType(type);
        if (schemas.Count > 0)
            root["components"] = new JsonObject { ["schemas"] = schemas };

        return root.ToJsonString(DefaultSerialization);
    }

    private static JsonObject OperationFor(string controllerName, MethodMeta method)
    {
        var op = new JsonObject
        {
            ["operationId"] = $"{controllerName}_{method.MethodName}",
            ["tags"] = new JsonArray(controllerName),
        };
        if (!string.IsNullOrWhiteSpace(method.Documentation))
            op["summary"] = method.Documentation;

        op["x-sleipnir-canonical-call"] = new JsonObject
        {
            ["controller"] = controllerName,
            ["method"] = method.MethodName,
        };

        if (method.Parameters.Count > 0)
        {
            var bodySchema = new JsonObject { ["type"] = "object" };
            var properties = new JsonObject();
            var required = new JsonArray();
            foreach (var p in method.Parameters)
            {
                // Discovery seeds ParameterMeta.Documentation from the method summary; repeating it on
                // every parameter property is noise — apply it only when it says something distinct.
                var paramDoc = p.Documentation is null || p.Documentation == method.Documentation
                    ? null
                    : p.Documentation;
                var propSchema = SchemaFor(p.ParameterType, paramDoc);
                if (p.DefaultValue is null)
                    required.Add(p.ParameterName);
                else
                    propSchema["default"] = SerializeExample(p.DefaultValue);
                properties[p.ParameterName] = propSchema;
            }
            bodySchema["properties"] = properties;
            if (required.Count > 0)
                bodySchema["required"] = required;
            op["requestBody"] = new JsonObject
            {
                ["required"] = true,
                ["content"] = Content(bodySchema),
            };
        }
        // No parameters → no requestBody at all (Postman then sends a bare POST; the canonical
        // envelope tolerates an empty/absent parameters list).

        var responses = new JsonObject
        {
            ["200"] = new JsonObject
            {
                ["description"] = "Success (2xx).",
                ["content"] = Content(SchemaFor(method.ReturnType)),
            },
            ["default"] = new JsonObject
            {
                ["description"] = "Error — SleipnirError payload (business error or unexpected failure).",
                ["content"] = Content(new JsonObject
                {
                    ["type"] = "object",
                    ["description"] = "SleipnirError shape; see PROTOCOL.md.",
                }),
            },
        };
        op["responses"] = responses;

        return op;
    }

    private static JsonObject Content(JsonNode schema) => new()
    {
        ["application/json"] = new JsonObject { ["schema"] = schema },
    };

    /// <summary>Map a usage-site TypeRef to a JSON Schema (OpenAPI 3.1 dialect) node.</summary>
    private static JsonObject SchemaFor(TypeRef type, string? documentation = null)
    {
        var schema = SchemaForCore(type);
        if (documentation is not null && schema["description"] is null)
            schema["description"] = documentation;
        return schema;
    }

    private static JsonObject SchemaForCore(TypeRef type)
    {
        // Occurrence-level nullability (§7). Scalars/arrays/maps take the JSON-Schema type array;
        // $ref usages take an anyOf wrap (a $ref sibling alongside "type" would be ignored by
        // pre-2019-09 validators, so the wrap is the safe form for 3.0-and-3.1 tooling alike).
        if (type.Nullable == true)
            return WrapNullable(SchemaForCore(ShallowNotNull(type)));

        switch (type.Kind)
        {
            case "scalar":
                return ScalarSchema(type.Name);
            case "array":
            case "stream":
                // stream: the invoker materializes IAsyncEnumerable<T> into a JSON array on the
                // wire (discovery-schema.md §4) — array IS the wire truth for the result.
                var arr = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = SchemaForCore(type.Element!),
                };
                if (type.Kind == "stream")
                    arr["description"] = "Streaming method (IAsyncEnumerable<T>) — materialized as a JSON array on the wire.";
                return arr;
            case "set":
                return new JsonObject
                {
                    ["type"] = "array",
                    ["uniqueItems"] = true,
                    ["items"] = SchemaForCore(type.Element!),
                };
            case "map":
                // JSON object keys are strings; discovery keys are scalar-kind in practice, so the
                // key side needs no representation (a non-string scalar key cannot survive JSON).
                return new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = SchemaForCore(type.Value!),
                };
            case "ref":
                return new JsonObject { ["$ref"] = "#/components/schemas/" + type.Ref };
            case "opaque":
                // Empty schema = anything; nativeName is a diagnostic hint only (§6), surfaced as
                // a description so it can't be branched on programmatically.
                return new JsonObject
                {
                    ["description"] = $"Opaque (unmodelled) type: {type.NativeName ?? "unknown"}. Treated as any JSON value.",
                };
            case "void":
                return new JsonObject();
            default:
                return new JsonObject
                {
                    ["description"] = $"Unmapped discovery kind '{type.Kind}'.",
                };
        }
    }

    private static JsonObject WrapNullable(JsonNode baseSchema)
    {
        // Prefer the compact type-array form when the base is a plain type schema.
        if (baseSchema is JsonObject o && o["type"] is JsonValue v && v.TryGetValue<string>(out var t) && o.ContainsKey("$ref") is false)
        {
            o["type"] = new JsonArray(t, "null");
            return o;
        }
        return new JsonObject
        {
            ["anyOf"] = new JsonArray(baseSchema, new JsonObject { ["type"] = "null" }),
        };
    }

    /// <summary>Copy of a TypeRef with only the nullability flag cleared (children keep theirs —
    /// nullable is occurrence-level and nested slots carry their own flags).</summary>
    private static TypeRef ShallowNotNull(TypeRef t) => new()
    {
        Kind = t.Kind,
        Name = t.Name,
        Element = t.Element,
        Key = t.Key,
        Value = t.Value,
        Ref = t.Ref,
        NativeName = t.NativeName,
        Nullable = null,
    };

    private static JsonObject ScalarSchema(string? name) => name switch
    {
        // The scalar table is closed (discovery-schema.md §3); the wire casing of values follows
        // System.Text.Json's serialization of the .NET source type.
        "string" => new JsonObject { ["type"] = "string" },
        "char" => new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 1 },
        "bool" => new JsonObject { ["type"] = "boolean" },
        "int" => new JsonObject { ["type"] = "integer", ["format"] = "int32" },
        "long" => new JsonObject { ["type"] = "integer", ["format"] = "int64" },
        "float" => new JsonObject { ["type"] = "number", ["format"] = "float" },
        "double" => new JsonObject { ["type"] = "number", ["format"] = "double" },
        "decimal" => new JsonObject { ["type"] = "number" }, // JSON has no decimal; STJ emits a number
        "datetime" => new JsonObject { ["type"] = "string", ["format"] = "date-time" },
        "datetimeoffset" => new JsonObject { ["type"] = "string", ["format"] = "date-time" },
        "dateonly" => new JsonObject { ["type"] = "string", ["format"] = "date" },
        "timeonly" => new JsonObject { ["type"] = "string", ["format"] = "time" },
        "timespan" => new JsonObject { ["type"] = "string", ["format"] = "duration" }, // ISO 8601 "c"
        "guid" => new JsonObject { ["type"] = "string", ["format"] = "uuid" },
        "bytes" => new JsonObject { ["type"] = "string", ["contentEncoding"] = "base64" }, // binary via SleipnirRequest.BinaryData
        "uri" => new JsonObject { ["type"] = "string", ["format"] = "uri" },
        "version" => new JsonObject { ["type"] = "string" },
        "any" => new JsonObject(), // empty schema = any JSON value
        _ => new JsonObject
        {
            ["description"] = $"Unknown scalar '{name}' (scalar table is closed — treated as any JSON value).",
        },
    };

    /// <summary>Map a registry TypeMeta (definition site) to a component schema. Property names are
    /// written in WIRE casing (camelCase) — the PascalCase names in discovery are C# identity;
    /// discovery's own `example` values are already wire-cased, and responses are serialized by
    /// System.Text.Json with camelCase policy, so the schema must match those.</summary>
    private static JsonObject SchemaForType(TypeMeta type)
    {
        if (type.Kind == "enum")
        {
            var schema = new JsonObject();
            var values = new JsonArray();
            var names = new JsonArray();
            var isString = false;
            foreach (var member in type.Members ?? new List<EnumMember>())
            {
                names.Add(member.Name);
                if (member.Value is string s) { values.Add(s); isString = true; }
                // SerializeToNode (not JsonValue.Create) — a boxed object? would produce an
                // untyped JsonValue needing a TypeInfoResolver at write time.
                else values.Add(JsonSerializer.SerializeToNode(member.Value ?? 0));
            }
            // System.Text.Json serializes C# enums as their underlying number by default —
            // numeric enum values are the wire truth; names ride along as an extension.
            schema["type"] = isString ? "string" : "integer";
            schema["enum"] = values;
            schema["x-sleipnir-enum-names"] = names;
            if (type.Example is not null)
                schema["example"] = SerializeExample(type.Example);
            return schema;
        }

        var obj = new JsonObject { ["type"] = "object" };
        var props = new JsonObject();
        var required = new JsonArray();
        foreach (var p in type.Properties)
        {
            props[JsonNamingPolicy.CamelCase.ConvertName(p.PropertyName)] = SchemaFor(p.PropertyType);
            if (p.PropertyType.Nullable != true)
                required.Add(JsonNamingPolicy.CamelCase.ConvertName(p.PropertyName));
        }
        obj["properties"] = props;
        if (required.Count > 0)
            obj["required"] = required;
        if (type.Example is not null)
            obj["example"] = SerializeExample(type.Example);
        return obj;
    }

    /// <summary>Serialize a TypeMeta.Example (a live .NET instance) with the SAME options the
    /// discovery wire uses, so the schema's example carries wire (camelCase) casing — a default
    /// JsonSerializerOptions call re-emits PascalCase CLR names and contradicts the schema's own
    /// camelCase property names.</summary>
    private static JsonNode SerializeExample(object example)
        => JsonSerializer.SerializeToNode(example, DiscoverySerialization.Options);
}