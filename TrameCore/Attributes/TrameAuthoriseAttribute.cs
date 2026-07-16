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

        public TrameAuthoriseAttribute()
        {
        }

        public TrameAuthoriseAttribute(string role)
        {
            Role = role;
        }

        /// <summary>
        /// Die Methode wird im TrameService vor dem eigentlichen Methodenaufruf aufgerufen.
        /// Gibt true zurück, wenn autorisiert; false sonst.
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
