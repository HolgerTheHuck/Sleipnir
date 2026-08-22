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
/// Duplicate-alias gate (audit 2026-08-22, D1 / finding F1).
///
/// Two requests exposing the SAME alias made resolution nondeterministic:
/// the availability check consulted <c>aliasToProvider</c> (last declaration wins),
/// while <see cref="SleipnirInvoker.ResolveParameterValues"/> merged the
/// ExposedDependencies of ALL prior responses — and
/// <c>ConcurrentDictionary.Values</c> has no defined order. The check could pass
/// against provider A while provider B's fragment was injected.
///
/// Contract now: a batch in which two requests expose the same alias is rejected
/// at batch entry with per-request 400s for EVERY request (the batch as a whole
/// is not executable) — fail-loud, same style as registration-time name uniqueness.
/// No controller method runs.
/// </summary>
public class AliasCollisionTests
{
    private readonly SleipnirInvoker _invoker;

    public AliasCollisionTests()
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

    // === Kollision → alle Requests bekommen 400 ================================

    [Fact]
    public async Task DuplicateAlias_BothProvidersAndConsumer_Get400()
    {
        // Zwei Provider exposen denselben Alias "cust" — der Batch ist nicht ausführbar.
        var providerA = Req("pa", "DepChain", "MakeDto",
            mapping: new() { ["cust"] = "$" }, ("id", "7"), ("name", "\"alice\""));
        var providerB = Req("pb", "DepChain", "MakeDto",
            mapping: new() { ["cust"] = "$.id" }, ("id", "9"), ("name", "\"bob\""));
        var consumer = Req("c", "DepChain", "TakeIdOnly", null, Alias("d", "cust"));

        var responses = (await _invoker.InvokeDi(
            new List<SleipnirRequest> { providerA, providerB, consumer },
            null, ExecutionMode.Parallel)).ToList();

        responses.Should().HaveCount(3);
        responses.Should().OnlyContain(r => r!.Code == (int)HttpStatusCode.BadRequest);
        foreach (var r in responses)
        {
            r!.Error.Should().NotBeNull();
            r.Error!.Message.Should().Contain("Duplicate alias '@cust'");
            r.Error!.Message.Should().Contain("exactly one request");
        }
        // Ids bleiben für die Client-Korrelation erhalten.
        responses.Select(r => r!.Id).Should().BeEquivalentTo(new[] { "pa", "pb", "c" });
    }

    [Fact]
    public async Task DuplicateAlias_NoControllerMethodRuns()
    {
        var providerA = Req("pa", "DepChain", "MakeDto",
            mapping: new() { ["a"] = "$" }, ("id", "1"), ("name", "\"x\""));
        var providerB = Req("pb", "DepChain", "MakeIdOnly",
            mapping: new() { ["a"] = "$.id" }, ("id", "2"));

        var responses = (await _invoker.InvokeDi(
            new List<SleipnirRequest> { providerA, providerB },
            null, ExecutionMode.Parallel)).ToList();

        responses.Should().OnlyContain(r => r!.Code == (int)HttpStatusCode.BadRequest);
        // MakeDto/MakeIdOnly sind pure Roundtrips ohne Zähler — die 400 am Batch-Eingang
        // beweist die Nicht-Ausführung bereits: eine Ausführung würde 2xx liefern.
    }

    [Fact]
    public async Task DuplicateAlias_SerialMode_AlsoRejected()
    {
        // Auto-detect routet ohnehin topologisch; der Gate muss aber auch dann greifen,
        // wenn die Kollision über den Serial-Pfad läuft (ExecuteSequentially nutzt dieselbe
        // Id-basierte Auflösung und hätte dasselbe Nondeterminismus-Problem).
        var providerA = Req("pa", "DepChain", "MakeDto",
            mapping: new() { ["dup"] = "$" }, ("id", "1"), ("name", "\"x\""));
        var providerB = Req("pb", "DepChain", "MakeDto",
            mapping: new() { ["dup"] = "$" }, ("id", "2"), ("name", "\"y\""));

        var responses = (await _invoker.InvokeDi(
            new List<SleipnirRequest> { providerA, providerB },
            null, ExecutionMode.Serial)).ToList();

        responses.Should().HaveCount(2);
        responses.Should().OnlyContain(r => r!.Code == (int)HttpStatusCode.BadRequest);
        responses[0]!.Error!.Message.Should().Contain("Duplicate alias '@dup'");
    }

    // === Keine Kollision: Distinct Aliases laufen normal ========================

    [Fact]
    public async Task DistinctAliases_ChainStillResolves()
    {
        // Regression-Guard gegen Over-Blocking: zwei Provider mit VERSCHIEDENEN Aliases
        // + ein Consumer je Alias müssen weiterhin funktionieren.
        var providerA = Req("pa", "DepChain", "MakeDto",
            mapping: new() { ["cust"] = "$" }, ("id", "7"), ("name", "\"alice\""));
        var providerB = Req("pb", "DepChain", "MakeIdOnly",
            mapping: new() { ["oid"] = "$.id" }, ("id", "9"));
        var consumer1 = Req("c1", "DepChain", "TakeIdOnly", null, Alias("d", "cust"));
        var consumer2 = Req("c2", "DepChain", "EchoInt", null, Alias("value", "oid"));

        var responses = (await _invoker.InvokeDi(
            new List<SleipnirRequest> { providerA, providerB, consumer1, consumer2 },
            null, ExecutionMode.Parallel)).ToList();

        responses.Should().HaveCount(4);
        responses.Should().OnlyContain(r => r!.Code == (int)HttpStatusCode.OK);
        responses.Single(r => r!.Id == "c1")!.Data.Value.Deserialize<int>().Should().Be(7);
        responses.Single(r => r!.Id == "c2")!.Data.Value.Deserialize<int>().Should().Be(9);
    }

    [Fact]
    public async Task SameAliasInDifferentBatches_IsAllowed()
    {
        // Der Gate gilt pro Batch: zwei separate InvokeDi-Aufrufe dürfen denselben
        // Alias-Namen jeweils eigenständig exposen.
        var batch1 = new List<SleipnirRequest>
        {
            Req("p1", "DepChain", "MakeDto", mapping: new() { ["cust"] = "$.id" }, ("id", "5"), ("name", "\"e\"")),
            Req("c1", "DepChain", "EchoInt", null, Alias("value", "cust")),
        };
        var batch2 = new List<SleipnirRequest>
        {
            Req("p2", "DepChain", "MakeDto", mapping: new() { ["cust"] = "$.id" }, ("id", "6"), ("name", "\"f\"")),
            Req("c2", "DepChain", "EchoInt", null, Alias("value", "cust")),
        };

        var r1 = (await _invoker.InvokeDi(batch1, null, ExecutionMode.Parallel)).ToList();
        var r2 = (await _invoker.InvokeDi(batch2, null, ExecutionMode.Parallel)).ToList();

        r1.Should().OnlyContain(r => r!.Code == (int)HttpStatusCode.OK);
        r2.Should().OnlyContain(r => r!.Code == (int)HttpStatusCode.OK);
        r1.Single(r => r!.Id == "c1")!.Data.Value.Deserialize<int>().Should().Be(5);
        r2.Single(r => r!.Id == "c2")!.Data.Value.Deserialize<int>().Should().Be(6);
    }

    [Fact]
    public async Task SameAliasTwiceOnOneRequest_MappingDictionarySemantics()
    {
        // Ein Request kann denselben Alias nicht doppelt deklarieren (Dictionary-Semantik:
        // letzter Key gewinnt beim Deserialisieren) — das ist KEINE Kollision. Der Test
        // dokumentiert, dass der Gate nur ÜBER Requests prüft, nicht innerhalb eines Mappings.
        var provider = Req("p", "DepChain", "MakeDto",
            mapping: new() { ["a"] = "$", ["a"] = "$.id" }, ("id", "3"), ("name", "\"z\""));
        var consumer = Req("c", "DepChain", "EchoInt", null, Alias("value", "a"));

        var responses = (await _invoker.InvokeDi(
            new List<SleipnirRequest> { provider, consumer },
            null, ExecutionMode.Parallel)).ToList();

        responses.Should().HaveCount(2);
        responses.Should().OnlyContain(r => r!.Code == (int)HttpStatusCode.OK);
        // "$.id" (letzte Deklaration im Dictionary-Literal) hat gewonnen.
        responses.Single(r => r!.Id == "c")!.Data.Value.Deserialize<int>().Should().Be(3);
    }

    // === Fehlermeldung nennt beide Provider-Keys ================================

    [Fact]
    public async Task DuplicateAlias_MessageNamesBothProviderKeys()
    {
        var providerA = Req("first-provider", "DepChain", "MakeDto",
            mapping: new() { ["x"] = "$" }, ("id", "1"), ("name", "\"x\""));
        var providerB = Req("second-provider", "DepChain", "MakeDto",
            mapping: new() { ["x"] = "$" }, ("id", "2"), ("name", "\"y\""));

        var responses = (await _invoker.InvokeDi(
            new List<SleipnirRequest> { providerA, providerB },
            null, ExecutionMode.Parallel)).ToList();

        var message = responses[0]!.Error!.Message;
        message.Should().Contain("'first-provider'");
        message.Should().Contain("'second-provider'");
    }

    [Fact]
    public async Task DuplicateAlias_IdlessProviders_FallBackToRouteKey()
    {
        // GraphKey-Fallback: id-lose Requests werden als Controller.Method gemeldet.
        var providerA = Req("", "DepChain", "MakeDto",
            mapping: new() { ["y"] = "$" }, ("id", "1"), ("name", "\"x\""));
        var providerB = Req("pb", "DepChain", "MakeDto",
            mapping: new() { ["y"] = "$" }, ("id", "2"), ("name", "\"y\""));

        var responses = (await _invoker.InvokeDi(
            new List<SleipnirRequest> { providerA, providerB },
            null, ExecutionMode.Parallel)).ToList();

        responses.Should().OnlyContain(r => r!.Code == (int)HttpStatusCode.BadRequest);
        responses[0]!.Error!.Message.Should().Contain("DepChain.MakeDto");
    }
}
