// Casing helpers — the load-bearing wire-correctness layer. Ported from
// clients/codegen/src/core/casing.ts to mirror System.Text.Json's
// JsonNamingPolicy.CamelCase exactly: Sleipnir writes object value properties
// camelCase on the wire, so generated [JsonPropertyName] values MUST match this
// transform or deserialization silently binds nothing.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

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
        var name = NormalizeTypeKey(fullName);
        var dot = name.LastIndexOf('.');
        return dot < 0 ? name : name.Substring(dot + 1);
    }

    /// <summary>
    /// Defensive normalization of a <c>discovery.types</c> key. Current servers flatten closed
    /// generics to <c>{Namespace}.{Name}Of{Arg1}And{Arg2}</c> (<c>MyApp.PageOfTicketDto</c>); older
    /// servers emitted the raw assembly-qualified CLR generic <c>FullName</c>
    /// (<c>MyApp.Page`1[[MyApp.TicketDto, MyApp, Version=…]]</c>). That form is rewritten to the
    /// same flat name; every other key is returned unchanged. Port of <c>normalizeTypeKey</c> in
    /// <c>clients/codegen/src/core/casing.ts</c> — keep both identical.
    /// </summary>
    public static string NormalizeTypeKey(string key)
    {
        if (string.IsNullOrEmpty(key) || (key.IndexOf('`') < 0 && key.IndexOf('[') < 0)) return key;
        try
        {
            var t = new ClrNameParser(key).ParseType();
            return t.Prefix + Mangle(t);
        }
        catch (FormatException)
        {
            return key; // not a CLR name we understand — leave untouched
        }
    }

    private sealed class ClrType
    {
        public string Prefix = "";
        public string Name = "";
        public int OwnArity = -1;
        public List<ClrType> Args = new();
        public int ArrayRank;
    }

    private static string Mangle(ClrType t)
    {
        var own = t.OwnArity >= 0
            ? t.Args.Skip(t.Args.Count - Math.Min(t.OwnArity, t.Args.Count)).ToList()
            : t.Args;
        var sb = new StringBuilder(t.Name);
        if (own.Count > 0) sb.Append("Of").Append(string.Join("And", own.Select(Mangle)));
        for (int i = 0; i < t.ArrayRank; i++) sb.Append("Array");
        return sb.ToString();
    }

    private sealed class ClrNameParser
    {
        private readonly string _s;
        private int _i;
        public ClrNameParser(string s) { _s = s; }

        public ClrType ParseType()
        {
            int start = _i;
            while (_i < _s.Length && _s[_i] != '[' && _s[_i] != ']' && _s[_i] != ',') _i++;
            var ident = _s.Substring(start, _i - start).Trim();
            if (ident.Length == 0) throw new FormatException("empty type name");
            var args = new List<ClrType>();
            int arrayRank = 0;
            while (Peek() == '[')
            {
                char next = _i + 1 < _s.Length ? _s[_i + 1] : '\0';
                if (next == ']' || next == ',')
                {
                    // array suffix: [] or [,]
                    _i++;
                    while (Peek() == ',') _i++;
                    Expect(']');
                    arrayRank++;
                }
                else
                {
                    if (args.Count > 0 || arrayRank > 0) throw new FormatException("unexpected generic argument list");
                    _i++; // [
                    while (true)
                    {
                        if (Peek() == '[')
                        {
                            _i++;
                            args.Add(ParseType());
                            SkipAssemblyQualifier();
                            Expect(']');
                        }
                        else
                        {
                            args.Add(ParseType());
                        }
                        if (Peek() == ',') { _i++; continue; }
                        break;
                    }
                    Expect(']');
                }
            }
            // Split namespace / enclosing types from the own name; drop arity markers.
            int cut = Math.Max(ident.LastIndexOf('.'), ident.LastIndexOf('+'));
            var prefix = Regex.Replace(ident.Substring(0, cut + 1), @"`\d+", "");
            var last = ident.Substring(cut + 1);
            var m = Regex.Match(last, @"^(.*?)`(\d+)$");
            bool nested = ident.IndexOf('+') >= 0;
            return new ClrType
            {
                Prefix = prefix,
                Name = m.Success ? m.Groups[1].Value : last,
                OwnArity = nested ? (m.Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0) : -1,
                Args = args,
                ArrayRank = arrayRank,
            };
        }

        /// <summary>Skips <c>, Assembly, Version=…, Culture=…, PublicKeyToken=…</c> up to the closing bracket.</summary>
        private void SkipAssemblyQualifier()
        {
            while (_i < _s.Length && _s[_i] != ']') _i++;
        }

        private char Peek() => _i < _s.Length ? _s[_i] : '\0';

        private void Expect(char c)
        {
            if (Peek() != c) throw new FormatException($"expected '{c}' at {_i}");
            _i++;
        }
    }

    /// <summary>Capitalize the first code point (<c>order</c> → <c>Order</c>); leave the rest untouched.</summary>
    public static string PascalCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return char.ToUpper(name[0], CultureInfo.InvariantCulture) + name.Substring(1);
    }
}