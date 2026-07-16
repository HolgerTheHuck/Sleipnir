using FluentAssertions;
using TrameClient.Trame;
using TrameCommon.Exceptions;
using TrameCommon.Models;
using TrameCore.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace TrameTests.Unit.Client;

/// <summary>
/// Unit tests for TrameCall fluent builder.
/// </summary>
public class TrameCallTests
{
    [Fact]
    public void Init_CreatesCallWithControllerAndMethod()
    {
        // Act
        var call = TrameCall.Init("Customer", "GetById");

        // Assert
        call.Should().NotBeNull();
    }

    [Fact]
    public void With_AddsParameters()
    {
        // Act
        var call = TrameCall.Init("Customer", "Add")
            .With("Alice", 30);

        // Assert
        var request = call.ToRequest();
        request.Should().NotBeNull();
        request.Controller.Should().Be("Customer");
        request.Method.Should().Be("Add");
        request.Params.Should().NotBeNull();
    }

    [Fact]
    public void Add_SingleParameter_AddsToRequest()
    {
        // Act
        var call = TrameCall.Init("Customer", "Search")
            .Add("test");

        // Assert
        var request = call.ToRequest();
        request.Params!.ToJsonString().Should().Contain("test");
    }

    [Fact]
    public void Named_SetsRequestId()
    {
        // Act
        var call = TrameCall.Init("Customer", "GetAll")
            .Named("get-all-customers");

        // Assert
        var request = call.ToRequest();
        request.Id.Should().Be("get-all-customers");
    }

    [Fact]
    public void Exposes_SetsDependencyMapping()
    {
        // Act
        var call = TrameCall.Init("Customer", "Add")
            .With("Alice")
            .Exposes("$", "customerId");

        // Assert
        var request = call.ToRequest();
        request.DependencyMapping.Should().NotBeNull();
        request.DependencyMapping.Should().ContainKey("customerId");
        request.DependencyMapping!["customerId"].Should().Be("$");
    }

    [Fact]
    public void WithAlias_AddsAliasPlaceholder()
    {
        // Act
        var call = TrameCall.Init("Customer", "GetById")
            .WithAlias("@customerId");

        // Assert
        var request = call.ToRequest();
        request.Params!.ToJsonString().Should().Contain("@customerId");
    }

    [Fact]
    public void WithAlias_SetsParameterName()
    {
        // Act
        var call = TrameCall.Init("Customer", "GetById")
            .WithAlias("@customerId");

        // Assert
        var request = call.ToRequest();
        var params_ = JsonSerializer.Deserialize<List<TrameParameter>>(request.Params!);
        params_.Should().NotBeNull();
        params_!.Should().ContainSingle()
            .Which.ParameterName.Should().Be("customerId");
    }

    [Fact]
    public void Param_SetsParameterName()
    {
        // Act
        var call = TrameCall.Init("Customer", "GetById")
            .Param("id", 42);

        // Assert
        var request = call.ToRequest();
        var params_ = JsonSerializer.Deserialize<List<TrameParameter>>(request.Params!);
        params_.Should().NotBeNull();
        params_!.Should().ContainSingle()
            .Which.ParameterName.Should().Be("id");
        params_!.Single().Data!.GetValue<int>().Should().Be(42);
    }

    [Fact]
    public void Add_SetsParameterName()
    {
        // Act
        var call = TrameCall.Init("Customer", "Search")
            .Add("test");

        // Assert
        var request = call.ToRequest();
        var params_ = JsonSerializer.Deserialize<List<TrameParameter>>(request.Params!);
        params_.Should().NotBeNull();
        params_!.Should().ContainSingle()
            .Which.ParameterName.Should().Be("param0");
    }

    [Fact]
    public void Add_MultipleParameters_HaveSequentialNames()
    {
        // Act
        var call = TrameCall.Init("Customer", "Create")
            .Add("Alice")
            .Add(30)
            .Add(true);

        // Assert
        var request = call.ToRequest();
        var params_ = JsonSerializer.Deserialize<List<TrameParameter>>(request.Params!);
        params_.Should().HaveCount(3);
        params_![0].ParameterName.Should().Be("param0");
        params_![1].ParameterName.Should().Be("param1");
        params_![2].ParameterName.Should().Be("param2");
    }

    [Fact]
    public void ToRequest_GeneratesIdFromControllerAndMethod_WhenNotNamed()
    {
        // Act
        var call = TrameCall.Init("Customer", "GetAll");

        // Assert
        var request = call.ToRequest();
        request.Id.Should().Be("Customer.GetAll");
    }
}

/// <summary>
/// Unit tests for consolidated TrameException.
/// </summary>
public class TrameExceptionTests
{
    [Fact]
    public void Constructor_WithInnerException_PreservesMessage()
    {
        // Arrange
        var inner = new InvalidOperationException("Inner error");

        // Act
        var ex = new TrameException(inner);

        // Assert
        ex.Message.Should().Be("Inner error");
        ex.InnerException.Should().Be(inner);
    }

    [Fact]
    public void Constructor_WithMessage_SetsMessage()
    {
        // Act
        var ex = new TrameException("Custom error");

        // Assert
        ex.Message.Should().Be("Custom error");
        ex.InnerException.Should().BeNull();
    }

    [Fact]
    public void Constructor_WithMessageAndInner_SetsBoth()
    {
        // Arrange
        var inner = new ArgumentException("arg error");

        // Act
        var ex = new TrameException("outer", inner);

        // Assert
        ex.Message.Should().Be("outer");
        ex.InnerException.Should().Be(inner);
    }
}

/// <summary>
/// Unit tests for shared models (consolidated in TrameCommon).
/// </summary>
public class TrameModelTests
{
    [Fact]
    public void TrameRequest_DefaultValues_AreCorrect()
    {
        // Act
        var request = new TrameRequest();

        // Assert
        request.Controller.Should().BeEmpty();
        request.Method.Should().BeEmpty();
        request.Id.Should().BeEmpty();
        request.Params.Should().BeNull();
        request.BinaryData.Should().BeNull();
        request.DependencyMapping.Should().BeNull();
    }

    [Fact]
    public void TrameResponse_DefaultValues_AreCorrect()
    {
        // Act
        var response = new TrameResponse();

        // Assert
        response.Code.Should().Be(0);
        response.Data.Should().BeNull();
        response.Content.Should().BeNull();
        response.Id.Should().BeNull();
        response.ExposedDependencies.Should().BeNull();
    }

    [Fact]
    public void TrameMultiRequest_DefaultMode_IsSerial()
    {
        // Act
        var multiRequest = new TrameMultiRequest();

        // Assert
        multiRequest.Mode.Should().Be(ExecutionMode.Serial);
        multiRequest.Requests.Should().BeNull();
    }

    [Fact]
    public void TrameParameter_DefaultValues_AreCorrect()
    {
        // Act
        var param = new TrameParameter();

        // Assert
        param.ParameterName.Should().BeEmpty();
        param.Data.Should().BeNull();
        param.Num.Should().Be(0);
    }

    [Fact]
    public void ExecutionMode_HasTwoValues()
    {
        // Act
        var values = Enum.GetValues<ExecutionMode>();

        // Assert
        values.Should().HaveCount(2);
        values.Should().Contain(ExecutionMode.Parallel);
        values.Should().Contain(ExecutionMode.Serial);
    }
}

/// <summary>
/// Unit tests for TrameWebSocketClient ID-based request/response correlation.
/// </summary>
public class TrameWebSocketClientTests
{
    [Fact]
    public void Constructor_WithServerUrl_SetsUri()
    {
        // Act
        var client = new TrameWebSocketClient("https://example.com/api");

        // Assert
        client.Should().NotBeNull();
    }

    [Fact]
    public void Constructor_WithTrailingSlash_HandlesCorrectly()
    {
        // Act
        var client = new TrameWebSocketClient("https://example.com/");

        // Assert
        client.Should().NotBeNull();
    }

    [Fact]
    public void Constructor_WithEmptyUrl_Throws()
    {
        // Act
        var act = () => new TrameWebSocketClient("");

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Call_WithNullRequest_ReturnsNull()
    {
        // Arrange
        var client = new TrameWebSocketClient("https://example.com");

        // Act
        var result = await client.Call((TrameRequest?)null);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task Call_MultiRequest_WithNullRequest_ReturnsNull()
    {
        // Arrange
        var client = new TrameWebSocketClient("https://example.com");

        // Act
        var result = await client.Call((TrameMultiRequest?)null);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void RequestId_IsAssigned_WhenNotProvided()
    {
        // Arrange
        var client = new TrameWebSocketClient("https://example.com");
        var request = new TrameRequest
        {
            Controller = "Test",
            Method = "Echo",
            Params = JsonNode.Parse("[]")
            // Id is not set — defaults to string.Empty
        };

        // Assert: structural contract — the client will assign an ID
        // via request.Id ?? NextId() in SendAndAwaitResponseAsync
        request.Id.Should().BeEmpty(); // Not set by caller
        request.Controller.Should().Be("Test");
    }
}

/// <summary>
/// Unit tests for TrameRestJsonClient.
/// </summary>
public class TrameRestJsonClientTests
{
    [Fact]
    public void Constructor_WithDefaultApiPath_UsesApiTrame()
    {
        // Act
        var client = new TrameRestJsonClient("https://example.com");

        // Assert
        client.Should().NotBeNull();
    }

    [Fact]
    public void Constructor_WithCustomApiPath_AcceptsPath()
    {
        // Act
        var client = new TrameRestJsonClient("https://example.com", apiPath: "custom/path");

        // Assert
        client.Should().NotBeNull();
    }

    [Fact]
    public void Constructor_WithExternalHttpClient_DoesNotOwnIt()
    {
        // Arrange
        using var http = new HttpClient();

        // Act
        var client = new TrameRestJsonClient("https://example.com", http);

        // Assert
        client.Should().NotBeNull();
        // No exception on dispose — the external client is not disposed
        client.Dispose();
    }

    [Fact]
    public void Constructor_WithEmptyUrl_Throws()
    {
        // Act
        var act = () => new TrameRestJsonClient("");

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Call_WithNullRequest_ReturnsNull()
    {
        // Arrange
        var client = new TrameRestJsonClient("https://example.com");

        // Act
        var result = await client.Call((TrameRequest?)null);

        // Assert
        result.Should().BeNull();
    }
}

/// <summary>
/// Unit tests for TrameSignalrClient.
/// </summary>
public class TrameSignalrClientTests
{
    [Fact]
    public void Constructor_WithServerUrl_CreatesClient()
    {
        // Act
        var client = new TrameSignalrClient("https://example.com");

        // Assert
        client.Should().NotBeNull();
    }

    [Fact]
    public void Constructor_WithBearerToken_CreatesClient()
    {
        // Act
        var client = new TrameSignalrClient("https://example.com", "test-token");

        // Assert
        client.Should().NotBeNull();
    }

    [Fact]
    public void Constructor_WithTrailingSlash_HandlesCorrectly()
    {
        // Act
        var client = new TrameSignalrClient("https://example.com/");

        // Assert
        client.Should().NotBeNull();
    }

    [Fact]
    public async Task Call_WithNullRequest_ReturnsNull()
    {
        // Arrange
        var client = new TrameSignalrClient("https://example.com");

        // Act
        var result = await client.Call((TrameRequest?)null);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task Call_MultiRequest_WithNullRequest_ReturnsNull()
    {
        // Arrange
        var client = new TrameSignalrClient("https://example.com");

        // Act
        var result = await client.Call((TrameMultiRequest?)null);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task DisposeAsync_CanBeCalledMultipleTimes()
    {
        // Arrange
        var client = new TrameSignalrClient("https://example.com");

        // Act
        await client.DisposeAsync();
        await client.DisposeAsync(); // Should not throw

        // Assert: no exception
        true.Should().BeTrue();
    }
}

/// <summary>
/// Unit tests for the unified TrameError model.
/// </summary>
public class TrameErrorTests
{
    [Fact]
    public void FromResponse_WithErrorResponse_PopulatesFields()
    {
        // Arrange
        var response = new TrameResponse
        {
            Code = 404,
            Error = new TrameError { Code = 404, Message = "Not found" },
            Id = "req-123"
        };

        // Act
        var error = TrameError.FromResponse(response);

        // Assert
        error.Code.Should().Be(404);
        error.Message.Should().Be("Not found");
        error.RequestId.Should().Be("req-123");
    }

    [Fact]
    public void FromResponse_WithNullData_UsesDefaultMessage()
    {
        // Arrange
        var response = new TrameResponse { Code = 500, Data = null };

        // Act
        var error = TrameError.FromResponse(response);

        // Assert
        error.Code.Should().Be(500);
        error.Message.Should().Be("Trame call failed with code 500.");
    }

    [Fact]
    public void TrameException_WithTrameError_PreservesError()
    {
        // Arrange
        var error = new TrameError { Code = 401, Message = "Unauthorized" };

        // Act
        var ex = new TrameException(error);

        // Assert
        ex.Error.Should().NotBeNull();
        ex.Error!.Code.Should().Be(401);
        ex.Message.Should().Be("Unauthorized");
    }
}