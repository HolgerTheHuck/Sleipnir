using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using TrameCommon;
using TrameCommon.Results;
using TrameCore.Attributes;
using TrameCore.Services;

namespace TrameHub.Interceptors;

/// <summary>
/// Built-in Auth-Interceptor (Phase 1). Evaluiert <c>[TrameAuthorise]</c> /
/// <c>[TrameAnonymous]</c> / <c>RequireAuthentication</c> *plus* die neue
/// <see cref="TrameAuthoriseAttribute.Policy"/> via ASP.NET Core
/// <see cref="IAuthorizationService"/>. Liefert <c>401</c> (Unauthenticated) oder
/// <c>403</c> (PermissionDenied) als Short-Circuit-Response, ohne die Method-Invocation
/// zu erreichen.
/// </summary>
/// <remarks>
/// <para>
/// Unterscheidet <c>401</c> (nicht authentifiziert) von <c>403</c> (authentifiziert,
/// aber Rolle/Policy verweigert) — das Roadmap-Item aus <c>SECURITY.md</c> /
/// <c>RELEASE-PLAN.md</c> Phase 3.1. Heute (1.0.0) liefert Trame für beides <c>401</c>;
/// mit Phase 1 wird <c>403</c> für PermissionDenied eingeführt.
/// </para>
/// <para>
/// <c>resource</c> ist in v1.1 <c>null</c> (command-orientiert, kein Resource-Begriff).
/// Ein <c>[TrameAuthorizeResource]</c>-Hook für resource-basierte Policies ist v1.x+.
/// Siehe <c>docs/design/phase-1-interceptor-pipeline.md</c> Entscheidung 3.
/// </para>
/// <para>
/// <see cref="IAuthorizationService"/> ist optional — ist er nicht registriert (z. B.
/// South-Bound ohne ASP.NET Core Authorization), aber eine Methode setzt
/// <see cref="TrameAuthoriseAttribute.Policy"/>, wird ein <c>500</c> geliefert
/// (Policies konfiguriert, aber kein IAuthorizationService verfügbar). Das ist ein
/// Konfigurationsfehler, kein Laufzeitfehler — wird einmalig beim ersten Policy-Call
/// sichtbar, statt still zu ignorieren.
/// </para>
/// </remarks>
public class TrameAuthorizationInterceptor : ITrameInterceptor
{
    private readonly IAuthorizationService? _authorizationService;
    private readonly ILogger<TrameAuthorizationInterceptor> _logger;
    private readonly bool _requireAuthentication;

    public TrameAuthorizationInterceptor(
        IAuthorizationService? authorizationService,
        ILogger<TrameAuthorizationInterceptor> logger,
        bool requireAuthentication)
    {
        _authorizationService = authorizationService;
        _logger = logger;
        _requireAuthentication = requireAuthentication;
    }

    public async Task<TrameResponse?> InvokeAsync(
        TrameInvocationContext context,
        TrameInvocationDelegate next)
    {
        // InvokeInfo ist nach dem Controller/Method-Resolve belegt (der Invoker
        // setzt es, bevor die Pipeline läuft). Vor dem Resolve (z. B. wenn ein
        // früher Interceptor short-circuitet) wäre es null — dann lassen wir die
        // Pipeline weiterlaufen, der Invoker kümmert sich um das Routing-404.
        var invokeInfo = context.InvokeInfo;
        if (invokeInfo == null)
        {
            return await next(context);
        }

        // [TrameAnonymous] → immer erlaubt, auch im RequireAuthentication-Modus.
        if (invokeInfo.AnonymousAttribute != null)
        {
            return await next(context);
        }

        var httpContext = context.HttpContext;
        var authenticated = httpContext?.User?.Identity?.IsAuthenticated ?? false;

        var authorise = invokeInfo.AuthoriseAttribute;

        // Unbestückte Methode im RequireAuthentication-Modus → IsAuthenticated verlangen.
        if (authorise == null)
        {
            if (_requireAuthentication && !authenticated)
                return UnauthorizedResponse();
            return await next(context);
        }

        // Explizite [TrameAuthorise]: IsAuthenticated prüfen (401 wenn nicht).
        if (!authenticated)
            return UnauthorizedResponse();

        // Role prüfen (403 wenn authentifiziert, aber Rolle verweigert).
        if (!string.IsNullOrEmpty(authorise.Role)
            && !(httpContext!.User.IsInRole(authorise.Role)))
        {
            return ForbiddenResponse();
        }

        // Policy prüfen (403 wenn authentifiziert, aber Policy verweigert).
        if (!string.IsNullOrEmpty(authorise.Policy))
        {
            if (_authorizationService == null)
            {
                _logger.LogError(
                    "Methode {Controller}.{Method} fordert Policy '{Policy}', aber IAuthorizationService ist nicht registriert. " +
                    "Registriere AddAuthorization() in der DI, um Policies zu nutzen.",
                    context.ControllerName, context.MethodName, authorise.Policy);
                return InternalServerErrorResponse(
                    $"Policy '{authorise.Policy}' angefordert, aber IAuthorizationService nicht registriert.");
            }

            var result = await _authorizationService.AuthorizeAsync(
                httpContext!.User, resource: null, policyName: authorise.Policy);

            if (!result.Succeeded)
                return ForbiddenResponse();
        }

        return await next(context);
    }

    private static TrameResponse UnauthorizedResponse() => TrameResults.Unauthorized();

    private static TrameResponse ForbiddenResponse() => TrameResults.Forbidden();

    private static TrameResponse InternalServerErrorResponse(string message)
        => TrameResults.Error(TrameErrorCodes.InternalServerError, message, TrameErrorCategory.Internal);
}