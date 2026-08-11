using System.Diagnostics;
using SleipnirCommon.Models;

namespace SleipnirCore.Tracing;

/// <summary>
/// OpenTelemetry-Instrumentierung des Sleipnir-Motors. Erzeugt pro RPC-Call und
/// pro Batch einen <see cref="Activity"/> mit OTel RPC-Semantic-Conventions.
/// Immer eingeschaltetet, aber kostenneutral ohne Listener:
/// <see cref="ActivitySource.StartActivity"/> liefert <c>null</c>, wenn kein
/// <c>ActivityListener</c> abonniert — alle Helfer no-then via Null-Check.
/// </summary>
/// <remarks>
/// Die Klasse ist <see langword="public"/>, damit <see cref="ActivitySourceName"/>
/// aus dem optionalen <c>Sleipnir.Telemetry</c>-Paket (andere Assembly) erreichbar ist.
/// Alle übrigen Member sind <see langword="internal"/>; die Instrumentierung ist
/// kein Teil des öffentlichen Vertrags. Konsumenten abonnieren den Quellennamen
/// <c>"Sleipnir"</c> aus ihrem eigenen OTel-Setup oder via <c>AddSleipnirTelemetry</c>.
/// </remarks>
public static class SleipnirTracing
{
    /// <summary>Name des ActivitySource, unter dem Sleipnir Spans emittiert.</summary>
    public const string ActivitySourceName = "Sleipnir";

    /// <summary>ActivitySource-Version folgt der Paketversion.</summary>
    internal static readonly ActivitySource Source = new(ActivitySourceName, "1.0.0");

    /// <summary>Startet einen per-Call-Activity mit rpc.*-Tags. Null ohne Listener.</summary>
    /// <param name="request">Die Sleipnir-Anfrage (Controller/Method/Id/BinaryData).</param>
    /// <returns>Ein gestarteter <see cref="Activity"/> oder <c>null</c>.</returns>
    internal static Activity? StartCall(SleipnirRequest request)
    {
        var activity = Source.StartActivity("SleipnirCall", ActivityKind.Internal);
        if (activity is null)
            return null;

        activity.SetTag("rpc.system", "sleipnir");
        activity.SetTag("rpc.service", request.Controller);
        activity.SetTag("rpc.method", request.Method);
        if (!string.IsNullOrEmpty(request.Id))
            activity.SetTag("sleipnir.request_id", request.Id);
        if (request.BinaryData is { Length: > 0 })
            activity.SetTag("sleipnir.binary.length", request.BinaryData.Length);

        return activity;
    }

    /// <summary>Startet einen Batch-Parent-Activity. Null ohne Listener.</summary>
    /// <param name="requests">Die Batch-Anfragen (für den Count-Tag).</param>
    /// <param name="mode">Die ausgeführte <see cref="ExecutionMode"/>.</param>
    /// <returns>Ein gestarteter <see cref="Activity"/> oder <c>null</c>.</returns>
    internal static Activity? StartBatch(IReadOnlyList<SleipnirRequest> requests, ExecutionMode mode)
    {
        var activity = Source.StartActivity("SleipnirBatch", ActivityKind.Internal);
        if (activity is null)
            return null;

        activity.SetTag("rpc.system", "sleipnir");
        activity.SetTag("sleipnir.batch.mode", mode.ToString());
        activity.SetTag("sleipnir.batch.count", requests.Count);

        return activity;
    }

    /// <summary>Setzt den OTel-Status aus der <see cref="SleipnirResponse"/>.</summary>
    /// <param name="activity">Der Call-/Batch-Activity (darf null sein — dann No-op).</param>
    /// <param name="response">Die Antwort (IsSuccess bestimmt Ok vs. Error).</param>
    public static void SetCallStatus(Activity? activity, SleipnirResponse? response)
    {
        if (activity is null)
            return;

        if (response?.IsSuccess == true)
            activity.SetStatus(ActivityStatusCode.Ok);
        else
            activity.SetStatus(ActivityStatusCode.Error, response?.Error?.Message ?? "RPC failed");
    }

    /// <summary>
    /// Zeichnet eine Ausnahme als OTel-Exception-Tags auf (exception.type/message/stacktrace).
    /// Ersetzt <c>Activity.RecordException</c>, dessen Erweiterungsmethode in der
    /// net8.0-Klassenbibliothek nicht auflösbar ist; explizite Tags sind äquivalent und
    /// OTel-konform.
    /// </summary>
    /// <param name="activity">Der Activity (darf null sein — dann No-op).</param>
    /// <param name="ex">Die zu recordede Ausnahme.</param>
    public static void RecordException(Activity? activity, Exception ex)
    {
        if (activity is null)
            return;

        activity.SetTag("exception.type", ex.GetType().FullName);
        activity.SetTag("exception.message", ex.Message);
        if (!string.IsNullOrEmpty(ex.StackTrace))
            activity.SetTag("exception.stacktrace", ex.StackTrace);
    }
}