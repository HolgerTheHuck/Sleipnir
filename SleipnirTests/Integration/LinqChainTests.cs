// Integration test for the typed Dep<T> authoring surface (Sleipnir.Client.Linq) against the real
// Sleipnir server. Runs a dependency-chained batch through the in-process Kestrel fixture against the
// deterministic DepChain controller (MakeDto → exposes id/name → EchoInt/EchoString consume them), so
// the @alias wiring — built from lambda selectors, type-checked at compile time — is exercised
// end-to-end through the server's topological batch path.
//
// The service contract below is a hand-written stand-in for a generated SleipnirContracts.g.cs (the
// ContractsEmitterTests cover the generator output against the real TypeRef IR). DTO properties are
// non-nullable here so Expose(o => o.Id) yields Dep<int> directly; generated DTOs emit nullable props
// (the documented papercut — see the package README).
using System.Text.Json.Serialization;
using FluentAssertions;
using Sleipnir.Client.Linq;
using SleipnirClient.Sleipnir;
using SleipnirTests.Fixtures;
using Xunit;

namespace SleipnirTests.Integration;

/// <summary>Hand-written contract mirroring a generated SleipnirContracts.g.cs for the DepChain controller.</summary>
[SleipnirServiceContract("DepChain")]
public interface IDepChainService
{
    [SleipnirMethodContract("MakeDto")]
    Task<ChainDto> MakeDto(Arg<int> id, Arg<string> name);

    [SleipnirMethodContract("EchoInt")]
    Task<int> EchoInt(Arg<int> value);

    [SleipnirMethodContract("EchoString")]
    Task<string> EchoString(Arg<string> value);
}

/// <summary>Result DTO of DepChain.MakeDto — [JsonPropertyName] carries the wire name JsonPathBuilder reads.</summary>
public class ChainDto
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

public class LinqChainTests : IClassFixture<TransportTestFixture>
{
    private readonly TransportTestFixture _fixture;

    public LinqChainTests(TransportTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task TypedDepChain_FlowsThroughServer()
    {
        using var rest = _fixture.CreateRestClient();
        var linq = new SleipnirLinqClient(rest);

        // Producer: MakeDto(7, "Foo") → {id:7, name:"Foo"}. Expose selectors build the result-relative
        // JsonPath from the expression ($.id, $.name) — drift-proof against the server's camelCase wire.
        var make = linq.Build((IDepChainService c) => c.MakeDto(7, "Foo"));
        Dep<int> id = make.Expose(o => o.Id);       // $.id  → Dep<int>
        Dep<string> name = make.Expose(o => o.Name); // $.name → Dep<string>

        // Consumers: the Dep<T> placeholders fit only same-T Arg<T> params (compile-time check).
        var echoId = linq.Build((IDepChainService c) => c.EchoInt(id));
        var echoName = linq.Build((IDepChainService c) => c.EchoString(name));

        // Serial batch — the server resolves the @alias placeholders against the producer's response.
        var batch = new SleipnirBatch(make, echoId, echoName);
        var responses = await linq.SendAsync(batch);

        // Correlated typed extraction by spec id.
        linq.ResultOf<int>(echoId, responses).Should().Be(7);
        linq.ResultOf<string>(echoName, responses).Should().Be("Foo");
    }

    [Fact]
    public async Task SingleCall_RoundtripsTypedResult()
    {
        using var rest = _fixture.CreateRestClient();
        var linq = new SleipnirLinqClient(rest);

        var call = linq.Build((IDepChainService c) => c.MakeDto(42, "Solo"));
        var dto = await linq.SendAsync(call);

        dto.Should().NotBeNull();
        dto!.Id.Should().Be(42);
        dto.Name.Should().Be("Solo");
    }
}