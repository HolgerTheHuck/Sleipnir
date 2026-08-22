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
/// Strict-Binding-Modus — die optionale, fail-laute Variante des @alias-Transfers.
///
/// Weak (Default) duck-typet still; Strict verlangt, dass das Fragment den Consumer-
/// Typ vollständig deckt (jede public read-write Eigenschaft muss im Fragment vorhanden
/// sein), sonst 400. Diese Tests stellen sicher, dass Strict genau die gefährliche
/// Richtung (consumer ⊋ fragment → silent default) in ein 400 umwandelt, dabei aber
/// die sichere Subset-Richtung (consumer ⊆ fragment) und das Subset-Fan-out erlaubt —
/// und cross-kind in beiden Modi 400 bleibt. Siehe DEPENDENCY_BINDING.md §7.
/// </summary>
public class AliasBindingStrictTests
{
    private readonly SleipnirInvoker _invoker;

    public AliasBindingStrictTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTransient<DependencyChainController>();
        var sp = services.BuildServiceProvider();
        _invoker = new SleipnirInvoker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<SleipnirInvoker>>());
        _invoker.Register<DependencyChainController>();
        _invoker.AliasBindingMode = AliasBindingMode.Strict; // Strict aktiviert
    }

    private static JsonNode? ToData(string jsonValue) =>
        jsonValue.StartsWith("@") ? JsonValue.Create(jsonValue) : JsonNode.Parse(jsonValue);

    private static SleipnirRequest Req(
        string id, string controller, string method,
        Dictionary<string, string>? mapping = null,
        params (string name, string jsonValue)[] parameters)
    {
        var paramList = parameters
            .Select(p => new SleipnirParameter { ParameterName = p.name, Data = ToData(p.jsonValue) })
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

    private static SleipnirRequest DtoProvider(string id, int dtoId, string dtoName, string alias, string path) =>
        Req(id, "DepChain", "MakeDto", new() { [alias] = path },
            ("id", dtoId.ToString()), ("name", JsonSerializer.Serialize(dtoName)));

    private static SleipnirRequest IdOnlyProvider(string id, int dtoId, string alias, string path) =>
        Req(id, "DepChain", "MakeIdOnly", new() { [alias] = path }, ("id", dtoId.ToString()));

    private static (string, string) Alias(string paramName, string alias) => (paramName, $"@{alias}");

    private async Task<SleipnirResponse> RunConsumer(params SleipnirRequest[] batch)
    {
        var responses = (await _invoker.InvokeDi(batch, null, ExecutionMode.Serial)).ToList();
        return responses.Single(r => r?.Id == batch[^1].Id)!;
    }

    // === Strict erlaubt die sichere Richtung (consumer ⊆ fragment) ================

    [Fact]
    public async Task Strict_SubsetSafe_ProviderWider_ConsumerIdOnly_Binds()
    {
        // Provider {Id,Name} → Consumer IdOnly {Id}: Id vorhanden, nichts fehlt → Strict erlaubt.
        var provider = DtoProvider("p", 7, "alice", "cust", "$");
        var consumer = Req("c", "DepChain", "TakeIdOnly", null, Alias("d", "cust"));

        var res = await RunConsumer(provider, consumer);

        res.Code.Should().Be((int)HttpStatusCode.OK);
        res.Data.Value.Deserialize<int>().Should().Be(7);
    }

    [Fact]
    public async Task Strict_SubsetFanOut_OneAliasManyConsumers_Binds()
    {
        // Das Nutzer-Muster bleibt in Strict erlaubt: ein ganzer Customer → IdOnly UND NameOnly.
        var provider = DtoProvider("p", 7, "alice", "cust", "$");
        var byId = Req("byId", "DepChain", "TakeIdOnly", null, Alias("d", "cust"));
        var byName = Req("byName", "DepChain", "TakeNameOnly", null, Alias("d", "cust"));

        var responses = (await _invoker.InvokeDi(
            new[] { provider, byId, byName }, null, ExecutionMode.Serial)).ToList();

        var idRes = responses.Single(r => r?.Id == "byId")!;
        var nameRes = responses.Single(r => r?.Id == "byName")!;

        idRes.Code.Should().Be((int)HttpStatusCode.OK);
        idRes.Data.Value.Deserialize<int>().Should().Be(7);
        nameRes.Code.Should().Be((int)HttpStatusCode.OK);
        nameRes.Data.Value.Deserialize<string>().Should().Be("alice");
    }

    [Fact]
    public async Task Strict_CompatibleScalar_IntToInt_Binds()
    {
        // Skalare haben keine deckbaren Eigenschaften → Strict greift nicht, STJ bindet normal.
        var provider = DtoProvider("p", 7, "alice", "cid", "$.id");
        var consumer = Req("c", "DepChain", "EchoInt", null, Alias("value", "cid"));

        var res = await RunConsumer(provider, consumer);

        res.Code.Should().Be((int)HttpStatusCode.OK);
        res.Data.Value.Deserialize<int>().Should().Be(7);
    }

    // === Strict verbietet die gefährliche Richtung (consumer ⊋ fragment) =========

    [Fact]
    public async Task Strict_MissingValueTypeProperty_Rejects400()
    {
        // Provider IdOnly {Id} → Consumer IdActive {Id, Active}: Active (Werttyp) fehlt → 400.
        // In Weak wäre dies 2xx mit Active=false (heimtückisch); Strict macht es laut.
        var provider = IdOnlyProvider("p", 7, "cust", "$");
        var consumer = Req("c", "DepChain", "TakeIdActive", null, Alias("d", "cust"));

        var res = await RunConsumer(provider, consumer);

        res.Code.Should().Be((int)HttpStatusCode.BadRequest);
        res.Error!.Message.Should().Contain("Strict alias binding");
        res.Error.Message.Should().Contain("Active");
        res.Error.Message.Should().Contain("@cust");
    }

    [Fact]
    public async Task Strict_MissingReferenceProperty_Rejects400()
    {
        // Provider IdOnly {Id} → Consumer TestDto {Id, Name}: Name (Referenz) fehlt → 400.
        // In Weak wäre dies 2xx mit Name=null; Strict verlangt volle Deckung.
        // (Seit D5 werden Wire-Namen gemeldet: camelCase "name".)
        var provider = IdOnlyProvider("p", 7, "cust", "$");
        var consumer = Req("c", "DepChain", "DescribeDto", null, Alias("d", "cust"));

        var res = await RunConsumer(provider, consumer);

        res.Code.Should().Be((int)HttpStatusCode.BadRequest);
        res.Error!.Message.Should().Contain("Strict alias binding");
        res.Error.Message.Should().Contain("name");
    }

    // === cross-kind bleibt in beiden Modi 400 (STJ wirft ohnehin) ================

    [Fact]
    public async Task Strict_CrossKind_NumberIntoString_Still400()
    {
        var provider = DtoProvider("p", 7, "alice", "cid", "$.id");
        var consumer = Req("c", "DepChain", "EchoString", null, Alias("value", "cid"));

        var res = await RunConsumer(provider, consumer);

        res.Code.Should().Be((int)HttpStatusCode.BadRequest);
        res.Error!.Message.Should().Contain("cannot be converted");
    }

    [Fact]
    public async Task Strict_KindMismatchOnOverlap_WithFullCoverage_Still400()
    {
        // Provider StringId {Id:string} → Consumer IdOnly {Id:int}: Id ist überlappend
        // vorhanden (Deckung erfüllt → Strict passt), aber der JSON-Kind stimmt nicht
        // (string vs. int) → STJ lehnt ab. Cross-kind ist in beiden Modi 400; Strict
        // prüft nur Deckung und lässt STJ den Kind-Konflikt werfen.
        var provider = Req("p", "DepChain", "MakeStringIdDto", new() { ["cust"] = "$" },
            ("id", JsonSerializer.Serialize("notanint")));
        var consumer = Req("c", "DepChain", "TakeIdOnly", null, Alias("d", "cust"));

        var res = await RunConsumer(provider, consumer);

        res.Code.Should().Be((int)HttpStatusCode.BadRequest);
        res.Error!.Message.Should().Contain("cannot be converted");
    }

    [Fact]
    public async Task Strict_KindMismatchButAlsoMissingProperty_ReportsMissingFirst()
    {
        // Provider StringId {Id:string} → Consumer IdActive {Id:int, Active:bool}: hier
        // fehlt Active UND Id hat den falschen Kind. Strict prüft Deckung zuerst und
        // meldet das fehlende Active (lauter und früher als der STJ-Kind-Fehler).
        var provider = Req("p", "DepChain", "MakeStringIdDto", new() { ["cust"] = "$" },
            ("id", JsonSerializer.Serialize("notanint")));
        var consumer = Req("c", "DepChain", "TakeIdActive", null, Alias("d", "cust"));

        var res = await RunConsumer(provider, consumer);

        res.Code.Should().Be((int)HttpStatusCode.BadRequest);
        res.Error!.Message.Should().Contain("Strict alias binding");
        res.Error.Message.Should().Contain("Active");
    }

    // === Casing: Strict liest case-insensitiv (wie STJ) =========================

    [Fact]
    public async Task Strict_PropertyMatch_IsCaseInsensitive()
    {
        // Consumer deklariert Id (PascalCase), Fragment liefert id (camelCase) — STJ liest
        // case-insensitiv, also ist Id für Strict vorhanden → bindet.
        var provider = DtoProvider("p", 7, "alice", "cust", "$");
        var consumer = Req("c", "DepChain", "TakeIdOnly", null, Alias("d", "cust"));

        var res = await RunConsumer(provider, consumer);

        res.Code.Should().Be((int)HttpStatusCode.OK);
        res.Data.Value.Deserialize<int>().Should().Be(7);
    }
}