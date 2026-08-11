// ACHTUNG: Diese Datei wird NICHT in SleipnirCommon kompiliert (SleipnirCommon referenziert
// nur MessagePack.Annotations, nicht die volle MessagePack-Assembly). Sie wird per
// <Compile Include> in SleipnirHub.csproj (MessagePack 2.5.x = Server) UND
// SleipnirClient.csproj (MessagePack 3.1.8 = Client) gelinkt — derselbe Source kompiliert
// gegen jede eigene MessagePack-Version. Bewiesen in spikes/MpFormatterProbe/.
//
// Nullable bewusst AUS, damit die IFormatterResolver/IMessagePackFormatter-Signatur
// in beiden Versionen matched (3.x hat teils ?-Annotationen, 2.x nicht).
#nullable disable

using System.Text.Json.Nodes;
using SleipnirCommon.Models;
using MessagePack;
using MessagePack.Formatters;
using MessagePack.Resolvers;

namespace SleipnirCommon.MessagePack;

/// <summary>
/// Serialisiert <see cref="JsonNode"/> (und <see cref="JsonNode"/>?) als rohes
/// MessagePack: JSON-Text → MessagePack-Tokens 1:1 via
/// <see cref="MessagePackSerializer.ConvertFromJson"/> (Serialize) bzw.
/// MessagePack-Tokens → JSON-Text via <see cref="MessagePackSerializer.ConvertToJson"/>
/// (Deserialize). MessagePack ist eine JSON-Superset — 1:1 ohne Schema.
/// </summary>
/// <remarks>
/// Spiegel von <see cref="JsonElementMessagePackFormatter"/> für die Request-Seite:
/// <see cref="SleipnirParameter.Data"/> und <see cref="SleipnirRequest.Params"/> sind seit
/// der Wire-Vereinfachung native <see cref="JsonNode"/>-Werte (kein JSON-String mehr),
/// und dieser Formatter schreibt sie direkt als native MessagePack-Tokens statt als
/// escapeten JSON-String in ein binäres Feld. Damit entfällt auch auf dem SignalR-Kanal
/// die Double-Wrapping-Tax auf der Request-Seite.
/// </remarks>
public sealed class JsonNodeMessagePackFormatter
    : IMessagePackFormatter<JsonNode>
{
    public static readonly JsonNodeMessagePackFormatter Instance = new();

    // Hinweis: JsonNode ist ein Referenztyp — in #nullable disable sind JsonNode und JsonNode?
    // derselbe CLR-Typ, daher genügt EINE Implementierung (im Gegensatz zu JsonElement, das
    // als struct JsonElement und Nullable<JsonElement> unterscheidet). null wird im Serialize
    // als MsgPack-Nil geschrieben, Deserialize liefert null zurück.

    public void Serialize(ref MessagePackWriter writer, JsonNode value, MessagePackSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNil();
            return;
        }
        // JSON-Text → MessagePack-Tokens. null-Options = StandardResolver intern
        // (keine Rekursion über unseren eigenen Resolver — reines JSON, keine Custom-Typen).
        byte[] bytes = MessagePackSerializer.ConvertFromJson(value.ToJsonString(), null, default);
        writer.WriteRaw(bytes);
    }

    public JsonNode Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        if (reader.TryReadNil())
            return null;
        // ReadRaw liefert in 2.x UND 3.x ReadOnlySequence<byte> — die hat keine instanz-
        // ToArray(). Daher: Single-Segment-Fast-Path via First.Span.ToArray(), sonst
        // Kopie über die Segmente. (Length ist hier int-trivial.)
        var raw = reader.ReadRaw();
        byte[] rawBytes;
        if (raw.IsSingleSegment)
            rawBytes = raw.First.Span.ToArray();
        else
        {
            rawBytes = new byte[checked((int)raw.Length)];
            int offset = 0;
            foreach (ReadOnlyMemory<byte> segment in raw)
            {
                segment.Span.CopyTo(rawBytes.AsSpan(offset));
                offset += segment.Length;
            }
        }
        string json = MessagePackSerializer.ConvertToJson(rawBytes, null, default);
        return JsonNode.Parse(json);
    }
}