// BuildEmitterInput + TypeRef normalization + CsTypeOfRef. Ported from
// clients/codegen/src/core/model.ts. The producer already builds the language-neutral
// TypeRef IR; this layer collapses enum refs to their numeric wire scalar (long —
// lossless for every C# enum backing) so the emitters never see an enum ref, and
// fixes the PascalCase→camelCase property-name bug (every emitted property runs
// through ToCamelCase here). The C# emitter then consumes the resolved input.
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sleipnir.Codegen.Core;

internal static class EmitterBuilder
{
    private static readonly TypeRef VoidRef = new() { Kind = "void" };
    private static readonly TypeRef OpaqueRef = new() { Kind = "opaque" };
    private static readonly TypeRef StringScalar = new() { Kind = "scalar", Name = "string" };

    /// <summary>Walk a validated discovery object once into an <see cref="EmitterInput"/>.</summary>
    public static EmitterInput Build(JsonObject discovery, NamingResolver resolver)
    {
        var typesObj = discovery["types"]!.AsObject();

        // First pass: register all object type names (so collision detection sees the full set);
        // enum TypeMetas stay in discovery for documentation but are not emitted as structured types.
        var enumKeys = new HashSet<string>();
        foreach (var kv in typesObj)
        {
            var tm = JsonSerializer.Deserialize<TypeMeta>(kv.Value, ReadOptions.Instance)!;
            if (tm.Kind == "enum") enumKeys.Add(kv.Key);
            else resolver.Register(kv.Key);
        }

        // Second pass: build resolved types (skip enums).
        var types = new List<ResolvedType>();
        foreach (var kv in typesObj)
        {
            var tm = JsonSerializer.Deserialize<TypeMeta>(kv.Value, ReadOptions.Instance)!;
            if (tm.Kind == "enum") continue;
            var props = new List<ResolvedProperty>();
            foreach (var p in tm.Properties ?? new())
                props.Add(new ResolvedProperty(Casing.ToCamelCase(p.PropertyName), p.PropertyName, NormalizeRef(p.PropertyType, enumKeys), p.Documentation));
            types.Add(new ResolvedType(kv.Key, resolver.Resolve(kv.Key), props));
        }

        // Controllers (preserve discovery order — the emitter does NOT sort).
        var controllers = new List<ResolvedController>();
        foreach (var item in discovery["controllers"]!.AsArray())
        {
            var c = JsonSerializer.Deserialize<ControllerMeta>(item, ReadOptions.Instance)!;
            controllers.Add(ResolveController(c, enumKeys));
        }

        return new EmitterInput(controllers, types, discovery);
    }

    private static ResolvedController ResolveController(ControllerMeta ctrl, HashSet<string> enumKeys)
    {
        var methods = new List<ResolvedMethod>();
        foreach (var m in ctrl.Methods ?? new())
            methods.Add(ResolveMethod(ctrl.Name, m, enumKeys));
        return new ResolvedController(ctrl.Name, Casing.PascalCase(ctrl.Name) + "Client", methods);
    }

    private static ResolvedMethod ResolveMethod(string controllerName, MethodMeta method, HashSet<string> enumKeys)
    {
        var isVoid = method.ReturnType?.Kind == "void";
        var parms = new List<ResolvedParameter>();
        foreach (var p in method.Parameters ?? new())
            parms.Add(new ResolvedParameter(p.ParameterName, NormalizeRef(p.ParameterType, enumKeys), p.Documentation));
        return new ResolvedMethod(
            method.MethodName,
            controllerName,
            parms,
            NormalizeRef(method.ReturnType ?? VoidRef, enumKeys),
            isVoid,
            method.Documentation);
    }

    /// <summary>Recursively collapse enum refs to their numeric wire scalar; recurse into element/key/value.</summary>
    private static TypeRef NormalizeRef(TypeRef r, HashSet<string> enumKeys)
    {
        if (r.Kind == "ref" && r.Ref != null && enumKeys.Contains(r.Ref))
        {
            // Enum serializes as its underlying integer on the wire; long is lossless for every C# enum backing.
            return new TypeRef { Kind = "scalar", Name = "long", Nullable = r.Nullable };
        }
        switch (r.Kind)
        {
            case "array":
            case "set":
            case "stream":
                return new TypeRef { Kind = r.Kind, Element = r.Element is { } e ? NormalizeRef(e, enumKeys) : null, Nullable = r.Nullable };
            case "map":
                return new TypeRef
                {
                    Kind = "map",
                    Key = r.Key is { } k ? NormalizeRef(k, enumKeys) : null,
                    Value = r.Value is { } v ? NormalizeRef(v, enumKeys) : null,
                    Nullable = r.Nullable,
                };
            default:
                return r;
        }
    }

    /// <summary>C# type string for a resolved ref (used by the C# emitter).</summary>
    public static string CsTypeOfRef(TypeRef r, NamingResolver resolver)
    {
        switch (r.Kind)
        {
            case "scalar": return Scalars.CsTypeOf(r.Name ?? "object");
            case "array": return "List<" + CsTypeOfRef(ElementOf(r), resolver) + ">";
            case "set": return "HashSet<" + CsTypeOfRef(ElementOf(r), resolver) + ">";
            // stream: the invoker materializes IAsyncEnumerable<T> to a list before serialization,
            // so the client receives a JSON array → List<T>.
            case "stream": return "List<" + CsTypeOfRef(ElementOf(r), resolver) + ">";
            case "map": return "Dictionary<" + CsTypeOfRef(r.Key ?? StringScalar, resolver) + ", " + CsTypeOfRef(r.Value ?? OpaqueRef, resolver) + ">";
            case "ref": return resolver.Resolve(r.Ref ?? "");
            case "opaque": return "object";
            case "void": return "void";
            default: return "object";
        }
    }

    /// <summary>The element TypeRef of an array/set/stream, or a fallback opaque ref.</summary>
    private static TypeRef ElementOf(TypeRef r) => r.Element ?? OpaqueRef;

    /// <summary>Append <c>?</c> to a POCO property type (discovery carries no nullability; callers narrow).</summary>
    public static string Nullable(string ty) => ty.EndsWith("?") ? ty : ty + "?";
}