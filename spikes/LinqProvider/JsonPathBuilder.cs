using System.Linq.Expressions;
using System.Text;

namespace Sleipnir.Spike.LinqProvider;

/// <summary>
/// Übersetzt einen Selector-Expression-Körper in einen ergebnisrelativen JsonPath
/// (Konvention des Sleipnir-Dependency-Chaining: Wurzel "$" ist das serialisierte
/// Resultat, Eigenschaften sind camelCase — der Server serialisiert Antworten mit
/// JsonNamingPolicy.CamelCase, und JsonPath ist case-sensitiv gegen das Wire-Dokument,
/// daher MUSS der Pfad camelCase sein, sonst trifft er nichts). Listen-Elemente über [i].
/// Unterstützte Formen:
///   <c>x</c>          → "$"
///   <c>x.Name</c>     → "$.name"
///   <c>x[0]</c>       → "$[0]"
///   <c>x[0].Id</c>    → "$[0].id"
///   <c>x.A.B</c>      → "$.a.b"
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
                // Das Wurzel-Argument selbst — kein weiterer Pfad-Teil.
                return;

            case MemberExpression member:
                BuildCore(member.Expression, sb);
                // camelCase: der C#-Eigenschaftsname (PascalCase) wird an das camelCase-
                // Wire-Dokument angepasst (Server serialisiert CamelCase, JsonPath ist
                // case-sensitiv). Z.B. "Name" → "name", "Id" → "id".
                sb.Append('.').Append(ToCamelCase(member.Member.Name));
                return;

            case UnaryExpression unary when unary.NodeType == ExpressionType.Convert:
                // Casting (z. B. (object)x) übergehen.
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

            default:
                throw new NotSupportedException(
                    $"JsonPath: nicht unterstützter Ausdruck '{expr?.NodeType}'. " +
                    "Nur Member-/Index-Zugriffe auf das Resultat sind erlaubt.");
        }
    }

    private static void AppendIndex(StringBuilder sb, Expression indexExpr)
    {
        // Konstanter Index → Literal; sonst kompilieren (Spike-Erweiterbarkeit).
        int index;
        if (indexExpr is ConstantExpression c && c.Value is int ci)
            index = ci;
        else
            index = (int)Expression.Lambda(indexExpr).Compile().DynamicInvoke()!;
        sb.Append('[').Append(index).Append(']');
    }

    /// <summary>Wandelt einen PascalCase-Eigenschaftsnamen in camelCase um
    ///  (erster Buchstabe klein). Einbuchstabe-Namen bleiben unverändert.</summary>
    private static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        if (name.Length == 1) return name.ToLowerInvariant();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }
}