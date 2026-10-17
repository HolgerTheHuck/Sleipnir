using FluentAssertions;
using SleipnirCommon.Models;
using SleipnirCore.Attributes;
using SleipnirCore.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json.Nodes;
using Xunit;

namespace SleipnirTests.Unit.Core;

/// <summary>
/// Executable spec for how BuildParameters treats a parameter the request does not carry, an
/// explicit JSON null, and the positional fallback — plus the wording of the resulting 400s and
/// of the routing misses (controller/method not found). Background: a missing non-nullable value
/// type used to bind a silent default(T) (or, with a data-less entry / explicit null, crash as a
/// NullReferenceException → generic 500), and the 400s named neither the call nor the parameter.
/// </summary>
public class ParameterBindingTests
{
    private readonly SleipnirInvoker _invoker;

    public ParameterBindingTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTransient<BindingTestController>();
        var provider = services.BuildServiceProvider();

        _invoker = new SleipnirInvoker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<ILogger<SleipnirInvoker>>());
        _invoker.Register<BindingTestController>();
    }

    /// <summary>One wire entry. <c>Data = Absent</c> omits the data field entirely.</summary>
    private sealed record Entry(string? Name, string? DataJson, int? Num = null);

    private const string Absent = "<absent>";

    private static SleipnirRequest Request(string method, params Entry[] entries)
    {
        var arr = new JsonArray();
        foreach (var e in entries)
        {
            var o = new JsonObject();
            if (e.Name != null) o["parameterName"] = e.Name;
            if (e.Num != null) o["num"] = e.Num;
            if (e.DataJson != Absent) o["data"] = e.DataJson == null ? null : JsonNode.Parse(e.DataJson);
            arr.Add(o);
        }
        return new SleipnirRequest { Controller = "Binding", Method = method, Params = arr, Id = method };
    }

    private async Task<SleipnirResponse> Call(SleipnirRequest request)
        => (await _invoker.InvokeDi(request, null))!;

    #region Omitted parameters

    [Fact]
    public async Task OmittedValueType_WithoutDefault_Is400NamingParameterTypeAndRoute()
    {
        var response = await Call(Request("Pair", new Entry("a", "1")));

        response.Code.Should().Be((int)HttpStatusCode.BadRequest);
        response.Error!.Message.Should().Be("Missing required parameter 'b' (type 'Int32') for 'Binding.Pair'.");
    }

    [Fact]
    public async Task EntryWithoutDataField_CountsAsOmitted()
    {
        // A data-less entry for a value type used to leave null in the args array → NRE → 500.
        var response = await Call(Request("Pair", new Entry("a", "1"), new Entry("b", Absent)));

        response.Code.Should().Be((int)HttpStatusCode.BadRequest);
        response.Error!.Message.Should().Contain("Missing required parameter 'b'");
    }

    [Fact]
    public async Task OmittedParameters_WithCSharpDefaults_BindTheDeclaredDefault()
    {
        // take = 20, kind = BindingOrderKind.Desc — previously default(T): 0 and Asc.
        var response = await Call(Request("Paged", new Entry("query", "\"q\"")));

        response.Code.Should().Be((int)HttpStatusCode.OK);
        response.Data!.Value.GetString().Should().Be("q:20:Desc");
    }

    [Fact]
    public async Task OmittedNullableParameters_BindNull()
    {
        var response = await Call(Request("Optional"));

        response.Code.Should().Be((int)HttpStatusCode.OK);
        response.Data!.Value.GetString().Should().Be("null|null");
    }

    [Fact]
    public async Task OmittedNonNullableReference_Is400()
    {
        // NRT non-nullable string = required, matching the discovery presence rule.
        var response = await Call(Request("Echo"));

        response.Code.Should().Be((int)HttpStatusCode.BadRequest);
        response.Error!.Message.Should().Be("Missing required parameter 'text' (type 'String') for 'Binding.Echo'.");
    }

    [Fact]
    public async Task OmittedObliviousReference_StaysNull()
    {
        // No NRT annotation context: required-ness is unknowable — the previous null binding stays.
        var response = await Call(Request("Legacy"));

        response.Code.Should().Be((int)HttpStatusCode.OK);
        response.Data!.Value.GetString().Should().Be("null");
    }

    [Fact]
    public async Task OmittedByteArray_IsFilledFromBinaryData()
    {
        var request = Request("Upload", new Entry("name", "\"f\""));
        request.BinaryData = new byte[] { 1, 2, 3 };

        var response = await Call(request);

        response.Code.Should().Be((int)HttpStatusCode.OK);
        response.Data!.Value.GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task OmittedByteArray_WithoutBinaryData_BindsNull()
    {
        var response = await Call(Request("Upload", new Entry("name", "\"f\"")));

        response.Code.Should().Be((int)HttpStatusCode.OK);
        response.Data!.Value.GetInt32().Should().Be(-1);
    }

    [Fact]
    public async Task CancellationToken_IsNeverRequired()
    {
        var response = await Call(Request("WithToken", new Entry("value", "7")));

        response.Code.Should().Be((int)HttpStatusCode.OK);
        response.Data!.Value.GetInt32().Should().Be(7);
    }

    #endregion

    #region Explicit null

    [Fact]
    public async Task ExplicitNull_ForNonNullableValueType_Is400()
    {
        // Used to reach the compiled delegate as null → NullReferenceException → generic 500.
        var response = await Call(Request("Pair", new Entry("a", null), new Entry("b", "2")));

        response.Code.Should().Be((int)HttpStatusCode.BadRequest);
        response.Error!.Message.Should().Be("Parameter 'a' of 'Binding.Pair' does not accept null (type 'Int32').");
    }

    [Fact]
    public async Task ExplicitNull_ForReferenceType_IsTheCallersChoice()
    {
        var response = await Call(Request("Optional", new Entry("count", null), new Entry("label", null)));

        response.Code.Should().Be((int)HttpStatusCode.OK);
        response.Data!.Value.GetString().Should().Be("null|null");
    }

    #endregion

    #region Positional fallback

    [Fact]
    public async Task PositionalFallback_DoesNotRebindAnEntryAlreadyBoundByName()
    {
        // Only `b` is sent and carries no num (= 0). It used to bind by name into b AND
        // positionally into a (index 0) — the call ran as Pair(2, 2).
        var response = await Call(Request("Pair", new Entry("b", "2")));

        response.Code.Should().Be((int)HttpStatusCode.BadRequest);
        response.Error!.Message.Should().Contain("Missing required parameter 'a'");
    }

    [Fact]
    public async Task NamedOnlyEntry_NeverBindsByPosition()
    {
        // The client's named entry for a parameter the server renamed (b -> c here: "bb" is the
        // stale name) carries num -1 (SleipnirParameter.NamedOnly): it must fail loudly instead
        // of being bound into b by position.
        var response = await Call(Request("Pair",
            new Entry("a", "1", SleipnirParameter.NamedOnly),
            new Entry("bb", "2", SleipnirParameter.NamedOnly)));

        response.Code.Should().Be((int)HttpStatusCode.BadRequest);
        response.Error!.Message.Should().Be("Missing required parameter 'b' (type 'Int32') for 'Binding.Pair'.");
    }

    [Fact]
    public async Task SleipnirCallParam_SendsNamedOnly_PositionalKeepsIndex()
    {
        var request = SleipnirClient.Sleipnir.SleipnirCall.Init("Binding", "Pair").Param("a", 1).With(2).ToRequest();
        // SleipnirCall serializes with default (PascalCase) names; the server reads both casings.
        var entries = request.Params!.AsArray()
            .Select(e => (Name: e!["ParameterName"]!.GetValue<string>(), Num: e["Num"]!.GetValue<int>()))
            .ToList();

        entries.Should().Equal(("a", SleipnirParameter.NamedOnly), ("param1", 1));
        (await Call(request)).Data!.Value.GetInt32().Should().Be(12);
    }

    [Fact]
    public async Task PositionalFallback_StillBindsUnmatchedNamesByNum()
    {
        // The fluent builder's param0/param1 shape (names match no parameter) binds by index.
        var response = await Call(Request("Pair", new Entry("param0", "1", 0), new Entry("param1", "2", 1)));

        response.Code.Should().Be((int)HttpStatusCode.OK);
        response.Data!.Value.GetInt32().Should().Be(12);
    }

    #endregion

    #region Messages

    [Fact]
    public async Task ConversionFailure_NamesRouteAndReceivedJsonKind()
    {
        var response = await Call(Request("Pair", new Entry("a", "\"x\""), new Entry("b", "2")));

        response.Code.Should().Be((int)HttpStatusCode.BadRequest);
        response.Error!.Message.Should().Be(
            "Parameter 'a' of 'Binding.Pair' cannot be converted to type 'Int32' (received JSON string).");
    }

    [Fact]
    public async Task BatchPath_ReportsMissingParameterPerRequest()
    {
        var ok = Request("Pair", new Entry("a", "1"), new Entry("b", "2"));
        var missing = Request("Echo");
        missing.Id = "missing";

        var responses = (await _invoker.InvokeDi(new[] { ok, missing }, null, ExecutionMode.Parallel)).ToList();

        // Parallel keeps request order.
        responses.Should().HaveCount(2);
        responses[0]!.Code.Should().Be((int)HttpStatusCode.OK);
        responses[1]!.Error!.Message
            .Should().Be("Missing required parameter 'text' (type 'String') for 'Binding.Echo'.");
    }

    [Fact]
    public async Task MethodNotFound_WithoutDetailedErrors_IsTheBareMessage()
    {
        var response = await Call(Request("Nope"));

        response.Code.Should().Be((int)HttpStatusCode.BadRequest);
        response.Error!.Message.Should().Be("Method 'Nope' not found on controller 'Binding'.");
    }

    [Fact]
    public async Task MethodNotFound_WithDetailedErrors_ListsMethodsAndStaleBuildHint()
    {
        _invoker.EnableDetailedErrors = true;

        var response = await Call(Request("Nope"));

        response.Error!.Message.Should().Be(
            "Method 'Nope' not found on controller 'Binding'. Available methods: Echo, Legacy, Optional, " +
            "Paged, Pair, Upload, WithToken. If it was added recently, the running server may be a stale build.");
    }

    [Fact]
    public async Task MethodNotFound_WithDetailedErrors_PointsAtACaseNearMiss()
    {
        _invoker.EnableDetailedErrors = true;

        var response = await Call(Request("pair"));

        response.Error!.Message.Should().Be(
            "Method 'pair' not found on controller 'Binding'. Did you mean 'Pair'? Names are case-sensitive.");
    }

    [Fact]
    public async Task ControllerNotFound_WithDetailedErrors_ListsControllers_AndKeepsTheJsonRpcPrefix()
    {
        _invoker.EnableDetailedErrors = true;
        var request = Request("Pair");
        request.Controller = "Ghost";

        var response = await Call(request);

        response.Code.Should().Be((int)HttpStatusCode.NotFound);
        // The "Controller '…'" prefix is what the JSON-RPC adapter maps to -32601.
        response.Error!.Message.Should().StartWith("Controller 'Ghost' not found.");
        response.Error.Message.Should().Contain("Registered controllers: Binding.");
    }

    #endregion
}

public enum BindingOrderKind { Asc, Desc }

/// <summary>Binding fixtures. AutoDiscover = false keeps it out of the integration hosts' scans.</summary>
[SleipnirController("Binding", AutoDiscover = false)]
public class BindingTestController
{
    [SleipnirMethod("Pair")]
    public int Pair(int a, int b) => a * 10 + b;

    [SleipnirMethod("Paged")]
    public string Paged(string query, int take = 20, BindingOrderKind kind = BindingOrderKind.Desc) => $"{query}:{take}:{kind}";

    [SleipnirMethod("Optional")]
    public string Optional(int? count, string? label) => $"{count?.ToString() ?? "null"}|{label ?? "null"}";

    [SleipnirMethod("Echo")]
    public string Echo(string text) => text;

#nullable disable
    [SleipnirMethod("Legacy")]
    public string Legacy(string text) => text ?? "null";
#nullable restore

    [SleipnirMethod("Upload")]
    public int Upload(string name, byte[]? payload) => payload?.Length ?? -1;

    [SleipnirMethod("WithToken")]
    public int WithToken(int value, CancellationToken ct) => value;
}
