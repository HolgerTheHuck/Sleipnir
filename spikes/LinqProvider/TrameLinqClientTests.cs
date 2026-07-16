using FluentAssertions;
using Trame.Model;
using Trame.Spike.LinqProvider;
using TrameClient.Trame;
using TrameCommon.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using System.Text.Json;
using Xunit;

namespace Trame.Spike.LinqProvider.Tests;

/// <summary>
/// In-memory Integrationstests für den LINQ-Provider-Spike gegen die Sample-App
/// (WebApplicationFactory&lt;Program&gt;). Sie nutzen die jetzt reparierte
/// Dependency-Chaining-Basis: ein typsicherer Batch mit einem numerischen Dep
/// (AddCustomer → id → GetCustomerById) und einem String-Dep über $.Name.
/// </summary>
public class TrameLinqClientTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public TrameLinqClientTests(WebApplicationFactory<Program> factory)
    {
        // Die Sample-App registriert [TrameController]-Typen (z. B. CustomerHandler)
        // per Auto-Scan in AddTrame — kein explizites Register nötig.
        _factory = factory;
    }

    private TrameLinqClient CreateClient()
    {
        // TrameRestJsonClient baut absolute URIs aus serverBaseUrl. Damit das
        // in-memory gegen den TestServer (WebApplicationFactory) läuft, muss
        // serverBaseUrl der BaseAddress des Factory-Clients entsprechen.
        var http = _factory.CreateClient();
        var baseAddr = http.BaseAddress?.ToString() ?? "http://localhost/";
        var restClient = new TrameRestJsonClient(baseAddr, http);
        return new TrameLinqClient(restClient);
    }

    [Fact]
    public async Task Build_und_SendAsync_baut_typisierten_Call_und_liefert_T()
    {
        // Arrange
        var client = CreateClient();
        var spec = client.Build((ICustomerService c) => c.AddCustomer("LinqSingle"));

        // Act
        var newId = await client.SendAsync(spec);

        // Assert
        newId.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Batch_mit_numerischem_Dep_verdrahtet_typsicher()
    {
        // Arrange: AddCustomer → id (Dep<int>) → GetCustomerById(@id). Die @alias-
        // Substitution und der topologische Batch-Pfad werden serverseitig geprüft;
        // der Spike stellt nur sicher, dass die Dep-Verdrahtung typsicher aus dem
        // C#-Code heraus entsteht (keine handgebauten JSON-Platzhalter).
        var client = CreateClient();
        var create = client.Build((ICustomerService c) => c.AddCustomer("LinqChain"));
        Dep<int> newId = create.Expose(); // Ganzes Resultat ($) → int
        var fetch = client.Build((ICustomerService c) => c.GetCustomerById(newId));

        var batch = new TrameBatch(create, fetch);

        // Act
        var responses = await client.SendAsync(batch);

        // Assert
        responses.Should().HaveCount(2);
        var fetched = client.ResultOf<Customer>(fetch, responses);
        fetched.Should().NotBeNull();
        fetched!.Name.Should().Be("LinqChain");
    }

    [Fact]
    public async Task Batch_mit_StringDep_ueber_PropertyPfad_liefert_Wert_intakt()
    {
        // Arrange: AddCustomer → Customer.Id (hier ganze $) → GetCustomerById →
        // Customer.Name ($.Name) → AddCustomer(@srcName). Die String-Substitution
        // muss die Quotes erhalten (typgetreue @alias-Substitution serverseitig).
        var client = CreateClient();
        var create = client.Build((ICustomerService c) => c.AddCustomer("SrcLinq"));
        Dep<int> createdId = create.Expose();
        var fetch = client.Build((ICustomerService c) => c.GetCustomerById(createdId));
        Dep<string> srcName = fetch.Expose(c => c.Name); // → "$.Name"
        var readd = client.Build((ICustomerService c) => c.AddCustomer(srcName));

        var batch = new TrameBatch(create, fetch, readd);

        // Act
        var responses = await client.SendAsync(batch);

        // Assert
        responses.Should().HaveCount(3);
        var readdedId = client.ResultOf<int>(readd, responses);
        readdedId.Should().BeGreaterThan(0);

        // Der zweite Kunde muss denselben Namen tragen — nur wenn der String-Dep
        // intakt durchgereicht wurde (Quotes erhalten, kein ToString-Stripping).
        var verify = client.Build((ICustomerService c) => c.GetCustomerById(readdedId));
        var secondCustomer = await client.SendAsync(verify);
        secondCustomer.Should().NotBeNull();
        secondCustomer!.Name.Should().Be("SrcLinq");
    }

    [Fact]
    public void ContractGenerator_erzeugt_Vertrag_fuer_Customer_aus_Discovery()
    {
        // Arrange
        var discovery = File.ReadAllText("discovery.sample.json");

        // Act
        var source = ContractGenerator.Generate(discovery);

        // Assert: die erzeugten Verträge tragen die Contract-Attribute und
        // spiegeln die Methoden des Customer-Controllers mit korrekten Typen.
        source.Should().Contain("[TrameServiceContract(\"Customer\")]");
        source.Should().Contain("interface ICustomerService");
        source.Should().Contain("[TrameMethodContract(\"AddCustomer\")]");
        source.Should().Contain("Task<int?> AddCustomer(Arg<string> name);");
        source.Should().Contain("[TrameMethodContract(\"GetCustomerById\")]");
        source.Should().Contain("Task<Customer?> GetCustomerById(Arg<int> id);");
        source.Should().Contain("[TrameMethodContract(\"UpdateCustomerName\")]");
        source.Should().Contain("Task UpdateCustomerName(Arg<int> customerId, Arg<string> newName);");
    }

    [Fact]
    public void Expose_baut_ergebnisrelativen_JsonPath_aus_Selector()
    {
        // Arrange: Customer-Resultat (GetCustomerById → Customer) liefert Name.
        var client = CreateClient();
        var fetch = client.Build((ICustomerService c) => c.GetCustomerById(1));

        // Act
        var whole = fetch.Expose();                       // "$" (ganzes Customer-Objekt)
        var name = fetch.Expose(c => c!.Name);            // "$.name" (camelCase-Wire)

        // Assert: die Pfade landen im dependencyMapping (ergebnisrelativ, camelCase
        // gegen das Wire-Dokument — der Server serialisiert CamelCase, JsonPath ist
        // case-sensitiv).
        fetch.DependencyMapping.Should().NotBeNull();
        fetch.DependencyMapping!.Values.Should().Contain("$");
        fetch.DependencyMapping!.Values.Should().Contain("$.name");

        // Deps tragen eindeutige Aliase.
        whole.Alias.Should().NotBe(name.Alias);
        whole.ToString().Should().StartWith("@");
    }
}