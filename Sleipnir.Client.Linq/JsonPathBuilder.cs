using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sleipnir.Client.Linq;

/// <summary>
/// Translates a selector-expression body into a result-relative JsonPath (Sleipnir dependency-chaining
/// convention: root "$" is the serialized result; properties are camelCase — the server serializes with
/// <see cref="JsonNamingPolicy.CamelCase"/> and JsonPath is case-sensitive against the wire document, so
/// the path MUST be camelCase or it matches nothing). List elements via [i]. Supported shapes:
///   <c>x</c>       → "$"
///   <c>x.Name</c>  → "$.name"
///   <c>x[0]</c>    → "$[0]"            (native array: ArrayIndex binary)
///   <c>x[0].Id</c> → "$[0].id"         (List&lt;T&gt; indexer: get_Item method call)
///   <c>x.Select(e => e.Id)</c> → "$[*].id"  (multi-match: server collects all matches into a List&lt;T&gt; param)
///   <c>x.A.B</c>   → "$.a.b"
///
/// The wire name of each property is read from its <see cref="JsonPropertyNameAttribute"/> when present
/// (the generated DTOs always carry it), falling back to <see cref="JsonNamingPolicy.CamelCase"/> applied
/// to the C# property name. Reading the attribute makes the path drift-proof: it uses exactly the name
/// the DTO emits, so it can never diverge from the wire even if a future emitter overrides a name.
/// </summary>
internal static class JsonPathBuilder
{
    public static string Build(Expression expr)
    {
        var sb = new StringBuilder();
        sb.Append('$');
        BuildCore(expr, sb);
        return sb.ToString();
    }

    private static void BuildCore(Expression? expr, StringBuilder sb)
    {
        switch (expr)
        {
            case null:
                return;

            case LambdaExpression lambda:
                BuildCore(lambda.Body, sb);
                return;

            case ParameterExpression:
                // The root argument itself — no further path segment.
                return;

            case MemberExpression member:
                BuildCore(member.Expression, sb);
                sb.Append('.').Append(WireName(member.Member));
                return;

            case UnaryExpression unary when unary.NodeType == ExpressionType.Convert:
                // Skip a cast (e.g. (object)x).
                BuildCore(unary.Operand, sb);
                return;

            case BinaryExpression binary when binary.NodeType == ExpressionType.ArrayIndex:
                BuildCore(binary.Left, sb);
                AppendIndex(sb, binary.Right);
                return;

            case IndexExpression index:
                BuildCore(index.Object, sb);
                foreach (var arg in index.Arguments) AppendIndex(sb, arg);
                return;

            case MethodCallExpression call when IsIndexer(call):
                // List<T> / IList<T> indexers compile to a get_Item(int) method call, not an
                // IndexExpression (only native arrays and certain indexers do). Generated contracts
                // emit List<T> for array-kind returns, so x[0].Id on a list result arrives here.
                BuildCore(call.Object, sb);
                AppendIndex(sb, call.Arguments[0]);
                return;

            case MethodCallExpression call when IsProjection(call, out var selectorLambda):
                // x.Select(e => e.Id) → "$[*].id". The server's multi-match rule (DEPENDENCY_BINDING.md)
                // collects every match of "$[*].id" into a JsonArray, injected as one List<T> parameter —
                // list fan-out into a parameter, never into N requests. This is the wildcard-Expose path
                // that makes explicit multi-hop SleipnirBatch chains ergonomic without the query façade.
                BuildCore(call.Arguments[0], sb);
                sb.Append("[*]");
                BuildCore(selectorLambda!.Body, sb);
                return;

            case MethodCallExpression call when IsCollectionPassthrough(call):
                // x.Select(e => e.Id).ToList() / .ToArray() — the terminal materializer does not change
                // the path (it only fixes the C# return type so Expose yields Dep<List<int>>); recurse
                // into the source so .Select(...).ToList() still produces "$[*].id".
                BuildCore(call.Arguments[0], sb);
                return;

            default:
                throw new NotSupportedException(
                    $"JsonPath: unsupported expression '{expr?.NodeType}'. " +
                    "Only member/index access on the result is allowed.");
        }
    }

    private static void AppendIndex(StringBuilder sb, Expression indexExpr)
    {
        // Constant index → literal; otherwise compile (spike-era extensibility for non-constant indices).
        int index;
        if (indexExpr is ConstantExpression c && c.Value is int ci)
            index = ci;
        else
            index = (int)Expression.Lambda(indexExpr).Compile().DynamicInvoke()!;
        sb.Append('[').Append(index).Append(']');
    }

    /// <summary>
    /// True when <paramref name="call"/> is a single-argument indexer getter (<c>get_Item</c>), as
    /// produced by <c>List&lt;T&gt;[i]</c> / <c>IList&lt;T&gt;[i]</c>. Generated contracts emit
    /// <c>List&lt;T&gt;</c> for array-kind returns, so this is the list-element selector path.
    /// </summary>
    private static bool IsIndexer(MethodCallExpression call)
        => call.Method.IsSpecialName
           && call.Method.Name == "get_Item"
           && call.Arguments.Count == 1;

    /// <summary>
    /// True when <paramref name="call"/> is an <c>Enumerable.Select</c>/<c>Queryable.Select</c> projection
    /// <c>source.Select(e => e.Prop)</c>; emits the wildcard <c>$[*]</c> then walks the selector body.
    /// The selector argument is a quoted <see cref="LambdaExpression"/> (expression-tree convention).
    /// </summary>
    private static bool IsProjection(MethodCallExpression call, out LambdaExpression? selector)
    {
        selector = null;
        if (call.Method.Name != "Select" || call.Arguments.Count != 2) return false;
        var arg = call.Arguments[1];
        // The lambda arrives wrapped in a Quote unary node.
        if (arg is UnaryExpression u && u.NodeType == ExpressionType.Quote && u.Operand is LambdaExpression lam)
            selector = lam;
        else if (arg is LambdaExpression lam2)
            selector = lam2;
        return selector is not null;
    }

    /// <summary>
    /// True when <paramref name="call"/> is a terminal materializer (<c>ToList</c>/<c>ToArray</c>) that
    /// does not alter the JsonPath — it only fixes the C# return type (so <c>Expose</c> yields
    /// <c>Dep&lt;List&lt;T&gt;&gt;</c>). The path is the source's path.
    /// </summary>
    private static bool IsCollectionPassthrough(MethodCallExpression call)
        => (call.Method.Name == "ToList" || call.Method.Name == "ToArray")
           && call.Arguments.Count == 1;

    /// <summary>
    /// Resolve a member's wire name: its <see cref="JsonPropertyNameAttribute"/> when present (the
    /// generated DTOs always set it), otherwise the <see cref="JsonNamingPolicy.CamelCase"/> transform of
    /// the C# member name. Both match the server's wire casing, so the path cannot drift.
    /// </summary>
    internal static string WireName(MemberInfo member)
    {
        var attr = member.GetCustomAttribute<JsonPropertyNameAttribute>();
        if (attr is not null && !string.IsNullOrEmpty(attr.Name))
            return attr.Name;
        return JsonNamingPolicy.CamelCase.ConvertName(member.Name);
    }
}