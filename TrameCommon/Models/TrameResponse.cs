using MessagePack;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TrameCommon.Models;

/// <summary>
/// Represents the response of an RPC call.
/// </summary>
[MessagePackObject]
public class TrameResponse
{
    [Key(0)]
    public int Code { get; set; }

    // Strukturierter Ergebniswert (kein JSON-String mehr). System.Text.Json schreibt
    // JsonElement roh in einem Pass ein → kein Double-Wrapping, kein `"`-Escape-Tax
    // (siehe WireSizeProbe-Spike: 25,5 KB → ~15,4 KB). MessagePack-Seite übernimmt
    // der JsonElementMessagePackFormatter (TrameHub/TrameClient, nicht in TrameCommon).
    //
    // Single-Pass-Optimierung: der Bulk-Pfad legt das Methoden-Resultat als rohe
    // UTF-8-Bytes in DataBytes ab (Server: SerializeToUtf8Bytes; Client: manuelles
    // Utf8JsonReader-Capture). Data bleibt dabei null und wird erst lazy materialisiert,
    // wenn es wirklich gelesen wird (Dep-Chaining, Call<T>-Fallback, Tests). Das spart
    // den vollständigen JsonDocument-Baum auf Server- UND Client-Seite (s. Plan).
    private byte[]? _dataBytes;
    private JsonElement? _data;
    private JsonDocument? _dataDoc; // hält den Puffer für die lazy JsonElement lebendig

    /// <summary>
    /// Rohe UTF-8-JSON-Bytes des Ergebniswerts (transient, transport-only wie
    /// <see cref="ContentStream"/>). Wenn gesetzt, bleibt <see cref="Data"/> null und
    /// wird erst beim ersten Lesezugriff lazy materialisiert. Der Server schreibt
    /// DataBytes direkt via <c>Utf8JsonWriter.WriteRawValue</c> in einem Pass in den
    /// Wire — kein JsonDocument-Baum, kein Re-Parse.
    /// </summary>
    [JsonIgnore]
    [IgnoreMember]
    public byte[]? DataBytes
    {
        get => _dataBytes;
        set { _dataBytes = value; _data = null; _dataDoc = null; }
    }

    [Key(1)]
    public JsonElement? Data
    {
        get
        {
            // Bereits materialisiert (direkt gesetzt oder lazy zuvor gelesen).
            if (_data.HasValue) return _data;
            // Kein Ergebniswert vorhanden.
            if (_dataBytes is null) return null;
            // Lazy: rohe Bytes → JsonDocument (Puffer bleibt über _dataDoc lebendig,
            // sonst wird die JsonElement nach Dispose invalid). Wurzel cachen, damit
            // wiederholte Lesezugriffe nicht erneut parsen.
            _dataDoc = JsonDocument.Parse(_dataBytes);
            _data = _dataDoc.RootElement;
            return _data;
        }
        set { _data = value; _dataDoc = null; _dataBytes = null; }
    }

    [Key(2)]
    public byte[]? Content { get; set; }

    /// <summary>
    /// Stream for large binary responses (avoid buffering into byte[]).
    /// When set, transports should stream directly instead of serializing Content.
    /// Not serialized by MessagePack/JSON (transient, transport-only).
    /// </summary>
    [IgnoreMember]
    [JsonIgnore]
    public Stream? ContentStream { get; set; }

    [Key(3)]
    public string? Id { get; set; }

    /// <summary>
    /// Resolved dependencies (alias → value) for sequential request chaining.
    /// </summary>
    [Key(4)]
    public Dictionary<string, string>? ExposedDependencies { get; set; }

    /// <summary>
    /// Structured error details, populated when Code != 200.
    /// </summary>
    [Key(5)]
    [JsonPropertyName("error")]
    public TrameError? Error { get; set; }

    /// <summary>
    /// Returns true if this response represents a successful call (Code 200-299).
    /// </summary>
    [JsonIgnore]
    [IgnoreMember]
    public bool IsSuccess => Code is >= 200 and <= 299;
}
