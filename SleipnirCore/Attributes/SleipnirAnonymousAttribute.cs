using System;

namespace SleipnirCore.Attributes
{
    /// <summary>
    /// Opt-out von <see cref="SleipnirHub.Extensions.SleipnirOptions.RequireAuthentication"/>.
    /// Eine mit <c>[SleipnirAnonymous]</c> markierte Methode bleibt auch dann
    /// aufrufbar, wenn der Server im North-Bound-Default-Deny-Modus läuft
    /// (<c>RequireAuthentication=true</c>) — typischerweise für Health/Ping oder
    /// andere bewusst öffentliche Endpunkte. Ohne <c>RequireAuthentication</c>
    /// hat das Attribut keine Wirkung (der South-Bound-Default ist ohnehin
    /// default-allow). Greift nur auf dem REST-Transport, wo jeder Request
    /// einzeln durch den Invoker gate; WebSocket- und SignalR-Verbindungen
    /// authentifizieren auf Verbindungsebene (Upgrade/Hub), dort gibt es kein
    /// per-Method-Opt-out. Siehe <c>SECURITY.md</c>.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public class SleipnirAnonymousAttribute : Attribute
    {
    }
}