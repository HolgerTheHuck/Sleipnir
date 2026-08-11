namespace SleipnirCommon.Results;

/// <summary>
/// Stabile, transport-uniforme Fehler-Code-Konstanten für Sleipnir. Ersetzen die Magic
/// Numbers, die früher verstreut in <see cref="SleipnirResults"/> und den privaten
/// Fabriken des <c>SleipnirInvoker</c> standen. Die numerischen Werte sind HTTP-Status-
/// Codes (kompatibel mit <see cref="System.Net.HttpStatusCode"/>) und bleiben stabil
/// innerhalb 1.x (siehe <c>STABILITY.md</c> §1.4). Eine künftige Fehler-Taxonomie
/// (semantische Kategorien, <see cref="SleipnirErrorCategory"/>) layert *zusätzlich*
/// auf, ohne die numerischen Codes umzubenennen.
/// </summary>
/// <remarks>
/// Phase 1 — siehe <c>docs/design/phase-1-interceptor-pipeline.md</c> und
/// <c>ERROR_CATALOG.md</c>. Die Codes sind die <c>SleipnirResponse.code</c> /
/// <c>SleipnirError.code</c> Werte auf dem Wire.
/// </remarks>
public static class SleipnirErrorCodes
{
    // ─── 2xx Success ────────────────────────────────────────────────────────
    public const int Ok = 200;
    public const int NoContent = 204;

    // ─── 4xx Client Errors ─────────────────────────────────────────────────
    /// <summary>Ungültige Parameter, Validierungsfehler, Binding-Fehler (400).</summary>
    public const int BadRequest = 400;
    /// <summary>Authentifizierung erforderlich oder fehlgeschlagen (401).</summary>
    public const int Unauthorized = 401;
    /// <summary>Authentifiziert, aber nicht erlaubt (403). Siehe ROADMAP Phase 1 —
    /// wird mit Policy-basiertem Auth unterschieden von 401 (heute noch 401 für beides).</summary>
    public const int Forbidden = 403;
    /// <summary>Controller oder Methode nicht gefunden, oder Business-NotFound (404).</summary>
    public const int NotFound = 404;
    /// <summary>Konflikt mit aktuellem Zustand, z. B. Duplikat (409).</summary>
    public const int Conflict = 409;
    /// <summary>Payload überschreitet Kardinalitäts-Cap oder Message-Größen-Limit (413).</summary>
    public const int RequestEntityTooLarge = 413;
    /// <summary>Client hat den Request abgebrochen (499, nur in Transports).</summary>
    public const int ClientClosedRequest = 499;

    // ─── 5xx Server Errors ─────────────────────────────────────────────────
    /// <summary>Unerwarteter Server-Fehler (500). Generische Message in Produktion.</summary>
    public const int InternalServerError = 500;
    /// <summary>Service überlastet / Rate-Limit getroffen (503).</summary>
    public const int ServiceUnavailable = 503;
}