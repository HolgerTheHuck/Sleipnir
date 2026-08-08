using FluentAssertions;
using TrameClient.Trame;
using TrameCommon.Models;
using TrameCore.Services;
using TrameTests.Fixtures;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace TrameTests.Integration;

/// <summary>
/// End-to-End-Tests für den WebSocket-Transport gegen einen realen Kestrel-Host.
/// Deckt die behobenen Korrektheits-Bugs ab: Response-Deserialisierung (A1),
/// UTF-8-Chunk-Decoding (A2), Void/204, Binary, parallele Korrelation (B3),
/// Cancellation, dotted Namespace.
/// </summary>
public class WebSocketTransportTests : IClassFixture<TransportTestFixture>
{
    private readonly TransportTestFixture _fixture;

    public WebSocketTransportTests(TransportTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Echo_Returns200_AndData()
    {
        // Beweist A1: pre-Fix wäre Code=0 / Data=null wegen Default-Options.
        var client = _fixture.CreateWsClient();
        var request = TrameCall.Init("TestInvoker", "Echo").With("hello").ToRequest();

        var resp = await client.Call(request);

        resp!.Code.Should().Be(200);
        resp.Data.Should().NotBeNull();
        resp.Data.Value.GetRawText().Should().Contain("hello");
    }

    [Fact]
    public async Task Echo_NonAscii_LargePayload_RoundTrips()
    {
        // Beweist A2: ≥8 KB mit Multi-Byte-Zeichen über Chunk-Grenzen hinweg.
        var client = _fixture.CreateWsClient();
        var payload = new string('ü', 3000) + "🎉" + new string('ä', 3000);
        var request = TrameCall.Init("TestInvoker", "Echo").With(payload).ToRequest();

        var result = await client.Call<string>(request);

        result.Should().Be(payload);
    }

    [Fact]
    public async Task VoidMethod_Returns204()
    {
        var client = _fixture.CreateWsClient();
        var request = TrameCall.Init("TestInvoker", "VoidMethod").With("data").ToRequest();

        var resp = await client.Call(request);

        resp!.Code.Should().Be(204);
        resp.IsSuccess.Should().BeTrue();
        resp.Data.Should().BeNull();
    }

    [Fact]
    public async Task UploadBlob_DownloadBlob_BinaryRoundTrip()
    {
        var client = _fixture.CreateWsClient();
        var bytes = Encoding.UTF8.GetBytes("hello-binary-payload-" + new string('x', 5000));

        // byte[] data wird aus BinaryData injiziert; filename als normaler Parameter.
        var upload = new TrameRequest
        {
            Controller = "TestInvoker",
            Method = "UploadBlob",
            Params = JsonSerializer.SerializeToNode(new[]
            {
                new TrameParameter { ParameterName = "filename", Data = JsonSerializer.SerializeToNode("f.bin") }
            }),
            BinaryData = bytes,
            Id = "TestInvoker.UploadBlob"
        };

        var uploadResp = await client.Call(upload);
        uploadResp!.Code.Should().Be(200);
        uploadResp.Data.Value.GetRawText().Should().Contain(bytes.Length.ToString());

        // DownloadBlob liefert byte[] -> Content-Feld.
        var download = TrameCall.Init("TestInvoker", "DownloadBlob").With("blob").ToRequest();
        var received = await client.CallBinary(download);

        received.Should().NotBeNull();
        received.Should().Equal(Encoding.UTF8.GetBytes("Blob content for blob"));
    }

    [Fact]
    public async Task ParallelEcho_CorrelatesCorrectly()
    {
        // Beweist B3: jeder parallele Call bekommt exakt sein eigenes Resultat.
        var client = _fixture.CreateWsClient();
        var tasks = Enumerable.Range(0, 5).Select(i =>
            client.Call(TrameCall.Init("TestInvoker", "Echo")
                .With($"msg{i}").Named($"req{i}").ToRequest())).ToList();

        var responses = await Task.WhenAll(tasks);

        for (var i = 0; i < 5; i++)
        {
            responses[i]!.Code.Should().Be(200);
            responses[i]!.Id.Should().Be($"req{i}");
            responses[i]!.Data.Value.GetRawText().Should().Contain($"msg{i}");
        }
    }

    [Fact]
    public async Task WithCancellation_PropagatesCancellation()
    {
        // Cancellation muss als OCE propagieren, nicht als TrameException (P0.7).
        var client = _fixture.CreateWsClient();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var request = TrameCall.Init("TestInvoker", "WithCancellation").With("x").ToRequest();

        var act = async () => await client.Call(request, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task NestedNamespace_Add_RoutesCorrectly()
    {
        var client = _fixture.CreateWsClient();
        var request = TrameCall.Init("Customer.Address.Contact", "Add").With("Alice").ToRequest();

        var resp = await client.Call(request);

        resp!.Code.Should().Be(200);
        resp.Data.Value.GetRawText().Should().Contain("added Alice");
    }

    [Fact]
    public async Task MultiCall_WithoutIds_CompletesInsteadOfHanging()
    {
        // R3 regression: a TrameMultiRequest without ids used to hang forever — the client
        // generated a correlation id but never wrote it into any request, so the server
        // echoed "" and the strict dispatcher dropped the response (callTimeout=null →
        // infinite wait). The short callTimeout makes a regression fail loud instead of
        // hanging the test runner.
        var client = new TrameWebSocketClient(_fixture.BaseUrl, callTimeout: TimeSpan.FromSeconds(5));

        // Build two requests and strip their ids — TrameCall.Init defaults Id to
        // "Controller.Method"; the regression requires genuinely id-less requests.
        var req1 = TrameCall.Init("TestInvoker", "Echo").With("a").ToRequest();
        var req2 = TrameCall.Init("TestInvoker", "Echo").With("b").ToRequest();
        req1.Id = null;
        req2.Id = null;
        var multi = new TrameMultiRequest
        {
            Mode = ExecutionMode.Parallel,
            Requests = new List<TrameRequest> { req1, req2 },
        };

        // Preconditions: none of the requests carry an id before the call.
        multi.Requests.Should().AllSatisfy(r => r.Id.Should().BeNullOrEmpty());

        var responses = await client.Call(multi);

        responses.Should().NotBeNull().And.HaveCount(2);
        responses!.Select(r => r!.Id).Should().AllSatisfy(id => id.Should().NotBeNullOrEmpty(),
            "the client must assign ids and the server must echo them so the batch correlates");
        responses!.Select(r => r!.Code).Should().AllBeEquivalentTo(200);

        await client.DisposeAsync();
    }
}