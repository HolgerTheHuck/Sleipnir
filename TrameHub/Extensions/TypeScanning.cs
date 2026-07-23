using System.Reflection;

namespace TrameHub.Extensions;

/// <summary>
/// Tolerant type enumeration for the AppDomain-wide <c>[TrameController]</c> auto-discovery scan.
/// </summary>
/// <remarks>
/// <see cref="Assembly.GetTypes()"/> throws <see cref="ReflectionTypeLoadException"/> as soon as a
/// single type in a loaded assembly cannot be resolved — for example a missing transitive
/// dependency of some unrelated assembly that merely happens to be loaded in the host (common in
/// test hosts and plugin scenarios, where e.g. a Roslyn source-generator assembly is present).
/// That would abort the entire scan and fail every transport. Controllers always live in our own
/// assemblies and always resolve, so returning the subset of types that did load and skipping the
/// rest loses no real controllers — it only stops an unrelated unloadable type from bringing down
/// the host.
/// </remarks>
internal static class TypeScanning
{
    /// <summary>Returns the types <paramref name="assembly"/> exposes, skipping any that fail to load.</summary>
    public static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // ex.Types holds every attempted type with nulls for the ones that failed to load.
            return ex.Types.OfType<Type>();
        }
    }
}