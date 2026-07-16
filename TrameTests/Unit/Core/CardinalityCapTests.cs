using FluentAssertions;
using TrameCommon.Models;
using TrameCore.Services;
using TrameTests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace TrameTests.Unit.Core;

/// <summary>
/// Kardinalitäts-Caps des Invokers — MaxParameterArrayLength (Default 1000) und
/// MaxResultElementCount (Default 10000), jeweils 0 = unbegrenzt. Schützen den Server
/// vor Riesen-Arrays, insb. beim @alias-Whole-Collection-Passthrough (server-seitig
/// erzeugte Arrays — Body-Size-Limits greifen dort nicht). Jeder Test baut einen
/// frischen Invoker mit explizit gesetztem Cap, um die Defaults nicht zu kreuzen.
/// </summary>
public class CardinalityCapTests
{
    private static TrameInvoker BuildInvoker(int maxParameterArrayLength, int maxResultElementCount)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTransient<TestInvokerController>();
        services.AddTransient<DependencyChainController>();
        var sp = services.BuildServiceProvider();
        var invoker = new TrameInvoker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<TrameInvoker>>());
        invoker.Register<TestInvokerController>();
        invoker.Register<DependencyChainController>();
        invoker.MaxParameterArrayLength = maxParameterArrayLength;
        invoker.MaxResultElementCount = maxResultElementCount;
        return invoker;
    }

    private static TrameRequest CreateRequest(string controller, string method,
        params (string name, string jsonValue)[] parameters)
    {
        var paramList = parameters.Select(p => new TrameParameter
        {
            ParameterName = p.name,
            Data = p.jsonValue.StartsWith("@") ? JsonValue.Create(p.jsonValue) : JsonNode.Parse(p.jsonValue)
        }).ToList();
        return new TrameRequest
        {
            Controller = controller,
            Method = method,
            Params = JsonSerializer.SerializeToNode(paramList),
            Id = $"{controller}.{method}"
        };
    }

    private static string JsonArray(int count) =>
        "[" + string.Join(",", Enumerable.Range(1, count)) + "]";

    #region MaxParameterArrayLength

    [Fact]
    public async Task MaxParameterArrayLength_RejectsOversizedArray()
    {
        // Arrange: Cap 5, Array mit 10 Elementen.
        var invoker = BuildInvoker(maxParameterArrayLength: 5, maxResultElementCount: 0);
        var request = CreateRequest("DepChain", "EchoIntList", ("values", JsonArray(10)));

        // Act
        var response = await invoker.InvokeDi(request, null);

        // Assert: 400 + Hinweis auf MaxParameterArrayLength. Methode wird nicht aufgerufen.
        response.Should().NotBeNull();
        response!.Code.Should().Be((int)HttpStatusCode.BadRequest);
        response.Error.Should().NotBeNull();
        response.Error!.Message.Should().Contain("MaxParameterArrayLength");
    }

    [Fact]
    public async Task MaxParameterArrayLength_AllowsUnderCap()
    {
        // Arrange: Cap 5, Array mit 3 Elementen.
        var invoker = BuildInvoker(maxParameterArrayLength: 5, maxResultElementCount: 0);
        var request = CreateRequest("DepChain", "EchoIntList", ("values", JsonArray(3)));

        // Act
        var response = await invoker.InvokeDi(request, null);

        // Assert: 200, Liste intakt durchgereicht.
        response.Should().NotBeNull();
        response!.Code.Should().Be((int)HttpStatusCode.OK);
        var echoed = response.Data!.Value.Deserialize<List<int>>();
        echoed.Should().HaveCount(3).And.BeEquivalentTo(new[] { 1, 2, 3 });
    }

    [Fact]
    public async Task MaxParameterArrayLength_Zero_AllowsUnlimited()
    {
        // Arrange: Cap 0 (unbegrenzt), großes Array (über dem Default von 1000).
        var invoker = BuildInvoker(maxParameterArrayLength: 0, maxResultElementCount: 0);
        var request = CreateRequest("DepChain", "EchoIntList", ("values", JsonArray(2000)));

        // Act
        var response = await invoker.InvokeDi(request, null);

        // Assert: 200 — 0 schaltet den Cap ab.
        response.Should().NotBeNull();
        response!.Code.Should().Be((int)HttpStatusCode.OK);
        var echoed = response.Data!.Value.Deserialize<List<int>>();
        echoed.Should().HaveCount(2000);
    }

    [Fact]
    public async Task MaxParameterArrayLength_StringParam_NotCounted()
    {
        // Arrange: Cap 5, aber ein langer String-Parameter. string ist IEnumerable<char>,
        // darf also NICHT als Collection gezählt werden.
        var invoker = BuildInvoker(maxParameterArrayLength: 5, maxResultElementCount: 0);
        var longString = new string('x', 500);
        var request = CreateRequest("TestInvoker", "Echo",
            ("message", JsonSerializer.Serialize(longString)));

        // Act
        var response = await invoker.InvokeDi(request, null);

        // Assert: 200 — String-Parameter wird nicht vom Array-Cap erfasst.
        response.Should().NotBeNull();
        response!.Code.Should().Be((int)HttpStatusCode.OK);
    }

    [Fact]
    public async Task MaxParameterArrayLength_RejectsFanOutArrayAssembledFromPriorResult()
    {
        // Arrange: Param-Cap 5, Result-Cap 0 (damit step1's 20-Elemente-Stream durchgeht).
        // step1: StreamNumbers(20) → List<int>[0..19]; Wildcard "$[*]" sammelt alle 20
        //   Treffer serverseitig zu einem Array "ids" (kein Client hat die 20 geschickt).
        // step2: EchoIntList(values=@ids) → Parameter List<int> mit 20 Elementen → Cap 5.
        // Beweist, dass der Parameter-Cap ein per @alias-Fan-out zur Laufzeit gebautes
        // Array erfasst — Body-Size-Limits greifen hier nicht (das Array entsteht erst
        // serverseitig aus einem Vorergebnis), der Kardinalitäts-Cap aber schon.
        var invoker = BuildInvoker(maxParameterArrayLength: 5, maxResultElementCount: 0);

        var step1 = new TrameRequest
        {
            Controller = "TestInvoker",
            Method = "StreamNumbers",
            Params = JsonSerializer.SerializeToNode(new List<TrameParameter>
            {
                new() { ParameterName = "count", Data = JsonNode.Parse("20") }
            }),
            Id = "s1",
            DependencyMapping = new Dictionary<string, string> { { "ids", "$[*]" } }
        };
        var step2 = new TrameRequest
        {
            Controller = "DepChain",
            Method = "EchoIntList",
            Params = JsonSerializer.SerializeToNode(new List<TrameParameter>
            {
                new() { ParameterName = "values", Data = JsonValue.Create("@ids") }
            }),
            Id = "s2"
        };

        // Act — DependencyMapping vorhanden → Auto-Detect Topological-Batch (Serial-Order).
        var responses = (await invoker.InvokeDi(new[] { step1, step2 }, null)).ToList();
        var byId = responses.ToDictionary(r => r!.Id ?? string.Empty);

        // Assert: step1 produziert OK (20 Elemente, Result-Cap aus); step2 400 durch Param-Cap.
        byId["s1"].Code.Should().Be((int)HttpStatusCode.OK);
        // ExposedDependencies beweist: das 20-Element-Array wurde serverseitig gebaut.
        JsonSerializer.Deserialize<List<int>>(byId["s1"].ExposedDependencies!["ids"])
            .Should().HaveCount(20);
        byId["s2"].Code.Should().Be((int)HttpStatusCode.BadRequest);
        byId["s2"].Error!.Message.Should().Contain("MaxParameterArrayLength");
    }

    #endregion

    #region MaxResultElementCount

    [Fact]
    public async Task MaxResultElementCount_RejectsOversizedStreamResult()
    {
        // Arrange: Result-Cap 5, Stream liefert 10 Elemente → Early-Stop beim Konsumieren.
        var invoker = BuildInvoker(maxParameterArrayLength: 0, maxResultElementCount: 5);
        var request = CreateRequest("TestInvoker", "StreamNumbers", ("count", "10"));

        // Act
        var response = await invoker.InvokeDi(request, null);

        // Assert: 413 + Hinweis auf MaxResultElementCount (Streaming-Pfad).
        response.Should().NotBeNull();
        response!.Code.Should().Be((int)HttpStatusCode.RequestEntityTooLarge);
        response.Error.Should().NotBeNull();
        response.Error!.Message.Should().Contain("MaxResultElementCount");
    }

    [Fact]
    public async Task MaxResultElementCount_AllowsUnderCapStream()
    {
        // Arrange: Result-Cap 5, Stream liefert 3 Elemente.
        var invoker = BuildInvoker(maxParameterArrayLength: 0, maxResultElementCount: 5);
        var request = CreateRequest("TestInvoker", "StreamNumbers", ("count", "3"));

        // Act
        var response = await invoker.InvokeDi(request, null);

        // Assert: 200, 3 Elemente.
        response.Should().NotBeNull();
        response!.Code.Should().Be((int)HttpStatusCode.OK);
        var numbers = response.Data!.Value.Deserialize<List<int>>();
        numbers.Should().HaveCount(3);
    }

    [Fact]
    public async Task MaxResultElementCount_RejectsOversizedMaterializedResult()
    {
        // Arrange: Result-Cap 3 (Param-Cap 0, damit der 10-Element-Input durchgeht),
        // EchoIntList gibt 10 Elemente zurück → ReturnResponse-Cap schlägt zu.
        var invoker = BuildInvoker(maxParameterArrayLength: 0, maxResultElementCount: 3);
        var request = CreateRequest("DepChain", "EchoIntList", ("values", JsonArray(10)));

        // Act
        var response = await invoker.InvokeDi(request, null);

        // Assert: 413 (materialisierter List<T>-Return-Pfad).
        response.Should().NotBeNull();
        response!.Code.Should().Be((int)HttpStatusCode.RequestEntityTooLarge);
        response.Error.Should().NotBeNull();
        response.Error!.Message.Should().Contain("MaxResultElementCount");
    }

    #endregion

    #region Defaults

    [Fact]
    public void Defaults_AreSecureByDefault()
    {
        // Ein nackter `new TrameInvoker(...)` ohne Optionen-Verdrahtung muss
        // trotzdem geschützt sein („Server schützt sich selbst").
        var services = new ServiceCollection();
        services.AddLogging();
        var sp = services.BuildServiceProvider();
        var invoker = new TrameInvoker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<TrameInvoker>>());

        invoker.MaxParameterArrayLength.Should().Be(1000);
        invoker.MaxResultElementCount.Should().Be(10000);
    }

    #endregion
}