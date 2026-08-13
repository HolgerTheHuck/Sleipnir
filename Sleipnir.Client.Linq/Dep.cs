namespace Sleipnir.Client.Linq;

/// <summary>
/// Type-safe marker for a value obtained from the result of a preceding call. A <see cref="Dep{T}"/>
/// is produced only by <see cref="SleipnirCallSpec{T}.Expose()"/> /
/// <see cref="SleipnirCallSpec{T}.Expose{TProp}"/> and is fed as an argument into a subsequent call's
/// lambda. The compiler guarantees the type fits: a <see cref="Dep{T}"/> of <c>int</c> only goes where an
/// <c>int</c> is expected — the wiring can no longer fail at runtime on a JSON-string placeholder
/// mismatch.
///
/// Internally <see cref="Dep{T}"/> carries only the alias name; the associated JsonPath is registered as
/// <c>dependencyMapping</c> on the producing call. The server resolves <c>@alias</c> before execution.
/// </summary>
public sealed class Dep<T>
{
    /// <summary>Alias name; referenced on the wire via <c>@Alias</c>.</summary>
    public string Alias { get; }

    internal Dep(string alias)
    {
        Alias = alias ?? throw new ArgumentNullException(nameof(alias));
    }

    public override string ToString() => $"@{Alias} (Dep<{typeof(T).Name}>)";
}