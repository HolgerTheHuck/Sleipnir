using System.Text.Json.Nodes;
using MessagePack;

namespace SleipnirCommon.Models;

/// <summary>
/// Represents a single method parameter in an RPC request.
/// Supports both positional (Num) and named (ParameterName) parameter styles.
/// </summary>
[MessagePackObject]
public class SleipnirParameter
{
    /// <summary>
    /// <see cref="Num"/> marker for a named entry: it binds by <see cref="ParameterName"/> only
    /// and never takes part in the positional fallback. No method parameter has index -1, so
    /// every server version treats it as name-only. Without it a named entry whose name no
    /// longer matches (a server-side rename) was silently bound by position instead of failing.
    /// </summary>
    public const int NamedOnly = -1;

    /// <summary>
    /// Positional parameter index — the fallback when <see cref="ParameterName"/> does not
    /// bind. <see cref="NamedOnly"/> (-1) for named entries.
    /// </summary>
    [Key(0)]
    public int Num { get; set; }

    /// <summary>
    /// Named parameter identifier (used by server-side invoker).
    /// </summary>
    [Key(1)]
    public string ParameterName { get; set; } = string.Empty;

    /// <summary>
    /// Nativer JSON-Wert des Parameters (Zahl, String, Bool, Objekt, Array) — kein
    /// JSON-String mehr. Ein <c>@alias</c>-Platzhalter ist ein String-Wert mit
    /// <c>@</c>-Präfix (z.B. <c>"@firstId"</c>); der Server erkennt ihn am <c>@</c>-Präfix.
    /// </summary>
    [Key(2)]
    public JsonNode? Data { get; set; }
}