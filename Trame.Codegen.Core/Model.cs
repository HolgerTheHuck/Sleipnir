// Discovery POCO model + the resolved intermediate the emitter consumes. The
// producer emits a language-neutral TypeRef IR (docs/discovery-schema.md); this
// layer is a passthrough that walks the raw DiscoveryInfo once to apply the
// wire-correctness fixes (camelCase property names, enum-ref→scalar collapse,
// opaque handling) and keeps the C# emitter thin. Ported from
// clients/codegen/src/core/model.ts.
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Trame.Codegen.Core;

// --- Wire contract POCOs (deserialized from the discovery JSON) ----------------------------

internal sealed class TypeRef
{
    public string Kind { get; set; } = "";
    public string? Name { get; set; }
    public string? Ref { get; set; }
    public bool? Nullable { get; set; }
    public string? NativeName { get; set; }
    public TypeRef? Element { get; set; }
    public TypeRef? Key { get; set; }
    public TypeRef? Value { get; set; }
}

internal sealed class PropertyMeta
{
    public string PropertyName { get; set; } = "";
    public TypeRef PropertyType { get; set; } = new();
    public string? Documentation { get; set; }
}

internal sealed class EnumMember
{
    public string Name { get; set; } = "";
    public int? Value { get; set; }
}

internal sealed class TypeMeta
{
    public string Kind { get; set; } = "";
    public string? TypeName { get; set; }
    public List<PropertyMeta>? Properties { get; set; }
    public List<EnumMember>? Members { get; set; }
    public JsonElement? Example { get; set; }
}

internal sealed class ParameterMeta
{
    public string ParameterName { get; set; } = "";
    public TypeRef ParameterType { get; set; } = new();
    public string? Documentation { get; set; }
}

internal sealed class MethodMeta
{
    public string MethodName { get; set; } = "";
    public TypeRef? ReturnType { get; set; }
    public List<ParameterMeta>? Parameters { get; set; }
    public string? Documentation { get; set; }
}

internal sealed class ControllerMeta
{
    public string Name { get; set; } = "";
    public List<MethodMeta>? Methods { get; set; }
}

// --- Resolved intermediate (the emitter's input) -------------------------------------------

internal sealed class ResolvedProperty
{
    public ResolvedProperty(string wireName, string declaredName, TypeRef typeRef, string? documentation)
    {
        WireName = wireName;
        DeclaredName = declaredName;
        TypeRef = typeRef;
        Documentation = documentation;
    }
    /// <summary>camelCase wire name (matches the server's CamelCase policy).</summary>
    public string WireName { get; }
    /// <summary>Original PascalCase name from discovery (for comments / C# emitter).</summary>
    public string DeclaredName { get; }
    public TypeRef TypeRef { get; }
    public string? Documentation { get; }
}

internal sealed class ResolvedType
{
    public ResolvedType(string fullName, string emittedName, List<ResolvedProperty> properties)
    {
        FullName = fullName;
        EmittedName = emittedName;
        Properties = properties;
    }
    public string FullName { get; }
    /// <summary>Emitted identifier (collision-disambiguated via NamingResolver).</summary>
    public string EmittedName { get; }
    public List<ResolvedProperty> Properties { get; }
}

internal sealed class ResolvedParameter
{
    public ResolvedParameter(string name, TypeRef typeRef, string? documentation)
    {
        Name = name;
        TypeRef = typeRef;
        Documentation = documentation;
    }
    /// <summary>Parameter name — bound case-sensitively on the wire, kept as-is.</summary>
    public string Name { get; }
    public TypeRef TypeRef { get; }
    public string? Documentation { get; }
}

internal sealed class ResolvedMethod
{
    public ResolvedMethod(string methodName, string controller, List<ResolvedParameter> parameters, TypeRef returnType, bool isVoid, string? documentation)
    {
        MethodName = methodName;
        Controller = controller;
        Parameters = parameters;
        ReturnType = returnType;
        IsVoid = isVoid;
        Documentation = documentation;
    }
    public string MethodName { get; }
    public string Controller { get; }
    public List<ResolvedParameter> Parameters { get; }
    public TypeRef ReturnType { get; }
    /// <summary>void / Task (no result) → the emitter still returns Call.</summary>
    public bool IsVoid { get; }
    public string? Documentation { get; }
}

internal sealed class ResolvedController
{
    public ResolvedController(string name, string className, List<ResolvedMethod> methods)
    {
        Name = name;
        ClassName = className;
        Methods = methods;
    }
    public string Name { get; }
    /// <summary>PascalCase emitted class name (<c>Order</c> → <c>OrderClient</c>).</summary>
    public string ClassName { get; }
    public List<ResolvedMethod> Methods { get; }
}

internal sealed class EmitterInput
{
    public EmitterInput(List<ResolvedController> controllers, List<ResolvedType> types, JsonObject discovery)
    {
        Controllers = controllers;
        Types = types;
        Discovery = discovery;
    }
    public List<ResolvedController> Controllers { get; }
    public List<ResolvedType> Types { get; }
    /// <summary>Raw discovery, retained for emitters that need example payloads.</summary>
    public JsonObject Discovery { get; }
}

// --- Emitter options ------------------------------------------------------------------------

public sealed class EmitCsOptions
{
    /// <summary>C# namespace for the generated file (default "Trame.Generated").</summary>
    public string? Namespace { get; set; }
    /// <summary>Base URL hint rendered into the file header comment.</summary>
    public string? BaseUrl { get; set; }
}

/// <summary>JSON options for reading the discovery contract (case-insensitive binding).</summary>
internal static class ReadOptions
{
    public static readonly JsonSerializerOptions Instance = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}