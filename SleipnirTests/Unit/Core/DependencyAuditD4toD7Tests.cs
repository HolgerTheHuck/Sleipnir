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
/// D4–D7 from the dependency-chaining audit (2026-08-22):
///
/// D4 — serial path authorizes BEFORE alias resolution (parity with the topological
///      path): an unauthorized request with an unresolvable alias gets 401, not 400.
/// D5 — Strict/Paranoid binding honors STJ metadata: [JsonIgnore] properties are not
///      required; [JsonPropertyName] renames are compared under the wire name.
/// D6 — extraction failures surface as "failed to extract '@a' (&lt;reason&gt;)" instead
///      of the misleading "did not expose".
/// D7 — self-dependency is rejected at graph build with a specific message.
/// </summary>
public class DependencyAuditD4toD7Tests
{
    private readonly SleipnirInvoker _invoker;

    public DependencyAuditD4toD7Tests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTransient<DependencyChainController>();
        services.AddTransient<AuthPropagationController>();
        var sp = services.BuildServiceProvider();
        _invoker = new SleipnirInvoker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<SleipnirInvoker>>());
        _invoker.Register<DependencyChainController>();
        _invoker.Register<AuthPropagationController>();
    }

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

    private static (string name, string jsonValue) Alias(string paramName, string alias) =>
        (paramName, $"@{alias}");

    /// <summary>HttpContext mit authentifiziertem User (optional Admin-Rolle).</summary>
    private static HttpContext AuthenticatedContext(bool admin = false)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, "tester") };
        if (admin) claims.Add(new Claim(ClaimTypes.Role, "Admin"));
        var identity = new ClaimsIdentity(claims, "TestAuth");
        return new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
    }

    // === D4: Serial-Pfad — Auth vor Auflösung ===================================

    [Fact]
    public async Task D4_Serial_UnauthorizedWithUnresolvableAlias_Gets401Not400()
    {
        // Kein Context → SecuredEcho ist unauthorized. Vor D4 lief die Alias-Auflösung
        // zuerst und meldete 400 "Unresolved dependencies" statt der verdienten 401.
        var request = Req("r", "AuthProp", "SecuredEcho", null, Alias("value", "missing"));

        var responses = (await _invoker.InvokeDi(
            new List<SleipnirRequest> { request }, null, ExecutionMode.Serial)).ToList();

        responses[0]!.Code.Should().Be((int)HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task D4_Serial_AuthorizedRequest_StillResolvesAlias()
    {
        // Regression-Guard: die Reihenfolge-Änderung darf legale Serial-Ketten nicht brechen.
        var provider = Req("p", "DepChain", "MakeDto",
            mapping: new() { ["cid"] = "$.id" }, ("id", "5"), ("name", "\"e\""));
        var consumer = Req("c", "DepChain", "EchoInt", null, Alias("value", "cid"));

        var responses = (await _invoker.InvokeDi(
            new List<SleipnirRequest> { provider, consumer }, null, ExecutionMode.Serial)).ToList();

        responses.Should().OnlyContain(r => r!.Code == (int)HttpStatusCode.OK);
        responses.Single(r => r!.Id == "c")!.Data.Value.Deserialize<int>().Should().Be(5);
    }

    [Fact]
    public async Task D4_Serial_AuthorizedWithUnresolvableAlias_StillGets400()
    {
        // Autorisiert + unversorgter Alias → weiterhin die saubere 400.
        var request = Req("r", "DepChain", "EchoInt", null, Alias("value", "missing"));

        var responses = (await _invoker.InvokeDi(
            new List<SleipnirRequest> { request }, null, ExecutionMode.Serial)).ToList();

        responses[0]!.Code.Should().Be((int)HttpStatusCode.BadRequest);
        responses[0]!.Error!.Message.Should().Contain("Unresolved dependencies");
    }

    // === D5: Strict/Paranoid ehren STJ-Metadaten ================================

    [Fact]
    public async Task D5_Strict_JsonIgnoreProperty_NotRequired()
    {
        // WireDto hat eine [JsonIgnore]-Property — das Fragment ohne sie muss im Strict-
        // Modus binden (vor D5: falsch-positiver 400).
        var provider = Req("p", "DepChain", "MakeWireDto",
            mapping: new() { ["w"] = "$" }, ("id", "1"), ("name", "\"x\""));
        var consumer = Req("c", "DepChain", "TakeWireDto", null, Alias("d", "w"));

        _invoker.AliasBindingMode = AliasBindingMode.Strict;
        var responses = (await _invoker.InvokeDi(
            new List<SleipnirRequest> { provider, consumer }, null, ExecutionMode.Parallel)).ToList();

        responses.Single(r => r!.Id == "c")!.Code.Should().Be((int)HttpStatusCode.OK);
    }

    [Fact]
    public async Task D5_Strict_JsonPropertyNameRenamed_WireNameSatisfies()
    {
        // RenamedDto.Id trägt [JsonPropertyName("ref")] — das Fragment muss den WIRE-Namen
        // "ref" liefern (vor D5 wurde der CLR-Name "Id" verglichen → falsch-negativ/positiv).
        var provider = Req("p", "DepChain", "MakeRenamedDto",
            mapping: new() { ["r"] = "$" }, ("id", "7"));
        var consumer = Req("c", "DepChain", "TakeRenamedDto", null, Alias("d", "r"));

        _invoker.AliasBindingMode = AliasBindingMode.Strict;
        var responses = (await _invoker.InvokeDi(
            new List<SleipnirRequest> { provider, consumer }, null, ExecutionMode.Parallel)).ToList();

        responses.Single(r => r!.Id == "c")!.Code.Should().Be((int)HttpStatusCode.OK);
        responses.Single(r => r!.Id == "c")!.Data.Value.Deserialize<int>().Should().Be(7);
    }

    [Fact]
    public async Task D5_Strict_MissingNonIgnoredProperty_StillRejected()
    {
        // Regression-Guard: Strict bleibt scharf für Properties, die wirklich gebunden werden.
        var provider = Req("p", "DepChain", "MakeIdOnly",
            mapping: new() { ["i"] = "$" }, ("id", "1"));
        var consumer = Req("c", "DepChain", "TakeWireDto", null, Alias("d", "i"));

        _invoker.AliasBindingMode = AliasBindingMode.Strict;
        var responses = (await _invoker.InvokeDi(
            new List<SleipnirRequest> { provider, consumer }, null, ExecutionMode.Parallel)).ToList();

        var consumerResponse = responses.Single(r => r!.Id == "c")!;
        consumerResponse.Code.Should().Be((int)HttpStatusCode.BadRequest);
        consumerResponse.Error!.Message.Should().Contain("Strict alias binding");
    }

    // === D6: Extraktions-Fehlerdiagnose =========================================

    [Fact]
    public async Task D6_ExtractionFailure_NamesRealCauseInsteadOfDidNotExpose()
    {
        // Der Provider exposet einen ungültigen JsonPath → Extraktion schlägt fehl. Der
        // Dependendent bekommt jetzt "failed to extract '@a' (invalid JsonPath)" statt
        // des irreführenden "did not expose '@a'".
        var provider = Req("p", "DepChain", "MakeDto",
            mapping: new() { ["bad"] = "$.nonexistent[" }, ("id", "1"), ("name", "\"x\""));
        var consumer = Req("c", "DepChain", "EchoInt", null, Alias("value", "bad"));

        var responses = (await _invoker.InvokeDi(
            new List<SleipnirRequest> { provider, consumer }, null, ExecutionMode.Parallel)).ToList();

        var consumerResponse = responses.Single(r => r!.Id == "c")!;
        consumerResponse.Code.Should().Be((int)HttpStatusCode.BadRequest);
        consumerResponse.Error!.Message.Should().Contain("failed to extract '@bad'");
        consumerResponse.Error!.Message.Should().NotContain("did not expose");
    }

    [Fact]
    public async Task D6_PathMatchesNothing_KeepsDidNotExposeMessage()
    {
        // Ein gültiger Pfad, der nichts matcht, ist KEIN Extraktions-Fehler — die bisherige
        // "did not expose"-Meldung bleibt korrekt.
        var provider = Req("p", "DepChain", "MakeDto",
            mapping: new() { ["gone"] = "$.nonexistent" }, ("id", "1"), ("name", "\"x\""));
        var consumer = Req("c", "DepChain", "EchoInt", null, Alias("value", "gone"));

        var responses = (await _invoker.InvokeDi(
            new List<SleipnirRequest> { provider, consumer }, null, ExecutionMode.Parallel)).ToList();

        var consumerResponse = responses.Single(r => r!.Id == "c")!;
        consumerResponse.Code.Should().Be((int)HttpStatusCode.BadRequest);
        consumerResponse.Error!.Message.Should().Contain("did not expose '@gone'");
    }

    // === D7: Self-Dependency wird am Graphen abgelehnt ==========================

    [Fact]
    public async Task D7_SelfDependency_RejectedWithSpecificMessage()
    {
        // Ein Request, der seinen eigenen Alias konsumiert, ist immer ein Konfigurationsfehler —
        // jetzt fail-loud am Graphen mit spezifischer Meldung statt Laufzeit-Unresolved.
        var request = Req("r", "DepChain", "EchoInt",
            mapping: new() { ["self"] = "$.id" }, Alias("value", "self"));

        var responses = (await _invoker.InvokeDi(
            new List<SleipnirRequest> { request }, null, ExecutionMode.Parallel)).ToList();

        responses[0]!.Code.Should().Be((int)HttpStatusCode.BadRequest);
        responses[0]!.Error!.Message.Should().Contain("depends on its own alias '@self'");
    }
}

