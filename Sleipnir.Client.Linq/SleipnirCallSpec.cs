using System.Linq.Expressions;
using System.Text.Json.Nodes;
using SleipnirCommon.Models;

namespace Sleipnir.Client.Linq;

/// <summary>
/// Root type of a built call. Holds the state (controller, method, id, serialized parameters,
/// dependencyMapping). The generic <see cref="SleipnirCallSpec{T}"/> adds only the type-safe
/// <c>Expose</c> methods; this base is non-generic so <see cref="SleipnirBatch"/> and
/// <see cref="SleipnirLinqClient.BuildVoid{TService}"/> can work without type parameters.
/// </summary>
public abstract class SleipnirCallSpec
{
    public string Controller { get; protected set; }
    public string Method { get; protected set; }
    public string Id { get; protected set; }
    public JsonNode? Params { get; set; }
    public Dictionary<string, string>? DependencyMapping { get; set; }
    private int _exposeCounter;

    protected SleipnirCallSpec(string controller, string method, string id, JsonNode? paramsNode)
    {
        Controller = controller;
        Method = method;
        Id = id;
        Params = paramsNode;
    }

    /// <summary>
    /// Expose a JsonPath from the result as an alias and register the associated <c>dependencyMapping</c>
    /// on this call. Called by the generic <c>Expose</c> methods on <see cref="SleipnirCallSpec{T}"/>.
    /// </summary>
    protected Dep<TProp> ExposePath<TProp>(string path)
    {
        // The alias must be purely alphanumeric + '_' for the server-side DependencyGraphBuilder
        // (ExtractAliases breaks on '.', '#', etc.) — otherwise the edge is not recognized, the
        // dependent lands in the wrong batch, and the @alias placeholder stays unresolved.
        var safeId = new string(Id.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray());
        var alias = $"{safeId}__dep{++_exposeCounter}";
        (DependencyMapping ??= new Dictionary<string, string>())[alias] = path;
        return new Dep<TProp>(alias);
    }

    public SleipnirRequest ToRequest() => new()
    {
        Controller = Controller,
        Method = Method,
        Id = Id,
        Params = Params,
        DependencyMapping = DependencyMapping
    };
}

/// <summary>
/// A type-safely built Sleipnir call with return type <typeparamref name="T"/>. Produced by
/// <see cref="SleipnirLinqClient.Build{TService,T}"/> / <see cref="SleipnirLinqClient.BuildVoid{TService}"/>.
/// <c>Expose</c> yields <see cref="Dep{T}"/> markers for dependent calls. The shared state lives in the
/// base class <see cref="SleipnirCallSpec"/>.
/// </summary>
public sealed class SleipnirCallSpec<T> : SleipnirCallSpec
{
    internal SleipnirCallSpec(string controller, string method, string id, JsonNode? paramsNode)
        : base(controller, method, id, paramsNode)
    {
    }

    /// <summary>
    /// Expose the entire result ($) as a <see cref="Dep{T}"/>. Registers <c>dependencyMapping</c>
    /// {alias → "$"} on this call.
    /// </summary>
    public Dep<T> Expose() => ExposePath<T>("$");

    /// <summary>
    /// Expose a property of the result (or an element path), e.g. <c>x =&gt; x!.Name</c> → "$.name" or
    /// <c>x =&gt; x[0].Id</c> → "$[0].id". The JsonPath is result-relative (Sleipnir convention) and built
    /// from the expression, so it cannot be mistyped.
    /// </summary>
    public Dep<TProp> Expose<TProp>(Expression<Func<T, TProp>> selector)
    {
        if (selector is null) throw new ArgumentNullException(nameof(selector));
        var path = JsonPathBuilder.Build(selector.Body);
        return ExposePath<TProp>(path);
    }
}