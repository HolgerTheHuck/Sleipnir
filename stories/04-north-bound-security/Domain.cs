using TrameCore.Attributes;

namespace TrameStories.Story04;

// === Story 04 Domain — "North-bound Security" =====================================
// Eine kleine Domain, die jede Bestückungs-Variante der Auth-Postur-Matrix zeigt:
//   - Health       : [TrameAnonymous]  → auch unauth erreichbar (Health/Ping-Pattern)
//   - PublicEcho   : unbestückt         → RequireAuthentication=true ⇒ 401 unauth
//   - SecretEcho   : [TrameAuthorise]   → verlangt Auth (Rolle egal)
//   - AdminEcho    : [TrameAuthorise(Role="Admin")] → verlangt Auth + Admin-Rolle
//   - (Klasse SecuredAll : Klassen-Level-[TrameAuthorise] schützt alle Methoden)

public sealed class Echo
{
    public string Text { get; set; } = "";
    public string Caller { get; set; } = "";
}

[TrameController("Health")]
public class HealthController
{
    // Bewusst öffentlich — auch im North-Bound-Default-Deny. Typisches Health/Ping-Pattern:
    // ein Load-Balancer-Probe ohne Token. [TrameAnonymous] optiert aus RequireAuthentication aus.
    [TrameMethod("Ping")]
    [TrameAnonymous]
    public string Ping() => "pong";
}

[TrameController("Echo")]
public class EchoController
{
    // Unbestückt — im South-Bound-Default erlaubt; mit RequireAuthentication=true ⇒ 401 unauth.
    [TrameMethod("PublicEcho")]
    public Echo PublicEcho(string text) => new() { Text = text, Caller = "public" };

    // Verlangt Auth (Rolle egal). [TrameAuthorise] greift unabhängig vom RequireAuthentication-Toggle.
    [TrameMethod("SecretEcho")]
    [TrameAuthorise]
    public Echo SecretEcho(string text) => new() { Text = text, Caller = "secret" };

    // Verlangt Auth + Admin-Rolle. trame-demo → 401; trame-admin → 200.
    [TrameMethod("AdminEcho")]
    [TrameAuthorise(Role = "Admin")]
    public Echo AdminEcho(string text) => new() { Text = text, Caller = "admin" };
}

// Klassen-Level-[TrameAuthorise] gilt als Default für alle Methoden des Controllers.
// North-Bound-Nutzung: ein bestückter Controller schützt alles; [TrameAnonymous] öffnet gezielt.
[TrameController("SecuredAll")]
[TrameAuthorise]
public class SecuredAllController
{
    [TrameMethod("Locked")]
    public string Locked() => "only-auth";

    // Methoden-Level-Opt-out schlägt den Klassen-Default.
    [TrameMethod("Opened")]
    [TrameAnonymous]
    public string Opened() => "anyone";
}