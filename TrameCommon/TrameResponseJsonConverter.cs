using System.Text.Json;
using System.Text.Json.Serialization;
using TrameCommon.Models;

namespace TrameCommon;

/// <summary>
/// Server-seitiger Write-Only-Converter für <see cref="TrameResponse"/> (REST + WS).
/// Schreibt <see cref="TrameResponse.DataBytes"/> via <c>Utf8JsonWriter.WriteRawValue</c>
/// roh in den Wire — ein Pass, kein JsonDocument-Baum (der Bulk-Pfad legt das Methoden-
/// Resultat per <c>SerializeToUtf8Bytes</c> als rohe UTF-8-Bytes ab). Ist nur
/// <see cref="TrameResponse.Data"/> gesetzt (Legacy-/ProblemDetails-Pfad), fällt der
/// Converter auf <c>Data.Value.WriteTo</c> zurück.
/// </summary>
/// <remarks>
/// Wire-Byte-Identität zum bisherigen System.Text.Json-camelCase-Output ist das harte
/// Kriterium (TS-Client, Transport-Tests): gleiche Keys, gleiche Reihenfolge
/// (code, data, content, id, exposedDependencies, error), gleiche Null-Behandlung
/// (Server-Options haben kein <c>WhenWritingNull</c> → Nullen werden geschrieben),
/// gleicher UnsafeRelaxed-Encoder. <see cref="Read"/> ist nicht implementiert — der
/// Server parst keine <see cref="TrameResponse"/> (nur Requests); der Client nutzt den
/// shared <c>TrameResponseParser</c> bzw. die Default-Deserialisierung.
/// </remarks>
public sealed class TrameResponseJsonConverter : JsonConverter<TrameResponse>
{
    public override void Write(Utf8JsonWriter writer, TrameResponse value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        // code (int, immer vorhanden)
        writer.WriteNumber("code", value.Code);

        // data: DataBytes bevorzugt (Single-Pass, roh), sonst JsonElement.WriteTo, sonst null.
        // Reihenfolge bewusst DataBytes-vor-Data, damit der Bulk-Pfad den lazy Getter nie
        // auslöst (kein JsonDocument-Baum auf dem Server).
        if (value.DataBytes is { } dataBytes && dataBytes.Length > 0)
        {
            writer.WritePropertyName("data");
            // skipInputValidation: DataBytes stammt aus SerializeToUtf8Bytes (vertrauenswürdig).
            writer.WriteRawValue(dataBytes, skipInputValidation: true);
        }
        else if (value.Data.HasValue)
        {
            writer.WritePropertyName("data");
            value.Data.Value.WriteTo(writer);
        }
        else
        {
            writer.WriteNull("data");
        }

        // content: byte[] → base64-String (System.Text.Json-Default) bzw. null.
        if (value.Content is { } content)
        {
            writer.WriteBase64String("content", content);
        }
        else
        {
            writer.WriteNull("content");
        }

        // id
        if (value.Id is { } id)
        {
            writer.WriteString("id", id);
        }
        else
        {
            writer.WriteNull("id");
        }

        // exposedDependencies: Dictionary<string,string>? — rekursiv mit denselben Options
        // (byte-identisch zum Default; Dictionary-Keys werden von der Policy nicht berührt).
        if (value.ExposedDependencies is { } exposed)
        {
            writer.WritePropertyName("exposedDependencies");
            JsonSerializer.Serialize(writer, exposed, options);
        }
        else
        {
            writer.WriteNull("exposedDependencies");
        }

        // error: TrameError? — rekursiv (respektiert [JsonPropertyName]-Attribute der TrameError).
        if (value.Error is { } error)
        {
            writer.WritePropertyName("error");
            JsonSerializer.Serialize(writer, error, options);
        }
        else
        {
            writer.WriteNull("error");
        }

        writer.WriteEndObject();
    }

    public override TrameResponse Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => throw new NotSupportedException(
            "TrameResponseJsonConverter ist Write-Only — der Server parst keine TrameResponse.");
}