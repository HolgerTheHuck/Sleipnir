using SleipnirCore.Model.Messages.Mex;

namespace SleipnirCore.Services.Helper;

/// <summary>
/// Finds every place the discovery contract uses an <c>opaque</c> type. Opaque means "not
/// modelled": a type declared outside the controller assemblies (typically a domain type from a
/// Core project), a framework envelope (the non-generic <c>SleipnirResponse</c>) or another BCL
/// type. Every generated client then sees <c>unknown</c>/<c>object</c>/<c>Any</c> — previously
/// without any hint at build or start time. <c>UseSleipnir</c> logs the sites once at startup;
/// <c>sleipnir-gen</c> runs the same scan over the discovery JSON (clients/codegen/src/core/opaque.ts).
/// </summary>
internal static class DiscoveryOpaqueScan
{
    /// <summary>A usage site (<c>Run.Execute → return</c>, <c>Run.Execute(summary)</c>,
    /// <c>Ns.InvoiceDto.Lines</c>) and the unmodelled type's short name.</summary>
    internal readonly record struct OpaqueUsage(string Site, string NativeName);

    internal static IReadOnlyList<OpaqueUsage> Find(DiscoveryInfo discovery)
    {
        var usages = new List<OpaqueUsage>();
        foreach (var controller in discovery.Controllers)
        {
            foreach (var method in controller.Methods)
            {
                var route = $"{controller.Name}.{method.MethodName}";
                var returnSite = method.ReturnType.Kind == "event" ? $"{route} → event payload" : $"{route} → return";
                Collect(method.ReturnType, returnSite, usages);
                foreach (var p in method.Parameters)
                    Collect(p.ParameterType, $"{route}({p.ParameterName})", usages);
            }
        }
        foreach (var (key, type) in discovery.Types)
        {
            foreach (var p in type.Properties)
                Collect(p.PropertyType, $"{key}.{p.PropertyName}", usages);
        }
        return usages;
    }

    private static void Collect(TypeRef? typeRef, string site, List<OpaqueUsage> usages)
    {
        if (typeRef == null)
            return;
        switch (typeRef.Kind)
        {
            case "opaque":
                usages.Add(new OpaqueUsage(site, typeRef.NativeName ?? "?"));
                break;
            case "array" or "set" or "stream" or "event":
                Collect(typeRef.Element, site, usages);
                break;
            case "map":
                Collect(typeRef.Key, site, usages);
                Collect(typeRef.Value, site, usages);
                break;
        }
    }
}
