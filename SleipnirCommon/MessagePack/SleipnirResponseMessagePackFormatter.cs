// ACHTUNG: Diese Datei wird NICHT in SleipnirCommon kompiliert (SleipnirCommon referenziert
// nur MessagePack.Annotations, nicht die volle MessagePack-Assembly). Sie wird per
// <Compile Include> in SleipnirHub.csproj (MessagePack 2.5.187 = Server) UND
// SleipnirClient.csproj (MessagePack 3.1.3 = Client) gelinkt — derselbe Source kompiliert
// gegen jede eigene MessagePack-Version (analog JsonElementMessagePackFormatter).
//
// Nullable bewusst AUS, damit die IMessagePackFormatter-Signatur in beiden Versionen
// matched (3.x hat teils ?-Annotationen, 2.x nicht).
#nullable disable

using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using SleipnirCommon.Models;
using MessagePack;
using MessagePack.Formatters;

namespace SleipnirCommon.MessagePack;

/// <summary>
/// MessagePack-Formatter für <see cref="SleipnirResponse"/> (SignalR-Kanal, Server +
/// Client). Schreibt die Response als 6-Element-Array in <c>[Key]</c>-Reihenfolge
/// (Code, Data, Content, Id, ExposedDependencies, Error) — wire-identisch zum
/// bisherigen Default-Object-Formatter.
/// </summary>
/// <remarks>
/// Single-Pass-Optimierung: ist <see cref="SleipnirResponse.DataBytes"/> gesetzt (Bulk-
/// Pfad), schreibt der Formatter Data direkt als native MessagePack-Tokens via
/// <see cref="MessagePackSerializer.ConvertFromJson(byte[], IFormatterResolver, CancellationToken)"/>
/// (rohe UTF-8-Bytes → MP-Tokens, ein Pass) — der lazy <see cref="SleipnirResponse.Data"/>-
/// Getter wird NICHT ausgelöst (kein JsonDocument-Baum, kein Re-Parse auf dem Server).
/// Ist nur Data gesetzt (Legacy-/ProblemDetails-Pfad), fällt er auf den
/// <see cref="JsonElementMessagePackFormatter"/> zurück (Status quo). Auf der
/// Client-Seite (Read) bleibt es beim Status quo: Data → JsonElement via
/// <see cref="JsonElementMessagePackFormatter"/> (kein Client-Single-Pass für SignalR,
/// siehe Plan). Damit lazy Data auf dem SignalR-Kanal nicht regressiert, MUSS dieser
/// Formatter den Default ersetzen — sonst würde der Object-Formatter Data materialisieren.
/// </remarks>
public sealed class SleipnirResponseMessagePackFormatter : IMessagePackFormatter<SleipnirResponse>
{
    public static readonly SleipnirResponseMessagePackFormatter Instance = new();

    public void Serialize(ref MessagePackWriter writer, SleipnirResponse value, MessagePackSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNil();
            return;
        }

        // 6 Elemente in [Key]-Reihenfolge, wie der Default-Object-Formatter.
        writer.WriteArrayHeader(6);

        // [0] Code
        writer.WriteInt32(value.Code);

        // [1] Data: DataBytes (raw UTF-8 → MP-Tokens, ein Pass) bevorzugt, sonst JsonElement-Formatter.
        var dataBytes = value.DataBytes;
        if (dataBytes != null && dataBytes.Length > 0)
        {
            // ConvertFromJson nimmt einen JSON-String (gleiche Überladung wie der
            // JsonElement-Formatter). Wir dekodieren DataBytes → String, OHNE den
            // lazy Data-Getter auszulösen (kein JsonDocument-Baum, kein Re-Parse auf
            // dem Server — das ist die Regression, die dieser Formatter abwendet).
            // null-Options = StandardResolver intern (keine Rekursion über unseren
            // eigenen Resolver — reines JSON, keine Custom-Typen).
            byte[] mp = MessagePackSerializer.ConvertFromJson(Encoding.UTF8.GetString(dataBytes), null, default);
            writer.WriteRaw(mp);
        }
        else
        {
            // Nullable-JsonElement-Variante: behandelt null (Data nicht gesetzt) als
            // MP-nil, sonst ConvertFromJson(GetRawText) — wire-identisch zum Default.
            ((IMessagePackFormatter<JsonElement?>)JsonElementMessagePackFormatter.Instance)
                .Serialize(ref writer, value.Data, options);
        }

        // [2] Content: byte[] als MessagePack bin (Default für byte[]) bzw. nil.
        // Sub-Formatter über den aktiven Resolver — versionsstabil (WriteBin fehlt
        // in 2.5.187/3.1.3 als direkte MessagePackWriter-Methode).
        if (value.Content is { } content)
        {
            options.Resolver.GetFormatterWithVerify<byte[]>().Serialize(ref writer, content, options);
        }
        else
        {
            writer.WriteNil();
        }

        // [3] Id — String-Formatter über den aktiven Resolver (WriteString(string) ist
        // in MessagePack 2.5.187/3.1.3 keine MessagePackWriter-Methode; der Resolver-
        // Pfad ist zudem byte-identisch mit dem Default-Object-Formatter).
        if (value.Id is { } id)
        {
            options.Resolver.GetFormatterWithVerify<string>().Serialize(ref writer, id, options);
        }
        else
        {
            writer.WriteNil();
        }

        // [4] ExposedDependencies: Dictionary<string,string>? — Sub-Formatter über den
        // aktiven Resolver (JsonElementResolver → StandardResolver für Dictionary).
        if (value.ExposedDependencies is { } exposed)
        {
            options.Resolver.GetFormatterWithVerify<Dictionary<string, string>>()
                .Serialize(ref writer, exposed, options);
        }
        else
        {
            writer.WriteNil();
        }

        // [5] Error: SleipnirError? — Sub-Formatter über den aktiven Resolver.
        if (value.Error is { } error)
        {
            options.Resolver.GetFormatterWithVerify<SleipnirError>()
                .Serialize(ref writer, error, options);
        }
        else
        {
            writer.WriteNil();
        }
    }

    public SleipnirResponse Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        if (reader.TryReadNil())
        {
            return null;
        }

        var count = reader.ReadArrayHeader();
        var response = new SleipnirResponse();

        // count ist normalerweise 6; toleranter Umgang, falls ein älterer Server
        // mehr/felder liefert (Default schreibt genau 6, aber Skip ist sicher).
        for (int i = 0; i < count; i++)
        {
            switch (i)
            {
                case 0:
                    response.Code = reader.ReadInt32();
                    break;
                case 1:
                    // Data via JsonElement-Formatter, Nullable-Variante: nil → null
                    // (Data.HasValue=false, wichtig für Void/204-Responses), sonst JsonElement.
                    response.Data = ((IMessagePackFormatter<JsonElement?>)JsonElementMessagePackFormatter.Instance)
                        .Deserialize(ref reader, options);
                    break;
                case 2:
                    response.Content = reader.TryReadNil()
                        ? null
                        : options.Resolver.GetFormatterWithVerify<byte[]>().Deserialize(ref reader, options);
                    break;
                case 3:
                    response.Id = reader.TryReadNil() ? null : reader.ReadString();
                    break;
                case 4:
                    response.ExposedDependencies = reader.TryReadNil()
                        ? null
                        : options.Resolver.GetFormatterWithVerify<Dictionary<string, string>>()
                            .Deserialize(ref reader, options);
                    break;
                case 5:
                    response.Error = reader.TryReadNil()
                        ? null
                        : options.Resolver.GetFormatterWithVerify<SleipnirError>()
                            .Deserialize(ref reader, options);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        return response;
    }
}