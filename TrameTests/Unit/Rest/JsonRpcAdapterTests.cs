using FluentAssertions;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TrameCommon.Models;
using TrameRest.JsonRpc;
using Xunit;

namespace TrameTests.Unit.Rest;

/// <summary>
/// Reine Übersetzungs-Tests für <see cref="JsonRpcAdapter"/> (kein Transport, kein DI).
/// Decken Request-Mapping (named/positional/dotted-controller/Capability/Invalid),
/// Response-Mapping (Erfolg/204/Binär/Fehler-mit-ProblemDetails/id-Echo) und die
/// Fehlercode-Map (-32601 Routing-404 vs. -32000 Business-404) ab. Die Orchestrierung
/// (Body, Batch, Notifications, ITrameCore-Aufruf) ist in <c>JsonRpcTransportTests</c>
/// (Integration) geprüft.
/// </summary>
public class JsonRpcAdapterTests
{
    private static JsonElement Item(string json)
        => JsonDocument.Parse(json).RootElement;

    private static JsonElement IdElement(string json) // z.B. "5" oder "\"abc\"" oder "null"
        => JsonDocument.Parse(json).RootElement;

    // === Request-Mapping ====================================================

    [Fact]
    public void ParseRequest_NamedParams_BuildsStringDataWithParameterNames()
    {
        var el = Item("""{"jsonrpc":"2.0","method":"DepChain.EchoInt","params":{"value":42},"id":1}""");
        var parsed = JsonRpcAdapter.ParseRequest(el);

        parsed.IsValid.Should().BeTrue();
        parsed.Request.Should().NotBeNull();
        parsed.Request!.Controller.Should().Be("DepChain");
        parsed.Request.Method.Should().Be("EchoInt");
        parsed.IsNotification.Should().BeFalse();

        // Params ist eine List<TrameParameter> als JsonArray; Data ist das rohe Fragment (JsonNode).
        var paramsList = JsonSerializer.Deserialize<List<TrameParameter>>(parsed.Request.Params!);
        paramsList.Should().ContainSingle(p => p.ParameterName == "value" && p.Data!.GetValue<int>() == 42);
    }

    [Fact]
    public void ParseRequest_PositionalParams_UsesNumIndex()
    {
        var el = Item("""{"jsonrpc":"2.0","method":"TestInvoker.Add","params":[3,4],"id":2}""");
        var parsed = JsonRpcAdapter.ParseRequest(el);

        var paramsList = JsonSerializer.Deserialize<List<TrameParameter>>(parsed.Request!.Params!);
        paramsList.Should().HaveCount(2);
        paramsList[0].Num.Should().Be(0);
        paramsList[0].Data!.GetValue<int>().Should().Be(3);
        paramsList[1].Num.Should().Be(1);
        paramsList[1].Data!.GetValue<int>().Should().Be(4);
    }

    [Fact]
    public void ParseRequest_DottedController_SplitsAtLastDot()
    {
        // "Customer.Address.Contact.Add" → Controller "Customer.Address.Contact", Methode "Add".
        var el = Item("""{"jsonrpc":"2.0","method":"Customer.Address.Contact.Add","params":{"name":"x"},"id":"a"}""");
        var parsed = JsonRpcAdapter.ParseRequest(el);

        parsed.Request!.Controller.Should().Be("Customer.Address.Contact");
        parsed.Request.Method.Should().Be("Add");
    }

    [Fact]
    public void ParseRequest_NoDotInMethod_IsInvalidRequest()
    {
        var el = Item("""{"jsonrpc":"2.0","method":"noqualifier","id":1}""");
        var parsed = JsonRpcAdapter.ParseRequest(el);

        parsed.IsValid.Should().BeFalse();
        parsed.ErrorCode.Should().Be(-32600);
    }

    [Fact]
    public void ParseRequest_ScalarParams_IsInvalidRequest()
    {
        var el = Item("""{"jsonrpc":"2.0","method":"C.M","params":42,"id":1}""");
        var parsed = JsonRpcAdapter.ParseRequest(el);
        parsed.IsValid.Should().BeFalse();
        parsed.ErrorCode.Should().Be(-32600);
    }

    [Fact]
    public void ParseRequest_MissingJsonrpcVersion_IsInvalidRequest()
    {
        var el = Item("""{"method":"C.M","id":1}""");
        var parsed = JsonRpcAdapter.ParseRequest(el);
        parsed.IsValid.Should().BeFalse();
        parsed.ErrorCode.Should().Be(-32600);
    }

    [Fact]
    public void ParseRequest_WrongVersion_IsInvalidRequest()
    {
        var el = Item("""{"jsonrpc":"1.0","method":"C.M","id":1}""");
        var parsed = JsonRpcAdapter.ParseRequest(el);
        parsed.ErrorCode.Should().Be(-32600);
    }

    [Fact]
    public void ParseRequest_NoId_IsNotification()
    {
        var el = Item("""{"jsonrpc":"2.0","method":"C.M"}""");
        var parsed = JsonRpcAdapter.ParseRequest(el);
        parsed.IsValid.Should().BeTrue();
        parsed.IsNotification.Should().BeTrue();
        parsed.Id.Should().BeNull();
    }

    [Fact]
    public void ParseRequest_CapabilityMethods_AreMarkedNotTranslated()
    {
        var discover = JsonRpcAdapter.ParseRequest(Item("""{"jsonrpc":"2.0","method":"trame.discover","id":1}"""));
        discover.IsValid.Should().BeTrue();
        discover.Capability.Should().Be("trame.discover");
        discover.Request.Should().BeNull();

        var caps = JsonRpcAdapter.ParseRequest(Item("""{"jsonrpc":"2.0","method":"trame.capabilities","id":2}"""));
        caps.Capability.Should().Be("trame.capabilities");
        caps.Request.Should().BeNull();
    }

    [Fact]
    public void ParseRequest_NotAnObject_IsInvalidRequest()
    {
        var parsed = JsonRpcAdapter.ParseRequest(Item("42"));
        parsed.IsValid.Should().BeFalse();
        parsed.ErrorCode.Should().Be(-32600);
    }

    // === Fehlercode-Map =====================================================

    [Fact]
    public void MapErrorCode_Routing404_IsMethodNotFound()
    {
        JsonRpcAdapter.MapErrorCode(404, "Controller 'Ghost' not found.").Should().Be(-32601);
        JsonRpcAdapter.MapErrorCode(404, "Method 'X' not found on controller 'Y'.").Should().Be(-32601);
    }

    [Fact]
    public void MapErrorCode_Business404_IsServerError()
    {
        JsonRpcAdapter.MapErrorCode(404, "Customer '42' not found.").Should().Be(-32000);
    }

    [Theory]
    [InlineData(400, -32602)]
    [InlineData(422, -32602)]
    [InlineData(401, -32001)]
    [InlineData(403, -32001)]
    [InlineData(500, -32603)]
    [InlineData(429, -32000)]
    [InlineData(499, -32000)]
    public void MapErrorCode_TrimeTypeToRpc(int trame, int expected)
        => JsonRpcAdapter.MapErrorCode(trame, "x").Should().Be(expected);

    // === Response-Mapping ==================================================

    [Fact]
    public void MapResponse_Success_ResultIsData_IdEchoedWithOriginalType()
    {
        var trame = new TrameResponse
        {
            Code = 200,
            DataBytes = Encoding.UTF8.GetBytes("""{"id":42,"name":"alice"}"""),
        };
        var id = IdElement("7"); // Number 7

        var obj = JsonRpcAdapter.MapResponse(trame, id);

        obj["jsonrpc"]!.GetValue<string>().Should().Be("2.0");
        obj.ContainsKey("error").Should().BeFalse();
        obj["result"]!["id"]!.GetValue<int>().Should().Be(42);
        obj["result"]!["name"]!.GetValue<string>().Should().Be("alice");
        obj["id"]!.GetValue<int>().Should().Be(7); // Originaltyp Number bewahrt
    }

    [Fact]
    public void MapResponse_StringId_EchoedAsString()
    {
        var trame = new TrameResponse { Code = 200, DataBytes = Encoding.UTF8.GetBytes("\"hi\"") };
        var obj = JsonRpcAdapter.MapResponse(trame, IdElement("\"req-1\""));
        obj["id"]!.GetValue<string>().Should().Be("req-1");
        obj["result"]!.GetValue<string>().Should().Be("hi");
    }

    [Fact]
    public void MapResponse_204_ResultIsNull()
    {
        var trame = new TrameResponse { Code = 204 };
        var obj = JsonRpcAdapter.MapResponse(trame, IdElement("1"));
        // result MUSS vorhanden (JSON-RPC) und JSON-null sein — nicht abwesend.
        obj.ContainsKey("result").Should().BeTrue();
        obj.ToJsonString().Should().Contain("\"result\":null");
        obj.ContainsKey("error").Should().BeFalse();
    }

    [Fact]
    public void MapResponse_BinaryContent_ResultIsBase64String()
    {
        var bytes = Encoding.UTF8.GetBytes("blob");
        var trame = new TrameResponse { Code = 200, Content = bytes };
        var obj = JsonRpcAdapter.MapResponse(trame, IdElement("1"));
        obj["result"]!.GetValue<string>().Should().Be(Convert.ToBase64String(bytes));
    }

    [Fact]
    public void MapResponse_Error_UsesMappedCodeAndMessageAndData()
    {
        var trame = new TrameResponse
        {
            Code = 422,
            DataBytes = Encoding.UTF8.GetBytes("""{"title":"Invalid","status":422}"""),
            Error = new TrameError { Code = 422, Message = "Invalid input." },
        };
        var obj = JsonRpcAdapter.MapResponse(trame, IdElement("9"));

        obj.ContainsKey("result").Should().BeFalse();
        obj["error"]!["code"]!.GetValue<int>().Should().Be(-32602); // 422 → Invalid params
        obj["error"]!["message"]!.GetValue<string>().Should().Be("Invalid input.");
        obj["error"]!["data"]!["title"]!.GetValue<string>().Should().Be("Invalid"); // ProblemDetails als data
        obj["id"]!.GetValue<int>().Should().Be(9);
    }

    [Fact]
    public void MapResponse_ErrorWithDetailsString_DataIsDetails()
    {
        var trame = new TrameResponse
        {
            Code = 400,
            Error = new TrameError { Code = 400, Message = "bad", Details = "ParameterName=value" },
        };
        var obj = JsonRpcAdapter.MapResponse(trame, IdElement("1"));
        obj["error"]!["code"]!.GetValue<int>().Should().Be(-32602);
        obj["error"]!["data"]!.GetValue<string>().Should().Be("ParameterName=value");
    }

    [Fact]
    public void BuildResult_And_BuildError_Shapes()
    {
        var ok = JsonRpcAdapter.BuildResult(JsonNode.Parse("42"), IdElement("1"));
        ok["result"]!.GetValue<int>().Should().Be(42);
        ok["id"]!.GetValue<int>().Should().Be(1);

        var err = JsonRpcAdapter.BuildError(-32700, "Parse error.", null);
        err["error"]!["code"]!.GetValue<int>().Should().Be(-32700);
        err.ToJsonString().Should().Contain("\"id\":null"); // Parse error → id null (vorhanden)
    }

    [Fact]
    public void CapabilitiesManifest_ListsTrameStrengths()
    {
        var m = JsonRpcAdapter.CapabilitiesManifest();
        m["nativeClient"]!.GetValue<bool>().Should().BeTrue();
        m["chaining"]!.GetValue<bool>().Should().BeTrue();
        m["bindingModes"]!.AsArray().Select(n => n!.GetValue<string>())
            .Should().BeEquivalentTo(new[] { "Weak", "Strict", "Paranoid" });
        m["compatMode"]!["supported"]!.GetValue<bool>().Should().BeTrue();
    }
}