namespace TrameCommon;

/// <summary>
/// Wird vom Auth-Pfad geworfen, wenn der User authentifiziert ist, aber die
/// Autorisierung verweigert wurde (Rolle/Policy nicht erfüllt). Der Aufrufer
/// (<c>TrameInvoker</c> / <c>TrameAuthorizationInterceptor</c>) übersetzt sie in eine
/// <c>403 Forbidden</c>-Response (<see cref="Results.TrameErrorCategory.PermissionDenied"/>).
/// Im Unterschied zu <see cref="UnauthorizedAccessException"/>, die <c>401</c>
/// (<see cref="Results.TrameErrorCategory.Unauthenticated"/>) auslöst — "nicht eingeloggt".
/// </summary>
/// <remarks>
/// Phase 1 — siehe <c>docs/design/phase-1-interceptor-pipeline.md</c>. Unterscheidet
/// <c>401</c> (Unauthenticated) von <c>403</c> (PermissionDenied), wie in
/// <c>SECURITY.md</c> / <c>RELEASE-PLAN.md</c> als Roadmap-Item gefordert.
/// </remarks>
public class ForbiddenAccessException : Exception
{
    public ForbiddenAccessException() : base("Forbidden.") { }
    public ForbiddenAccessException(string message) : base(message) { }
    public ForbiddenAccessException(string message, Exception innerException)
        : base(message, innerException) { }
}