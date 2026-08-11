using BenchmarkDotNet.Attributes;
using System.Text.Json.Nodes;
using SleipnirCommon.Models;

namespace SleipnirBench;

/// <summary>
/// 100-Call-Matrix: 100× GetCustomerById — nativ (REST/gRPC) seriell+parallel
/// vs. Sleipnir-Batch (REST/WebSocket/SignalR) parallel+serial. Ein Sleipnir-Batch = 1
/// Roundtrip, der 100 Aufrufe serverseitig bündelt; nativ braucht 100 Roundtrips.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
[HideColumns("Error", "StdDev")]
public class BatchCallBenchmarks
{
    private BenchmarkFixture _fixture = null!;
    private List<SleipnirRequest> _batchRequests = null!;
    private const int BatchSize = 100;

    [GlobalSetup]
    public void Setup()
    {
        _fixture = new BenchmarkFixture();
        _fixture.Initialize();
        _batchRequests = new List<SleipnirRequest>();
        for (int i = 1; i <= BatchSize; i++)
        {
            _batchRequests.Add(new SleipnirRequest
            {
                Controller = "Customer",
                Method = "GetCustomerById",
                Params = JsonNode.Parse("[{\"" + "ParameterName" + "\":\"" + "id" + "\",\"" + "Data" + "\":" + i + "}]"),
                Id = "req-" + i
            });
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _fixture.Dispose();

    // ─── Natives REST (100 Roundtrips) ───────────────────────────────

    [Benchmark(Description = "REST: 100x sequential GetCustomerById", Baseline = true)]
    [BenchmarkCategory("REST")]
    public async Task Rest_Sequential100()
    {
        for (int i = 1; i <= BatchSize; i++)
            await _fixture.RestGetCustomerById(i);
    }

    [Benchmark(Description = "REST: 100x parallel GetCustomerById")]
    [BenchmarkCategory("REST")]
    public async Task Rest_Parallel100()
    {
        var tasks = Enumerable.Range(1, BatchSize).Select(i => _fixture.RestGetCustomerById(i));
        await Task.WhenAll(tasks);
    }

    // ─── Sleipnir REST (1 Roundtrip, 100 Aufrufe gebündelt) ─────────────

    [Benchmark(Description = "Sleipnir REST: 100x batch parallel")]
    [BenchmarkCategory("Sleipnir")]
    public async Task<object?> SleipnirRest_BatchParallel()
    {
        var multiRequest = new SleipnirMultiRequest
        {
            Mode = ExecutionMode.Parallel,
            Requests = _batchRequests
        };
        return await _fixture.SleipnirRestClient.Call(multiRequest);
    }

    [Benchmark(Description = "Sleipnir REST: 100x batch serial")]
    [BenchmarkCategory("Sleipnir")]
    public async Task<object?> SleipnirRest_BatchSerial()
    {
        var multiRequest = new SleipnirMultiRequest
        {
            Mode = ExecutionMode.Serial,
            Requests = _batchRequests.ToList()
        };
        return await _fixture.SleipnirRestClient.Call(multiRequest);
    }

    // ─── Sleipnir WebSocket (1 Roundtrip über persistente Verbindung) ────

    [Benchmark(Description = "Sleipnir WebSocket: 100x batch parallel")]
    [BenchmarkCategory("Sleipnir")]
    public async Task<object?> SleipnirWs_BatchParallel()
    {
        var multiRequest = new SleipnirMultiRequest
        {
            Mode = ExecutionMode.Parallel,
            Requests = _batchRequests
        };
        return await _fixture.SleipnirWebSocketClient.Call(multiRequest);
    }

    [Benchmark(Description = "Sleipnir WebSocket: 100x batch serial")]
    [BenchmarkCategory("Sleipnir")]
    public async Task<object?> SleipnirWs_BatchSerial()
    {
        var multiRequest = new SleipnirMultiRequest
        {
            Mode = ExecutionMode.Serial,
            Requests = _batchRequests.ToList()
        };
        return await _fixture.SleipnirWebSocketClient.Call(multiRequest);
    }

    // ─── Sleipnir SignalR (1 Roundtrip, MessagePack-Binär) ───────────────

    [Benchmark(Description = "Sleipnir SignalR: 100x batch parallel")]
    [BenchmarkCategory("Sleipnir")]
    public async Task<object?> SleipnirSignalR_BatchParallel()
    {
        var multiRequest = new SleipnirMultiRequest
        {
            Mode = ExecutionMode.Parallel,
            Requests = _batchRequests
        };
        return await _fixture.SleipnirSignalrClient.Call(multiRequest);
    }

    [Benchmark(Description = "Sleipnir SignalR: 100x batch serial")]
    [BenchmarkCategory("Sleipnir")]
    public async Task<object?> SleipnirSignalR_BatchSerial()
    {
        var multiRequest = new SleipnirMultiRequest
        {
            Mode = ExecutionMode.Serial,
            Requests = _batchRequests.ToList()
        };
        return await _fixture.SleipnirSignalrClient.Call(multiRequest);
    }

    // ─── Natives gRPC (100 Roundtrips, Protobuf) ────────────────────

    [Benchmark(Description = "gRPC: 100x sequential GetCustomerById")]
    [BenchmarkCategory("gRPC")]
    public async Task Grpc_Sequential100()
    {
        for (int i = 1; i <= BatchSize; i++)
            await _fixture.GrpcGetCustomerById(i);
    }

    [Benchmark(Description = "gRPC: 100x parallel GetCustomerById")]
    [BenchmarkCategory("gRPC")]
    public async Task Grpc_Parallel100()
    {
        var tasks = Enumerable.Range(1, BatchSize).Select(i => _fixture.GrpcGetCustomerById(i));
        await Task.WhenAll(tasks);
    }
}