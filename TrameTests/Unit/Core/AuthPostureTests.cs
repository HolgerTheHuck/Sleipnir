using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using TrameCommon.Models;
using TrameCore.Services;
using TrameTests.Fixtures;
using Xunit;

namespace TrameTests.Unit.Core;

/// <summary>
/// Auth-Postur-Matrix für den North-Bound-Default-Deny-Modus
/// (<see cref="TrameInvoker.RequireAuthentication"/>=true). Bis v1 war RequireAuthentication
/// tote Option; jetzt ist es ein lebendiger Toggle, der unbestückte Methoden hinter Auth
/// legt, während <c>[TrameAuthorise]</c>/<c>[TrameAuthorise(Role=…)]</c> wie bisher greifen
/// und <c>[TrameAnonymous]</c> gezielt öffnet. Defense-in-Depth: die per-Method-Entscheidung
/// fällt im Invoker (<c>CheckAuthorisation</c>), nicht im Transport-Endpoint — dadurch
/// bleibt das <c>[TrameAnonymous]</c>-Opt-out intakt (ein REST-Endpoint-weites
/// <c>RequireAuthorization</c> würde unauth schon vor dem Invoker blockieren).
/// </summary>
public class AuthPostureTests
{
    private readonly TrameInvoker _invoker;

    public AuthPostureTests()
    {
        AuthPostureController.ResetCounters();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTransient<AuthPostureController>();
        services.AddTransient<AuthPostureClassLevelController>();
        var sp = services.BuildServiceProvider();
        _invoker = new TrameInvoker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<TrameInvoker>>());
        _invoker.Register<AuthPostureController>();
        _invoker.Register<AuthPostureClassLevelController>();
    }

    private static TrameRequest Req(string method) => new()
    {
        Controller = "AuthPosture",
        Method = method,
        Params = JsonNode.Parse("[]"),
        Id = method
    };

    private static TrameRequest ReqClass(string method) => new()
    {
        Controller = "AuthPostureClass",
        Method = method,
        Params = JsonNode.Parse("[]"),
        Id = method
    };

    /// <summary>HttpContext mit authentifiziertem User (optional Admin-Rolle).</summary>
    private static HttpContext AuthenticatedContext(bool admin = false)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, "tester") };
        if (admin) claims.Add(new Claim(ClaimTypes.Role, "Admin"));
        var identity = new ClaimsIdentity(claims, "TestAuth");
        return new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
    }

    private static async Task<HttpStatusCode> Invoke(TrameInvoker invoker, TrameRequest req, HttpContext? ctx)
    {
        var resp = await invoker.InvokeDi(req, ctx);
        return (HttpStatusCode)resp!.Code;
    }

    // === South-Bound-Default: RequireAuthentication=false (heutiges Verhalten) ============

    [Fact]
    public async Task Off_Undecorated_AllowsUnauthenticated()
    {
        _invoker.RequireAuthentication = false;
        (await Invoke(_invoker, Req("Open"), null)).Should().Be(HttpStatusCode.OK);
        AuthPostureController.OpenCalls.Should().Be(1);
    }

    [Fact]
    public async Task Off_TrampedAuthorise_StillRequiresAuthentication()
    {
        _invoker.RequireAuthentication = false;
        // [TrameAuthorise] greift unabhängig vom Toggle — unauth bleibt 401.
        (await Invoke(_invoker, Req("Locked"), null)).Should().Be(HttpStatusCode.Unauthorized);
        (await Invoke(_invoker, Req("Locked"), AuthenticatedContext())).Should().Be(HttpStatusCode.OK);
        AuthPostureController.LockedCalls.Should().Be(1);
    }

    [Fact]
    public async Task Off_Anonymous_IsNoop_WhenToggleOff()
    {
        _invoker.RequireAuthentication = false;
        // [TrameAnonymous] ohne Toggle ändert nichts — Methode ist ohnehin default-allow.
        (await Invoke(_invoker, Req("Public"), null)).Should().Be(HttpStatusCode.OK);
        AuthPostureController.PublicCalls.Should().Be(1);
    }

    // === North-Bound-Default-Deny: RequireAuthentication=true ===========================

    [Fact]
    public async Task On_Undecorated_DeniesUnauthenticated()
    {
        _invoker.RequireAuthentication = true;
        // Der zentrale Neueffekt: eine unbestückte Methode wird hinter Auth gelegt.
        (await Invoke(_invoker, Req("Open"), null)).Should().Be(HttpStatusCode.Unauthorized);
        AuthPostureController.OpenCalls.Should().Be(0);
    }

    [Fact]
    public async Task On_Undecorated_AllowsAuthenticated()
    {
        _invoker.RequireAuthentication = true;
        (await Invoke(_invoker, Req("Open"), AuthenticatedContext())).Should().Be(HttpStatusCode.OK);
        AuthPostureController.OpenCalls.Should().Be(1);
    }

    [Fact]
    public async Task On_TrampedAuthorise_RequiresAuthenticationAndRole()
    {
        _invoker.RequireAuthentication = true;
        (await Invoke(_invoker, Req("Locked"), null)).Should().Be(HttpStatusCode.Unauthorized);
        (await Invoke(_invoker, Req("Locked"), AuthenticatedContext())).Should().Be(HttpStatusCode.OK);
        // AdminOnly: auth ohne Admin-Rolle → 401 (OnAuthorization gibt false).
        (await Invoke(_invoker, Req("AdminOnly"), AuthenticatedContext(admin: false)))
            .Should().Be(HttpStatusCode.Unauthorized);
        (await Invoke(_invoker, Req("AdminOnly"), AuthenticatedContext(admin: true)))
            .Should().Be(HttpStatusCode.OK);
        AuthPostureController.LockedCalls.Should().Be(1);
        AuthPostureController.AdminCalls.Should().Be(1);
    }

    [Fact]
    public async Task On_Anonymous_OptOut_AllowsUnauthenticated()
    {
        _invoker.RequireAuthentication = true;
        // [TrameAnonymous] öffnet gezielt — selbst im Default-Deny bleibt Public erreichbar.
        (await Invoke(_invoker, Req("Public"), null)).Should().Be(HttpStatusCode.OK);
        (await Invoke(_invoker, Req("Public"), AuthenticatedContext())).Should().Be(HttpStatusCode.OK);
        AuthPostureController.PublicCalls.Should().Be(2);
    }

    // === Klassen-Level-[TrameAuthorise] vererbt sich ====================================

    [Fact]
    public async Task On_ClassLevelAuthorise_ProtectsInheritedMethod()
    {
        _invoker.RequireAuthentication = true;
        // Klassen-Default schützt auch die unbestückte Methode.
        (await Invoke(_invoker, ReqClass("Inherited"), null)).Should().Be(HttpStatusCode.Unauthorized);
        (await Invoke(_invoker, ReqClass("Inherited"), AuthenticatedContext()))
            .Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task On_ClassLevelAuthorise_MethodAnonymousOptOutWins()
    {
        _invoker.RequireAuthentication = true;
        // Methoden-Level-[TrameAnonymous] schlägt den Klassen-[TrameAuthorise]-Default.
        (await Invoke(_invoker, ReqClass("Opened"), null)).Should().Be(HttpStatusCode.OK);
    }

    // === Batch: Default-Deny ist per-Request, nicht per-Batch ===========================

    [Fact]
    public async Task On_Batch_PerRequestDeny_AnonymousStillOpen_OthersRun()
    {
        _invoker.RequireAuthentication = true;
        var batch = new List<TrameRequest>
        {
            Req("Open"),     // unauth → 401
            Req("Public"),    // [TrameAnonymous] → 200 auch unauth
            Req("Locked"),    // unauth → 401
        };
        var responses = (await _invoker.InvokeDi(batch, null, ExecutionMode.Parallel)).ToList();
        responses.Should().HaveCount(3);
        responses[0]!.Code.Should().Be((int)HttpStatusCode.Unauthorized);
        responses[1]!.Code.Should().Be((int)HttpStatusCode.OK);
        responses[1]!.Data.Value.Deserialize<string>().Should().Be("public");
        responses[2]!.Code.Should().Be((int)HttpStatusCode.Unauthorized);
        // Open und Locked wurden im Auth-Pre-Pass abgewiesen, nicht ausgeführt.
        AuthPostureController.OpenCalls.Should().Be(0);
        AuthPostureController.LockedCalls.Should().Be(0);
        AuthPostureController.PublicCalls.Should().Be(1);
    }
}