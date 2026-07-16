namespace Trame.Spike.LinqProvider;

/// <summary>
/// Nicht-generische Sicht auf ein <see cref="Arg{T}"/>-Argument, damit der
/// LINQ-Client ohne Reflexion auf den generischen Typ prüfen kann, ob ein
/// Argument ein <see cref="Dep{T}"/>-Platzhalter oder ein konkreter Wert ist.
/// </summary>
public interface IArg
{
    /// <summary>True, wenn das Argument ein <see cref="Dep{T}"/>-Platzhalter ist.</summary>
    bool IsDep { get; }

    /// <summary>Alias des <see cref="Dep{T}"/> oder null, falls es ein Wert ist.</summary>
    string? Alias { get; }

    /// <summary>Der konkrete (geboxte) Wert oder null, falls es ein Dep ist.</summary>
    object? Value { get; }
}

/// <summary>
/// Argument-Wrapper für Vertrags-Parameter: akzeptiert sowohl konkrete Werte
/// (<c>int</c>, <c>string</c>, …) als auch <see cref="Dep{T}"/>-Platzhalter über
/// implizite Konvertierungen. Dadurch lässt sich im Lambda beides notieren:
///   <c>c.GetCustomerById(5)</c>          (int → Arg&lt;int&gt;)
///   <c>c.GetCustomerById(newId)</c>     (Dep&lt;int&gt; → Arg&lt;int&gt;)
/// und der Compiler prüft den Typ — ein <c>Dep&lt;string&gt;</c> an einer
/// <c>Arg&lt;int&gt;</c>-Stelle ist ein Compile-Fehler.
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

    public bool IsDep => _dep != null;
    public string? Alias => _dep?.Alias;
    public object? Value => _value;

    public static implicit operator Arg<T>(T value) => new(value);
    public static implicit operator Arg<T>(Dep<T> dep) => new(dep);
}