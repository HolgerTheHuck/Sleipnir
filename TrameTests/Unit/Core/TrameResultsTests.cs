using FluentAssertions;
using TrameCommon.Models;
using TrameCommon.Results;
using System.Text.Json;
using Xunit;

namespace TrameTests.Unit.Core;

/// <summary>
/// Unit tests für <see cref="TrameResults"/> — verifiziert Code/Data/Error-Belegung
/// und IsSuccess-Ableitung für den unterstützten Controller-Fehlerkanal.
/// </summary>
public class TrameResultsTests
{
    [Fact]
    public void Ok_object_serializes_result_and_sets_200()
    {
        var resp = TrameResults.Ok(new { Id = 7, Name = "Alice" });

        resp.Code.Should().Be(200);
        resp.IsSuccess.Should().BeTrue();
        resp.Error.Should().BeNull();
        // Default-Options = camelCase (wie der Invoker-Pfad).
        resp.Data.Value.GetRawText().Should().Contain("\"id\":7");
        resp.Data.Value.GetRawText().Should().Contain("\"name\":\"Alice\"");
    }

    [Fact]
    public void Ok_null_returns_no_content()
    {
        var resp = TrameResults.Ok((object?)null);

        resp.Code.Should().Be(204);
        resp.IsSuccess.Should().BeTrue();
        resp.Data.Should().BeNull();
    }

    [Fact]
    public void Ok_string_keeps_raw_data()
    {
        var resp = TrameResults.Ok("{\"id\":42}");

        resp.Code.Should().Be(200);
        resp.Data.Value.GetRawText().Should().Be("{\"id\":42}");
    }

    [Fact]
    public void Ok_binary_sets_base64_data_and_raw_content()
    {
        var bytes = new byte[] { 1, 2, 3, 250 };

        var resp = TrameResults.Ok(bytes);

        resp.Code.Should().Be(200);
        resp.Content.Should().BeEquivalentTo(bytes);
        // Bytes liegen ausschließlich in Content (kein Base64-String mehr in Data).
        resp.Data.Should().BeNull();
    }

    [Fact]
    public void NoContent_is_204_without_data()
    {
        var resp = TrameResults.NoContent();

        resp.Code.Should().Be(204);
        resp.IsSuccess.Should().BeTrue();
        resp.Data.Should().BeNull();
    }

    [Fact]
    public void Error_sets_code_data_and_structured_error()
    {
        var resp = TrameResults.Error(404, "Customer '99' not found.");

        resp.Code.Should().Be(404);
        resp.IsSuccess.Should().BeFalse();
        // Data ist bei Fehlern null; die Message wohnt ausschließlich in Error.Message.
        resp.Data.Should().BeNull();
        resp.Error.Should().NotBeNull();
        resp.Error!.Code.Should().Be(404);
        resp.Error.Message.Should().Be("Customer '99' not found.");
        resp.Error.Details.Should().BeNull();
        resp.Error.RequestId.Should().BeNull(); // wird vom Invoker/Transport gesetzt
    }

    [Fact]
    public void Error_with_details_carries_them()
    {
        var resp = TrameResults.Error(400, "Invalid parameter 'id'.",
            TrameErrorCategory.InvalidArgument, "Expected positive int, got -1.");

        resp.Error!.Details.Should().Be("Expected positive int, got -1.");
        resp.Error!.Category.Should().Be(TrameErrorCategory.InvalidArgument);
    }

    [Theory]
    [InlineData(nameof(TrameResults.BadRequest), 400, nameof(TrameErrorCategory.InvalidArgument))]
    [InlineData(nameof(TrameResults.Unauthorized), 401, nameof(TrameErrorCategory.Unauthenticated))]
    [InlineData(nameof(TrameResults.Forbidden), 403, nameof(TrameErrorCategory.PermissionDenied))]
    [InlineData(nameof(TrameResults.NotFound), 404, nameof(TrameErrorCategory.NotFound))]
    [InlineData(nameof(TrameResults.Conflict), 409, nameof(TrameErrorCategory.Conflict))]
    [InlineData(nameof(TrameResults.InternalServerError), 500, nameof(TrameErrorCategory.Internal))]
    public void Convenience_methods_set_semantic_category(string method, int expectedCode, string expectedCategory)
    {
        var resp = method switch
        {
            nameof(TrameResults.BadRequest) => TrameResults.BadRequest("x"),
            nameof(TrameResults.Unauthorized) => TrameResults.Unauthorized(),
            nameof(TrameResults.Forbidden) => TrameResults.Forbidden(),
            nameof(TrameResults.NotFound) => TrameResults.NotFound("x"),
            nameof(TrameResults.Conflict) => TrameResults.Conflict("x"),
            nameof(TrameResults.InternalServerError) => TrameResults.InternalServerError("x"),
            _ => throw new InvalidOperationException($"unmapped method {method}"),
        };

        resp.Code.Should().Be(expectedCode);
        resp.IsSuccess.Should().BeFalse();
        resp.Error!.Code.Should().Be(expectedCode);
        resp.Error!.Category.Should().Be(Enum.Parse<TrameErrorCategory>(expectedCategory));
    }

    [Fact]
    public void Unauthorized_defaults_to_401()
    {
        var resp = TrameResults.Unauthorized();

        resp.Code.Should().Be(401);
        resp.Error!.Message.Should().Be("Unauthorized.");
    }

    [Fact]
    public void ProblemDetails_serializes_camel_case_and_maps_title_detail_to_error()
    {
        var problem = new ProblemDetails
        {
            Type = "https://example.com/errors/customer-not-found",
            Title = "Customer not found.",
            Status = 404,
            Detail = "No customer with id 99.",
            Instance = "/api/trame/json",
        };

        var resp = TrameResults.Error(problem);

        resp.Code.Should().Be(404);
        resp.IsSuccess.Should().BeFalse();

        // RFC 7807: CamelCase-Keys im Data-JSON (Data ist jetzt JsonElement).
        var doc = resp.Data.Value;
        doc.TryGetProperty("type", out _).Should().BeTrue();
        doc.TryGetProperty("title", out _).Should().BeTrue();
        doc.TryGetProperty("status", out _).Should().BeTrue();
        doc.TryGetProperty("detail", out _).Should().BeTrue();
        doc.GetProperty("status").GetInt32().Should().Be(404);

        // Title/Detail zusätzlich auf TrameError gespiegelt (einfache Clients).
        resp.Error!.Message.Should().Be("Customer not found.");
        resp.Error.Details.Should().Be("No customer with id 99.");
        resp.Error.Code.Should().Be(404);
    }
}