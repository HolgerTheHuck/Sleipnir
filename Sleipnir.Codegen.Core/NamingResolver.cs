// NamingResolver — maps full .NET type names to emitted identifier names,
// disambiguating short-name collisions by prefixing parent namespace segments.
// Ported from clients/codegen/src/core/naming.ts. Story-01 has no collisions, so
// every name resolves to its short form (byte-identical to the TS emitter).
using System;
using System.Collections.Generic;
using System.Linq;

namespace Sleipnir.Codegen.Core;

internal sealed class NamingResolver
{
    private readonly Dictionary<string, string> _names = new();
    private readonly Dictionary<string, HashSet<string>> _byShort = new(StringComparer.Ordinal);

    /// <summary>Register a type by its full name (idempotent).</summary>
    public void Register(string fullName)
    {
        if (_names.ContainsKey(fullName)) return;
        var shortName = Casing.ShortName(fullName);
        if (!_byShort.TryGetValue(shortName, out var set))
        {
            set = new HashSet<string>(StringComparer.Ordinal);
            _byShort[shortName] = set;
        }
        set.Add(fullName);
    }

    /// <summary>The emitted name for a full name. Must be registered first.</summary>
    public string Resolve(string fullName)
    {
        if (_names.TryGetValue(fullName, out var cached)) return cached;
        var shortName = Casing.ShortName(fullName);
        var siblings = _byShort.TryGetValue(shortName, out var s) ? s : new HashSet<string>(StringComparer.Ordinal) { fullName };
        string name = siblings.Count <= 1 ? shortName : Disambiguate(fullName, shortName, siblings);
        _names[fullName] = name;
        return name;
    }

    /// <summary>
    /// Prefix parent segments of <paramref name="fullName"/> until the candidate is unique among
    /// the colliding <paramref name="siblings"/>. <c>Foo.Bar.Order</c> → <c>BarOrder</c> → <c>FooBarOrder</c>.
    /// </summary>
    private static string Disambiguate(string fullName, string shortName, HashSet<string> siblings)
    {
        var parts = fullName.Split('.');
        // parts[last] === shortName. Prepend parents from nearest outward.
        string name = shortName;
        for (int depth = 1; depth < parts.Length; depth++)
        {
            var parent = parts[parts.Length - 1 - depth];
            name = PascalConcat(parent) + name;
            bool clashes = siblings.Any(other => other != fullName && CandidateAtDepth(other, depth) == name);
            if (!clashes) return name;
        }
        return name;
    }

    /// <summary>The disambiguation candidate for <paramref name="fullName"/> at <paramref name="depth"/> prepended parents.</summary>
    private static string CandidateAtDepth(string fullName, int depth)
    {
        var parts = fullName.Split('.');
        string name = parts[parts.Length - 1];
        for (int d = 1; d <= depth && d < parts.Length; d++)
        {
            name = PascalConcat(parts[parts.Length - 1 - d]) + name;
        }
        return name;
    }

    /// <summary>Concatenate a parent segment onto a PascalCase name, preserving casing.</summary>
    private static string PascalConcat(string segment)
    {
        if (string.IsNullOrEmpty(segment)) return "";
        return Casing.PascalCase(segment);
    }
}