// ACHTUNG: Diese Datei wird NICHT in SleipnirCommon kompiliert (SleipnirCommon referenziert
// nur MessagePack.Annotations, nicht die volle MessagePack-Assembly). Sie wird per
// <Compile Include> in SleipnirHub.csproj (MessagePack 2.5.187 = Server) UND
// SleipnirClient.csproj (MessagePack 3.1.3 = Client) gelinkt — derselbe Source kompiliert
// gegen jede eigene MessagePack-Version. Bewiesen in spikes/MpFormatterProbe/.
//
// Nullable bewusst AUS, damit die IFormatterResolver/IMessagePackFormatter-Signatur
// in beiden Versionen matched (3.x hat teils ?-Annotationen, 2.x nicht).
#nullable disable

using System.Text.Json;
using SleipnirCommon.Models;
using MessagePack;
using MessagePack.Formatters;
using MessagePack.Resolvers;

namespace SleipnirCommon.MessagePack;

/// <summary>
/// Serialisiert <see cref="JsonElement"/> (und <see cref="JsonElement"/>) als rohes
/// MessagePack: JSON-Text → MessagePack-Tokens 1:1 via
/// <see cref="MessagePackSerializer.ConvertFromJson"/> (Serialize) bzw.
/// MessagePack-Tokens → JSON-Text via <see cref="MessagePackSerializer.ConvertToJson"/>
/// (Deserialize). MessagePack ist eine JSON-Superset — 1:1 ohne Schema.
/// </summary>
/// <remarks>
/// Damit entfällt auf dem SignalR-Kanal die Double-Wrapping-Tax: Data ist seit dem
/// Single-Pass-Fix ein strukturierter JsonElement-Wert (kein JSON-String mehr), und
/// dieser Formatter schreibt ihn direkt als native MessagePack-Tokens statt als
/// escapeten JSON-String in ein binäres Feld.
/// </remarks>
public sealed class JsonElementMessagePackFormatter
    : IMessagePackFormatter<JsonElement>, IMessagePackFormatter<JsonElement?>
{
    public static readonly JsonElementMessagePackFormatter Instance = new();

    public void Serialize(ref MessagePackWriter writer, JsonElement value, MessagePackSerializerOptions options)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
        {
            writer.WriteNil();
            return;
        }
        // JSON-Text → MessagePack-Tokens. null-Options = StandardResolver intern
        // (keine Rekursion über unseren eigenen Resolver — reines JSON, keine Custom-Typen).
        byte[] bytes = MessagePackSerializer.ConvertFromJson(value.GetRawText(), null, default);
        writer.WriteRaw(bytes);
    }

    public JsonElement Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        if (reader.TryReadNil())
            return default;
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
        // Clone() löst den JsonDocument-Scope — Element bleibt über den Dispose hinaus gültig.
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    public void Serialize(ref MessagePackWriter writer, JsonElement? value, MessagePackSerializerOptions options)
    {
        if (!value.HasValue || value.Value.ValueKind == JsonValueKind.Undefined)
        {
            writer.WriteNil();
            return;
        }
        Serialize(ref writer, value.Value, options);
    }

    JsonElement? IMessagePackFormatter<JsonElement?>.Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        if (reader.TryReadNil())
            return null;
        return Deserialize(ref reader, options);
    }
}

/// <summary>
/// Resolver: JsonElement/JsonElement? und JsonNode/JsonNode? → unsere Formatter, sonst
/// StandardResolver. Ein eigener Resolver statt CompositeResolver, weil die
/// CompositeResolver-Überladungen sich zwischen 2.x und 3.x unterscheiden —
/// IFormatterResolver.GetFormatter&lt;T&gt; ist versionsstabil.
/// </summary>
public sealed class JsonElementResolver : IFormatterResolver
{
    public static readonly IFormatterResolver Instance = new JsonElementResolver();

    public IMessagePackFormatter<T> GetFormatter<T>()
    {
        if (typeof(T) == typeof(JsonElement))
            return (IMessagePackFormatter<T>)(object)JsonElementMessagePackFormatter.Instance;
        if (typeof(T) == typeof(JsonElement?))
            return (IMessagePackFormatter<T>)(object)JsonElementMessagePackFormatter.Instance;
        // Request-Seite: SleipnirParameter.Data / SleipnirRequest.Params sind native JsonNode-Werte.
        // (JsonNode ist ein Referenztyp — typeof(JsonNode?) == typeof(JsonNode), ein Case reicht.)
        if (typeof(T) == typeof(System.Text.Json.Nodes.JsonNode))
            return (IMessagePackFormatter<T>)(object)JsonNodeMessagePackFormatter.Instance;
        // SleipnirResponse: eigener Formatter, damit der Bulk-Pfad Data aus DataBytes
        // schreibt (ConvertFromJson, ein Pass) statt den lazy Data-Getter auszulösen
        // (sonst JsonDocument-Baum + Re-Parse auf dem Server → Regression).
        if (typeof(T) == typeof(SleipnirResponse))
            return (IMessagePackFormatter<T>)(object)SleipnirResponseMessagePackFormatter.Instance;
        return StandardResolver.Instance.GetFormatter<T>();
    }
}