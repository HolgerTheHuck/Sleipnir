using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using SleipnirCommon.Models;

namespace SleipnirCommon.Results;

/// <summary>
/// Statische Fabrik für <see cref="SleipnirResponse"/>-Instanzen aus Controllern
/// (Attribut-basiert: <c>[SleipnirController]</c>/<c>[SleipnirMethod]</c>). Erzeugt
/// saubere Responses inkl. strukturiertem <see cref="SleipnirError"/> bei non-2xx,
/// sodass die Fehlermeldung verlustfrei beim Client ankommt — im C#-Client über
/// die geworfene <c>SleipnirException.Error.Message</c>, im JS/TS-Client über
/// <c>response.error.message</c> bzw. die DevUI-Anzeige.
/// </summary>
/// <remarks>
/// Der <see cref="SleipnirCore.Services.SleipnirInvoker"/> gibt eine vom Controller
/// zurückgegebene <see cref="SleipnirResponse"/> unverändert durch
/// (<c>ReturnResponse</c>: <c>if (result is SleipnirResponse sleipnirResp) return sleipnirResp;</c>).
/// Damit ist das der unterstützte Kanal, um aus einer Controller-Methode einen
/// eigenen Status-Code + Fehlertext an den Client zu liefern. Geworfene
/// Exceptions hingegen werden pauschal zu <c>500</c> mit generischer Message
/// (kein Leak in Produktion) — für client-sichtbare Validierungs-/Domain-Fehler
/// also <emphasis>nicht</emphasis> werfen, sondern <c>SleipnirResults.Error(...)</c>
/// zurückgeben.
/// </remarks>
public static class SleipnirResults
{
    // camelCase + relaxed Encoder (wie der Invoker). ProblemDetails (RFC 7807) wird
    // kanonisch in CamelCase serialisiert; UnsafeRelaxed verhindert `"`-Escaping.
    private static readonly JsonSerializerOptions CamelCaseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 200 OK mit JSON-serialisiertem Ergebnis. <paramref name="result"/> wird
    /// strukturiert (camelCase, relaxed Encoder) als rohe UTF-8-Bytes in
    /// <see cref="SleipnirResponse.DataBytes"/> abgelegt — identisch zum Framework-Pfad
    /// im Invoker, sodass Controller- und Default-Serialisierung dasselbe Wire-Bild
    /// ergeben. Der Transport-Converter schreibt die Bytes in einem Pass (kein
    /// JsonDocument-Baum). <see cref="SleipnirResponse.Data"/> bleibt null und wird erst
    /// lazy materialisiert, wenn ein Reader zugreift. <c>null</c> ergibt 204 No Content.
    /// </summary>
    public static SleipnirResponse Ok(object? result)
    {
        if (result is null) return NoContent();
        return new SleipnirResponse
        {
            Code = SleipnirErrorCodes.Ok,
            DataBytes = JsonSerializer.SerializeToUtf8Bytes(result, CamelCaseJsonOptions),
        };
    }

    /// <summary>
    /// 200 OK mit bereits JSON-serialisiertem String. Der String wird als rohe
    /// UTF-8-Bytes in <see cref="SleipnirResponse.DataBytes"/> abgelegt und vom
    /// Transport-Converter in einem Pass in den Wire geschrieben — <b>erfordert
    /// gültiges JSON</b> (Wire-Break gegenüber der alten String-through-Data-Semantik).
    /// Nützlich, wenn der Caller selbst serialisiert.
    /// </summary>
    public static SleipnirResponse Ok(string jsonData) => new()
    {
        Code = SleipnirErrorCodes.Ok,
        DataBytes = Encoding.UTF8.GetBytes(jsonData),
    };

    /// <summary>
    /// 200 OK mit binärem Ergebnis: die Rohbytes ausschließlich in
    /// <see cref="SleipnirResponse.Content"/> (kein Base64-String mehr in Data → keine
    /// doppelte Belegung, kein Parse-Problem im Dependency-Pfad). Spiegel des
    /// Invoker-byte[]-Pfads.
    /// </summary>
    public static SleipnirResponse Ok(byte[] binary) => new()
    {
        Code = SleipnirErrorCodes.Ok,
        Content = binary,
    };

    /// <summary>204 No Content — für <c>void</c>-/<c>Task</c>-Methoden ohne Ergebnis.</summary>
    public static SleipnirResponse NoContent() => new() { Code = SleipnirErrorCodes.NoContent };

    /// <summary>
    /// Non-2xx-Fehlerantwort. <paramref name="message"/> wohnt ausschließlich in
    /// <see cref="SleipnirError.Message"/> (Data bleibt null) — strukturierte Clients
    /// lesen <c>response.error.message</c>, der C#-Client wirft
    /// <c>SleipnirException.Error.Message</c>, der JS/TS-Client <c>response.error.message</c>.
    /// Die semantische <paramref name="category"/> wird *zusätzlich* zum numerischen
    /// <paramref name="code"/> gesetzt (Phase 1, siehe <c>ERROR_CATALOG.md</c>); Default
    /// <see cref="SleipnirErrorCategory.None"/> für Abwärtskompatibilität.
    /// </summary>
    /// <param name="code">HTTP-like Status-Code (siehe <see cref="SleipnirErrorCodes"/>).</param>
    /// <param name="message">Mensch-lesbare Fehlernachricht (sichtbar beim Client).</param>
    /// <param name="category">Semantische Kategorie (maschinenlesbar, transport-uniform).</param>
    /// <param name="details">Optionale Details (z. B. Diagnose-Hinweis; NICHT für
    /// sensible Stack-Traces verwenden — die gehören in <c>EnableDetailedErrors</c>
    /// auf Server-Seite und werden dort vom Invoker verwaltet).</param>
    public static SleipnirResponse Error(int code, string message,
        SleipnirErrorCategory category = SleipnirErrorCategory.None, string? details = null) => new()
        {
            Code = code,
            Data = null,
            Error = new SleipnirError
            {
                Code = code,
                Message = message,
                Details = details,
                Category = category,
            },
        };

    /// <summary>400 Bad Request — ungültige Parameter / Validierungsfehler.</summary>
    public static SleipnirResponse BadRequest(string message, string? details = null)
        => Error(SleipnirErrorCodes.BadRequest, message, SleipnirErrorCategory.InvalidArgument, details);

    /// <summary>401 Unauthorized — Authentifizierung erforderlich/fehlgeschlagen.</summary>
    public static SleipnirResponse Unauthorized(string message = "Unauthorized.")
        => Error(SleipnirErrorCodes.Unauthorized, message, SleipnirErrorCategory.Unauthenticated);

    /// <summary>403 Forbidden — authentifiziert, aber nicht erlaubt (Policy-basiert, Phase 1).</summary>
    public static SleipnirResponse Forbidden(string message = "Forbidden.", string? details = null)
        => Error(SleipnirErrorCodes.Forbidden, message, SleipnirErrorCategory.PermissionDenied, details);

    /// <summary>404 Not Found — Ressource/Entität nicht gefunden (Business-NotFound).</summary>
    public static SleipnirResponse NotFound(string message, string? details = null)
        => Error(SleipnirErrorCodes.NotFound, message, SleipnirErrorCategory.NotFound, details);

    /// <summary>409 Conflict — Konflikt mit aktuellem Zustand (z. B. Duplikat).</summary>
    public static SleipnirResponse Conflict(string message, string? details = null)
        => Error(SleipnirErrorCodes.Conflict, message, SleipnirErrorCategory.Conflict, details);

    /// <summary>
    /// 500 Internal Server Error — nur für Controller, die bewusst eine interne
    /// Fehlersituation signalisieren wollen. Im Normalfall wirft man stattdessen
    /// eine Exception (der Invoker erzeugt das generische 500 + Dev-Details).
    /// </summary>
    public static SleipnirResponse InternalServerError(string message, string? details = null)
        => Error(SleipnirErrorCodes.InternalServerError, message, SleipnirErrorCategory.Internal, details);

    /// <summary>
    /// Non-2xx-Fehlerantwort im RFC-7807-ProblemDetails-Stil. <paramref name="problem"/>
    /// wird strukturiert (CamelCase) als <see cref="JsonElement"/> in
    /// <see cref="SleipnirResponse.Data"/> serialisiert (kanonisch für Interop); zusätzlich
    /// werden <see cref="SleipnirError.Message"/> = <c>title</c> und
    /// <see cref="SleipnirError.Details"/> = <c>detail</c> belegt, damit einfache Clients
    /// eine Klartext-Message sehen und ProblemDetails-kundige Clients das vollständige
    /// Objekt in <c>Data</c> lesen.
    /// </summary>
    public static SleipnirResponse Error(ProblemDetails problem) => new()
    {
        Code = problem.Status,
        Data = JsonSerializer.SerializeToElement(problem, CamelCaseJsonOptions),
        Error = new SleipnirError
        {
            Code = problem.Status,
            Message = problem.Title ?? "Unknown error",
            Details = problem.Detail,
        },
    };
}

/// <summary>
/// Minimaler RFC-7807-ProblemDetails-Datensatz (application/problem+json). Alle
/// Felder optional bis auf <see cref="Status"/>; <see cref="Type"/> ist ein URI-
/// Bezeichner des Fehlertyps (RFC-Default <c>"about:blank"</c>), <see cref="Instance"/>
/// ein URI der konkreten Vorkommens (z. B. Request-/Ressourcen-Bezug).
/// </summary>
public sealed class ProblemDetails
{
    public string? Type { get; set; }
    public string? Title { get; set; }
    public int Status { get; set; }
    public string? Detail { get; set; }
    public string? Instance { get; set; }
}