// Shared Formatter-Source für den MpFormatterProbe-Spike.
//
// Wird per <Compile Include> in ProbeMp2.csproj (MessagePack 2.5.187 = Server)
// UND ProbeMp3.csproj (MessagePack 3.1.3 = Client) gelinkt — derselbe Source
// kompiliert gegen jede eigene MessagePack-Version. Genau das, was später in
// SleipnirHub + SleipnirClient passiert (Step 8 des Single-Pass-Fix-Plans).
//
// Nullable bewusst AUS, damit die IFormatterResolver/IMessagePackFormatter-Signatur
// in beiden Versionen matched (3.x hat teils ?-Annotationen, 2.x nicht — beides
// kompiliert mit Nullable=disable sauber).
#nullable disable

using System.Text.Json;
using MessagePack;
using MessagePack.Formatters;
using MessagePack.Resolvers;

namespace Sleipnir.Spikes.MessagePack;

/// <summary>
/// Serialisiert <see cref="JsonElement"/> (und <see cref="JsonElement"/>) als
/// rohes MessagePack: JSON-String → MessagePack-Tokens 1:1 via
/// <see cref="MessagePackSerializer.ConvertFromJson"/> (Serialize) bzw.
/// MessagePack-Tokens → JSON-String via
/// <see cref="MessagePackSerializer.ConvertToJson"/> (Deserialize).
/// MessagePack ist eine JSON-Superset — hence 1:1 ohne Schema.
/// </summary>
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
        // (keine Rekursion über unseren eigenen Resolver — reines JSON, keine
        // Custom-Typen).
        byte[] bytes = MessagePackSerializer.ConvertFromJson(value.GetRawText(), null, default);
        writer.WriteRaw(bytes);
    }

    public JsonElement Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        if (reader.TryReadNil())
            return default;
        // ReadRaw liefert in 2.x UND 3.x ReadOnlySequence<byte> — die hat keine instanz-
        // ToArray(). Daher: Single-Segment-Fast-Path via First.Span.ToArray(), sonst
        // CopyTo in ein passend großes Array. (Length ist hier int-trivial.)
        var raw = reader.ReadRaw();
        byte[] rawBytes;
        if (raw.IsSingleSegment)
            rawBytes = raw.First.Span.ToArray();
        else
        {
            // Multi-Segment (selten bei MessagePack-Frames, aber korrekt behandeln):
            // ReadOnlySequence<byte> ist iterierbar über ReadOnlyMemory<byte>-Segmente.
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
/// Resolver: JsonElement/JsonElement? → unser Formatter, sonst StandardResolver.
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
        return StandardResolver.Instance.GetFormatter<T>();
    }
}