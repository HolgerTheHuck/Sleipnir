using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using TrameCommon.Models;
using TrameCore.Services;
using TrameTests.Fixtures;
using Xunit;

namespace TrameTests.Unit.Core;

/// <summary>
/// North-Bound-Härtung-Tests für die nicht-auth-spezifischen Caps:
///  - <b>A.3 Batch-Cap</b> (<see cref="TrameInvoker.MaximumBatchSize"/>): direkte In-Process-Aufrufer
///    kriegen eine <see cref="InvalidOperationException"/>, wenn der Batch das Cap überschreitet;
///    die Transport-Endpunkte (REST /json/multi, JSON-RPC batch, WS multi) wandeln das in ein
///    frühes 400. Cap=0 (Default) bleibt unbegrenzt — non-breaking für South-Bound.
///  - <b>A.4 JsonPath-Begrenzung</b> (<see cref="TrameInvoker.MaxDependencyPathLength"/>,
///    <see cref="TrameInvoker.AllowRecursiveDescent"/>): ein zu langer oder ein per
///    <c>AllowRecursiveDescent=false</c> verbotener <c>$..</c>-Pfad wird VOR dem Parsen
///    verworfen — der Provider exposiert den Alias nicht, der Dependent bekommt ein sauberes
///    400 statt eines CPU-Stalls über großem Graph.
/// </summary>
public class NorthBoundHardeningTests
{
    private readonly TrameInvoker _invoker;

    public NorthBoundHardeningTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTransient<DependencyChainController>();
        var sp = services.BuildServiceProvider();
        _invoker = new TrameInvoker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<TrameInvoker>>());
        _invoker.Register<DependencyChainController>();
    }

    private static TrameRequest Req(string id, string method,
        Dictionary<string, string>? mapping = null,
        params (string name, string jsonValue)[] parameters)
    {
        var paramList = parameters
            .Select(p => new TrameParameter
            {
                ParameterName = p.name,
                Data = p.jsonValue.StartsWith("@") ? JsonValue.Create(p.jsonValue) : JsonNode.Parse(p.jsonValue)
            }).ToList();
        return new TrameRequest
        {
            Id = id,
            Controller = "DepChain",
            Method = method,
            Params = JsonSerializer.SerializeToNode(paramList),
            DependencyMapping = mapping,
        };
    }

    // === A.3 Batch-Cap ==================================================================

    [Fact]
    public async Task BatchCap_Zero_AllowsArbitraryCount_NonBreaking()
    {
        // Default (0) = unbegrenzt — South-Bound unverändert.
        _invoker.MaximumBatchSize = 0;
        var batch = new List<TrameRequest>
        {
            Req("r1", "EchoLong", null, ("value", "1")),
            Req("r2", "EchoLong", null, ("value", "2")),
            Req("r3", "EchoLong", null, ("value", "3")),
        };
        var act = async () => (await _invoker.InvokeDi(batch, null, ExecutionMode.Parallel)).ToList();
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task BatchCap_Exceeded_ThrowsForDirectCaller()
    {
        _invoker.MaximumBatchSize = 2;
        var batch = new List<TrameRequest>
        {
            Req("r1", "EchoLong", null, ("value", "1")),
            Req("r2", "EchoLong", null, ("value", "2")),
            Req("r3", "EchoLong", null, ("value", "3")),
        };
        var act = async () => (await _invoker.InvokeDi(batch, null, ExecutionMode.Parallel)).ToList();
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*MaximumBatchSize*");
    }

    [Fact]
    public async Task BatchCap_AtLimit_Passes()
    {
        _invoker.MaximumBatchSize = 2;
        var batch = new List<TrameRequest>
        {
            Req("r1", "EchoLong", null, ("value", "1")),
            Req("r2", "EchoLong", null, ("value", "2")),
        };
        var responses = (await _invoker.InvokeDi(batch, null, ExecutionMode.Parallel)).ToList();
        responses.Should().HaveCount(2);
        responses[0]!.Data.Value.Deserialize<long>().Should().Be(1L);
    }

    // === A.4 JsonPath-Begrenzung ========================================================

    [Fact]
    public async Task JsonPath_OverMaxLength_NotExposed_DependentGets400()
    {
        // Provider deklariert einen Pfad jenseits MaxDependencyPathLength — die Extraktion
        // wirft vor dem Parsen, der Alias bleibt ungesetzt, der Dependent bekommt 400.
        _invoker.MaxDependencyPathLength = 8; // kurze Grenze für den Test
        var longPath = "$." + new string('x', 50);
        var step1 = Req("p", "MakeDto", new() { ["id"] = longPath },
            ("id", "7"), ("name", "\"Alice\""));
        var step2 = Req("c", "EchoLong", null, ("value", "@id"));

        var responses = (await _invoker.InvokeDi(new[] { step1, step2 }, null, ExecutionMode.Serial)).ToList();

        var provider = responses.Single(r => r?.Id == "p")!;
        // Provider läuft erfolgreich durch, exposiert den Alias aber NICHT.
        provider.Code.Should().Be(200);
        (provider.ExposedDependencies == null || !provider.ExposedDependencies.ContainsKey("id"))
            .Should().BeTrue();

        var dependent = responses.Single(r => r?.Id == "c")!;
        dependent.Code.Should().Be(400);
        dependent.Error.Should().NotBeNull();
    }

    [Fact]
    public async Task JsonPath_RecursiveDescentDisabled_NotExposed_DependentGets400()
    {
        // AllowRecursiveDescent=false verbietet $.. — der teuerste Pfad-Typ über großen
        // Graphen. Der Alias wird nicht exposiert, der Dependent bekommt 400.
        _invoker.AllowRecursiveDescent = false;
        var step1 = Req("p", "MakeDto", new() { ["id"] = "$..id" },
            ("id", "7"), ("name", "\"Alice\""));
        var step2 = Req("c", "EchoLong", null, ("value", "@id"));

        var responses = (await _invoker.InvokeDi(new[] { step1, step2 }, null, ExecutionMode.Serial)).ToList();

        var provider = responses.Single(r => r?.Id == "p")!;
        provider.Code.Should().Be(200);
        (provider.ExposedDependencies == null || !provider.ExposedDependencies.ContainsKey("id"))
            .Should().BeTrue();

        var dependent = responses.Single(r => r?.Id == "c")!;
        dependent.Code.Should().Be(400);
        dependent.Error.Should().NotBeNull();
    }

    [Fact]
    public async Task JsonPath_DefaultLimits_AllowLegitimateShortPath()
    {
        // Default (256, recursive erlaubt) — ein normaler Pfad funktioniert unverändert.
        _invoker.MaxDependencyPathLength = 256;
        _invoker.AllowRecursiveDescent = true;
        var step1 = Req("p", "MakeDto", new() { ["id"] = "$.id" },
            ("id", "7"), ("name", "\"Alice\""));
        var step2 = Req("c", "EchoLong", null, ("value", "@id"));

        var responses = (await _invoker.InvokeDi(new[] { step1, step2 }, null, ExecutionMode.Serial)).ToList();
        var dependent = responses.Single(r => r?.Id == "c")!;
        dependent.Code.Should().Be(200);
        dependent.Data.Value.Deserialize<long>().Should().Be(7L);
    }
}