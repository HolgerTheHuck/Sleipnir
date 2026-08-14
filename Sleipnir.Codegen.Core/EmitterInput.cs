// BuildEmitterInput + TypeRef normalization + CsTypeOfRef. Ported from
// clients/codegen/src/core/model.ts. The producer already builds the language-neutral
// TypeRef IR; this layer collapses enum refs to their numeric wire scalar (long —
// lossless for every C# enum backing) so the emitters never see an enum ref, and
// fixes the PascalCase→camelCase property-name bug (every emitted property runs
// through ToCamelCase here). The C# emitter then consumes the resolved input.
using System;
using System.Collections.Generic;
using System.Linq;
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
                props.Add(new ResolvedProperty(Casing.ToCamelCase(p.PropertyName), p.PropertyName, NormalizeRef(p.PropertyType, enumKeys), p.Documentation, p.Navigation));
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

    /// <summary>
    /// Drift-gate validation for navigation edges — the LINQ-contracts path only (called from
    /// <c>SleipnirCodegen.EmitContracts</c>, never <c>EmitClient</c>, so the Tier-1 source-generator
    /// design-time path is unaffected). For each resolved property carrying a <c>Navigation</c>:
    /// resolves <c>Fetch</c> to a real controller+method; resolves <c>Param</c> (explicit must name a
    /// collection parameter, absent is inferred from the method's single collection parameter and
    /// written back so the emitter always emits a non-null value); validates <c>Key</c> names a scalar
    /// property of the parent type whose scalar type matches the fetch parameter's element type
    /// (strict, no widening); and validates the navigation target is an expanded contract type
    /// (<c>ref</c> or a collection of <c>ref</c>), not <c>opaque</c>. Throws
    /// <see cref="DiscoveryShapeException"/> on any violation (refuse-to-emit, not a runtime failure).
    /// </summary>
    internal static void ValidateNavigation(EmitterInput input)
    {
        foreach (var type in input.Types)
        {
            foreach (var prop in type.Properties)
            {
                var nav = prop.Navigation;
                if (nav is null) continue;

                var where = $"type \"{type.FullName}\" property \"{prop.DeclaredName}\" navigation";

                // --- Fetch: "Controller.Method" split at the LAST dot → controller + method. ---
                var fetch = nav.Fetch ?? "";
                var dot = fetch.LastIndexOf('.');
                if (dot <= 0 || dot == fetch.Length - 1)
                    throw new DiscoveryShapeException($"{where}: fetch \"{fetch}\" is not a \"Controller.Method\" pair (missing dot).");
                var ctrlName = fetch.Substring(0, dot);
                var methodName = fetch.Substring(dot + 1);

                var controller = input.Controllers.FirstOrDefault(c => string.Equals(c.Name, ctrlName, StringComparison.Ordinal));
                if (controller is null)
                    throw new DiscoveryShapeException($"{where}: fetch references unknown controller \"{ctrlName}\".");
                var method = controller.Methods.FirstOrDefault(m => string.Equals(m.MethodName, methodName, StringComparison.Ordinal));
                if (method is null)
                    throw new DiscoveryShapeException($"{where}: fetch references unknown method \"{methodName}\" on controller \"{ctrlName}\".");

                // --- Param: explicit must name a collection param; absent → infer the single collection param. ---
                var collectionParams = method.Parameters.Where(p => p.TypeRef.Kind is "array" or "set" or "stream").ToList();
                if (!string.IsNullOrEmpty(nav.Param))
                {
                    var named = method.Parameters.FirstOrDefault(p => string.Equals(p.Name, nav.Param, StringComparison.Ordinal));
                    if (named is null)
                        throw new DiscoveryShapeException($"{where}: param \"{nav.Param}\" is not a parameter of \"{fetch}\".");
                    if (named.TypeRef.Kind is not ("array" or "set" or "stream"))
                        throw new DiscoveryShapeException($"{where}: param \"{nav.Param}\" of \"{fetch}\" is not a collection (List<T>/T[]/IEnumerable<T>).");
                }
                else
                {
                    if (collectionParams.Count == 0)
                        throw new DiscoveryShapeException($"{where}: fetch \"{fetch}\" has no collection parameter to infer param from; specify Param explicitly.");
                    if (collectionParams.Count > 1)
                        throw new DiscoveryShapeException($"{where}: fetch \"{fetch}\" has multiple collection parameters ({string.Join(", ", collectionParams.Select(p => p.Name))}); specify Param explicitly.");
                    nav.Param = collectionParams[0].Name;
                }

                var fetchParam = method.Parameters.First(p => string.Equals(p.Name, nav.Param, StringComparison.Ordinal));

                // --- Key: must name a scalar property of the parent type (by wire name). ---
                if (string.IsNullOrEmpty(nav.Key))
                    throw new DiscoveryShapeException($"{where}: key is empty.");
                var keyProp = type.Properties.FirstOrDefault(p => string.Equals(p.WireName, nav.Key, StringComparison.OrdinalIgnoreCase));
                if (keyProp is null)
                    throw new DiscoveryShapeException($"{where}: key \"{nav.Key}\" is not a property of \"{type.EmittedName}\".");
                if (keyProp.TypeRef.Kind != "scalar")
                    throw new DiscoveryShapeException($"{where}: key \"{nav.Key}\" must be a scalar property; \"{type.EmittedName}.{keyProp.DeclaredName}\" is kind \"{keyProp.TypeRef.Kind}\".");

                // --- Element-type match: key scalar == fetch param element scalar (strict, no widening). ---
                var paramElem = fetchParam.TypeRef.Element;
                if (paramElem is null || paramElem.Kind != "scalar")
                    throw new DiscoveryShapeException($"{where}: fetch param \"{nav.Param}\" element is not a scalar (cannot match key \"{nav.Key}\").");
                if (!string.Equals(keyProp.TypeRef.Name, paramElem.Name, StringComparison.Ordinal))
                    throw new DiscoveryShapeException($"{where}: key \"{nav.Key}\" scalar \"{keyProp.TypeRef.Name}\" does not match fetch param \"{nav.Param}\" element scalar \"{paramElem.Name}\".");

                // --- Opaque-target check: nav target must be an expanded contract type (ref or collection of ref). ---
                TypeRef? leafRef = prop.TypeRef.Kind == "ref" ? prop.TypeRef
                    : prop.TypeRef.Kind is "array" or "set" or "stream" ? prop.TypeRef.Element
                    : null;
                if (leafRef is null || leafRef.Kind != "ref")
                    throw new DiscoveryShapeException($"{where}: navigation target must be a contract type (ref) or a collection thereof; property \"{prop.DeclaredName}\" is kind \"{prop.TypeRef.Kind}\".");
            }
        }
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