using System.Linq.Expressions;
using System.Text.Json.Nodes;

namespace Sleipnir.Spike.LinqProvider;

/// <summary>
/// Typsicherer Marker für einen Wert, der aus dem Resultat eines Vorgänger-Calls
/// bezogen wird. <see cref="Dep{T}"/> entsteht ausschließlich über
/// <see cref="SleipnirCallSpec{T}.Expose()"/> / <see cref="SleipnirCallSpec{T}.Expose{TProp}"/>
/// und wird als Argument in einen Folge-Call (Lambda) eingesteckt. Der Compiler
/// stellt sicher, dass der Typ stimmt: ein <see cref="Dep{int}"/> passt nur dort,
/// wo ein <c>int</c> erwartet wird — die Verdrahtung kann also nicht mehr zur
/// Laufzeit an einem JSON-String-Platzhalter scheitern.
///
/// Intern trägt <see cref="Dep{T}"/> nur den Alias-Namen; der zugehörige JsonPath
/// ist beim erzeugenden Call als <c>dependencyMapping</c> hinterlegt. Der Server
/// löst <c>@alias</c> vor der Ausführung auf.
/// </summary>
public sealed class Dep<T>
{
    /// <summary>Alias-Name, serverseitig über <c>@Alias</c> referenzierbar.</summary>
    public string Alias { get; }

    internal Dep(string alias)
    {
        Alias = alias ?? throw new ArgumentNullException(nameof(alias));
    }

    public override string ToString() => $"@{Alias} (Dep<{typeof(T).Name}>)";
}

/// <summary>
/// Ein typsicher gebauter Sleipnir-Call mit Rückgabetyp <typeparamref name="T"/>.
/// Entsteht aus <see cref="SleipnirLinqClient.Build{T}"/> /
/// <see cref="SleipnirLinqClient.BuildVoid{TService}"/>. Über <c>Expose</c> werden
/// <see cref="Dep{T}"/>-Marker für Folge-Calls bereitgestellt. Der gemeinsame
/// Zustand liegt in der Basisklasse <see cref="SleipnirCallSpec"/>.
/// </summary>
public sealed class SleipnirCallSpec<T> : SleipnirCallSpec
{
    internal SleipnirCallSpec(string controller, string method, string id, JsonNode? paramsNode)
        : base(controller, method, id, paramsNode)
    {
    }

    /// <summary>
    /// Stellt das gesamte Resultat ($) als <see cref="Dep{T}"/> bereit. Registriert
    /// gleichzeitig das <c>dependencyMapping</c> {alias → "$"} an diesem Call.
    /// </summary>
    public Dep<T> Expose() => ExposePath<T>("$");

    /// <summary>
    /// Stellt eine Eigenschaft des Resultats (oder einen Element-Pfad) bereit, z. B.
    /// <c>x =&gt; x.Name</c> → "$.Name" oder <c>x =&gt; x[0].Id</c> → "$[0].Id".
    /// Der JsonPath ist ergebnisrelativ (siehe Sleipnir-Konvention).
    /// </summary>
    public Dep<TProp> Expose<TProp>(Expression<Func<T, TProp>> selector)
    {
        if (selector == null) throw new ArgumentNullException(nameof(selector));
        var path = JsonPathBuilder.Build(selector.Body);
        return ExposePath<TProp>(path);
    }
}