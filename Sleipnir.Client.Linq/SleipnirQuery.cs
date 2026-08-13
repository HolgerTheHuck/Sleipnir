using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SleipnirCommon.Models;

namespace Sleipnir.Client.Linq;

// ─────────────────────────────────────────────────────────────────────────────
//  Public surface: the covariant query marker + the pre-include root. The shared
//  mutable state (QueryState) is carried by the abstract base; the covariant
//  interface is a thin marker so EF-style overload disambiguation + lambda-body
//  type-checking can flow the leaf type (see LINQ_QUERY.md §2/§5).
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The post-navigation state: <c>linq.From(...).Include(c =&gt; c.Kontakt)</c> yields an
/// <see cref="ISleipnirQuery{TEntity, TLeaf}"/>. The interface is **covariant** in both params (it carries
/// no members using them in input), so <c>ISleipnirQuery&lt;Customer, List&lt;Ansprechpartner&gt;?&gt;</c>
/// converts to <c>ISleipnirQuery&lt;Customer, IEnumerable&lt;Ansprechpartner&gt;?&gt;</c> — which is how the
/// collection-vs-reference <c>ThenInclude</c> overloads disambiguate by lambda body (the same trick EF Core
/// uses for <c>IIncludableQueryable&lt;,&gt;</c>). The leaf type is the navigation property type
/// <em>as written</em> (collection or reference); the element type is tracked at runtime in
/// <see cref="SleipnirQueryBase.State"/>.
/// </summary>
public interface ISleipnirQuery<out TEntity, out TLeaf> { }

/// <summary>
/// Non-generic abstract base carrying the shared <see cref="QueryState"/>. Both
/// <see cref="SleipnirQuery{TEntity}"/> (pre-include root) and the internal post-include concrete type
/// derive from it, so the extension methods reach the state by a single downcast — without leaking the
/// internal state type through the public covariant interface.
/// </summary>
public abstract class SleipnirQueryBase
{
    internal QueryState State { get; }
    internal SleipnirQueryBase(QueryState state) => State = state;
}

/// <summary>
/// The pre-include root: the state after <see cref="SleipnirLinqClient.From{TService,TEntity}"/> and before
/// any <c>.Include</c>. <typeparamref name="TEntity"/> is the root method's return element type
/// (collection-root queries: the method returns <c>Task&lt;List&lt;TEntity&gt;?&gt;</c>). A
/// <see cref="SleipnirQuery{TEntity}"/> is intentionally NOT an <see cref="ISleipnirQuery{TEntity,TLeaf}"/>,
/// so the root <c>.Include</c> overload (whose <c>this</c> is this type) never collides with the sibling
/// <c>.Include</c> (whose <c>this</c> is the interface).
/// </summary>
public sealed class SleipnirQuery<TEntity> : SleipnirQueryBase
{
    internal SleipnirQuery(QueryState state) : base(state) { }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Internal query-node + navigation-edge model (LINQ_QUERY.md §5).
// ─────────────────────────────────────────────────────────────────────────────

internal sealed class QueryNode
{
    public int Index;
    public Type EntityType = null!;   // element type loaded by this node (Customer/Kontakt/…)
    public bool IsCollection;          // Tier 2: always true (root + every fetch return a list)
    public List<object>? Entities;     // filled at Materialize (the deserialized element instances)
    public string RequestId = "";      // set at Build; correlates to SleipnirResponse.Id at stitch
}

internal sealed class NavigationEdge
{
    public int ProviderIndex;          // the node that exports the alias
    public int ConsumerIndex;          // the fetch node that consumes it
    public string FetchController = "";
    public string FetchMethod = "";
    public string KeyWire = "";        // [SleipnirNavigation].Key — parent's per-element key (wire name)
    public string ChildKeyWire = "";   // resolved child join-back key (wire name)
    public string Param = "";          // fetch method's parameter name receiving the key list
    public string Alias = "";          // the @alias this provider exports / the consumer consumes
    public PropertyInfo NavProperty = null!; // the nav property on the provider entity (stitch assigns here)
    public bool IsCollectionNav;        // NavProperty is a collection vs a single reference
    public Type ChildElementType = null!;    // the consumer node's element type
}

internal sealed class QueryState
{
    public SleipnirCallSpec RootSpec;          // spec for node 0 (built by SleipnirLinqClient.Build)
    public readonly List<QueryNode> Nodes = new();
    public readonly List<NavigationEdge> Edges = new();
    public int LeafNodeIndex;                  // current leaf node — the ThenInclude provider
    public int AliasCounter;                   // nav alias naming (nav0, nav1, …)
    public readonly SleipnirLinqClient Client;  // for CreateFetchRequest + JsonSerializerOptions

    internal QueryState(SleipnirLinqClient client, SleipnirCallSpec rootSpec, Type rootEntityType)
    {
        Client = client;
        RootSpec = rootSpec;
        Nodes.Add(new QueryNode { Index = 0, EntityType = rootEntityType, IsCollection = true });
        LeafNodeIndex = 0;
    }
}

/// <summary>
/// The internal post-include concrete query. Shares the <see cref="SleipnirQueryBase.State"/> (mutated by
/// each <c>.Include</c>/<c>.ThenInclude</c>); only the generic leaf param changes per step, driving the
/// compile-time type progression.
/// </summary>
internal sealed class Query<TEntity, TLeaf> : SleipnirQueryBase, ISleipnirQuery<TEntity, TLeaf>
{
    internal Query(QueryState state) : base(state) { }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Fluent extensions: Include / ThenInclude / Where / Build. EF-Core-shaped, but
//  compiled client-side at .Build() — the server only ever sees the plain
//  @alias/dependencyMapping multi-request (LINQ_QUERY.md §4).
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Fluent query builders over <see cref="SleipnirQuery{TEntity}"/> / <see cref="ISleipnirQuery{TEntity,TLeaf}"/>.</summary>
public static class SleipnirQueryExtensions
{
    /// <summary>
    /// Eager-load a navigation from the root. <c>c =&gt; c.Kontakt</c> is compile-checked (<c>Kontakt</c> must
    /// be a property of <typeparamref name="TEntity"/>); <c>[SleipnirNavigation]</c> on that property supplies
    /// the fetch edge. Leaf advances <typeparamref name="TEntity"/> → the navigation property type
    /// <typeparamref name="TProp"/> (reference or collection, as written).
    /// </summary>
    public static ISleipnirQuery<TEntity, TProp> Include<TEntity, TProp>(
        this SleipnirQuery<TEntity> query, Expression<Func<TEntity, TProp>> navigation)
        where TEntity : class
        => Navigation.AddInclude(query.State, navigation, fromRoot: true);

    /// <summary>
    /// Sibling navigation from the **root** (EF parity): after a chain, <c>.Include(c =&gt; c.X)</c> loads a
    /// root-level navigation, not one off the current leaf. Leaf resets to the navigation property type.
    /// </summary>
    public static ISleipnirQuery<TEntity, TProp> Include<TEntity, TLeaf, TProp>(
        this ISleipnirQuery<TEntity, TLeaf> query, Expression<Func<TEntity, TProp>> navigation)
        where TEntity : class
        => Navigation.AddInclude(((SleipnirQueryBase)query).State, navigation, fromRoot: true);

    /// <summary>
    /// Continue a chain off a **reference** navigation leaf. <c>k =&gt; k.Ansprechpartner</c> is
    /// compile-checked against the current leaf type. Selected by overload resolution when the previous
    /// navigation was a reference (the leaf type is the entity, not a collection).
    /// </summary>
    public static ISleipnirQuery<TEntity, TNext> ThenInclude<TEntity, TLeaf, TNext>(
        this ISleipnirQuery<TEntity, TLeaf> query, Expression<Func<TLeaf, TNext>> navigation)
        where TEntity : class
        => Navigation.AddThenInclude<TEntity, TNext>(((SleipnirQueryBase)query).State, navigation, typeof(TLeaf));

    /// <summary>
    /// Continue a chain off a **collection** navigation leaf. The previous leaf type is a collection
    /// (<c>List&lt;TElement&gt;?</c>); via interface covariance it converts to
    /// <c>IEnumerable&lt;TElement&gt;?</c>, so <typeparamref name="TElement"/> is the element and
    /// <c>a =&gt; a.X</c> is compile-checked against the element — not the collection. Selected by overload
    /// resolution when the previous navigation was a collection (the reference overload's lambda does not
    /// type-check against a collection type, leaving this the unique applicable candidate).
    /// </summary>
    public static ISleipnirQuery<TEntity, TNext> ThenInclude<TEntity, TElement, TNext>(
        this ISleipnirQuery<TEntity, IEnumerable<TElement>?> query, Expression<Func<TElement, TNext>> navigation)
        where TEntity : class
        => Navigation.AddThenInclude<TEntity, TNext>(((SleipnirQueryBase)query).State, navigation, typeof(TElement));

    /// <summary>
    /// Bind further root-method parameters via an eq-predicate (LINQ_QUERY.md §7). Sugar over the
    /// <c>From</c> call args: each <c>c.Prop == value</c> clause (joined by <c>&amp;&amp;</c>) binds the root
    /// method parameter whose wire name matches the property's wire name. No query engine — the method
    /// <em>is</em> the filter; supported operators are <c>==</c> and <c>&amp;&amp;</c> only.
    /// </summary>
    public static SleipnirQuery<TEntity> Where<TEntity>(
        this SleipnirQuery<TEntity> query, Expression<Func<TEntity, bool>> predicate)
        where TEntity : class
    {
        Navigation.ApplyWhere(query.State, predicate, typeof(TEntity));
        return query;
    }

    /// <inheritdoc cref="Where{TEntity}(SleipnirQuery{TEntity}, Expression{Func{TEntity,bool}})"/>
    public static ISleipnirQuery<TEntity, TLeaf> Where<TEntity, TLeaf>(
        this ISleipnirQuery<TEntity, TLeaf> query, Expression<Func<TEntity, bool>> predicate)
        where TEntity : class
    {
        Navigation.ApplyWhere(((SleipnirQueryBase)query).State, predicate, typeof(TEntity));
        return query;
    }

    /// <summary>Compile the navigation chain into a <see cref="SleipnirMultiRequest"/> of plain
    /// <c>@alias</c>-wired method calls (one per entity type loaded). The server auto-selects the
    /// topological batch executor because the requests carry <c>dependencyMapping</c>.</summary>
    public static SleipnirMultiRequest Build<TEntity>(this SleipnirQuery<TEntity> query)
        => Navigation.Build(query.State);

    /// <inheritdoc cref="Build{TEntity}(SleipnirQuery{TEntity})"/>
    public static SleipnirMultiRequest Build<TEntity, TLeaf>(this ISleipnirQuery<TEntity, TLeaf> query)
        => Navigation.Build(((SleipnirQueryBase)query).State);
}

// ─────────────────────────────────────────────────────────────────────────────
//  Navigation — the compile/Build machinery (internal). Splits cleanly from the
//  extension surface so the generic plumbing is testable in isolation.
// ─────────────────────────────────────────────────────────────────────────────

internal static class Navigation
{
    public static ISleipnirQuery<TEntity, TProp> AddInclude<TEntity, TProp>(
        QueryState state, Expression<Func<TEntity, TProp>> navigation, bool fromRoot)
        where TEntity : class
    {
        var navProp = ExtractNavProperty(navigation);
        var navAttr = navProp.GetCustomAttribute<SleipnirNavigationAttribute>()
            ?? throw new InvalidOperationException(
                $"Property '{navProp.DeclaringType?.Name}.{navProp.Name}' has no [SleipnirNavigation] " +
                "— only navigation properties can be Included. Declare it on the server DTO " +
                "(it flows to the client attribute through EmitContracts).");
        var (element, isCollection) = ResolveElementType(navProp.PropertyType);
        var providerIndex = fromRoot ? 0 : state.LeafNodeIndex;
        var consumerIndex = state.Nodes.Count;
        state.Nodes.Add(new QueryNode { Index = consumerIndex, EntityType = element, IsCollection = true });
        state.Edges.Add(BuildEdge(state, providerIndex, consumerIndex, navProp, navAttr, isCollection, element));
        state.LeafNodeIndex = consumerIndex;
        return new Query<TEntity, TProp>(state);
    }

    public static ISleipnirQuery<TEntity, TNext> AddThenInclude<TEntity, TNext>(
        QueryState state, LambdaExpression navigation, Type declaredLeafType)
        where TEntity : class
    {
        // 'declaredLeafType' is the type the compiler checked the selector against (the element for a
        // collection leaf via covariance, or the reference leaf itself). The provider is the current leaf
        // node; its element type must match — a runtime guard backing up the compile-time check.
        var navProp = ExtractNavProperty(navigation);
        var navAttr = navProp.GetCustomAttribute<SleipnirNavigationAttribute>()
            ?? throw new InvalidOperationException(
                $"Property '{navProp.DeclaringType?.Name}.{navProp.Name}' has no [SleipnirNavigation] " +
                "— only navigation properties can be ThenIncluded.");
        var providerIndex = state.LeafNodeIndex;
        var providerEntityType = state.Nodes[providerIndex].EntityType;
        if (declaredLeafType != providerEntityType)
            throw new InvalidOperationException(
                $"ThenInclude selector is typed against '{declaredLeafType.Name}' but the current leaf is " +
                $"'{providerEntityType.Name}'. The chain went wrong: ThenInclude operates on the last " +
                "navigation's element type.");
        var (element, isCollection) = ResolveElementType(navProp.PropertyType);
        var consumerIndex = state.Nodes.Count;
        state.Nodes.Add(new QueryNode { Index = consumerIndex, EntityType = element, IsCollection = true });
        state.Edges.Add(BuildEdge(state, providerIndex, consumerIndex, navProp, navAttr, isCollection, element));
        state.LeafNodeIndex = consumerIndex;
        return new Query<TEntity, TNext>(state);
    }

    public static SleipnirMultiRequest Build(QueryState state)
    {
        // Tier 2: every node is a collection (root returns List<TEntity>, each fetch returns List<E>),
        // so the per-element key path is uniformly "$[*].{Key}".
        var exportsByProvider = new Dictionary<int, Dictionary<string, string>>();
        foreach (var edge in state.Edges)
        {
            // GetOrAdd by provider index — the `??=` operator cannot be used on a dictionary indexer
            // because its expansion reads the getter first, and the getter throws KeyNotFoundException
            // for an absent key (the default it would supply never gets the chance).
            if (!exportsByProvider.TryGetValue(edge.ProviderIndex, out var bucket))
                exportsByProvider[edge.ProviderIndex] = bucket = new Dictionary<string, string>();
            bucket[edge.Alias] = "$[*]." + edge.KeyWire;
        }

        // The edge that feeds each consumer (its consumed alias + fetch metadata).
        var incomingByConsumer = state.Edges.ToDictionary(e => e.ConsumerIndex);

        var requests = new List<SleipnirRequest>(state.Nodes.Count);
        for (var i = 0; i < state.Nodes.Count; i++)
        {
            var node = state.Nodes[i];
            exportsByProvider.TryGetValue(i, out var exports);

            if (i == 0)
            {
                // Root: merge its outgoing aliases onto the root spec's dependencyMapping (root carries the
                // exported keys for every sibling/first-level Include), then materialize the request.
                state.RootSpec.DependencyMapping =
                    state.RootSpec.DependencyMapping is { } existing
                        ? MergeInto(existing, exports)
                        : exports;
                var req = state.RootSpec.ToRequest();
                node.RequestId = req.Id ?? "";
                requests.Add(req);
            }
            else
            {
                var edge = incomingByConsumer[i];
                var req = state.Client.CreateFetchRequest(
                    edge.FetchController, edge.FetchMethod, edge.Param, edge.Alias, exports);
                node.RequestId = req.Id;
                requests.Add(req);
            }
        }
        return new SleipnirMultiRequest { Requests = requests, Mode = ExecutionMode.Serial };
    }

    public static void ApplyWhere<TEntity>(QueryState state, Expression<Func<TEntity, bool>> predicate, Type entityType)
    {
        var bindings = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
        CollectEqualBindings(predicate.Body, bindings, entityType);
        if (bindings.Count == 0) return;

        var opts = SleipnirLinqClient.Options;
        var list = state.RootSpec.Params?.Deserialize<List<SleipnirParameter>>(opts) ?? new List<SleipnirParameter>();
        var existing = list.ToDictionary(p => p.ParameterName, StringComparer.OrdinalIgnoreCase);
        foreach (var (wire, value) in bindings)
        {
            if (existing.TryGetValue(wire, out var param))
                param.Data = value;
            else
                list.Add(new SleipnirParameter { Num = list.Count, ParameterName = wire, Data = value });
        }
        state.RootSpec.Params = JsonSerializer.SerializeToNode(list, opts);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static NavigationEdge BuildEdge(
        QueryState state, int providerIndex, int consumerIndex,
        PropertyInfo navProp, SleipnirNavigationAttribute navAttr, bool isCollectionNav, Type childElement)
    {
        var providerEntity = state.Nodes[providerIndex].EntityType;
        var childKeyWire = !string.IsNullOrEmpty(navAttr.ChildKey)
            ? navAttr.ChildKey!
            : isCollectionNav
                ? JsonNamingPolicy.CamelCase.ConvertName(providerEntity.Name) + "Id"  // child FK back to parent
                : "id";                                                              // child PK (reference nav)

        if (string.IsNullOrEmpty(navAttr.Param))
            throw new InvalidOperationException(
                $"[SleipnirNavigation] on '{navProp.DeclaringType?.Name}.{navProp.Name}' declares no Param — " +
                $"required so the fetch method '{navAttr.Fetch}' knows which parameter receives the key list. " +
                "EmitContracts validates and emits this at generation time.");
        var (fetchController, fetchMethod) = SplitFetch(navAttr.Fetch);

        return new NavigationEdge
        {
            ProviderIndex = providerIndex,
            ConsumerIndex = consumerIndex,
            FetchController = fetchController,
            FetchMethod = fetchMethod,
            KeyWire = navAttr.Key,
            ChildKeyWire = childKeyWire,
            Param = navAttr.Param!,
            Alias = "nav" + state.AliasCounter++,
            NavProperty = navProp,
            IsCollectionNav = isCollectionNav,
            ChildElementType = childElement,
        };
    }

    private static PropertyInfo ExtractNavProperty(Expression body)
    {
        // Unwrap a Quote/Convert wrapper; the selector body must be a single member access (c => c.Kontakt).
        var expr = body is UnaryExpression { NodeType: ExpressionType.Quote } q ? q.Operand : body;
        if (expr is LambdaExpression lam) expr = lam.Body;
        if (expr is UnaryExpression { NodeType: ExpressionType.Convert } c) expr = c.Operand;
        if (expr is MemberExpression me && me.Member is PropertyInfo pi) return pi;
        throw new InvalidOperationException(
            $"Include/ThenInclude selector must be a single member access (e.g. c => c.Kontakt); " +
            $"got '{expr?.NodeType}'.");
    }

    /// <summary>
    /// Resolve the element type + collection-ness of a navigation property type as it appears at runtime
    /// (reference-nullable annotations are erased: <c>Kontakt?</c> → <c>Kontakt</c>,
    /// <c>List&lt;AP&gt;?</c> → <c>List&lt;AP&gt;</c>). A collection nav implements <c>IEnumerable&lt;T&gt;</c>.
    /// </summary>
    private static (Type element, bool isCollection) ResolveElementType(Type propertyType)
    {
        if (propertyType == typeof(string)) return (propertyType, false);
        var enumerable = propertyType.GetInterfaces().Prepend(propertyType)
            .FirstOrDefault(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        if (enumerable is not null)
            return (enumerable.GetGenericArguments()[0], true);
        return (propertyType, false);
    }

    private static (string controller, string method) SplitFetch(string fetch)
    {
        var i = fetch.LastIndexOf('.');
        if (i <= 0 || i == fetch.Length - 1)
            throw new InvalidOperationException(
                $"[SleipnirNavigation].Fetch must be 'Controller.Method' (got '{fetch}').");
        return (fetch[..i], fetch[(i + 1)..]);
    }

    private static void CollectEqualBindings(
        Expression expr, Dictionary<string, JsonNode?> bindings, Type entityType)
    {
        var opts = SleipnirLinqClient.Options;
        if (expr is BinaryExpression { NodeType: ExpressionType.AndAlso } andExpr)
        {
            CollectEqualBindings(andExpr.Left, bindings, entityType);
            CollectEqualBindings(andExpr.Right, bindings, entityType);
            return;
        }
        if (expr is BinaryExpression { NodeType: ExpressionType.Equal } eq)
        {
            string wire;
            object? value;
            if (eq.Left is MemberExpression lm)
            { wire = JsonPathBuilder.WireName(lm.Member); value = Evaluate(eq.Right); }
            else if (eq.Right is MemberExpression rm)
            { wire = JsonPathBuilder.WireName(rm.Member); value = Evaluate(eq.Left); }
            else throw new InvalidOperationException(
                ".Where supports only c.Prop == value clauses (member == constant). " +
                "Other operators (>, Contains, …) need a server query engine.");
            bindings[wire] = JsonSerializer.SerializeToNode(value, opts);
            return;
        }
        throw new InvalidOperationException(
            ".Where supports only == and && (no other operators). " +
            "Bind method parameters in the From(...) call instead.");
    }

    private static object? Evaluate(Expression expr)
        => expr is ConstantExpression ce ? ce.Value : Expression.Lambda(expr).Compile().DynamicInvoke();

    private static Dictionary<string, string> MergeInto(
        Dictionary<string, string> target, Dictionary<string, string>? source)
    {
        if (source is null) return target;
        foreach (var kv in source) target[kv.Key] = kv.Value;
        return target;
    }
}