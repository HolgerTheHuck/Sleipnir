using FluentAssertions;
using TrameClient.Trame;
using TrameCommon.Models;
using TrameCore.Services;
using TrameTests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace TrameTests.Integration;

/// <summary>
/// Integration tests for the REST transport (POST /api/trame/json, /api/trame/json/multi, GET /api/trame/discovery).
/// Uses WebApplicationFactory to spin up the sample app in-memory.
/// </summary>
public class RestTransportTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public RestTransportTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // Ensure the test controller is registered
                services.AddTransient<TestInvokerController>();
                services.AddTransient<DependencyChainController>();
                var sp = services.BuildServiceProvider();
                var invoker = sp.GetRequiredService<ITrameCore>();
                invoker.Register<TestInvokerController>();
                invoker.Register<DependencyChainController>();
            });
        });
        _client = _factory.CreateClient();
    }

    private static StringContent CreateJsonContent(object payload)
        => new(JsonSerializer.Serialize(payload, _jsonOpts), Encoding.UTF8, "application/json");

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

    [Fact]
    public async Task Discovery_Returns200_AndContainsControllers()
    {
        // Act
        var response = await _client.GetAsync("/api/trame/discovery");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task SingleCall_Echo_Returns200()
    {
        // Arrange
        var request = CreateRequest("TestService", "GetAdresse",
            ("id", "1"), ("greet", "\"Hello\""));

        // Act
        var response = await _client.PostAsync("/api/trame/json", CreateJsonContent(request));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SingleCall_CustomerGetAll_Returns200()
    {
        // Arrange
        var request = CreateRequest("Customer", "GetAllCustomers");

        // Act
        var response = await _client.PostAsync("/api/trame/json", CreateJsonContent(request));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task MultiCall_Parallel_Returns200()
    {
        // Arrange
        var multiRequest = new TrameMultiRequest
        {
            Mode = ExecutionMode.Parallel,
            Requests = new List<TrameRequest>
            {
                CreateRequest("Customer", "GetAllCustomers"),
                CreateRequest("Customer", "GetAllCustomers")
            }
        };

        // Act
        var response = await _client.PostAsync("/api/trame/json/multi", CreateJsonContent(multiRequest));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task MultiCall_Serial_Returns200()
    {
        // Arrange
        var multiRequest = new TrameMultiRequest
        {
            Mode = ExecutionMode.Serial,
            Requests = new List<TrameRequest>
            {
                CreateRequest("Customer", "GetAllCustomers"),
                CreateRequest("Customer", "GetAllCustomers")
            }
        };

        // Act
        var response = await _client.PostAsync("/api/trame/json/multi", CreateJsonContent(multiRequest));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task MultiCall_EmptyRequests_Returns400()
    {
        // Arrange
        var multiRequest = new TrameMultiRequest
        {
            Mode = ExecutionMode.Parallel,
            Requests = new List<TrameRequest>()
        };

        // Act
        var response = await _client.PostAsync("/api/trame/json/multi", CreateJsonContent(multiRequest));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SingleCall_FluentPositional_BindsByIndex()
    {
        // Arrange: the fluent builder emits param0/param1; the server must bind them
        // positionally and return the greet value echoed from GetAdresse.
        var request = TrameCall.Init("TestService", "GetAdresse")
            .With(1, "Hi")
            .ToRequest();

        // Act
        var response = await _client.PostAsync("/api/trame/json", CreateJsonContent(request));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var trameResp = JsonSerializer.Deserialize<TrameResponse>(content, _jsonOpts);
        trameResp!.Code.Should().Be(200);
        trameResp.Data.Value.GetRawText().Should().Contain("\"greet\":\"Hi\"");
        trameResp.Data.Value.GetRawText().Should().Contain("\"id\":1");
    }

    [Fact]
    public async Task SingleCall_FluentNamed_BindsByName()
    {
        // Arrange: named parameters bind by explicit name, independent of order.
        var request = TrameCall.Init("TestService", "GetAdresse")
            .Param("greet", "Hola")
            .Param("id", 2)
            .ToRequest();

        // Act
        var response = await _client.PostAsync("/api/trame/json", CreateJsonContent(request));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var trameResp = JsonSerializer.Deserialize<TrameResponse>(content, _jsonOpts);
        trameResp!.Code.Should().Be(200);
        trameResp.Data.Value.GetRawText().Should().Contain("\"greet\":\"Hola\"");
        trameResp.Data.Value.GetRawText().Should().Contain("\"id\":2");
    }

    /// <summary>
    /// End-to-end numeric dependency chain via REST /json/multi:
    /// AddCustomer(name) -> int id, exposes the whole result ("$") as "newId";
    /// GetCustomerById(@newId) -> Customer. Da step1 ein DependencyMapping deklariert,
    /// routed der Server automatisch über den topologischen Batch-Pfad
    /// (ExecuteInDependencyBatches), der @alias-Platzhalter gegen die Responses
    /// VORHERIGER Batches auflösen muss. Regression-Test für zwei früher kaputte
    /// Server-Bugs: (1) der topologische Pfad löste @alias gar nicht auf, (2) die
    /// Substitution stringifizierte Werte, sodass numerische Parameter brachen.
    /// </summary>
    [Fact]
    public async Task DependencyChain_NumericAlias_AutoDetect_ResolvesAndReturnsCustomer()
    {
        // Arrange
        var step1 = new TrameRequest
        {
            Controller = "Customer",
            Method = "AddCustomer",
            Params = JsonSerializer.SerializeToNode(new List<TrameParameter>
            {
                new() { ParameterName = "name", Data = JsonNode.Parse("\"DepChainIT\"") }
            }),
            Id = "chain-step1",
            DependencyMapping = new Dictionary<string, string> { { "newId", "$" } }
        };
        var step2 = new TrameRequest
        {
            Controller = "Customer",
            Method = "GetCustomerById",
            Params = JsonSerializer.SerializeToNode(new List<TrameParameter>
            {
                new() { ParameterName = "id", Data = JsonValue.Create("@newId") }
            }),
            Id = "chain-step2"
        };
        var multi = new TrameMultiRequest
        {
            // Mode ist hier bedeutungslos — das DependencyMapping triggert Auto-Detect
            // und damit den topologischen Batch-Pfad unabhängig vom angegebenen Mode.
            Mode = ExecutionMode.Parallel,
            Requests = new List<TrameRequest> { step1, step2 }
        };

        // Act
        var response = await _client.PostAsync("/api/trame/json/multi", CreateJsonContent(multi));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var responses = JsonSerializer.Deserialize<List<TrameResponse>>(content, _jsonOpts);
        responses.Should().NotBeNull().And.HaveCount(2);
        var byId = responses!.ToDictionary(r => r.Id ?? string.Empty);

        byId["chain-step1"].Code.Should().Be(200);
        byId["chain-step1"].ExposedDependencies.Should().NotBeNull();
        byId["chain-step1"].ExposedDependencies!.Should().ContainKey("newId");

        byId["chain-step2"].Code.Should().Be(200);
        byId["chain-step2"].Error.Should().BeNull();
        // Der @newId-Platzhalter muss durch die numerische Id aus step1 ersetzt worden
        // sein — sonst läge hier ein 500 (NullRef bei int-Unboxing) oder ein 400 vor.
        byId["chain-step2"].Data.Value.GetRawText().Should().Contain("\"name\":\"DepChainIT\"");
    }

    /// <summary>
    /// Drei-Stufen-Chain, die zusätzlich zur numerischen ($) auch eine String-Dependency
    /// über einen ergebnisrelativen Eigenschafts-Pfad ($.name) prüft:
    ///   step1: AddCustomer("SrcNameIT") -> int id; expose newId = "$"
    ///   step2: GetCustomerById(@newId) -> Customer { Name }; expose srcName = "$.name"
    ///   step3: AddCustomer(@srcName)   -> int id (zweiter Kunde mit gleichem Namen)
    /// Stellt sicher, dass die typgetreue Substitution auch String-Werte intakt
    /// transportiert — der JSON-String "\"SrcNameIT\"" (mit Quotes) muss erhalten
    /// bleiben, sonst bricht die Deserialisierung in den string-Parameter "name".
    /// </summary>
    [Fact]
    public async Task DependencyChain_StringAlias_PropertyPath_PreservesValue()
    {
        // Arrange
        var step1 = new TrameRequest
        {
            Controller = "Customer",
            Method = "AddCustomer",
            Params = JsonSerializer.SerializeToNode(new List<TrameParameter>
            {
                new() { ParameterName = "name", Data = JsonNode.Parse("\"SrcNameIT\"") }
            }),
            Id = "str-step1",
            DependencyMapping = new Dictionary<string, string> { { "newId", "$" } }
        };
        var step2 = new TrameRequest
        {
            Controller = "Customer",
            Method = "GetCustomerById",
            Params = JsonSerializer.SerializeToNode(new List<TrameParameter>
            {
                new() { ParameterName = "id", Data = JsonValue.Create("@newId") }
            }),
            Id = "str-step2",
            DependencyMapping = new Dictionary<string, string> { { "srcName", "$.name" } }
        };
        var step3 = new TrameRequest
        {
            Controller = "Customer",
            Method = "AddCustomer",
            Params = JsonSerializer.SerializeToNode(new List<TrameParameter>
            {
                new() { ParameterName = "name", Data = JsonValue.Create("@srcName") }
            }),
            Id = "str-step3"
        };
        var multi = new TrameMultiRequest
        {
            Mode = ExecutionMode.Parallel,
            Requests = new List<TrameRequest> { step1, step2, step3 }
        };

        // Act
        var response = await _client.PostAsync("/api/trame/json/multi", CreateJsonContent(multi));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var responses = JsonSerializer.Deserialize<List<TrameResponse>>(content, _jsonOpts);
        responses.Should().NotBeNull().And.HaveCount(3);
        var byId = responses!.ToDictionary(r => r.Id ?? string.Empty);

        byId["str-step1"].Code.Should().Be(200);
        byId["str-step2"].Code.Should().Be(200);
        byId["str-step2"].ExposedDependencies.Should().NotBeNull();
        // $.name liefert den JSON-String "\"SrcNameIT\"" (mit Quotes) — nur so
        // übersteht die Substitution für ein string-Ziel intakt.
        byId["str-step2"].ExposedDependencies!["srcName"].Should().Be("\"SrcNameIT\"");

        // step3 muss erfolgreich sein und einen neuen Kunden mit dem übernommenen
        // Namen angelegt haben — ein kaputtes String-Handling (fehlende Quotes)
        // würde hier in einem 400/500 enden.
        byId["str-step3"].Code.Should().Be(200);
        byId["str-step3"].Error.Should().BeNull();
        byId["str-step3"].Data.Should().NotBeNull();
    }

    /// <summary>
    /// Ganzes Objekt als Dependency über HTTP: MakeDto(7,"Alice") exposes "$" als "dto";
    /// EchoDto(@dto) muss das Objekt mit Id=7 zurückgeben. End-to-End-Guard für die
    /// Objekt-Substitution über den REST-/json/multi-Pfad (topologischer Auto-Detect).
    /// </summary>
    [Fact]
    public async Task DependencyChain_ObjectWholeResult_AutoDetect_ResolvesOverHttp()
    {
        // Arrange
        var step1 = new TrameRequest
        {
            Controller = "DepChain",
            Method = "MakeDto",
            Params = JsonSerializer.SerializeToNode(new List<TrameParameter>
            {
                new() { ParameterName = "id", Data = JsonNode.Parse("7") },
                new() { ParameterName = "name", Data = JsonNode.Parse("\"Alice\"") }
            }),
            Id = "obj-s1",
            DependencyMapping = new Dictionary<string, string> { { "dto", "$" } }
        };
        var step2 = new TrameRequest
        {
            Controller = "DepChain",
            Method = "EchoDto",
            Params = JsonSerializer.SerializeToNode(new List<TrameParameter>
            {
                new() { ParameterName = "dto", Data = JsonValue.Create("@dto") }
            }),
            Id = "obj-s2"
        };
        var multi = new TrameMultiRequest
        {
            Mode = ExecutionMode.Parallel,
            Requests = new List<TrameRequest> { step1, step2 }
        };

        // Act
        var response = await _client.PostAsync("/api/trame/json/multi", CreateJsonContent(multi));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var responses = JsonSerializer.Deserialize<List<TrameResponse>>(content, _jsonOpts);
        var byId = responses!.ToDictionary(r => r.Id ?? string.Empty);
        byId["obj-s2"].Code.Should().Be(200);
        byId["obj-s2"].Error.Should().BeNull();
        var dto = byId["obj-s2"].Data.Value.Deserialize<TestDto>(_jsonOpts);
        dto!.Id.Should().Be(7);
        dto.Name.Should().Be("Alice");
    }

    /// <summary>
    /// Ganze Collection als Dependency über HTTP: MakeDtoList() exposes "$" als "dtos";
    /// CountDtoList(@dtos) muss 3 liefern. End-to-End-Guard für Array-Substitution.
    /// </summary>
    [Fact]
    public async Task DependencyChain_ArrayWholeResult_AutoDetect_ResolvesOverHttp()
    {
        // Arrange
        var step1 = new TrameRequest
        {
            Controller = "DepChain",
            Method = "MakeDtoList",
            Params = JsonNode.Parse("[]"),
            Id = "arr-s1",
            DependencyMapping = new Dictionary<string, string> { { "dtos", "$" } }
        };
        var step2 = new TrameRequest
        {
            Controller = "DepChain",
            Method = "CountDtoList",
            Params = JsonSerializer.SerializeToNode(new List<TrameParameter>
            {
                new() { ParameterName = "dtos", Data = JsonValue.Create("@dtos") }
            }),
            Id = "arr-s2"
        };
        var multi = new TrameMultiRequest
        {
            Mode = ExecutionMode.Parallel,
            Requests = new List<TrameRequest> { step1, step2 }
        };

        // Act
        var response = await _client.PostAsync("/api/trame/json/multi", CreateJsonContent(multi));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var responses = JsonSerializer.Deserialize<List<TrameResponse>>(content, _jsonOpts);
        var byId = responses!.ToDictionary(r => r.Id ?? string.Empty);
        byId["arr-s2"].Code.Should().Be(200);
        byId["arr-s2"].Error.Should().BeNull();
        byId["arr-s2"].Data.Value.Deserialize<int>().Should().Be(3);
    }

    /// <summary>
    /// Mehrere @alias-Platzhalter in einen Request aus zwei Vorläufern über HTTP
    /// (Diamond-Verbraucher): step1 exposes "$.id" als "id", step2 exposes "$.name" als
    /// "name", step3 MakeDto(@id, @name) kombiniert beide.
    /// </summary>
    [Fact]
    public async Task DependencyChain_MultipleAliases_AutoDetect_ResolvesOverHttp()
    {
        // Arrange
        var step1 = new TrameRequest
        {
            Controller = "DepChain",
            Method = "MakeDto",
            Params = JsonSerializer.SerializeToNode(new List<TrameParameter>
            {
                new() { ParameterName = "id", Data = JsonNode.Parse("7") },
                new() { ParameterName = "name", Data = JsonNode.Parse("\"Alice\"") }
            }),
            Id = "ma-s1",
            DependencyMapping = new Dictionary<string, string> { { "id", "$.id" } }
        };
        var step2 = new TrameRequest
        {
            Controller = "DepChain",
            Method = "MakeDto",
            Params = JsonSerializer.SerializeToNode(new List<TrameParameter>
            {
                new() { ParameterName = "id", Data = JsonNode.Parse("99") },
                new() { ParameterName = "name", Data = JsonNode.Parse("\"Bob\"") }
            }),
            Id = "ma-s2",
            DependencyMapping = new Dictionary<string, string> { { "name", "$.name" } }
        };
        var step3 = new TrameRequest
        {
            Controller = "DepChain",
            Method = "MakeDto",
            Params = JsonSerializer.SerializeToNode(new List<TrameParameter>
            {
                new() { ParameterName = "id", Data = JsonValue.Create("@id") },
                new() { ParameterName = "name", Data = JsonValue.Create("@name") }
            }),
            Id = "ma-s3"
        };
        var multi = new TrameMultiRequest
        {
            Mode = ExecutionMode.Parallel,
            Requests = new List<TrameRequest> { step1, step2, step3 }
        };

        // Act
        var response = await _client.PostAsync("/api/trame/json/multi", CreateJsonContent(multi));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var responses = JsonSerializer.Deserialize<List<TrameResponse>>(content, _jsonOpts);
        var byId = responses!.ToDictionary(r => r.Id ?? string.Empty);
        byId["ma-s3"].Code.Should().Be(200);
        byId["ma-s3"].Error.Should().BeNull();
        var dto = byId["ma-s3"].Data.Value.Deserialize<TestDto>(_jsonOpts);
        dto!.Id.Should().Be(7);
        dto.Name.Should().Be("Bob");
    }

    /// <summary>
    /// Nicht auflösbarer @alias über HTTP: step2 fragt "@nonexistent" ab, den kein
    /// Vorläufer exposed. Der /json/multi-Endpoint liefert HTTP 200 mit pro-Response-
    /// Codes; step2 muss 400 tragen (kein 500, keine Server-Exception).
    /// </summary>
    [Fact]
    public async Task DependencyChain_UnresolvedAlias_Returns400OverHttp()
    {
        // Arrange
        var step1 = new TrameRequest
        {
            Controller = "DepChain",
            Method = "MakeDto",
            Params = JsonSerializer.SerializeToNode(new List<TrameParameter>
            {
                new() { ParameterName = "id", Data = JsonNode.Parse("7") },
                new() { ParameterName = "name", Data = JsonNode.Parse("\"Alice\"") }
            }),
            Id = "un-s1",
            DependencyMapping = new Dictionary<string, string> { { "id", "$.id" } }
        };
        var step2 = new TrameRequest
        {
            Controller = "DepChain",
            Method = "EchoLong",
            Params = JsonSerializer.SerializeToNode(new List<TrameParameter>
            {
                new() { ParameterName = "value", Data = JsonValue.Create("@nonexistent") }
            }),
            Id = "un-s2"
        };
        var multi = new TrameMultiRequest
        {
            Mode = ExecutionMode.Parallel,
            Requests = new List<TrameRequest> { step1, step2 }
        };

        // Act
        var response = await _client.PostAsync("/api/trame/json/multi", CreateJsonContent(multi));

        // Assert — Endpoint bleibt 200, der Fehler steckt in der Einzel-Response
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var responses = JsonSerializer.Deserialize<List<TrameResponse>>(content, _jsonOpts);
        var byId = responses!.ToDictionary(r => r.Id ?? string.Empty);
        byId["un-s1"].Code.Should().Be(200);
        byId["un-s2"].Code.Should().Be(400);
        byId["un-s2"].Error.Should().NotBeNull();
    }

    /// <summary>
    /// Mode=Serial mit DependencyMapping über HTTP: Auto-Detect übersteuert auf den
    /// topologischen Pfad, die Chain muss trotzdem korrekt auflösen. Garantiert, dass
    /// ein Client, der Serial anfordert und trotzdem Dependencies deklariert, keine
    /// kaputte Antwort bekommt.
    /// </summary>
    [Fact]
    public async Task DependencyChain_ModeSerial_WithMapping_ResolvesOverHttp()
    {
        // Arrange
        var step1 = new TrameRequest
        {
            Controller = "DepChain",
            Method = "EchoLong",
            Params = JsonSerializer.SerializeToNode(new List<TrameParameter>
            {
                new() { ParameterName = "value", Data = JsonNode.Parse("42") }
            }),
            Id = "se-s1",
            DependencyMapping = new Dictionary<string, string> { { "big", "$" } }
        };
        var step2 = new TrameRequest
        {
            Controller = "DepChain",
            Method = "EchoLong",
            Params = JsonSerializer.SerializeToNode(new List<TrameParameter>
            {
                new() { ParameterName = "value", Data = JsonValue.Create("@big") }
            }),
            Id = "se-s2"
        };
        var multi = new TrameMultiRequest
        {
            Mode = ExecutionMode.Serial,
            Requests = new List<TrameRequest> { step1, step2 }
        };

        // Act
        var response = await _client.PostAsync("/api/trame/json/multi", CreateJsonContent(multi));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var responses = JsonSerializer.Deserialize<List<TrameResponse>>(content, _jsonOpts);
        var byId = responses!.ToDictionary(r => r.Id ?? string.Empty);
        byId["se-s2"].Code.Should().Be(200);
        byId["se-s2"].Error.Should().BeNull();
        byId["se-s2"].Data.Value.Deserialize<long>().Should().Be(42L);
    }

}