using System.Text.Json.Nodes;
using MessagePack;

namespace TrameCommon.Models;

/// <summary>
/// Represents a single method parameter in an RPC request.
/// Supports both positional (Num) and named (ParameterName) parameter styles.
/// </summary>
[MessagePackObject]
public class TrameParameter
{
    /// <summary>
    /// Positional parameter index (used by client-side TrameCall builder).
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