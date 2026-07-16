// Canonical scalar table + C# type map. Ported from
// clients/codegen/src/core/scalars.ts (csTypeOf). The canonical name sets encode
// System.Text.Json's JSON-kind mapping; only CsTypeOf is needed by the C# emitter
// (the TS/Python maps stay on the Node core).
using System;
using System.Collections.Generic;

namespace Trame.Codegen.Core;

internal static class Scalars
{
    /// <summary>C# type string for a scalar name. Complex names fall back to the short name.</summary>
    public static string CsTypeOf(string name)
    {
        var k = string.IsNullOrEmpty(name) ? "" : name.ToLowerInvariant().Trim();
        if (CsMap.TryGetValue(k, out var mapped)) return mapped;
        return name.IndexOf('.') >= 0 ? Casing.ShortName(name) : name;
    }

    private static readonly Dictionary<string, string> CsMap = new()
    {
        { "string", "string" },
        { "int", "int" }, { "int32", "int" }, { "int64", "long" }, { "long", "long" },
        { "short", "short" }, { "byte", "byte" }, { "sbyte", "sbyte" },
        { "uint", "uint" }, { "ulong", "ulong" }, { "ushort", "ushort" },
        { "double", "double" }, { "decimal", "decimal" }, { "float", "float" }, { "single", "float" },
        { "number", "double" }, { "bigint", "long" },
        { "bool", "bool" }, { "boolean", "bool" },
        { "datetime", "DateTime" }, { "datetimeoffset", "DateTimeOffset" },
        { "dateonly", "DateOnly" }, { "timeonly", "TimeOnly" }, { "timespan", "TimeSpan" },
        { "guid", "Guid" }, { "uri", "Uri" }, { "char", "char" }, { "version", "Version" },
        { "object", "object" }, { "dynamic", "object" },
        { "bytes", "byte[]" }, // base64 on the wire → byte[]
    };
}