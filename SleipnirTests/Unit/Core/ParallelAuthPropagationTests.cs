using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using SleipnirCommon.Models;
using SleipnirCore.Services;
using SleipnirTests.Fixtures;
using Xunit;

namespace SleipnirTests.Unit.Core;

/// <summary>
/// HttpContext-Nebenläufigkeit + Dependent-Propagierung.
///
/// Der Batch-Pfad berührt den HttpContext nur noch serial im Auth-Pre-Pass
/// (ResolveAndAuthorizeAsync); die parallele Ausführung (ExecuteAuthorized per
/// Task.WhenAll) greift nie concurrent darauf zu. Die Tests erfassen zwei
/// Verhaltenseigenschaften end-to-end über den SleipnirInvoker:
///
///  1. Per-Request-Batch-Semantik: ein nicht autorisierter Request im Parallel-Batch
///     wird 401, die anderen laufen weiter und succeedieren (JSON-RPC-konform).
///  2. Dependent-Propagierung im topologischen Pfad: Dependents eines fehlgeschlagenen
///     Providers laufen nicht, sondern bekommen eine erklärende 400 — und zwar transitiv
///     über die ganze Kette, ohne dass die Controller-Methoden aufgerufen werden.
///
/// Die Tests teilen sich die xUnit-Collection "auth-propagation" (serialisiert), weil
/// die Aufruf-Zähler an <see cref="AuthPropagationController"/> static sind und vor
/// jedem Test zurückgesetzt werden.
/// </summary>
[Collection("auth-propagation")]
public class ParallelAuthPropagationTests
{
    private readonly SleipnirInvoker _invoker;

    public ParallelAuthPropagationTests()
    {
        AuthPropagationController.ResetCounters();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTransient<AuthPropagationController>();
        services.AddTransient<DependencyChainController>();
        var sp = services.BuildServiceProvider();
        _invoker = new SleipnirInvoker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<SleipnirInvoker>>());
        _invoker.Register<AuthPropagationController>();
        _invoker.Register<DependencyChainController>();
    }

    /// <summary>Baut einen SleipnirRequest mit Id, benannten JSON-Parametern und optionalem
    ///  Provider-Expose (DependencyMapping). @alias-Consumer über Alias(...).</summary>
    private static SleipnirRequest Req(
        string id, string controller, string method,
        Dictionary<string, string>? mapping = null,
        params (string name, string jsonValue)[] parameters)
    {
        var paramList = parameters
            .Select(p => new SleipnirParameter
            {
                ParameterName = p.name,
                Data = p.jsonValue.StartsWith("@") ? JsonValue.Create(p.jsonValue) : JsonNode.Parse(p.jsonValue)
            })
            .ToList();
        return new SleipnirRequest
        {
            Id = id,
            Controller = controller,
            Method = method,
            Params = JsonSerializer.SerializeToNode(paramList),
            DependencyMapping = mapping,
        };
    }

    /// <summary>Consumer-Parameter, der einen @alias-Platzhalter trägt (roher Alias-Text).</summary>
    private static (string name, string jsonValue) Alias(string paramName, string alias) =>
        (paramName, $"@{alias}");

    /// <summary>HttpContext mit authentifiziertem User (optional Admin-Rolle), sodass
    ///  [SleipnirAuthorise] / [SleipnirAuthorise(Role="Admin")] passieren.</summary>
    private static HttpContext AuthenticatedContext(bool admin = false)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, "tester") };
        if (admin) claims.Add(new Claim(ClaimTypes.Role, "Admin"));
        var identity = new ClaimsIdentity(claims, "TestAuth");
        return new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
    }

    // === Parallel: Per-Request 401, andere succeedieren =========================

    [Fact]
    public async Task Parallel_MixedAuth_OnlyUnauthorizedRequestFailsOthersSucceed()
    {
        // Echo (kein Auth) + SecuredEcho (Auth, kein Context → 401) + Echo (kein Auth).
        var batch = new List<SleipnirRequest>
        {
            Req("r1", "AuthProp", "Echo", null, ("value", "\"a\"")),
            Req("r2", "AuthProp", "SecuredEcho", null, ("value", "\"x\"")),
            Req("r3", "AuthProp", "Echo", null, ("value", "\"b\"")),
        };

        var responses = (await _invoker.InvokeDi(batch, null, ExecutionMode.Parallel)).ToList();

        responses.Should().HaveCount(3);
        responses[0]!.Code.Should().Be((int)HttpStatusCode.OK);
        responses[0]!.Data.Value.Deserialize<string>().Should().Be("a");
        responses[1]!.Code.Should().Be((int)HttpStatusCode.Unauthorized);
        responses[2]!.Code.Should().Be((int)HttpStatusCode.OK);
        responses[2]!.Data.Value.Deserialize<string>().Should().Be("b");
        // SecuredEcho wird wegen 401 im Pre-Pass nicht ausgeführt.
        AuthPropagationController.SecuredCalls.Should().Be(0);
        AuthPropagationController.EchoCalls.Should().Be(2);
    }

    [Fact]
    public async Task Parallel_AuthorizedContext_AllSucceed()
    {
        var ctx = AuthenticatedContext();
        var batch = new List<SleipnirRequest>
        {
            Req("r1", "AuthProp", "Echo", null, ("value", "\"a\"")),
            Req("r2", "AuthProp", "SecuredEcho", null, ("value", "\"x\"")),
        };

        var responses = (await _invoker.InvokeDi(batch, ctx, ExecutionMode.Parallel)).ToList();

        responses[0]!.Code.Should().Be((int)HttpStatusCode.OK);
        responses[1]!.Code.Should().Be((int)HttpStatusCode.OK);
        responses[1]!.Data.Value.Deserialize<string>().Should().Be("x");
        AuthPropagationController.SecuredCalls.Should().Be(1);
    }

    // === Topologisch: Propagierung eines nicht-autorisierten Providers ===========

    [Fact]
    public async Task Topology_UnauthorizedProvider_DependentsPropagateAndAreNotInvoked()
    {
        // A: SecuredEcho (Auth, kein Context → 401), exposet "a" aus seinem Ergebnis.
        // B: Echo @a, exposet "b".
        // C: Echo @b.
        var batch = new List<SleipnirRequest>
        {
            Req("A", "AuthProp", "SecuredEcho", new() { ["a"] = "$" }, ("value", "\"prov\"")),
            Req("B", "AuthProp", "Echo", new() { ["b"] = "$" }, Alias("value", "a")),
            Req("C", "AuthProp", "Echo", null, Alias("value", "b")),
        };

        var responses = (await _invoker.InvokeDi(batch, null, ExecutionMode.Serial)).ToList();

        // A: 401.
        responses.Single(r => r?.Id == "A")!.Code.Should().Be((int)HttpStatusCode.Unauthorized);

        // B: 400 mit erklärender Propagierungsmeldung (Provider A unauthorized).
        var b = responses.Single(r => r?.Id == "B")!;
        b.Code.Should().Be((int)HttpStatusCode.BadRequest);
        b.Error!.Message.Should().Contain("unauthorized (401)");
        b.Error.Message.Should().Contain("provider 'A'");

        // C: 400 — B wurde übersprungen (Code 400, kein "b" exportiert) → transitiv.
        var c = responses.Single(r => r?.Id == "C")!;
        c.Code.Should().Be((int)HttpStatusCode.BadRequest);
        c.Error!.Message.Should().Contain("provider 'B'");

        // Keine der Methoden wurde ausgeführt — A schlug im Auth-Pre-Pass fehl,
        // B und C wurden vor der Ausführung als unavailable erkannt.
        AuthPropagationController.SecuredCalls.Should().Be(0);
        AuthPropagationController.EchoCalls.Should().Be(0);
    }

    // === Topologisch: Provider liefert, exposet den Alias aber nicht ==============

    [Fact]
    public async Task Topology_ProviderDidNotExpose_DependentGetsDidNotExpose()
    {
        // A: MakeDto(5) succeediert, aber der Expose-Pfad "$.nonexistent" matcht nichts
        //    → "a" landet nicht in ExposedDependencies.
        // B: EchoInt @a → sollte "did not expose '@a'" bekommen.
        var batch = new List<SleipnirRequest>
        {
            Req("A", "DepChain", "MakeDto", new() { ["a"] = "$.nonexistent" },
                ("id", "5"), ("name", JsonSerializer.Serialize("x"))),
            Req("B", "DepChain", "EchoInt", null, Alias("value", "a")),
        };

        var responses = (await _invoker.InvokeDi(batch, null, ExecutionMode.Serial)).ToList();

        responses.Single(r => r?.Id == "A")!.Code.Should().Be((int)HttpStatusCode.OK);
        var b = responses.Single(r => r?.Id == "B")!;
        b.Code.Should().Be((int)HttpStatusCode.BadRequest);
        b.Error!.Message.Should().Contain("did not expose '@a'");
        b.Error.Message.Should().Contain("provider 'A'");
    }

    // === Topologisch: Provider-Fehler mit non-null Data exposet nichts ===========

    [Fact]
    public async Task Topology_ProviderErrorWithData_ExposesNothing_DependentGetsHttpCode()
    {
        // A: FailWithProblem(422) liefert einen non-2xx-Fehler IM ProblemDetails-Stil —
        //    Data ist non-null ({ title:"Invalid", status:422, detail:"bad input" }),
        //    und der Expose-Pfad "$.title" träfe. Ohne Status-Gate würde die Extraktion
        //    "Invalid" aus dem Fehler-Payload als ExposedDependency "t" liefern (Datenleck
        //    aus dem Fehler-Payload). Mit Gate: ExposedDependencies bleibt leer.
        // B: EchoInt @t → bekommt die Propagierung "returned HTTP 422" (kein Wert aus
        //    dem Fehler-Payload, kein "did not expose", weil der Provider-Status nicht 2xx).
        var batch = new List<SleipnirRequest>
        {
            Req("A", "DepChain", "FailWithProblem", new() { ["t"] = "$.title" },
                ("status", "422")),
            Req("B", "DepChain", "EchoInt", null, Alias("value", "t")),
        };

        var responses = (await _invoker.InvokeDi(batch, null, ExecutionMode.Serial)).ToList();

        var a = responses.Single(r => r?.Id == "A")!;
        a.Code.Should().Be(422);
        // Der Fix: aus einem Fehler-Response darf nichts exposet werden — selbst dann
        // nicht, wenn Data existiert und der JsonPath treffen würde.
        a.ExposedDependencies.Should().BeNullOrEmpty();

        var b = responses.Single(r => r?.Id == "B")!;
        b.Code.Should().Be((int)HttpStatusCode.BadRequest);
        b.Error!.Message.Should().Contain("returned HTTP 422");
        b.Error.Message.Should().Contain("provider 'A'");
    }

    // === Topologisch: Dangling-Alias (kein Provider für den Alias) ===============

    [Fact]
    public async Task Topology_NoProviderForAlias_DependentGetsNoProviderExposes()
    {
        // A: MakeDto exposet "a" (hält den Batch topologisch).
        // B: EchoInt @ghost — für "ghost" gibt es keinen Provider.
        var batch = new List<SleipnirRequest>
        {
            Req("A", "DepChain", "MakeDto", new() { ["a"] = "$.id" },
                ("id", "5"), ("name", JsonSerializer.Serialize("x"))),
            Req("B", "DepChain", "EchoInt", null, Alias("value", "ghost")),
        };

        var responses = (await _invoker.InvokeDi(batch, null, ExecutionMode.Serial)).ToList();

        responses.Single(r => r?.Id == "A")!.Code.Should().Be((int)HttpStatusCode.OK);
        var b = responses.Single(r => r?.Id == "B")!;
        b.Code.Should().Be((int)HttpStatusCode.BadRequest);
        b.Error!.Message.Should().Contain("no provider exposes '@ghost'");
    }

    // === Topologisch: Happy-Path-Regression (Kette succeediert weiterhin) =========

    [Fact]
    public async Task Topology_HappyPath_ChainStillSucceeds()
    {
        var batch = new List<SleipnirRequest>
        {
            Req("A", "DepChain", "MakeDto", new() { ["a"] = "$.id" },
                ("id", "7"), ("name", JsonSerializer.Serialize("alice"))),
            Req("B", "DepChain", "EchoInt", null, Alias("value", "a")),
        };

        var responses = (await _invoker.InvokeDi(batch, null, ExecutionMode.Serial)).ToList();

        responses.Single(r => r?.Id == "A")!.Code.Should().Be((int)HttpStatusCode.OK);
        var b = responses.Single(r => r?.Id == "B")!;
        b.Code.Should().Be((int)HttpStatusCode.OK);
        b.Data.Value.Deserialize<int>().Should().Be(7);
    }

    // === Topologisch: autorisierter Provider → Kette läuft durch =================

    [Fact]
    public async Task Topology_AuthorizedProvider_ChainSucceeds()
    {
        var ctx = AuthenticatedContext();
        var batch = new List<SleipnirRequest>
        {
            Req("A", "AuthProp", "SecuredEcho", new() { ["a"] = "$" }, ("value", "\"prov\"")),
            Req("B", "AuthProp", "Echo", null, Alias("value", "a")),
        };

        var responses = (await _invoker.InvokeDi(batch, ctx, ExecutionMode.Serial)).ToList();

        responses.Single(r => r?.Id == "A")!.Code.Should().Be((int)HttpStatusCode.OK);
        var b = responses.Single(r => r?.Id == "B")!;
        b.Code.Should().Be((int)HttpStatusCode.OK);
        b.Data.Value.Deserialize<string>().Should().Be("prov");
        AuthPropagationController.SecuredCalls.Should().Be(1);
        AuthPropagationController.EchoCalls.Should().Be(1);
    }
}

/// <summary>xUnit-Collection, die die auth-propagation-Tests serialisiert (static Counter).</summary>
[CollectionDefinition("auth-propagation")]
public class AuthPropagationCollection { }