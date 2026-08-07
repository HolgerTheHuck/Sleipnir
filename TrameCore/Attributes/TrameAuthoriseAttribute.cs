using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace TrameCore.Attributes
{
    /// <summary>
    /// Authorize-Attribute für Trame. Prüft, ob der Nutzer authentifiziert ist
    /// und/oder eine bestimmte Rolle hat. Auf Methoden-Ebene anwendbar; auf
    /// Klassen-Ebene gilt es als Default für alle Methoden des Controllers
    /// (Methoden-Ebene gewinnt, falls vorhanden). Im North-Bound-Modus
    /// (<see cref="TrameHub.Extensions.TrameOptions.RequireAuthentication"/>=true)
    /// verlangen auch unbestückte Methoden einen authentifizierten User; mit
    /// <c>[TrameAnonymous]</c> lässt sich eine Methode explizit öffnen.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
    public class TrameAuthoriseAttribute : Attribute
    {
        /// <summary>
        /// Optionale Rolle, die ein User haben muss, damit der Aufruf erlaubt ist.
        /// </summary>
        public string? Role { get; set; }

        /// <summary>
        /// Optionale ASP.NET Core Authorization-Policy, die evaluiert wird (Phase 1).
        /// Wenn gesetzt, prüft der <c>TrameAuthorizationInterceptor</c> die Policy via
        /// <c>IAuthorizationService.AuthorizeAsync(user, resource: null, policy)</c>
        /// *zusätzlich* zu <see cref="Role"/>. Schlägt die Policy fehl, ist die Response
        /// <c>403 Forbidden</c> (PermissionDenied) — authentifiziert, aber nicht erlaubt.
        /// Schlägt die Authentifizierung fehl (nicht eingeloggt), bleibt es
        /// <c>401 Unauthorized</c> (Unauthenticated). Siehe <c>SECURITY.md</c> /
        /// <c>docs/design/phase-1-interceptor-pipeline.md</c>. <c>resource</c> ist in
        /// v1.1 <c>null</c> (command-orientiert, kein Resource-Begriff); ein
        /// <c>[TrameAuthorizeResource]</c>-Hook für resource-basierte Policies ist v1.x+.
        /// </summary>
        public string? Policy { get; set; }

        public TrameAuthoriseAttribute()
        {
        }

        public TrameAuthoriseAttribute(string role)
        {
            Role = role;
        }

        /// <summary>
        /// Die Methode wird im TrameService vor dem eigentlichen Methodenaufruf aufgerufen.
        /// Gibt true zurück, wenn authentifiziert UND (falls gesetzt) die Rolle erfüllt ist.
        /// Policy-Evaluation läuft *nicht* hier (die braucht IAuthorizationService, der
        /// im TrameAuthorizationInterceptor in TrameHub injiziert wird) — diese Methode
        /// prüft nur Role + IsAuthenticated. Policy = false (hier) heißt also *nicht*
        /// "verweigert", sondern "nicht hier geprüft" — der Interktor übernimmt sie.
        /// </summary>
        public virtual Task<bool> OnAuthorization(HttpContext? context)
        {
            // Ohne HttpContext keine Autorisierungsinfo -> verweigern
            if (context == null)
                return Task.FromResult(false);

            // Ist der User überhaupt authentifiziert?
            if (!context.User.Identity?.IsAuthenticated ?? false)
                return Task.FromResult(false);

            // Prüfen wir zusätzlich eine Rolle?
            if (!string.IsNullOrEmpty(Role))
            {
                // Falls der User nicht in dieser Rolle ist -> false
                if (!context.User.IsInRole(Role))
                {
                    return Task.FromResult(false);
                }
            }

            // Falls wir hier ankommen, hat der User alles erfüllt
            return Task.FromResult(true);
        }
    }
}
