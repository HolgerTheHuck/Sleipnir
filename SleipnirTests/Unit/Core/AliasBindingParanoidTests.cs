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
/// Paranoid-Binding-Modus — die fail-lauteste Variante des @alias-/Literal-Transfers.
///
/// Paranoid ist ein Superset von Strict und schließt dessen zwei Lücken: (a) es prüft
/// <b>alle</b> Parameter — <c>@alias</c>-sourced <i>und</i> Literale — und (b) es prüft
/// <b>rekursiv</b>, steigt also in verschachtelte Objekte und Array-Elemente ab. Jede
/// public read-write Eigenschaft des Consumer-Typs, in jeder Tiefe, muß im Fragment
/// vorhanden sein, sonst 400. Diese Tests stellen sicher, daß Paranoid genau die beiden
/// stillen Lücken von Strict (Literale + verschachtelte fehlende Werttypen) laut macht,
/// dabei aber die sichere Subset-Richtung, Widening, cross-kind (STJ) und Skalare unangetastet
/// läßt. Siehe DEPENDENCY_BINDING.md §7.
/// </summary>
public class AliasBindingParanoidTests
{
    private readonly SleipnirInvoker _invoker;

    public AliasBindingParanoidTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTransient<DependencyChainController>();
        var sp = services.BuildServiceProvider();
        _invoker = new SleipnirInvoker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<SleipnirInvoker>>());
        _invoker.Register<DependencyChainController>();
        _invoker.AliasBindingMode = AliasBindingMode.Paranoid; // Paranoid aktiviert
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

    /// <summary>Ein Literal-Parameter: der JSON-Wert direkt (kein @alias).</summary>
    private static (string, string) Lit(string paramName, string jsonLiteral) => (paramName, jsonLiteral);

    /// <summary>Ein @alias-Parameter: roher @name-String (kein JSON-Quote, sonst erkennt der
    ///  Server das @ nicht). Siehe AliasBindingTests für die Begründung.</summary>
    private static (string, string) Alias(string paramName, string alias) => (paramName, $"@{alias}");

    private async Task<SleipnirResponse> RunSingle(SleipnirRequest req)
    {
        var responses = (await _invoker.InvokeDi(new[] { req }, null, ExecutionMode.Serial)).ToList();
        return responses.Single(r => r?.Id == req.Id)!;
    }

    // === Neu gegenüber Strict: Literale werden geprüft ===========================

    [Fact]
    public async Task Paranoid_LiteralMissingValueType_Rejects400()
    {
        // TakeIdActive(int Id, bool Active) mit Literal {id:7} — Active (Werttyp) fehlt.
        // Weak UND Strict lassen das still durch (Active=false); Paranoid prüft Literale.
        var res = await RunSingle(Req("c", "DepChain", "TakeIdActive", null,
            Lit("d", """{"id":7}""")));

        res.Code.Should().Be((int)HttpStatusCode.BadRequest);
        res.Error!.Message.Should().Contain("Paranoid binding");
        res.Error.Message.Should().Contain("Active");
        res.Error.Message.Should().Contain("'d'");
    }

    [Fact]
    public async Task Paranoid_LiteralMissingReference_Rejects400()
    {
        // DescribeDto(TestDto{Id,Name}) mit Literal {id:7} — Name (Referenz) fehlt.
        // (Seit D5 werden Wire-Namen gemeldet: camelCase "name".)
        var res = await RunSingle(Req("c", "DepChain", "DescribeDto", null,
            Lit("d", """{"id":7}""")));

        res.Code.Should().Be((int)HttpStatusCode.BadRequest);
        res.Error!.Message.Should().Contain("name");
    }

    // === Neu gegenüber Strict: rekursive Tiefe in verschachtelte Objekte =========

    [Fact]
    public async Task Paranoid_LiteralNestedMissingZip_Rejects400()
    {
        // TakeOrder(OrderDto{Id, Address{Street,Zip}}) mit Literal {id:1,address:{street:"A"}}.
        // Top-Level: Id + Address vorhanden (Strict wäre hier fertig). Paranoid steigt in
        // Address ab und findet das fehlende Zip (Werttyp) → 400.
        // (Seit D5 werden Wire-Namen gemeldet: camelCase "address.zip".)
        var res = await RunSingle(Req("c", "DepChain", "TakeOrder", null,
            Lit("o", """{"id":1,"address":{"street":"A"}}""")));

        res.Code.Should().Be((int)HttpStatusCode.BadRequest);
        res.Error!.Message.Should().Contain("Paranoid binding");
        res.Error.Message.Should().Contain("zip");
        res.Error.Message.Should().Contain("address.zip");
    }

    [Fact]
    public async Task Paranoid_LiteralNestedFull_Binds()
    {
        // Vollständiges verschachteltes Literal → nichts fehlt → bindet, korrektes Zip.
        var res = await RunSingle(Req("c", "DepChain", "TakeOrder", null,
            Lit("o", """{"id":1,"address":{"street":"A","zip":5}}""")));

        res.Code.Should().Be((int)HttpStatusCode.OK);
        res.Data.Value.Deserialize<string>().Should().Be("1/A/5");
    }

    // === Neu gegenüber Strict: Array-Elemente werden rekursiv geprüft ============

    [Fact]
    public async Task Paranoid_LiteralArrayElementMissingNested_Rejects400()
    {
        // TakeOrderList(List<OrderDto>) mit Literal-Array; zweites Element fehlt Zip.
        // Paranoid steigt in jedes Element ab; Element [1].Address.Zip fehlt → 400.
        var res = await RunSingle(Req("c", "DepChain", "TakeOrderList", null,
            Lit("list", """[{"id":1,"address":{"street":"A","zip":1}},{"id":2,"address":{"street":"B"}}]""")));

        res.Code.Should().Be((int)HttpStatusCode.BadRequest);
        res.Error!.Message.Should().Contain("Paranoid binding");
        res.Error.Message.Should().Contain("list[1]");
        res.Error.Message.Should().Contain("zip");
    }

    [Fact]
    public async Task Paranoid_LiteralArrayFull_Binds()
    {
        // Vollständiges Array → jedes Element voll gedeckt → bindet, Länge 2.
        var res = await RunSingle(Req("c", "DepChain", "TakeOrderList", null,
            Lit("list", """[{"id":1,"address":{"street":"A","zip":1}},{"id":2,"address":{"street":"B","zip":2}}]""")));

        res.Code.Should().Be((int)HttpStatusCode.OK);
        res.Data.Value.Deserialize<int>().Should().Be(2);
    }

    // === Strict läßt beide Lücken noch durch (beweist den Delta) =================
    // Diese Tests laufen gegen einen frischen Strict-Invoker und zeigen: genau die
    // Fälle, die Paranoid oben ablehnt, laufen in Strict (flach, nur @alias) noch still durch.

    [Fact]
    public async Task Strict_LiteralMissingValueType_Still2xx()
    {
        var invoker = NewInvoker(AliasBindingMode.Strict);
        var responses = (await invoker.InvokeDi(
            new[] { Req("c", "DepChain", "TakeIdActive", null, Lit("d", """{"id":7}""")) },
            null, ExecutionMode.Serial)).ToList();
        var res = responses.Single(r => r?.Id == "c")!;

        res.Code.Should().Be((int)HttpStatusCode.OK);
        res.Data.Value.Deserialize<string>().Should().Be("7/False"); // Active still auf false
    }

    [Fact]
    public async Task Strict_LiteralNestedMissingZip_Still2xx()
    {
        var invoker = NewInvoker(AliasBindingMode.Strict);
        var responses = (await invoker.InvokeDi(
            new[] { Req("c", "DepChain", "TakeOrder", null, Lit("o", """{"id":1,"address":{"street":"A"}}""")) },
            null, ExecutionMode.Serial)).ToList();
        var res = responses.Single(r => r?.Id == "c")!;

        // Strict prüft nur Top-Level (Id + Address vorhanden) und nur @alias — hier ist es
        // ein Literal, also gar kein Check. Zip still 0, 2xx.
        res.Code.Should().Be((int)HttpStatusCode.OK);
        res.Data.Value.Deserialize<string>().Should().Be("1/A/0");
    }

    // === @alias-Pfad: Paranoid ist Superset von Strict (rekursiv auf Aliasen) ====

    [Fact]
    public async Task Paranoid_AliasMissingValueType_Rejects400()
    {
        // Provider IdOnly {Id} → Consumer IdActive {Id,Active} per @alias.
        var provider = Req("p", "DepChain", "MakeIdOnly", new() { ["cust"] = "$" }, ("id", "7"));
        var consumer = Req("c", "DepChain", "TakeIdActive", null, Alias("d", "cust"));

        var res = await RunBatch(provider, consumer);

        res.Code.Should().Be((int)HttpStatusCode.BadRequest);
        res.Error!.Message.Should().Contain("Paranoid binding");
        res.Error.Message.Should().Contain("Active");
    }

    [Fact]
    public async Task Paranoid_AliasSubsetFanOut_Binds()
    {
        // Das Nutzer-Muster bleibt erlaubt: ein ganzer Customer → IdOnly UND NameOnly.
        var provider = Req("p", "DepChain", "MakeDto", new() { ["cust"] = "$" },
            ("id", "7"), ("name", JsonSerializer.Serialize("alice")));
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
    public async Task Paranoid_AliasNestedMissingZip_Rejects400()
    {
        // Provider MakeOrderNoZip (Dictionary → JSON ohne zip) → Consumer TakeOrder per @alias.
        // Paranoid steigt auf dem Alias-Pfad rekursiv in Address ab und findet fehlendes Zip.
        // (Seit D5 werden Wire-Namen gemeldet: camelCase "address.zip".)
        var provider = Req("p", "DepChain", "MakeOrderNoZip", new() { ["order"] = "$" },
            ("id", "1"), ("street", JsonSerializer.Serialize("A")));
        var consumer = Req("c", "DepChain", "TakeOrder", null, Alias("o", "order"));

        var res = await RunBatch(provider, consumer);

        res.Code.Should().Be((int)HttpStatusCode.BadRequest);
        res.Error!.Message.Should().Contain("Paranoid binding");
        res.Error.Message.Should().Contain("address.zip");
    }

    [Fact]
    public async Task Paranoid_AliasNestedFull_Binds()
    {
        // Provider MakeOrder (vollständig) → Consumer TakeOrder per @alias → bindet, Zip=5.
        var provider = Req("p", "DepChain", "MakeOrder", new() { ["order"] = "$" },
            ("id", "1"), ("street", JsonSerializer.Serialize("A")), ("zip", "5"));
        var consumer = Req("c", "DepChain", "TakeOrder", null, Alias("o", "order"));

        var res = await RunBatch(provider, consumer);

        res.Code.Should().Be((int)HttpStatusCode.OK);
        res.Data.Value.Deserialize<string>().Should().Be("1/A/5");
    }

    [Fact]
    public async Task Paranoid_AliasCrossKind_Still400()
    {
        // Provider $.id (number) → Consumer EchoString (string) — cross-kind, STJ wirft.
        var provider = Req("p", "DepChain", "MakeDto", new() { ["cid"] = "$.id" },
            ("id", "7"), ("name", JsonSerializer.Serialize("alice")));
        var consumer = Req("c", "DepChain", "EchoString", null, Alias("value", "cid"));

        var res = await RunBatch(provider, consumer);

        res.Code.Should().Be((int)HttpStatusCode.BadRequest);
        res.Error!.Message.Should().Contain("cannot be converted");
    }

    // === Invarianten: Skalare, Widening, Subset bleiben erlaubt ==================

    [Fact]
    public async Task Paranoid_ScalarLiteral_NotOverChecked()
    {
        // EchoInt(int) mit Literal 7 — Skalar hat keine deckbaren Eigenschaften; Paranoid
        // darf keine falschen 400 werfen.
        var res = await RunSingle(Req("c", "DepChain", "EchoInt", null, Lit("value", "7")));

        res.Code.Should().Be((int)HttpStatusCode.OK);
        res.Data.Value.Deserialize<int>().Should().Be(7);
    }

    [Fact]
    public async Task Paranoid_Widening_IntToLong_Allowed()
    {
        // EchoLong(long) mit Literal 7 (int) — Widening bleibt erlaubt; long hat keine
        // deckbaren Eigenschaften.
        var res = await RunSingle(Req("c", "DepChain", "EchoLong", null, Lit("value", "7")));

        res.Code.Should().Be((int)HttpStatusCode.OK);
        res.Data.Value.Deserialize<long>().Should().Be(7L);
    }

    [Fact]
    public async Task Paranoid_SubsetLiteral_Binds()
    {
        // TakeIdOnly(IdOnly{Id}) mit Literal {id:1,name:"x"} — Consumer ⊆ Literal, nichts
        // fehlt (extra name fällt weg). Subset-Fan-out funktioniert auch bei Literalen.
        var res = await RunSingle(Req("c", "DepChain", "TakeIdOnly", null,
            Lit("d", """{"id":1,"name":"x"}""")));

        res.Code.Should().Be((int)HttpStatusCode.OK);
        res.Data.Value.Deserialize<int>().Should().Be(1);
    }

    [Fact]
    public async Task Paranoid_StringListLiteral_Binds()
    {
        // List<int> mit Literal-Array von Skalaren — Element-Typ int hat keine deckbaren
        // Eigenschaften; Paranoid steigt nicht ab; bindet.
        var res = await RunSingle(Req("c", "DepChain", "EchoIntList", null,
            Lit("values", "[1,2,3]")));

        res.Code.Should().Be((int)HttpStatusCode.OK);
        res.Data.Value.Deserialize<int[]>().Should().Equal(1, 2, 3);
    }

    // === Hilfsmethoden ===========================================================

    private async Task<SleipnirResponse> RunBatch(params SleipnirRequest[] batch)
    {
        var responses = (await _invoker.InvokeDi(batch, null, ExecutionMode.Serial)).ToList();
        return responses.Single(r => r?.Id == batch[^1].Id)!;
    }

    private static SleipnirInvoker NewInvoker(AliasBindingMode mode)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTransient<DependencyChainController>();
        var sp = services.BuildServiceProvider();
        var invoker = new SleipnirInvoker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<SleipnirInvoker>>());
        invoker.Register<DependencyChainController>();
        invoker.AliasBindingMode = mode;
        return invoker;
    }
}