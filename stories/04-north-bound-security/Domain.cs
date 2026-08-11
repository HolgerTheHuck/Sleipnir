using SleipnirCore.Attributes;

namespace SleipnirStories.Story04;

// === Story 04 Domain — "North-bound Security" =====================================
// Eine kleine Domain, die jede Bestückungs-Variante der Auth-Postur-Matrix zeigt:
//   - Health       : [SleipnirAnonymous]  → auch unauth erreichbar (Health/Ping-Pattern)
//   - PublicEcho   : unbestückt         → RequireAuthentication=true ⇒ 401 unauth
//   - SecretEcho   : [SleipnirAuthorise]   → verlangt Auth (Rolle egal)
//   - AdminEcho    : [SleipnirAuthorise(Role="Admin")] → verlangt Auth + Admin-Rolle
//   - (Klasse SecuredAll : Klassen-Level-[SleipnirAuthorise] schützt alle Methoden)

public sealed class Echo
{
    public string Text { get; set; } = "";
    public string Caller { get; set; } = "";
}

[SleipnirController("Health")]
public class HealthController
{
    // Bewusst öffentlich — auch im North-Bound-Default-Deny. Typisches Health/Ping-Pattern:
    // ein Load-Balancer-Probe ohne Token. [SleipnirAnonymous] optiert aus RequireAuthentication aus.
    [SleipnirMethod("Ping")]
    [SleipnirAnonymous]
    public string Ping() => "pong";
}

[SleipnirController("Echo")]
public class EchoController
{
    // Unbestückt — im South-Bound-Default erlaubt; mit RequireAuthentication=true ⇒ 401 unauth.
    [SleipnirMethod("PublicEcho")]
    public Echo PublicEcho(string text) => new() { Text = text, Caller = "public" };

    // Verlangt Auth (Rolle egal). [SleipnirAuthorise] greift unabhängig vom RequireAuthentication-Toggle.
    [SleipnirMethod("SecretEcho")]
    [SleipnirAuthorise]
    public Echo SecretEcho(string text) => new() { Text = text, Caller = "secret" };

    // Verlangt Auth + Admin-Rolle. sleipnir-demo → 401; sleipnir-admin → 200.
    [SleipnirMethod("AdminEcho")]
    [SleipnirAuthorise(Role = "Admin")]
    public Echo AdminEcho(string text) => new() { Text = text, Caller = "admin" };
}

// Klassen-Level-[SleipnirAuthorise] gilt als Default für alle Methoden des Controllers.
// North-Bound-Nutzung: ein bestückter Controller schützt alles; [SleipnirAnonymous] öffnet gezielt.
[SleipnirController("SecuredAll")]
[SleipnirAuthorise]
public class SecuredAllController
{
    [SleipnirMethod("Locked")]
    public string Locked() => "only-auth";

    // Methoden-Level-Opt-out schlägt den Klassen-Default.
    [SleipnirMethod("Opened")]
    [SleipnirAnonymous]
    public string Opened() => "anyone";
}