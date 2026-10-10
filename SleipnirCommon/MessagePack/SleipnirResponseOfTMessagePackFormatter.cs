// ACHTUNG: Diese Datei wird NICHT in SleipnirCommon kompiliert (SleipnirCommon referenziert
// nur MessagePack.Annotations, nicht die volle MessagePack-Assembly). Sie wird per
// <Compile Include> in SleipnirHub.csproj (MessagePack 2.5.187 = Server) UND
// SleipnirClient.csproj (MessagePack 3.1.3 = Client) gelinkt — derselbe Source kompiliert
// gegen jede eigene MessagePack-Version (analog SleipnirResponseMessagePackFormatter).
//
// Nullable bewusst AUS, damit die IMessagePackFormatter-Signatur in beiden Versionen
// matched (3.x hat teils ?-Annotationen, 2.x nicht).
#nullable disable

using System.Reflection;
using System.Text.Json;
using SleipnirCommon.Models;
using MessagePack;
using MessagePack.Formatters;

namespace SleipnirCommon.MessagePack;

/// <summary>
/// MessagePack-Shim für den generischen Typed Envelope <see cref="SleipnirResponse{T}"/>
/// (SignalR-Kanal). Die abgeleitete Klasse trägt KEINE eigene MP-Metadaten und null
/// zusätzlichen Wire-Zustand — der Shim delegiert auf den Basenformatter
/// (<see cref="SleipnirResponseMessagePackFormatter"/>), sodass der 6-Element-Basis-Shape
/// unverändert geschrieben wird.
/// </summary>
/// <remarks>
/// Wofür der Shim existiert: der SignalR Result-Pfad löst den Formatter über den
/// RUNTIME-Typ auf (nicht den deklarierten Task&lt;SleipnirResponse?&gt;-Typ). Ohne
/// Shim landet GetFormatter&lt;SleipnirResponse&lt;T&gt;&gt; im StandardResolver →
/// DynamicObjectResolver → "not marked by MessagePackObject"-Fehler. Der Resolver
/// (<see cref="JsonElementResolver"/>) fängt die generische Definition ab und liefert
/// diesen Shim. Deserialize rekonstruiert den typisierten Wrapper um denselben Payload
/// (DataBytes bevorzugt, sonst Data).
/// </remarks>
public sealed class SleipnirResponseOfTMessagePackFormatter<T> : IMessagePackFormatter<SleipnirResponse<T>>
{
    public void Serialize(ref MessagePackWriter writer, SleipnirResponse<T> value, MessagePackSerializerOptions options)
        => SleipnirResponseMessagePackFormatter.Instance.Serialize(ref writer, value, options);

    public SleipnirResponse<T> Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        var response = SleipnirResponseMessagePackFormatter.Instance.Deserialize(ref reader, options);
        if (response is null)
            return null;

        var typed = new SleipnirResponse<T>
        {
            Content = response.Content,
            Id = response.Id,
            ExposedDependencies = response.ExposedDependencies,
            Error = response.Error,
            Code = response.Code,
        };

        // DataBytes zuerst (setter löscht Data), dann die JsonElement-Variante nur wenn
        // keine Bytes vorhanden sind — deckt beide Lazy-Paths des Basenformatters ab.
        if (response.DataBytes is { } dataBytes)
            typed.DataBytes = dataBytes;
        else
            typed.Data = response.Data;

        return typed;
    }
}

/// <summary>
/// Statischer Cache pro geschlossenem Envelope-Typen: <see cref="JsonElementResolver.GetFormatter{T}"/>
/// wird von MessagePack pro <see langword="typeof"/>&nbsp;<typeparamref name="T"/> einmalig
/// gefragt, der Shim wird also nur einmal konstruiert.
/// </summary>
public static class SleipnirResponseOfTFormatterCache<T>
{
    public static readonly IMessagePackFormatter<SleipnirResponse<T>> Instance = new SleipnirResponseOfTMessagePackFormatter<T>();
}

/// <summary>
/// Helfer für den Resolver: löst den geschlossen Shim-Typen für einen geschlossen
/// SleipnirResponse&lt;T&gt;-Typen auf (nur auf dem MP-Pfad benutzt, T ist dort immer
/// ein geschlossener generischer Typ).
/// </summary>
internal static class SleipnirResponseOfTFormatterResolver
{
    public static object GetShimInstance(Type closedEnvelopeType)
    {
        var arg = closedEnvelopeType.GetGenericArguments()[0];
        var cacheType = typeof(SleipnirResponseOfTFormatterCache<>).MakeGenericType(arg);
        // Feldname "Instance" ist in diesem File definiert (statisches readonly Feld).
        return cacheType
            .GetField("Instance", BindingFlags.Public | BindingFlags.Static)!
            .GetValue(null)!;
    }
}