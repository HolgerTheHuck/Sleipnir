// Casing helpers — the load-bearing wire-correctness layer. Ported from
// clients/codegen/src/core/casing.ts to mirror System.Text.Json's
// JsonNamingPolicy.CamelCase exactly: Sleipnir writes object value properties
// camelCase on the wire, so generated [JsonPropertyName] values MUST match this
// transform or deserialization silently binds nothing.
using System;
using System.Globalization;

namespace Sleipnir.Codegen.Core;

internal static class Casing
{
    /// <summary>
    /// Convert a .NET PascalCase / acronym-laden identifier to camelCase, matching
    /// <c>System.Text.Json.JsonNamingPolicy.CamelCase</c> on the server. Rules:
    /// starts lowercase → unchanged; a leading run of uppercase letters is lowercased;
    /// if that run is longer than one char AND is followed by a lowercase char, the
    /// last uppercase char stays uppercase (<c>ID</c> → <c>id</c>,
    /// <c>IPAddress</c> → <c>ipAddress</c>, <c>Id</c> → <c>id</c>).
    /// </summary>
    public static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        var chars = name.ToCharArray();
        int i = 0;
        while (i < chars.Length && chars[i] >= 'A' && chars[i] <= 'Z') i++;
        if (i == 0) return name; // starts lowercase → unchanged
        int end = i;
        if (i > 1 && i < chars.Length && chars[i] >= 'a' && chars[i] <= 'z') end = i - 1;
        for (int k = 0; k < end; k++)
        {
            chars[k] = char.ToLower(chars[k], CultureInfo.InvariantCulture);
        }
        return new string(chars);
    }

    /// <summary>Last <c>.</c>-segment of a full type name (<c>MyApp.Foo.Order</c> → <c>Order</c>).</summary>
    public static string ShortName(string fullName)
    {
        if (string.IsNullOrEmpty(fullName)) return fullName;
        var dot = fullName.LastIndexOf('.');
        return dot < 0 ? fullName : fullName.Substring(dot + 1);
    }

    /// <summary>Capitalize the first code point (<c>order</c> → <c>Order</c>); leave the rest untouched.</summary>
    public static string PascalCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return char.ToUpper(name[0], CultureInfo.InvariantCulture) + name.Substring(1);
    }
}