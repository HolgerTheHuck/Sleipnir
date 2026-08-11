using System.Collections.Generic;
using System.Text.Json;
using SleipnirCommon.Models;

namespace SleipnirClient.Sleipnir;

/// <summary>
/// Shared Single-Pass-Parser für <see cref="SleipnirResponse"/> (REST + WebSocket).
/// Fährt einen einzigen <see cref="Utf8JsonReader"/>-Loop über die Wire-Bytes und
/// belegt <see cref="SleipnirResponse.DataBytes"/> mit den rohen <c>data</c>-JSON-Bytes
/// (via Token-Offset-Slicing) — <see cref="SleipnirResponse.Data"/> bleibt null und wird
/// erst lazy materialisiert, wenn ein Reader zugreift (Dep-Chaining, Call&lt;T&gt;-
/// Fallback). Damit entfällt das Materialisieren des vollständigen JsonDocument-Baums
/// auf der Client-Seite (3 Parses → 1: ID, Envelope und T in einem Pass).
/// </summary>
/// <remarks>
/// Ein <c>JsonConverter</c> hat keinen Zugriff auf den Byte-Puffer → kein sauberes
/// Raw-Byte-Capture. Daher dieser separate Helper, der über den client-eigenen
/// Puffer läuft. Liest by-name (nicht by-position), ist also robust gegen
/// umgestellte Property-Reihenfolge.
/// </remarks>
public static class SleipnirResponseParser
{
    // Reuse-Options für die Sub-Deserialisierung von exposedDependencies (Dictionary)
    // und error (SleipnirError). Case-insensitiv, damit sowohl camelCase- als auch
    // PascalCase-Server-Responses binden (Spiegel SleipnirClientBase.JsonOptions).
    private static readonly JsonSerializerOptions SubOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Parst eine einzelne <see cref="SleipnirResponse"/> aus rohen UTF-8-JSON-Bytes.
    /// </summary>
    public static SleipnirResponse Parse(ReadOnlySpan<byte> utf8)
    {
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions());
        if (!reader.Read())
            throw new JsonException("Leerer JSON-Stream — keine SleipnirResponse.");

        // Single-Response ist ein Objekt; ein Array wäre ein Batch (ParseArray).
        if (reader.TokenType == JsonTokenType.StartArray)
            throw new JsonException("Array-Wurzel — für Batches ParseArray verwenden.");
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Erwartet Object-Wurzel für SleipnirResponse.");

        return ParseObject(ref reader, utf8);
    }

    /// <summary>
    /// Parst einen Batch (JSON-Array) in eine Liste von <see cref="SleipnirResponse"/>.
    /// </summary>
    public static List<SleipnirResponse?> ParseArray(ReadOnlySpan<byte> utf8)
    {
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions());
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("Erwartet Array-Wurzel für SleipnirMultiResponse.");

        var list = new List<SleipnirResponse?>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.StartObject)
                list.Add(ParseObject(ref reader, utf8));
            else
            {
                // Unexpected token (z. B. null-Element) → überspringen, null eintragen.
                reader.Skip();
                list.Add(null);
            }
        }
        return list;
    }

    /// <summary>
    /// Parst eine Response, wenn der Reader bereits auf <c>StartObject</c> steht.
    /// Der originale Puffer wird gebraucht, um DataBytes als Offset-Slice zu schneiden
    /// (Utf8JsonReader legt die Bytes nicht offen).
    /// </summary>
    private static SleipnirResponse ParseObject(ref Utf8JsonReader reader, ReadOnlySpan<byte> original)
    {
        var resp = new SleipnirResponse();

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;

            var name = reader.ValueSpan;

            // Auf den Wert-Token vorrücken.
            if (!reader.Read())
                throw new JsonException("Unerwartetes Ende im SleipnirResponse-Objekt.");

            if (name.SequenceEqual("code"u8))
            {
                resp.Code = reader.TokenType == JsonTokenType.Null ? 0 : reader.GetInt32();
            }
            else if (name.SequenceEqual("data"u8))
            {
                if (reader.TokenType == JsonTokenType.Null)
                {
                    resp.DataBytes = null; // kein Ergebniswert
                }
                else
                {
                    // Raw-Byte-Capture via Token-Offsets. Skip() verlässt den gesamten
                    // Wert (Objekt/Array/Skalar); TokenStartIndex..BytesConsumed sind
                    // die absoluten Offsets in den Original-Bytes.
                    int start = (int)reader.TokenStartIndex;
                    reader.Skip();
                    int end = (int)reader.BytesConsumed;
                    resp.DataBytes = original.Slice(start, end - start).ToArray();
                }
            }
            else if (name.SequenceEqual("content"u8))
            {
                resp.Content = reader.TokenType == JsonTokenType.Null ? null : reader.GetBytesFromBase64();
            }
            else if (name.SequenceEqual("id"u8))
            {
                resp.Id = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
            }
            else if (name.SequenceEqual("exposedDependencies"u8))
            {
                resp.ExposedDependencies = reader.TokenType == JsonTokenType.Null
                    ? null
                    : JsonSerializer.Deserialize<Dictionary<string, string>>(ref reader, SubOptions);
            }
            else if (name.SequenceEqual("error"u8))
            {
                resp.Error = reader.TokenType == JsonTokenType.Null
                    ? null
                    : JsonSerializer.Deserialize<SleipnirError>(ref reader, SubOptions);
            }
            else
            {
                // Unbekanntes Feld (z. B. isSuccess vom Legacy-Server) → überspringen.
                reader.Skip();
            }
        }

        return resp;
    }
}