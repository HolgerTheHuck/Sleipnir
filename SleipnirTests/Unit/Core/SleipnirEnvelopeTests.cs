using FluentAssertions;
using SleipnirCommon.Models;
using SleipnirCommon.Results;
using SleipnirTests.Fixtures;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace SleipnirTests.Unit.Core;

/// <summary>
/// Unit tests for the generic typed envelope <see cref="SleipnirResponse{T}"/> — the
/// static factories (mirroring SleipnirResults), the <c>Value</c> accessor and the
/// wire-state invariants (same {code,data,error} image as the non-generic path).
/// Transport/pass-through/discovery behavior is covered by
/// SleipnirInvokerTests/SleipnirDiscoveryServiceTests/EnvelopeTransportTests.
/// </summary>
public class SleipnirEnvelopeTests
{
    // Same options as the factories (camelCase + WhenWritingNull + relaxed encoder) —
    // duplicated locally because the shared instance is internal to SleipnirCommon.
    private static readonly JsonSerializerOptions WireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [Fact]
    public void Ok_payload_serializes_camel_case_and_matches_non_generic_wire_image()
    {
        var typed = SleipnirResponse<TestDto>.Ok(new TestDto { Id = 5, Name = "Found" });
        var plain = SleipnirResults.Ok(new TestDto { Id = 5, Name = "Found" });

        typed.Code.Should().Be(200);
        typed.IsSuccess.Should().BeTrue();
        typed.Error.Should().BeNull();
        // Identical wire image to the non-generic Ok path (byte-level invariance).
        typed.DataBytes.Should().BeEquivalentTo(plain.DataBytes);
    }

    [Fact]
    public void Ok_null_returns_no_content()
    {
        var typed = SleipnirResponse<TestDto>.Ok(null);

        typed.Code.Should().Be(204);
        typed.IsSuccess.Should().BeTrue();
        typed.Data.Should().BeNull();
    }

    [Fact]
    public void Value_decodes_payload_from_data_bytes()
    {
        var typed = SleipnirResponse<TestDto>.Ok(new TestDto { Id = 5, Name = "Found" });

        var value = typed.Value;

        value.Should().NotBeNull();
        value!.Id.Should().Be(5);
        value.Name.Should().Be("Found");
        // camelCase round-trip: Data is still lazily null until first read.
        typed.Data.Value.GetRawText().Should().Contain("\"id\":5");
    }

    [Fact]
    public void Value_reads_from_json_element_path_too()
    {
        // Set via the JsonElement property (legacy/ProblemDetails path), not DataBytes.
        var typed = new SleipnirResponse<TestDto> { Code = 200 };
        ((SleipnirResponse)typed).Data = JsonSerializer.SerializeToElement(
            new TestDto { Id = 3, Name = "ElementPath" }, WireOptions);

        var value = typed.Value;

        value.Should().NotBeNull();
        value!.Id.Should().Be(3);
        value.Name.Should().Be("ElementPath");
    }

    [Fact]
    public void Value_is_default_on_error_envelope()
    {
        var typed = SleipnirResponse<TestDto>.NotFound("Customer '99' not found.");

        typed.Value.Should().BeNull();
    }

    [Fact]
    public void Value_is_default_on_no_content()
    {
        var typed = SleipnirResponse<TestDto>.Ok(null);

        typed.Value.Should().BeNull();
    }

    [Fact]
    public void Error_factory_sets_code_and_structured_error_and_leaves_data_null()
    {
        var typed = SleipnirResponse<TestDto>.NotFound("Customer '99' not found.");

        typed.Code.Should().Be(404);
        typed.IsSuccess.Should().BeFalse();
        typed.Data.Should().BeNull();
        typed.Error.Should().NotBeNull();
        typed.Error!.Code.Should().Be(404);
        typed.Error.Message.Should().Be("Customer '99' not found.");
    }

    [Theory]
    [InlineData(nameof(SleipnirResponse<int>.BadRequest), 400, nameof(SleipnirErrorCategory.InvalidArgument))]
    [InlineData(nameof(SleipnirResponse<int>.Unauthorized), 401, nameof(SleipnirErrorCategory.Unauthenticated))]
    [InlineData(nameof(SleipnirResponse<int>.Forbidden), 403, nameof(SleipnirErrorCategory.PermissionDenied))]
    [InlineData(nameof(SleipnirResponse<int>.NotFound), 404, nameof(SleipnirErrorCategory.NotFound))]
    [InlineData(nameof(SleipnirResponse<int>.Conflict), 409, nameof(SleipnirErrorCategory.Conflict))]
    [InlineData(nameof(SleipnirResponse<int>.InternalServerError), 500, nameof(SleipnirErrorCategory.Internal))]
    public void Convenience_factories_set_semantic_category(string method, int expectedCode, string expectedCategory)
    {
        var typed = method switch
        {
            nameof(SleipnirResponse<int>.BadRequest) => SleipnirResponse<int>.BadRequest("x"),
            nameof(SleipnirResponse<int>.Unauthorized) => SleipnirResponse<int>.Unauthorized(),
            nameof(SleipnirResponse<int>.Forbidden) => SleipnirResponse<int>.Forbidden(),
            nameof(SleipnirResponse<int>.NotFound) => SleipnirResponse<int>.NotFound("x"),
            nameof(SleipnirResponse<int>.Conflict) => SleipnirResponse<int>.Conflict("x"),
            nameof(SleipnirResponse<int>.InternalServerError) => SleipnirResponse<int>.InternalServerError("x"),
            _ => throw new InvalidOperationException($"unmapped method {method}"),
        };

        typed.Code.Should().Be(expectedCode);
        typed.IsSuccess.Should().BeFalse();
        typed.Error!.Code.Should().Be(expectedCode);
        typed.Error!.Category.Should().Be(Enum.Parse<SleipnirErrorCategory>(expectedCategory));
    }

    [Fact]
    public void Error_with_details_carries_them()
    {
        var typed = SleipnirResponse<TestDto>.BadRequest("Invalid parameter 'id'.", "ParameterName=id");

        typed.Code.Should().Be(400);
        typed.Error!.Category.Should().Be(SleipnirErrorCategory.InvalidArgument);
        typed.Error!.Details.Should().Be("ParameterName=id");
    }

    /// <summary>
    /// Deliberate overload-trap guard: the generic envelope has NO raw-JSON Ok(string) —
    /// a string payload is serialized AS a JSON string value (not interpreted as raw JSON).
    /// Authors who pre-serialize keep SleipnirResults.Ok(string) on the non-generic envelope.
    /// </summary>
    [Fact]
    public void Ok_string_serializes_as_json_string_not_raw_json()
    {
        var typed = SleipnirResponse<string>.Ok("{\"id\":42}");

        // Raw JSON would have produced the object form {"id":42}; the generic Ok
        // always means "serialize the payload", so we get a JSON string literal.
        typed.Data.Value.GetRawText().Should().Be("\"{\\\"id\\\":42}\"");
        typed.Value.Should().Be("{\"id\":42}");
    }
}