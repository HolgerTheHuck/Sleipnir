using System.Text.Json.Serialization;

namespace SleipnirCommon.Results;

/// <summary>
/// Semantische Fehler-Kategorie — *zusätzlich* zum numerischen <see cref="SleipnirError.Code"/>,
/// nicht ersetzend (siehe <c>STABILITY.md</c> §1.4). Erlaubt Clients, Fehler einheitlich
/// über alle Transporte zu behandeln, ohne HTTP-Status + JSON-RPC-Code + Domain-Code
/// mischen zu müssen. Die Kategorie ist maschinenlesbar und sprachübergreifend stabil;
/// generierte Clients (Codegen) können pro Kategorie typisierte Exceptions werfen.
/// </summary>
/// <remarks>
/// Phase 1 — siehe <c>docs/design/phase-1-interceptor-pipeline.md</c>. Additives Feld
/// (<c>SleipnirError.Category</c>, Key 4); bestehende Clients ignorieren es (STABILITY.md §3.2).
/// Anlehnung an gRPC-Status-Codes zur Senkung polyglotter Adoption-Reibung.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SleipnirErrorCategory
{
    /// <summary>
    /// Keine Kategorie — Default, wenn keine gesetzt wurde. Bestehende 1.0.0-Responses
    /// haben <c>null</c>/<c>None</c> und bleiben damit abwärtskompatibel.
    /// </summary>
    None = 0,

    /// <summary>
    /// Ungültige Argumente / Parameter / Validierung (400). Entspricht gRPC <c>INVALID_ARGUMENT</c>.
    /// </summary>
    InvalidArgument = 1,

    /// <summary>
    /// Fehlende oder ungültige Authentifizierung (401). gRPC <c>UNAUTHENTICATED</c>.
    /// </summary>
    Unauthenticated = 2,

    /// <summary>
    /// Authentifiziert, aber nicht erlaubt (403). gRPC <c>PERMISSION_DENIED</c>.
    /// Wird mit Policy-basiertem Auth (ROADMAP Phase 1) von <see cref="Unauthenticated"/>
    /// unterschieden — heute noch beides unter 401.
    /// </summary>
    PermissionDenied = 3,

    /// <summary>
    /// Ressource / Entität / Controller / Methode nicht gefunden (404). gRPC <c>NOT_FOUND</c>.
    /// </summary>
    NotFound = 4,

    /// <summary>
    /// Konflikt mit aktuellem Zustand, z. B. Duplikat, veraltete Version (409). gRPC <c>ALREADY_EXISTS</c> / <c>FAILED_PRECONDITION</c>.
    /// </summary>
    Conflict = 5,

    /// <summary>
    /// Vorbedingung nicht erfüllt, z. B. Dependency-Kette unterbrochen, Provider fehlgeschlagen (400). gRPC <c>FAILED_PRECONDITION</c>.
    /// </summary>
    FailedPrecondition = 6,

    /// <summary>
    /// Ressource erschöpft — Kardinalitäts-Cap, Message-Größen-Limit, Rate-Limit getroffen (413/429/503). gRPC <c>RESOURCE_EXHAUSTED</c>.
    /// </summary>
    ResourceExhausted = 7,

    /// <summary>
    /// Unerwarteter interner Server-Fehler (500). gRPC <c>INTERNAL</c> / <c>UNKNOWN</c>.
    /// </summary>
    Internal = 8,

    /// <summary>
    /// Service nicht verfügbar / überlastet (503). gRPC <c>UNAVAILABLE</c>.
    /// </summary>
    Unavailable = 9,

    /// <summary>
    /// Client hat den Request abgebrochen (499). Keine direkte gRPC-Entsprechung.
    /// </summary>
    Cancelled = 10,
}