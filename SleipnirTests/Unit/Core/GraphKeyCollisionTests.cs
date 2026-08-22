using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using SleipnirCommon.Models;
using SleipnirCore.Services;
using SleipnirTests.Fixtures;
using Xunit;

namespace SleipnirTests.Unit.Core;

/// <summary>
/// GraphKey-collision gate (audit 2026-08-22, D3 / finding F3).
///
/// The graph key of a request is its id, falling back to "Controller.Method" for id-less
/// requests. Collisions silently corrupted alias resolution before D3:
/// - two id-less requests on the same route: the graph builder's requestById overwrites,
///   priorResponses takes the last write, ExecuteSequentially's TryAdd fails for the second;
/// - a request id that happens to equal another request's Controller.Method fallback
///   collides the same way — an alias could bind to the WRONG provider's response.
///
/// Contract now: a batch whose requests resolve to duplicate graph keys is rejected at
/// batch entry with per-request 400s for EVERY request (fail-loud, same style as D1).
/// Same route twice WITH distinct ids remains legal.
/// </summary>
public class GraphKeyCollisionTests
{
    private readonly SleipnirInvoker _invoker;

    public GraphKeyCollisionTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTransient<DependencyChainController>();
        var sp = services.BuildServiceProvider();
        _invoker = new SleipnirInvoker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<SleipnirInvoker>>());
        _invoker.Register<DependencyChainController>();
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

    // === Kollisionen → alle Requests bekommen 400 ==============================

    [Fact]
    public async Task DuplicateIds_AllRequestsGet400()
    {
        var a1 = Req("dup", "DepChain", "MakeDto",
            mapping: new() { ["a"] = "$.id" }, ("id", "1"), ("name", "\"x\""));
        var a2 = Req("dup", "DepChain", "MakeIdOnly",
            mapping: new() { ["b"] = "$.id" }, ("id", "2"));

        var responses = (await _invoker.InvokeDi(
            new List<SleipnirRequest> { a1, a2 }, null, ExecutionMode.Parallel)).ToList();

        responses.Should().HaveCount(2);
        responses.Should().OnlyContain(r => r!.Code == (int)HttpStatusCode.BadRequest);
        responses[0]!.Error!.Message.Should().Contain("Duplicate request key 'dup'");
        responses[0]!.Error!.Message.Should().Contain("unique id");
    }

    [Fact]
    public async Task IdlessRequests_SameRoute_Collide()
    {
        // Zwei id-lose Requests auf DepChain.MakeDto → beide fallen auf "DepChain.MakeDto".
        var r1 = Req("", "DepChain", "MakeDto", null, ("id", "1"), ("name", "\"x\""));
        var r2 = Req("", "DepChain", "MakeDto", null, ("id", "2"), ("name", "\"y\""));

        var responses = (await _invoker.InvokeDi(
            new List<SleipnirRequest> { r1, r2 }, null, ExecutionMode.Parallel)).ToList();

        responses.Should().HaveCount(2);
        responses.Should().OnlyContain(r => r!.Code == (int)HttpStatusCode.BadRequest);
        responses[0]!.Error!.Message.Should().Contain("Duplicate request key 'DepChain.MakeDto'");
    }

    [Fact]
    public async Task IdMatchingOtherRouteFallback_Collides()
    {
        // Subtiler Fall: die Id "DepChain.MakeDto" des ersten Requests kollidiert mit dem
        // Fallback-Key des id-losen zweiten Requests auf dieselbe Route.
        var withId = Req("DepChain.MakeDto", "DepChain", "EchoInt", null, ("value", "5"));
        var idless = Req("", "DepChain", "MakeDto", null, ("id", "7"), ("name", "\"x\""));

        var responses = (await _invoker.InvokeDi(
            new List<SleipnirRequest> { withId, idless }, null, ExecutionMode.Parallel)).ToList();

        responses.Should().HaveCount(2);
        responses.Should().OnlyContain(r => r!.Code == (int)HttpStatusCode.BadRequest);
        responses[0]!.Error!.Message.Should().Contain("Duplicate request key 'DepChain.MakeDto'");
    }

    [Fact]
    public async Task Collision_InSerialPath_AlsoRejected()
    {
        // Der Serial-Pfad keyed seine @alias-Auflösung über dieselbe Id-Logik
        // (responses.TryAdd schlägt bei Duplikaten still fehl) — gleiche Validierung.
        var a1 = Req("dup", "DepChain", "MakeDto", null, ("id", "1"), ("name", "\"x\""));
        var a2 = Req("dup", "DepChain", "EchoInt", null, ("value", "2"));

        var responses = (await _invoker.InvokeDi(
            new List<SleipnirRequest> { a1, a2 }, null, ExecutionMode.Serial)).ToList();

        responses.Should().HaveCount(2);
        responses.Should().OnlyContain(r => r!.Code == (int)HttpStatusCode.BadRequest);
    }

    // === Keine Kollision: legale Batches laufen normal ==========================

    [Fact]
    public async Task SameRoute_DistinctIds_IsLegal()
    {
        // Regression-Guard gegen Over-Blocking: dieselbe Route zweimal MIT verschiedenen
        // Ids ist ausdrücklich erlaubt und funktioniert inklusive Chaining.
        var p1 = Req("p1", "DepChain", "MakeDto",
            mapping: new() { ["c1"] = "$.id" }, ("id", "7"), ("name", "\"alice\""));
        var p2 = Req("p2", "DepChain", "MakeDto",
            mapping: new() { ["c2"] = "$.id" }, ("id", "9"), ("name", "\"bob\""));
        var consumer1 = Req("k1", "DepChain", "EchoInt", null, Alias("value", "c1"));
        var consumer2 = Req("k2", "DepChain", "EchoInt", null, Alias("value", "c2"));

        var responses = (await _invoker.InvokeDi(
            new List<SleipnirRequest> { p1, p2, consumer1, consumer2 },
            null, ExecutionMode.Parallel)).ToList();

        responses.Should().HaveCount(4);
        responses.Should().OnlyContain(r => r!.Code == (int)HttpStatusCode.OK);
        responses.Single(r => r!.Id == "k1")!.Data.Value.Deserialize<int>().Should().Be(7);
        responses.Single(r => r!.Id == "k2")!.Data.Value.Deserialize<int>().Should().Be(9);
    }

    [Fact]
    public async Task SingleIdlessRequest_IsLegal()
    {
        // Ein einzelner id-loser Request hat keinen Kollisionspartner — Fallback-Key ok.
        var request = Req("", "DepChain", "EchoInt", null, ("value", "42"));

        var responses = (await _invoker.InvokeDi(
            new List<SleipnirRequest> { request }, null, ExecutionMode.Parallel)).ToList();

        responses.Should().HaveCount(1);
        responses[0]!.Code.Should().Be((int)HttpStatusCode.OK);
        responses[0]!.Data.Value.Deserialize<int>().Should().Be(42);
    }

    [Fact]
    public async Task IdlessRequests_DifferentRoutes_AreDistinctKeys()
    {
        // Zwei id-lose Requests auf VERSCHIEDENEN Routen → verschiedene Fallback-Keys.
        var r1 = Req("", "DepChain", "MakeDto", null, ("id", "1"), ("name", "\"x\""));
        var r2 = Req("", "DepChain", "MakeIdOnly", null, ("id", "2"));

        var responses = (await _invoker.InvokeDi(
            new List<SleipnirRequest> { r1, r2 }, null, ExecutionMode.Parallel)).ToList();

        responses.Should().HaveCount(2);
        responses.Should().OnlyContain(r => r!.Code == (int)HttpStatusCode.OK);
    }

    // === Kollision verhindert falsche Alias-Bindung ============================

    [Fact]
    public async Task Collision_RejectedBeforeWrongAliasBindingCanOccur()
    {
        // Das Szenario aus dem Audit: zwei Requests mit derselben Id, von denen einer
        // einen Alias exposiert — vor D3 konnte der Consumer das Fragment des FALSCHEN
        // Requests erhalten (letzter Write in priorResponses gewinnt). Jetzt: 400 statt
        // stiller Datenverwechslung.
        var provider = Req("same", "DepChain", "MakeDto",
            mapping: new() { ["cid"] = "$.id" }, ("id", "111"), ("name", "\"provider\""));
        var noise = Req("same", "DepChain", "EchoString", null, ("value", "\"noise\""));
        var consumer = Req("c", "DepChain", "EchoInt", null, Alias("value", "cid"));

        var responses = (await _invoker.InvokeDi(
            new List<SleipnirRequest> { provider, noise, consumer },
            null, ExecutionMode.Parallel)).ToList();

        responses.Should().HaveCount(3);
        responses.Should().OnlyContain(r => r!.Code == (int)HttpStatusCode.BadRequest);
        // Entscheidend: der Consumer wurde NICHT mit einem zufälligen Fragment ausgeführt.
        responses.Single(r => r!.Id == "c")!.Data.Should().BeNull();
    }
}
