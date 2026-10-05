using FluentAssertions;
using SleipnirClient.Sleipnir;
using SleipnirCommon.Exceptions;
using SleipnirCommon.Models;
using SleipnirTests.Fixtures;
using System.Text;
using System.Text.Json;
using Xunit;

namespace SleipnirTests.Integration;

/// <summary>
/// End-to-End-Tests für den SignalR-Transport (MessagePack) gegen einen realen
/// Kestrel-Host. Deckt Echo/204/Binary, parallele Korrelation, Cancellation
/// und den Bearer/JWT-Nachweis (A4) ab.
/// </summary>
public class SignalRTransportTests : IClassFixture<TransportTestFixture>
{
    private readonly TransportTestFixture _fixture;

    public SignalRTransportTests(TransportTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Echo_Returns200_AndData()
    {
        var client = _fixture.CreateSignalrClient();
        var request = SleipnirCall.Init("TestInvoker", "Echo").With("hello").ToRequest();

        var resp = await client.Call(request);

        resp!.Code.Should().Be(200);
        resp.Data.Should().NotBeNull();
        resp.Data.Value.GetRawText().Should().Contain("hello");
    }

    [Fact]
    public async Task VoidMethod_Returns204()
    {
        var client = _fixture.CreateSignalrClient();
        var request = SleipnirCall.Init("TestInvoker", "VoidMethod").With("data").ToRequest();

        var resp = await client.Call(request);

        resp!.Code.Should().Be(204);
        resp.IsSuccess.Should().BeTrue();
        resp.Data.Should().BeNull();
    }

    [Fact]
    public async Task DownloadBlob_BinaryRoundTrip()
    {
        var client = _fixture.CreateSignalrClient();
        var request = SleipnirCall.Init("TestInvoker", "DownloadBlob").With("blob").ToRequest();

        var received = await client.CallBinary(request);

        received.Should().NotBeNull();
        received.Should().Equal(Encoding.UTF8.GetBytes("Blob content for blob"));
    }

    [Fact]
    public async Task ParallelEcho_CorrelatesCorrectly()
    {
        var client = _fixture.CreateSignalrClient();
        var tasks = Enumerable.Range(0, 5).Select(i =>
            client.Call(SleipnirCall.Init("TestInvoker", "Echo")
                .With($"msg{i}").Named($"req{i}").ToRequest())).ToList();

        var responses = await Task.WhenAll(tasks);

        for (var i = 0; i < 5; i++)
        {
            responses[i]!.Code.Should().Be(200);
            responses[i]!.Id.Should().Be($"req{i}");
            responses[i]!.Data.Value.GetRawText().Should().Contain($"msg{i}");
        }
    }

    // Audit F5/R6: the hub batch path had no batch-size gate (only the invoker backstop)
    // and 0% coverage. Round-trip through DoWorkMany over MessagePack.

    [Fact]
    public async Task DoWorkMany_WithinBatchCap_CorrelatesCorrectly()
    {
        var client = _fixture.CreateSignalrClient();
        var batch = new SleipnirMultiRequest
        {
            Mode = ExecutionMode.Parallel,
            Requests = Enumerable.Range(0, 5).Select(i =>
                SleipnirCall.Init("TestInvoker", "Echo")
                    .With($"msg{i}").Named($"req{i}").ToRequest()).ToList()
        };

        var responses = (await client.Call(batch))!;

        var list = responses!.ToList();
        for (var i = 0; i < 5; i++)
        {
            list[i]!.Code.Should().Be(200);
            list[i]!.Id.Should().Be($"req{i}");
            list[i]!.Data.Value.GetRawText().Should().Contain($"msg{i}");
        }
    }

    [Fact]
    public async Task DoWorkMany_AboveBatchCap_FailsWithHubException()
    {
        var client = _fixture.CreateSignalrClient();
        var batch = new SleipnirMultiRequest
        {
            Mode = ExecutionMode.Parallel,
            Requests = Enumerable.Range(0, 51).Select(i =>
                SleipnirCall.Init("TestInvoker", "Echo")
                    .With($"msg{i}").ToRequest()).ToList()
        };

        // The hub gate throws a HubException carrying the cap message (the client wraps
        // it in SleipnirException with the HubException as the inner exception).
        var act = async () => await client.Call(batch);

        (await act.Should().ThrowAsync<SleipnirException>())
            .WithInnerException<Exception>()
            .Which.Message.Should().Contain("Batch exceeds MaximumBatchSize (50)");
    }

    [Fact]
    public async Task Cancellation_PropagatesAsOce()
    {
        var client = _fixture.CreateSignalrClient();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var request = SleipnirCall.Init("TestInvoker", "WithCancellation").With("x").ToRequest();

        var act = async () => await client.Call(request, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Secured_WithValidBearer_Returns200()
    {
        // Beweist A4: der Bearer-Ctor setzt den Token; SignalR übermittelt ihn;
        // Test-Auth validiert -> [SleipnirAuthorise] -> 200.
        var client = _fixture.CreateSignalrClient(TestAuthHandler.ValidToken);
        var request = SleipnirCall.Init("TestInvoker", "Secured").With("secret").ToRequest();

        var resp = await client.Call(request);

        resp!.Code.Should().Be(200);
        resp.Data.Value.GetRawText().Should().Contain("secret");
    }

    [Fact]
    public async Task Secured_WithoutBearer_Returns401()
    {
        var client = _fixture.CreateSignalrClient(bearer: null);
        var request = SleipnirCall.Init("TestInvoker", "Secured").With("secret").ToRequest();

        var resp = await client.Call(request);

        resp!.Code.Should().Be(401);
    }

    [Fact]
    public async Task Echo_OverJsonProtocol_RoundTrips()
    {
        // Beweist P2.3: optionales JSON-Protokoll statt MessagePack.
        var client = _fixture.CreateSignalrClient(useMessagePack: false);
        var request = SleipnirCall.Init("TestInvoker", "Echo").With("json-hello").ToRequest();

        var resp = await client.Call(request);

        resp!.Code.Should().Be(200);
        resp.Data.Value.GetRawText().Should().Contain("json-hello");
    }
}