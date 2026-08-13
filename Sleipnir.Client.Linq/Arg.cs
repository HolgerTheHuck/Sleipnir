namespace Sleipnir.Client.Linq;

/// <summary>
/// Non-generic view over an <see cref="Arg{T}"/> so the LINQ client can inspect, without reflection on
/// the generic type, whether an argument is a <see cref="Dep{T}"/> placeholder or a concrete value.
/// </summary>
public interface IArg
{
    /// <summary>True when the argument is a <see cref="Dep{T}"/> placeholder.</summary>
    bool IsDep { get; }

    /// <summary>The alias of the <see cref="Dep{T}"/>, or null when it is a value.</summary>
    string? Alias { get; }

    /// <summary>The concrete (boxed) value, or null when it is a Dep.</summary>
    object? Value { get; }
}

/// <summary>
/// Argument wrapper for contract parameters: accepts both concrete values (<c>int</c>, <c>string</c>,
/// …) and <see cref="Dep{T}"/> placeholders via implicit conversions. Both can be written inside a
/// lambda:
///   <c>c.GetCustomerById(5)</c>       (int → Arg&lt;int&gt;)
///   <c>c.GetCustomerById(newId)</c>  (Dep&lt;int&gt; → Arg&lt;int&gt;)
/// and the compiler checks the type — a <c>Dep&lt;string&gt;</c> where an <c>Arg&lt;int&gt;</c> is
/// expected is a compile error. This is the load-bearing compile-time check that combats runtime
/// uncertainty in <c>@alias</c> wiring.
/// </summary>
public readonly struct Arg<T> : IArg
{
    private readonly T? _value;
    private readonly Dep<T>? _dep;

    public Arg(T value)
    {
        _value = value;
        _dep = null;
    }

    public Arg(Dep<T> dep)
    {
        _value = default;
        _dep = dep;
    }

    /// <summary>True when this argument is a <see cref="Dep{T}"/> placeholder.</summary>
    public bool IsDep => _dep is not null;

    /// <summary>The alias of the <see cref="Dep{T}"/>, or null for a value.</summary>
    public string? Alias => _dep?.Alias;

    /// <summary>The concrete (boxed) value, or null for a Dep.</summary>
    public object? Value => _value;

    /// <summary>Build from a concrete literal value.</summary>
    public static implicit operator Arg<T>(T value) => new(value);

    /// <summary>Build from a typed dependency placeholder. Same T only — the compile-time check.</summary>
    public static implicit operator Arg<T>(Dep<T> dep) => new(dep);
}