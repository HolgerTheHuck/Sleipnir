using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using TrameCommon.Models;
using TrameCore.Services;
using TrameTests.Fixtures;
using Xunit;

namespace TrameTests.Unit.Core;

/// <summary>
/// Alias-Binding-Matrix — das JSON-Mapping zwischen Provider und Consumer per @alias.
///
/// Diese Tests erfassen das in PROTOCOL.md → „Alias Serialization & Type Binding"
/// spezifizierte Laufzeitverhalten end-to-end über den TrameInvoker: ein Provider
/// exposet einen JsonPath aus seinem Ergebnis, ein späterer Request referenziert ihn
/// als @alias-Platzhalter, und BuildParameters deserialisiert das extrahierte Fragment
/// per System.Text.Json in den deklarierten Consumer-Parametertyp. Geprüft werden die
/// vier Runtime-Ergebnisse (kompatibel, cross-kind 400, object→object-Duck-Typing,
/// unresolved) sowie das Subset-Fan-out-Muster (ein ganzes Objekt als Alias → mehrere
/// Consumer, die sich je ihr Feld duck-typen).
/// </summary>
public class AliasBindingTests
{
    private readonly TrameInvoker _invoker;

    public AliasBindingTests()
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

    /// <summary>Wandelt einen JSON-kodierten Wert-ODER einen rohen @alias-String in ein
    ///  JsonNode? um: führendes @ → JsonValue.Create (roher Alias-Platzhalter), sonst
    ///  JsonNode.Parse (JSON-kodierter Wert).</summary>
    private static JsonNode? ToData(string jsonValue) =>
        jsonValue.StartsWith("@") ? JsonValue.Create(jsonValue) : JsonNode.Parse(jsonValue);

    /// <summary>Baut einen TrameRequest mit Id, benannten Parametern (Data als JsonNode)
    ///  und optionaler dependencyMapping (Provider-Expose). Consumer-@alias-Parameter
    ///  einfach als ("paramName", "\"@aliasName\"") übergeben.</summary>
    private static TrameRequest Req(
        string id,
        string controller,
        string method,
        Dictionary<string, string>? mapping = null,
        params (string name, string jsonValue)[] parameters)
    {
        var paramList = parameters
            .Select(p => new TrameParameter { ParameterName = p.name, Data = ToData(p.jsonValue) })
            .ToList();
        return new TrameRequest
        {
            Id = id,
            Controller = controller,
            Method = method,
            Params = JsonSerializer.SerializeToNode(paramList),
            DependencyMapping = mapping,
        };
    }

    /// <summary>Provider: DepChain.MakeDto(id,name) mit Expose.</summary>
    private static TrameRequest DtoProvider(string id, int dtoId, string dtoName,
        string alias, string jsonPath) =>
        Req(id, "DepChain", "MakeDto",
            mapping: new() { [alias] = jsonPath },
            ("id", dtoId.ToString()),
            ("name", JsonSerializer.Serialize(dtoName)));

    private static TrameRequest IdOnlyProvider(string id, int dtoId, string alias, string jsonPath) =>
        Req(id, "DepChain", "MakeIdOnly",
            mapping: new() { [alias] = jsonPath },
            ("id", dtoId.ToString()));

    private static TrameRequest StringIdProvider(string id, string dtoId, string alias, string jsonPath) =>
        Req(id, "DepChain", "MakeStringIdDto",
            mapping: new() { [alias] = jsonPath },
            ("id", JsonSerializer.Serialize(dtoId)));

    /// <summary>Consumer-Parameter, der einen @alias-Platzhalter trägt. Das Data-Feld
    ///  hält den ROHEN Alias-Text „@name" (ohne JSON-Anführungszeichen) als JsonValue —
    ///  nur so erkennt ContainsAlias das führende @ und ReplaceDependencyByAlias
    ///  substituiert es. Ein versehentlich JSON-kodiertes „\"@name\"" startet mit „"“
    ///  und wird nicht erkannt.</summary>
    private static (string name, string jsonValue) Alias(string paramName, string alias) =>
        (paramName, $"@{alias}");

    /// <summary>Führt Provider+Consumer aus und liefert die Consumer-Response.</summary>
    private async Task<TrameResponse> RunConsumer(params TrameRequest[] batch)
    {
        var responses = (await _invoker.InvokeDi(batch, null, ExecutionMode.Serial)).ToList();
        var consumer = responses.Single(r => r?.Id == batch[^1].Id);
        return consumer!;
    }

    // === Kompatibel (2xx) ========================================================

    [Fact]
    public async Task Alias_CompatibleScalar_IntToInt_Binds()
    {
        var provider = DtoProvider("p", 7, "alice", "cid", "$.id");
        var consumer = Req("c", "DepChain", "EchoInt", null, Alias("value", "cid"));

        var res = await RunConsumer(provider, consumer);

        res.Code.Should().Be((int)HttpStatusCode.OK);
        res.Data.Value.Deserialize<int>().Should().Be(7);
    }

    [Fact]
    public async Task Alias_Widening_IntToLong_Binds()
    {
        var provider = DtoProvider("p", 7, "alice", "cid", "$.id");
        var consumer = Req("c", "DepChain", "EchoLong", null, Alias("value", "cid"));

        var res = await RunConsumer(provider, consumer);

        res.Code.Should().Be((int)HttpStatusCode.OK);
        res.Data.Value.Deserialize<long>().Should().Be(7L);
    }

    [Fact]
    public async Task Alias_ObjectToObject_SameType_Binds()
    {
        var provider = DtoProvider("p", 7, "alice", "cust", "$");
        var consumer = Req("c", "DepChain", "EchoDto", null, Alias("dto", "cust"));

        var res = await RunConsumer(provider, consumer);

        res.Code.Should().Be((int)HttpStatusCode.OK);
        var raw = res.Data.Value.GetRawText();
        raw.Should().Contain("\"id\":7");
        raw.Should().Contain("\"name\":\"alice\"");
    }

    // === Subset-Fan-out (das nützliche Muster: ein Alias → viele Consumer) =======

    [Fact]
    public async Task Alias_SubsetSafe_ProviderWider_ConsumerIdOnly_DropsName()
    {
        // Provider {Id, Name} → Consumer IdOnly {Id}: Name fällt still weg, Id bindet.
        var provider = DtoProvider("p", 7, "alice", "cust", "$");
        var consumer = Req("c", "DepChain", "TakeIdOnly", null, Alias("d", "cust"));

        var res = await RunConsumer(provider, consumer);

        res.Code.Should().Be((int)HttpStatusCode.OK);
        res.Data.Value.Deserialize<int>().Should().Be(7);
    }

    [Fact]
    public async Task Alias_SubsetSafe_ProviderWider_ConsumerNameOnly_DropsId()
    {
        // Provider {Id, Name} → Consumer NameOnly {Name}: Id fällt weg, Name bindet.
        var provider = DtoProvider("p", 7, "alice", "cust", "$");
        var consumer = Req("c", "DepChain", "TakeNameOnly", null, Alias("d", "cust"));

        var res = await RunConsumer(provider, consumer);

        res.Code.Should().Be((int)HttpStatusCode.OK);
        res.Data.Value.Deserialize<string>().Should().Be("alice");
    }

    /// <summary>
    /// Das Muster, das der Nutzer beschrieben hat: ein ganzes Customer-Objekt einmal
    /// exposet („$"), derselbe @cust-Alias speist zwei Consumer — einer, der nur Id
    /// deklariert, einer, der nur Name deklariert. Beide duck-typen sich ihr Feld,
    /// der Rest landet in /dev/null. Ein Provider, zwei typisierte Consumer.
    /// </summary>
    [Fact]
    public async Task Alias_SubsetFanOut_OneAliasManyConsumers_EachPicksItsField()
    {
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

    // === object→object: gefährliche Richtungen (silent, aber 2xx) ================

    [Fact]
    public async Task Alias_MissingReferenceProperty_ConsumerWider_NameBecomesNull()
    {
        // Provider IdOnly {Id} → Consumer TestDto {Id, Name}: Name fehlt (Referenz) → null.
        var provider = IdOnlyProvider("p", 7, "cust", "$");
        var consumer = Req("c", "DepChain", "DescribeDto", null, Alias("d", "cust"));

        var res = await RunConsumer(provider, consumer);

        res.Code.Should().Be((int)HttpStatusCode.OK); // kein 400 — Referenz wird null
        res.Data.Value.Deserialize<string>().Should().Be("7/"); // Name == null
    }

    /// <summary>
    /// Der heimtückische Fall: Provider IdOnly {Id} → Consumer IdActive {Id, Active},
    /// Active ist ein Werttyp (bool) und fehlt im Provider → System.Text.Json setzt es
    /// still auf false. Der Call ist 2xx mit falscher Business-Daten (Active=false,
    /// ohne dass der Provider je Active geliefert hat). Genau das flagt der DevUI-Checker.
    /// </summary>
    [Fact]
    public async Task Alias_MissingValueTypeProperty_ConsumerWider_SilentlyDefaults()
    {
        var provider = IdOnlyProvider("p", 7, "cust", "$");
        var consumer = Req("c", "DepChain", "TakeIdActive", null, Alias("d", "cust"));

        var res = await RunConsumer(provider, consumer);

        res.Code.Should().Be((int)HttpStatusCode.OK); // kein 400 — Werttyp wird still default
        res.Data.Value.Deserialize<string>().Should().Be("7/False"); // Active==false
    }

    // === cross-kind → 400 ========================================================

    [Fact]
    public async Task Alias_CrossKind_NumberIntoString_Returns400()
    {
        // $.id ist eine Zahl (7) → Consumer string-Parameter → STJ lehnt ab (kein AllowReadingFromString).
        var provider = DtoProvider("p", 7, "alice", "cid", "$.id");
        var consumer = Req("c", "DepChain", "EchoString", null, Alias("value", "cid"));

        var res = await RunConsumer(provider, consumer);

        res.Code.Should().Be((int)HttpStatusCode.BadRequest);
        res.Error!.Message.Should().Contain("cannot be converted");
    }

    [Fact]
    public async Task Alias_CrossKind_StringIntoInt_Returns400()
    {
        // $.name ist ein String ("alice") → Consumer int-Parameter → 400.
        var provider = DtoProvider("p", 7, "alice", "cname", "$.name");
        var consumer = Req("c", "DepChain", "EchoInt", null, Alias("value", "cname"));

        var res = await RunConsumer(provider, consumer);

        res.Code.Should().Be((int)HttpStatusCode.BadRequest);
        res.Error!.Message.Should().Contain("cannot be converted");
    }

    [Fact]
    public async Task Alias_CrossKind_WholeObjectIntoScalarInt_Returns400()
    {
        // Ganzes Objekt ($) → nackter int-Parameter → object→scalar cross-kind → 400.
        // Das ist die Grenze des Subset-Fan-out: nackter Skalar empfängt kein ganzes Objekt.
        var provider = DtoProvider("p", 7, "alice", "cust", "$");
        var consumer = Req("c", "DepChain", "EchoInt", null, Alias("value", "cust"));

        var res = await RunConsumer(provider, consumer);

        res.Code.Should().Be((int)HttpStatusCode.BadRequest);
        res.Error!.Message.Should().Contain("cannot be converted");
        res.Error.Message.Should().Contain("Int32");
    }

    [Fact]
    public async Task Alias_KindMismatchOnOverlappingProperty_Returns400()
    {
        // Provider StringId {Id:string} → Consumer IdActive {Id:int, Active:bool}:
        // Id überlappt, aber JSON-Kind inkonsistent (string vs. int) → STJ lehnt ab.
        var provider = StringIdProvider("p", "notanint", "cust", "$");
        var consumer = Req("c", "DepChain", "TakeIdActive", null, Alias("d", "cust"));

        var res = await RunConsumer(provider, consumer);

        res.Code.Should().Be((int)HttpStatusCode.BadRequest);
        res.Error!.Message.Should().Contain("cannot be converted");
        res.Error.Message.Should().Contain("IdActiveDto");
    }

    // === unresolved → 400 ========================================================

    [Fact]
    public async Task Alias_UnresolvedAlias_Returns400()
    {
        // Consumer referenziert @bogus, das kein früherer Request exposed hat.
        var consumer = Req("c", "DepChain", "EchoInt", null, Alias("value", "bogus"));

        var responses = await _invoker.InvokeDi(new[] { consumer }, null, ExecutionMode.Serial);

        var res = responses.Single()!;
        res.Code.Should().Be((int)HttpStatusCode.BadRequest);
        res.Error!.Message.Should().Contain("Unresolved dependencies");
        res.Error.Message.Should().Contain("bogus");
    }

    // === Casing: JsonPath ist case-sensitiv gegen den camelCase-Wire =============

    [Fact]
    public async Task Alias_JsonPath_PascalCase_DoesNotMatch_ReturnsUnresolvedOr400()
    {
        // Der Wire ist camelCase (id), JsonPath ist case-sensitiv: $.Id trifft nichts.
        // Der Alias wird nicht exposed (ExtractValue liefert null). Da der Provider ein
        // DependencyMapping trägt, schaltet der Auto-Detect auf den topologischen Pfad,
        // und die Verfügbarkeits-Propagierung fängt das VOR der Ausführung ab: der Consumer
        // läuft nicht, sondern bekommt eine erklärende 400 „did not expose" (statt erst
        // zur Laufzeit am fehlenden Alias mit nichtssagendem „Unresolved dependencies").
        var provider = DtoProvider("p", 7, "alice", "cid", "$.Id"); // PascalCase → kein Treffer
        var consumer = Req("c", "DepChain", "EchoInt", null, Alias("value", "cid"));

        var res = await RunConsumer(provider, consumer);

        res.Code.Should().Be((int)HttpStatusCode.BadRequest);
        res.Error!.Message.Should().Contain("did not expose '@cid'");
        res.Error.Message.Should().Contain("provider 'p'");
    }
}