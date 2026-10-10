using FluentAssertions;
using SleipnirClient.Sleipnir;
using SleipnirCommon.Models;
using SleipnirTests.Fixtures;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace SleipnirTests.Integration;

/// <summary>
/// End-to-end tests for the generic typed envelope SleipnirResponse&lt;T&gt; over the real
/// transports. The critical invariant: the derived envelope rides the declared-base
/// serialization on every wire (write-only JSON converter for REST/WS, manual MessagePack
/// formatter for SignalR) — its wire image is byte-identical to the non-generic
/// SleipnirResults path, and the MessagePack roundtrip keeps code/data/error intact.
/// </summary>
public class EnvelopeTransportTests : IClassFixture<TransportTestFixture>
{
    private readonly TransportTestFixture _fixture;

    public EnvelopeTransportTests(TransportTestFixture fixture)
    {
        _fixture = fixture;
    }

    private static SleipnirRequest CreateRequest(string controller, string method, string id,
        params (string name, string jsonValue)[] parameters)
    {
        var paramList = parameters.Select(p => new SleipnirParameter
        {
            ParameterName = p.name,
            Data = p.jsonValue.StartsWith("@") ? JsonValue.Create(p.jsonValue) : JsonNode.Parse(p.jsonValue)
        }).ToList();

        return new SleipnirRequest
        {
            Controller = controller,
            Method = method,
            Params = JsonSerializer.SerializeToNode(paramList),
            Id = id
        };
    }

    private async Task<string> PostRestAsync(string payloadJson)
    {
        var client = new HttpClient();
        var content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
        var response = await client.PostAsync(_fixture.BaseUrl + "api/sleipnir/json", content);
        response.StatusCode.Should().Be(HttpStatusCode.OK); // envelope-at-200
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// The generic envelope and the non-generic SleipnirResults path must produce the
    /// identical REST wire body (same converter, same key order, same payload bytes).
    /// Same request Id on both calls so the correlation field cannot differ either.
    /// </summary>
    [Fact]
    public async Task Rest_GenericEnvelopeOk_WireImageMatchesNonGenericPath()
    {
        var envelopeBody = await PostRestAsync(JsonSerializer.Serialize(
            CreateRequest("TestEnvelope", "GetEnvelopeOr404", "t", ("id", "5"))));
        var plainBody = await PostRestAsync(JsonSerializer.Serialize(
            CreateRequest("TestInvoker", "GetOr404", "t", ("id", "5"))));

        envelopeBody.Should().Be(plainBody);
        envelopeBody.Should().Contain("\"code\":200");
        envelopeBody.Should().Contain("\"name\":\"Found\"");
    }

    [Fact]
    public async Task Rest_GenericEnvelope404_CarriesCodeAndMessage()
    {
        var body = await PostRestAsync(JsonSerializer.Serialize(
            CreateRequest("TestEnvelope", "GetEnvelopeOr404", "t", ("id", "99"))));

        var doc = JsonNode.Parse(body)!;
        doc["code"]!.GetValue<int>().Should().Be(404);
        doc["error"]!["message"]!.GetValue<string>().Should().Be("Customer '99' not found.");
        doc["error"]!["category"]!.GetValue<string>().Should().Be("NotFound");
        body.Should().Contain("\"data\":null");
    }

    // --- SignalR + MessagePack: the derived-instance roundtrip -------------------
    // DoWork declares typeof(SleipnirResponse) as its return type, so the manual
    // MessagePack formatter (base-typed) handles the derived envelope — this is the
    // regression guard for the "no MessagePackObject on the generic class" decision.

    [Fact]
    public async Task SignalR_MessagePack_GenericEnvelope_SuccessRoundtrip()
    {
        var client = _fixture.CreateSignalrClient();
        var request = CreateRequest("TestEnvelope", "GetEnvelopeOr404", "t", ("id", "7"));

        var resp = await client.Call(request);

        resp!.Code.Should().Be(200);
        resp.IsSuccess.Should().BeTrue();
        resp.Data.Should().NotBeNull();
        resp.Data.Value.GetRawText().Should().Contain("\"id\":7");

        // The wire serves the plain base envelope (the client has no way to know T —
        // the generated client narrows via Call<T>). Payload integrity is what the
        // roundtrip must prove (camelCase wire → case-insensitive deserialization).
        var payload = resp.Data.Value.Deserialize<TestDto>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        payload!.Name.Should().Be("Found");
    }

    [Fact]
    public async Task SignalR_MessagePack_GenericEnvelope_ErrorRoundtripKeepsCodeAndMessage()
    {
        var client = _fixture.CreateSignalrClient();
        var request = CreateRequest("TestEnvelope", "GetEnvelopeOr404", "t", ("id", "99"));

        var resp = await client.Call(request);

        resp!.Code.Should().Be(404);
        resp.IsSuccess.Should().BeFalse();
        resp.Data.Should().BeNull();
        resp.Error.Should().NotBeNull();
        resp.Error!.Code.Should().Be(404);
        resp.Error.Message.Should().Be("Customer '99' not found.");
    }

    [Fact]
    public async Task SignalR_MessagePack_GenericEnvelope_UnauthorizedRoundtrip()
    {
        var client = _fixture.CreateSignalrClient();
        var request = CreateRequest("TestEnvelope", "UnauthorizedEnvelope", "t");

        var resp = await client.Call(request);

        resp!.Code.Should().Be(401);
        resp.Error!.Message.Should().Be("invalid credentials.");
    }

    [Fact]
    public async Task WebSocket_GenericEnvelope_SuccessRoundtrip()
    {
        var client = _fixture.CreateWsClient();
        var request = CreateRequest("TestEnvelope", "GetEnvelopeOr404", "t", ("id", "7"));

        var resp = await client.Call(request);

        resp!.Code.Should().Be(200);
        resp.Data.Value.GetRawText().Should().Contain("\"name\":\"Found\"");
    }
}