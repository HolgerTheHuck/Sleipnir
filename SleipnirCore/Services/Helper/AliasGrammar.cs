using System.Text.Json.Nodes;

namespace SleipnirCore.Services.Helper;

/// <summary>
/// Single alias grammar — the one parser for <c>@alias</c> placeholder strings, used by
/// every detection site: the invoker's alias scan (<c>ContainsAlias</c>), the placeholder
/// substitution (<c>ReplaceDependencyByAliasCore</c>), and the graph builder's edge
/// extraction (<c>ExtractAliases</c>/<c>CollectAliases</c>). Before this class existed,
/// the three sites disagreed (one trimmed leading whitespace, one didn't; alias-name
/// boundaries differed), so a literal like <c>" @x"</c> was detected but never substituted.
///
/// <para><b>Grammar (v1.2, audit 2026-08-22 / D2):</b></para>
/// <list type="bullet">
/// <item>A string value is an alias reference iff it starts with exactly one <c>@</c>
/// followed by at least one alias character. Detection is <b>trim-free</b>: leading
/// whitespace makes it a literal.</item>
/// <item><c>@@text</c> is the escape for a literal string starting with <c>@</c>: the
/// value <c>"@@order"</c> reaches the controller as the literal string <c>"@order"</c>.
/// A lone <c>"@"</c> is a literal, not an alias.</item>
/// <item>The alias name is the maximal run of <c>[A-Za-z0-9_]</c> after the first
/// <c>@</c>. A trailing dot or other delimiter ends the name (e.g. <c>"@a.b"</c> refers
/// to alias <c>a</c>) — matching the client-side lexicon documented in LINQ_QUERY.md.</item>
/// </list>
///
/// <para>Escaping is applied by the framework on substitution paths only where a raw
/// string is consumed as data: see <see cref="Unescape"/> and <see cref="IsEscapedLiteral"/>.</para>
/// </summary>
public static class AliasGrammar
{
    /// <summary>Alias-name characters: letters, digits, underscore (the client-side
    ///  lexicon; see LINQ_QUERY.md). Everything else terminates the name.</summary>
    private static bool IsAliasChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

    /// <summary>
    /// Classifies a raw string value under the alias grammar.
    /// </summary>
    /// <param name="value">The raw string value (no trimming — literals may carry whitespace).</param>
    /// <param name="alias">The alias name when the result is <see cref="AliasKind.AliasReference"/>;
    /// the unescaped literal text when it is <see cref="AliasKind.EscapedLiteral"/>; otherwise empty.</param>
    /// <returns>The kind of the string under the grammar.</returns>
    public static AliasKind Classify(string? value, out string alias)
    {
        alias = string.Empty;
        if (string.IsNullOrEmpty(value) || value[0] != '@')
            return AliasKind.Literal;

        // "@@" prefix → escaped literal "@…".
        if (value.Length >= 2 && value[1] == '@')
        {
            alias = value[1..];
            return AliasKind.EscapedLiteral;
        }

        // "@" alone → literal.
        if (value.Length == 1)
            return AliasKind.Literal;

        // "@name" → alias iff at least one alias char follows.
        var sb = new System.Text.StringBuilder();
        for (int i = 1; i < value.Length; i++)
        {
            char c = value[i];
            if (!IsAliasChar(c)) break;
            sb.Append(c);
        }
        if (sb.Length == 0)
            return AliasKind.Literal; // e.g. "@." or "@-" → literal

        alias = sb.ToString();
        return AliasKind.AliasReference;
    }

    /// <summary>
    /// True iff the string is an alias reference under the grammar (trim-free).
    /// Convenience over <see cref="Classify"/>.
    /// </summary>
    public static bool IsAlias(string? value)
        => value is not null && Classify(value, out _) == AliasKind.AliasReference;

    /// <summary>
    /// True iff the string is an escaped literal (<c>@@…</c>) that must be unescaped to
    /// <c>@…</c> before reaching user code.
    /// </summary>
    public static bool IsEscapedLiteral(string? value)
        => value is not null && Classify(value, out _) == AliasKind.EscapedLiteral;

    /// <summary>
    /// Returns the unescaped text of an escaped literal (<c>"@@x"</c> → <c>"@x"</c>);
    /// any other input is returned unchanged.
    /// </summary>
    public static string Unescape(string value)
        => IsEscapedLiteral(value) ? value[1..] : value;

    /// <summary>
    /// Walks the node tree and reports whether any string value is an alias reference
    /// (trim-free, escape-aware). Replaces the invoker's ad-hoc <c>ContainsAlias</c>.
    /// </summary>
    public static bool ContainsAlias(JsonNode? node)
    {
        if (node == null) return false;
        if (node is JsonValue v && v.TryGetValue<string>(out var s))
            return IsAlias(s);
        if (node is JsonObject obj)
        {
            foreach (var kvp in obj)
            {
                if (ContainsAlias(kvp.Value)) return true;
            }
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (ContainsAlias(item)) return true;
            }
        }
        return false;
    }
}

/// <summary>Classification of a raw string value under the alias grammar.</summary>
public enum AliasKind
{
    /// <summary>An ordinary literal string (includes <c>"@"</c>, <c>" @x"</c>, <c>"@."</c>).</summary>
    Literal,
    /// <summary>An alias reference (<c>"@name"</c>); the name is [A-Za-z0-9_]+.</summary>
    AliasReference,
    /// <summary>An escaped literal (<c>"@@x"</c> → literal <c>"@x"</c>).</summary>
    EscapedLiteral,
}
