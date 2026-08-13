using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SleipnirClient.Sleipnir;
using SleipnirCommon.Models;

namespace Sleipnir.Client.Linq;

/// <summary>
/// A LINQ-provider-style client: builds a type-safe <see cref="SleipnirCallSpec{T}"/> from a lambda
/// <c>(TService c) =&gt; c.Method(args)</c>. Controller/method names come from the contract attributes
/// (<see cref="SleipnirServiceContractAttribute"/> / <see cref="SleipnirMethodContractAttribute"/>),
/// parameter names from the method signature. Arguments are JSON-serialized; <see cref="Dep{T}"/>
/// arguments become <c>@alias</c> placeholders. The compile-time type check lives in the lambda: a
/// <see cref="Dep{T}"/> of the wrong type does not convert into the parameter's <see cref="Arg{T}"/>,
/// so the wiring is rejected by the compiler.
/// </summary>
public sealed class SleipnirLinqClient
{
    private readonly ISleipnirClient _transport;
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private int _idCounter;

    public SleipnirLinqClient(ISleipnirClient transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    /// <summary>
    /// The JSON options used to (de)serialize call arguments and results (camelCase wire, case-insensitive
    /// read). Exposed internally so the Tier-2 query façade (<c>SleipnirQuery</c>) shares the exact same
    /// contract for <c>.Where</c> param serialization and <c>Materialize</c> deserialization.
    /// </summary>
    internal static JsonSerializerOptions Options => _jsonOpts;

    /// <summary>
    /// Build a typed call with a return value:
    /// <c>client.Build((IOrderService c) =&gt; c.Create(dto))</c> → <see cref="SleipnirCallSpec{T}"/>
    /// with T = the method's return type.
    /// </summary>
    public SleipnirCallSpec<T> Build<TService, T>(Expression<Func<TService, Task<T>>> call)
        where TService : class
        => (SleipnirCallSpec<T>)BuildCore(typeof(T), call);

    /// <summary>
    /// Build a void call (a method without a return value):
    /// <c>client.BuildVoid((IOrderService c) =&gt; c.Delete(5))</c>.
    /// </summary>
    public SleipnirCallSpec<object> BuildVoid<TService>(Expression<Func<TService, Task>> call)
        where TService : class
        => (SleipnirCallSpec<object>)BuildCore(typeof(object), call);

    /// <summary>
    /// Start a Tier-2 <see cref="SleipnirQuery{TEntity}"/> from a collection-root controller-method call:
    /// <c>linq.From((ICustomerService c) =&gt; c.SelectCustomer(10, "hallo"))</c>. <typeparamref name="TEntity"/>
    /// is the element type of the method's <c>Task&lt;List&lt;TEntity&gt;?&gt;</c> return — known from the
    /// generated contract, so the whole <c>.Include</c>/<c>.ThenInclude</c> chain is compile-checked
    /// client-side. The root spec is produced by the Tier-1 <see cref="Build{TService,T}"/> (param binding
    /// reuses <c>Arg&lt;T&gt;</c> verbatim); navigations are added by the <c>SleipnirQuery</c> extensions and
    /// compiled to <c>@alias</c> edges at <c>.Build()</c>. See <c>LINQ_QUERY.md</c>.
    /// </summary>
    public SleipnirQuery<TEntity> From<TService, TEntity>(Expression<Func<TService, Task<List<TEntity>?>>> call)
        where TService : class
        where TEntity : class
    {
        var rootSpec = Build<TService, List<TEntity>?>(call);
        return new SleipnirQuery<TEntity>(new QueryState(this, rootSpec, typeof(TEntity)));
    }

    private SleipnirCallSpec BuildCore(Type resultType, LambdaExpression call)
    {
        if (call?.Body is not MethodCallExpression mce)
            throw new ArgumentException(
                "Expected a single method call on the service, e.g. c => c.Method(args).");

        var svcType = mce.Method.DeclaringType
            ?? throw new ArgumentException("Method call without a declaring type.");
        var controller = svcType.GetCustomAttribute<SleipnirServiceContractAttribute>()
            ?? throw new ArgumentException(
                $"{svcType.Name} carries no [SleipnirServiceContract] — is it a generated contract?");
        var methodAttr = mce.Method.GetCustomAttribute<SleipnirMethodContractAttribute>()
            ?? throw new ArgumentException(
                $"{mce.Method.Name} carries no [SleipnirMethodContract].");

        // Parameter names come from the method signature; argument values are resolved by value.
        // Contract parameters are Arg<T> wrappers — an Arg carries either a concrete value or a
        // Dep<T> placeholder (→ @alias).
        var paramInfos = mce.Method.GetParameters();
        var parameters = new List<SleipnirParameter>(paramInfos.Length);
        for (int i = 0; i < paramInfos.Length; i++)
        {
            object? value = EvaluateArgument(mce.Arguments[i]);
            JsonNode? data;
            if (value is IArg arg)
            {
                // Arg<T>: Dep → @alias (server-side substitution, native string value with '@' prefix),
                // otherwise the native JSON value (no JSON string wrapping).
                data = arg.IsDep
                    ? JsonValue.Create("@" + arg.Alias)
                    : JsonSerializer.SerializeToNode(arg.Value, _jsonOpts);
            }
            else
            {
                // Fallback: a bare value (no Arg<T> wrapper).
                data = JsonSerializer.SerializeToNode(value, _jsonOpts);
            }
            parameters.Add(new SleipnirParameter
            {
                Num = i,
                ParameterName = paramInfos[i].Name ?? $"param{i}",
                Data = data
            });
        }

        var id = $"{controller.Controller}.{methodAttr.Method}#{++_idCounter}";
        var specType = typeof(SleipnirCallSpec<>).MakeGenericType(resultType);
        return (SleipnirCallSpec)Activator.CreateInstance(
            specType,
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            new object[] { controller.Controller, methodAttr.Method, id, JsonSerializer.SerializeToNode(parameters) },
            null)!;
    }

    /// <summary>
    /// Resolve an argument expression at runtime. The spike compiles each argument individually — this
    /// covers constants, captured locals (closures), and <see cref="Dep{T}"/> markers uniformly. (A full
    /// LINQ provider would fold constant parts at build time.)
    /// </summary>
    private static object? EvaluateArgument(Expression arg)
    {
        // Constant directly — saves the compile and is the common form.
        if (arg is ConstantExpression ce)
            return ce.Value;
        // Otherwise compile & invoke (covers captured locals and Deps).
        return Expression.Lambda(arg).Compile().DynamicInvoke();
    }

    /// <summary>
    /// Send a single typed call and deserialize the result as T.
    /// </summary>
    public async Task<T?> SendAsync<T>(SleipnirCallSpec<T> spec, CancellationToken ct = default)
    {
        var resp = await _transport.Call(spec.ToRequest(), ct);
        return Deserialize<T>(resp);
    }

    /// <summary>
    /// Send a batch (multi-request) over the transport. Returns the raw responses; typed extraction
    /// via <see cref="ResultOf{T}"/>.
    /// </summary>
    public async Task<IReadOnlyList<SleipnirResponse>> SendAsync(
        SleipnirBatch batch, CancellationToken ct = default)
    {
        var multi = batch.ToMultiRequest();
        var responses = await _transport.Call(multi, ct);
        return responses?.OfType<SleipnirResponse>().ToList()
               ?? new List<SleipnirResponse>();
    }

    /// <summary>
    /// Extract the typed result of a particular spec from a batch response list (correlation by Id).
    /// </summary>
    public T? ResultOf<T>(SleipnirCallSpec<T> spec, IReadOnlyList<SleipnirResponse> responses)
    {
        var resp = responses.FirstOrDefault(r => r.Id == spec.Id);
        return Deserialize<T>(resp);
    }

    /// <summary>
    /// Send a Tier-2 query batch (a <see cref="SleipnirMultiRequest"/> produced by
    /// <c>SleipnirQuery.Build()</c>) over the transport. Returns the raw responses, correlated to the
    /// query nodes by Id at <see cref="Materialize{TEntity}"/>.
    /// </summary>
    public async Task<IReadOnlyList<SleipnirResponse>> SendAsync(
        SleipnirMultiRequest batch, CancellationToken ct = default)
    {
        var responses = await _transport.Call(batch, ct);
        return responses?.OfType<SleipnirResponse>().ToList() ?? new List<SleipnirResponse>();
    }

    // ── Tier-2 query support ──────────────────────────────────────────────

    /// <summary>
    /// Build the <see cref="SleipnirRequest"/> for a navigation fetch node: one parameter (the fetch
    /// method's key-list param) wired to the provider's exported alias as <c>@alias</c>, plus the node's
    /// own outgoing <paramref name="exports"/> (for ThenInclude depth). Used by the query compiler only.
    /// </summary>
    internal SleipnirRequest CreateFetchRequest(
        string controller, string method, string param, string alias, Dictionary<string, string>? exports)
    {
        var p = new SleipnirParameter { Num = 0, ParameterName = param, Data = JsonValue.Create("@" + alias) };
        return new SleipnirRequest
        {
            Controller = controller,
            Method = method,
            Id = $"{controller}.{method}#{++_idCounter}",
            Params = JsonSerializer.SerializeToNode(new[] { p }, _jsonOpts),
            DependencyMapping = exports,
        };
    }

    /// <summary>
    /// Stitch a query's flat per-node response lists into the nested client-side graph and return the root
    /// entities (LINQ_QUERY.md §6). <c>@alias</c> resolved the *fetch* (the right rows); this joins children
    /// onto parents by each edge's key. The overloads accept either the pre-include root or any
    /// post-include leaf form.
    /// </summary>
    public List<TEntity> Materialize<TEntity>(
        SleipnirQuery<TEntity> query, IReadOnlyList<SleipnirResponse> responses)
        => MaterializeCore<TEntity>(query.State, responses);

    /// <inheritdoc cref="Materialize{TEntity}(SleipnirQuery{TEntity}, IReadOnlyList{SleipnirResponse})"/>
    public List<TEntity> Materialize<TEntity, TLeaf>(
        ISleipnirQuery<TEntity, TLeaf> query, IReadOnlyList<SleipnirResponse> responses)
        => MaterializeCore<TEntity>(((SleipnirQueryBase)query).State, responses);

    private List<TEntity> MaterializeCore<TEntity>(QueryState state, IReadOnlyList<SleipnirResponse> responses)
    {
        // 1. Correlate responses to nodes by Id; deserialize each node's result into List<EntityType>.
        var byId = new Dictionary<string, SleipnirResponse>(StringComparer.Ordinal);
        foreach (var r in responses)
            if (r.Id is not null) byId[r.Id] = r;

        foreach (var node in state.Nodes)
        {
            if (!byId.TryGetValue(node.RequestId, out var resp)
                || resp.Code is < 200 or > 299
                || !resp.Data.HasValue || resp.Data.Value.ValueKind == JsonValueKind.Null)
            {
                node.Entities = new List<object>();
                continue;
            }
            var listType = typeof(List<>).MakeGenericType(node.EntityType);
            var deserialized = resp.Data.Value.Deserialize(listType, _jsonOpts);
            node.Entities = ((IList)deserialized!).Cast<object>().ToList();
        }

        // 2. Stitch each edge in order (a DAG in edge-creation order is topological: each provider was
        //    created before its consumer). A child stitched onto a parent is already stitched to its own
        //    children (ThenInclude depth falls out of the ordering).
        foreach (var edge in state.Edges)
        {
            var parents = state.Nodes[edge.ProviderIndex].Entities!;
            var children = state.Nodes[edge.ConsumerIndex].Entities!;
            var parentKeyProp = ResolveWireProperty(state.Nodes[edge.ProviderIndex].EntityType, edge.KeyWire);
            var childKeyProp = ResolveWireProperty(edge.ChildElementType, edge.ChildKeyWire);

            if (edge.IsCollectionNav)
            {
                // Collection nav: group children by their FK back to the parent; assign each parent its list.
                // Keys are nullable (e.g. int? FK) → box to object; Dictionary<object,_> with the default
                // comparer handles a null runtime key (hashes to 0), so the non-null annotation is safe.
                var groups = new Dictionary<object, IList>();
                foreach (var c in children)
                {
                    var key = (object)childKeyProp.GetValue(c)!;
                    if (!groups.TryGetValue(key, out var list))
                    {
                        list = (IList)Activator.CreateInstance(
                            typeof(List<>).MakeGenericType(edge.ChildElementType))!;
                        groups[key] = list;
                    }
                    list.Add(c);
                }
                foreach (var parent in parents)
                {
                    var key = (object)parentKeyProp.GetValue(parent)!;
                    edge.NavProperty.SetValue(parent,
                        groups.TryGetValue(key, out var list)
                            ? list
                            : Activator.CreateInstance(typeof(List<>).MakeGenericType(edge.ChildElementType))!);
                }
            }
            else
            {
                // Reference nav: index children by their PK; assign each parent its single (or null).
                var byChildKey = new Dictionary<object, object>();
                foreach (var c in children)
                {
                    var key = (object)childKeyProp.GetValue(c)!;
                    if (!byChildKey.ContainsKey(key)) byChildKey[key] = c;  // first wins (PK is unique)
                }
                foreach (var parent in parents)
                {
                    var key = (object)parentKeyProp.GetValue(parent)!;
                    byChildKey.TryGetValue(key, out var child);
                    edge.NavProperty.SetValue(parent, child);
                }
            }
        }

        return state.Nodes[0].Entities!.Cast<TEntity>().ToList();
    }

    /// <summary>
    /// Resolve a wire name to a <see cref="PropertyInfo"/>: prefer <c>[JsonPropertyName(wireName)]</c>
    /// (generated DTOs carry it), else a case-insensitive PascalCase match (<c>kontaktId</c> →
    /// <c>KontaktId</c>). Used by the stitcher to read/write the join keys and set the navigation.
    /// </summary>
    private static PropertyInfo ResolveWireProperty(Type type, string wireName)
    {
        foreach (var p in type.GetProperties())
        {
            var attr = p.GetCustomAttribute<JsonPropertyNameAttribute>();
            if (attr is not null && attr.Name == wireName) return p;
        }
        var pascal = char.ToUpperInvariant(wireName[0]) + wireName[1..];
        return type.GetProperty(pascal, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
            ?? type.GetProperties().FirstOrDefault(p => string.Equals(p.Name, wireName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Navigation stitch: no property on '{type.Name}' matches wire name '{wireName}'. " +
                "Check [SleipnirNavigation].Key / ChildKey against the contract DTO.");
    }

    private static T? Deserialize<T>(SleipnirResponse? resp)
    {
        if (resp is null || resp.Code is < 200 or > 299) return default;
        // SleipnirResponse.Data is a JsonElement? (not a JSON string) since the response-side migration —
        // bind directly through the JsonElement deserializer.
        if (!resp.Data.HasValue || resp.Data.Value.ValueKind == JsonValueKind.Null) return default;
        try { return resp.Data.Value.Deserialize<T>(_jsonOpts); }
        catch { return default; }
    }
}