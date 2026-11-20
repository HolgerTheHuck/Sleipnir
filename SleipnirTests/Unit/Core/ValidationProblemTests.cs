using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FluentAssertions;
using MessagePack;
using MpKey = MessagePack.KeyAttribute;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SleipnirCommon;
using SleipnirCommon.Models;
using SleipnirCommon.Results;
using SleipnirClient.Sleipnir;
using SleipnirCore.Attributes;
using SleipnirCore.Services;
using SleipnirRest.JsonRpc;
using Xunit;

namespace SleipnirTests.Unit.Core;

/// <summary>
/// Field-level validation errors (<see cref="SleipnirError.Errors"/>, ASP.NET
/// <c>ValidationProblemDetails.errors</c> shape): the <c>ValidationProblem</c> factories, the
/// recursive <see cref="SleipnirValidation"/> walker and its key convention, the opt-in invoker
/// validation (single call + batch), and wire compatibility (JSON omission, MessagePack
/// old/new shapes, JSON-RPC <c>error.data</c>).
/// </summary>
public class ValidationProblemTests
{
    public class Step
    {
        [Required, RegularExpression(@"^\d{4}$", ErrorMessage = "Must be 4 digits.")]
        public string? Hsn { get; set; }
    }

    public class Scenario : IValidatableObject
    {
        [Required(ErrorMessage = "Name is required.")]
        public string? Name { get; set; }

        [JsonPropertyName("max_steps")]
        [Range(1, 10, ErrorMessage = "Out of range.")]
        public int MaxSteps { get; set; } = 1;

        public List<Step> Steps { get; set; } = new();

        public Scenario? Parent { get; set; }

        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (Steps.Count > MaxSteps)
                yield return new ValidationResult("Too many steps.", new[] { nameof(Steps) });
        }
    }

    [SleipnirController("Validated", AutoDiscover = false)]
    public class ValidatedController
    {
        public static int Calls;

        [SleipnirMethod("Save")]
        public string Save(Scenario scenario)
        {
            Interlocked.Increment(ref Calls);
            return scenario.Name!;
        }

        [SleipnirMethod("Rename")]
        public string Rename([Required, StringLength(5, ErrorMessage = "Too long.")] string newName) => newName;

        [SleipnirMethod("Manual")]
        public SleipnirResponse Manual() => SleipnirResults.ValidationProblem(
            new Dictionary<string, string[]> { ["name"] = new[] { "Taken." } });
    }

    private static SleipnirInvoker CreateInvoker(bool validate)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTransient<ValidatedController>();
        var sp = services.BuildServiceProvider();
        var invoker = new SleipnirInvoker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<SleipnirInvoker>>())
        { ValidateParameters = validate };
        invoker.Register<ValidatedController>();
        return invoker;
    }

    private static SleipnirRequest Req(string method, string paramsJson, string id = "1") => new()
    {
        Controller = "Validated",
        Method = method,
        Params = JsonNode.Parse(paramsJson),
        Id = id,
    };

    private const string InvalidScenario =
        """[{"parameterName":"scenario","num":-1,"data":{"name":null,"max_steps":1,"steps":[{"hsn":"1234"},{"hsn":"12x"}]}}]""";

    private const string ValidScenario =
        """[{"parameterName":"scenario","num":-1,"data":{"name":"ok","max_steps":3,"steps":[{"hsn":"1234"}]}}]""";

    // ---- factories ------------------------------------------------------------------------

    [Fact]
    public void ValidationProblem_Is400InvalidArgumentWithErrors()
    {
        var source = new Dictionary<string, string[]> { ["name"] = new[] { "Required." } };
        var resp = SleipnirResults.ValidationProblem(source);

        resp.Code.Should().Be(400);
        resp.Error!.Category.Should().Be(SleipnirErrorCategory.InvalidArgument);
        resp.Error.Message.Should().Be(SleipnirResults.DefaultValidationMessage);
        resp.Error.Errors.Should().ContainKey("name").WhoseValue.Should().Equal("Required.");

        source["other"] = new[] { "x" };
        resp.Error.Errors.Should().NotContainKey("other", "the factory copies the dictionary");
    }

    [Fact]
    public void TypedEnvelope_ValidationProblem_Mirrors()
    {
        var resp = SleipnirResponse<string>.ValidationProblem(
            new Dictionary<string, string[]> { ["a"] = new[] { "b" } }, "Custom.");

        resp.Code.Should().Be(400);
        resp.Error!.Message.Should().Be("Custom.");
        resp.Error.Errors!["a"].Should().Equal("b");
    }

    [Fact]
    public void ValidationProblem_FromValidationResults_CamelCasesMembersUnderPrefix()
    {
        var resp = SleipnirResults.ValidationProblem(new[]
        {
            new ValidationResult("Bad.", new[] { "Name", "Steps[1].Hsn" }),
            new ValidationResult("Whole object."),
        }, prefix: "scenario");

        resp.Error!.Errors!.Keys.Should().BeEquivalentTo("scenario.name", "scenario.steps[1].hsn", "scenario");
    }

    // ---- walker ---------------------------------------------------------------------------

    [Fact]
    public void Validate_DescendsIntoListsAndUsesWireNames()
    {
        var scenario = new Scenario
        {
            Name = null,
            MaxSteps = 0,
            Steps = { new Step { Hsn = "1234" }, new Step { Hsn = "12x" } },
        };

        var errors = SleipnirValidation.Validate(scenario, "scenario");

        errors.Should().ContainKey("scenario.name").WhoseValue.Should().Equal("Name is required.");
        errors.Should().ContainKey("scenario.max_steps", "[JsonPropertyName] wins over camelCase");
        errors.Should().ContainKey("scenario.steps[1].hsn").WhoseValue.Should().Equal("Must be 4 digits.");
        errors.Should().NotContainKey("scenario.steps[0].hsn");
    }

    [Fact]
    public void Validate_RunsValidatableObjectWhenAttributesPass()
    {
        var scenario = new Scenario { Name = "x", MaxSteps = 1, Steps = { new Step { Hsn = "1111" }, new Step { Hsn = "2222" } } };

        SleipnirValidation.Validate(scenario, "s").Should().ContainKey("s.steps")
            .WhoseValue.Should().Equal("Too many steps.");
    }

    [Fact]
    public void Validate_SurvivesCycles_AndValidGraphIsEmpty()
    {
        var a = new Scenario { Name = "a" };
        a.Parent = a;

        SleipnirValidation.Validate(a, "a").Should().BeEmpty();
        SleipnirValidation.Validate(null, "x").Should().BeEmpty();
    }

    // ---- invoker --------------------------------------------------------------------------

    [Fact]
    public async Task Invoker_ValidationOn_Returns400WithFieldErrors_AndSkipsTheMethod()
    {
        var invoker = CreateInvoker(validate: true);
        var before = ValidatedController.Calls;

        var resp = await invoker.InvokeDi(Req("Save", InvalidScenario), null);

        resp!.Code.Should().Be(400);
        resp.Error!.Category.Should().Be(SleipnirErrorCategory.InvalidArgument);
        resp.Error.Errors.Should().ContainKeys("scenario.name", "scenario.steps[1].hsn");
        resp.Id.Should().Be("1");
        ValidatedController.Calls.Should().Be(before, "a validation failure never reaches the method");
    }

    [Fact]
    public async Task Invoker_ValidationOn_ValidInputPasses()
    {
        var resp = await CreateInvoker(validate: true).InvokeDi(Req("Save", ValidScenario), null);

        resp!.Code.Should().Be(200);
        resp.Error.Should().BeNull();
    }

    [Fact]
    public async Task Invoker_ValidationOff_IsTheDefault()
    {
        var resp = await CreateInvoker(validate: false).InvokeDi(Req("Save", InvalidScenario), null);

        resp!.Code.Should().Be(200, "validation is opt-in");
    }

    [Fact]
    public async Task Invoker_ValidatesParameterAttributes()
    {
        var resp = await CreateInvoker(validate: true).InvokeDi(
            Req("Rename", """[{"parameterName":"newName","num":-1,"data":"toolong"}]"""), null);

        resp!.Code.Should().Be(400);
        resp.Error!.Errors.Should().ContainKey("newName").WhoseValue.Should().Equal("Too long.");
    }

    [Theory]
    [InlineData(ExecutionMode.Parallel)]
    [InlineData(ExecutionMode.Serial)]
    public async Task Invoker_Batch_FailsOnlyTheInvalidRequest(ExecutionMode mode)
    {
        var invoker = CreateInvoker(validate: true);

        var responses = (await invoker.InvokeDi(
            new[] { Req("Save", InvalidScenario, "bad"), Req("Save", ValidScenario, "good") }, null, mode)).ToList();

        responses.Single(r => r.Id == "bad").Error!.Errors.Should().ContainKey("scenario.steps[1].hsn");
        responses.Single(r => r.Id == "good").Code.Should().Be(200);
    }

    [Fact]
    public async Task Invoker_PassesControllerValidationProblemThrough()
    {
        var resp = await CreateInvoker(validate: false).InvokeDi(Req("Manual", "[]"), null);

        resp!.Code.Should().Be(400);
        resp.Error!.Errors!["name"].Should().Equal("Taken.");
    }

    // ---- wire -----------------------------------------------------------------------------

    private static readonly JsonSerializerOptions WireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new SleipnirResponseJsonConverter(), new JsonStringEnumConverter() },
    };

    [Fact]
    public void Json_ErrorsOmittedWhenNull_PresentWhenSet()
    {
        var plain = JsonSerializer.Serialize(SleipnirResults.NotFound("nope"), WireOptions);
        var invalid = JsonSerializer.Serialize(SleipnirResults.ValidationProblem(
            new Dictionary<string, string[]> { ["a.b[0]"] = new[] { "x" } }), WireOptions);

        plain.Should().NotContain("\"errors\"", "existing error payloads stay byte-identical");
        JsonNode.Parse(invalid)!["error"]!["errors"]!["a.b[0]"]![0]!.GetValue<string>().Should().Be("x");
    }

    [MessagePackObject]
    public class LegacyError
    {
        [MpKey(0)] public int Code { get; set; }
        [MpKey(1)] public string Message { get; set; } = "";
        [MpKey(2)] public string? Details { get; set; }
        [MpKey(3)] public string? RequestId { get; set; }
        [MpKey(4)] public SleipnirErrorCategory Category { get; set; }
    }

    [Fact]
    public void MessagePack_RoundTrip_AndBothDirectionsOfVersionSkew()
    {
        var current = SleipnirResults.ValidationProblem(
            new Dictionary<string, string[]> { ["name"] = new[] { "Required." } }).Error!;

        var roundTrip = MessagePackSerializer.Deserialize<SleipnirError>(MessagePackSerializer.Serialize(current));
        roundTrip.Errors!["name"].Should().Equal("Required.");

        // New server → old client: the extra array element is skipped.
        var legacy = MessagePackSerializer.Deserialize<LegacyError>(MessagePackSerializer.Serialize(current));
        legacy.Code.Should().Be(400);
        legacy.Category.Should().Be(SleipnirErrorCategory.InvalidArgument);

        // Old server → new client: the missing element leaves Errors null.
        var fromOld = MessagePackSerializer.Deserialize<SleipnirError>(
            MessagePackSerializer.Serialize(new LegacyError { Code = 404, Message = "x" }));
        fromOld.Code.Should().Be(404);
        fromOld.Errors.Should().BeNull();
    }

    [Fact]
    public void CsClient_ParsesErrorsFromTheWire()
    {
        var wire = JsonSerializer.SerializeToUtf8Bytes(SleipnirResults.ValidationProblem(
            new Dictionary<string, string[]> { ["scenario.steps[1].hsn"] = new[] { "Must be 4 digits." } }), WireOptions);

        var parsed = SleipnirResponseParser.Parse(wire);

        parsed.Error!.Category.Should().Be(SleipnirErrorCategory.InvalidArgument);
        parsed.Error.Errors!["scenario.steps[1].hsn"].Should().Equal("Must be 4 digits.");
    }

    [Fact]
    public void JsonRpc_PutsErrorsIntoErrorData()
    {
        var resp = SleipnirResults.ValidationProblem(
            new Dictionary<string, string[]> { ["name"] = new[] { "Required." } }, details: "see form");

        var rpc = JsonRpcAdapter.MapResponse(resp, id: null);

        rpc["error"]!["code"]!.GetValue<int>().Should().Be(-32602);
        rpc["error"]!["data"]!["errors"]!["name"]![0]!.GetValue<string>().Should().Be("Required.");
        rpc["error"]!["data"]!["details"]!.GetValue<string>().Should().Be("see form");
    }
}
