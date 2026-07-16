using BenchmarkDotNet.Attributes;
using System.Text.Json.Nodes;
using TrameCommon.Models;

namespace TrameBench;

/// <summary>
/// 100-Call-Matrix: 100× GetCustomerById — nativ (REST/gRPC) seriell+parallel
/// vs. Trame-Batch (REST/WebSocket/SignalR) parallel+serial. Ein Trame-Batch = 1
/// Roundtrip, der 100 Aufrufe serverseitig bündelt; nativ braucht 100 Roundtrips.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
[HideColumns("Error", "StdDev")]
public class BatchCallBenchmarks
{
    private BenchmarkFixture _fixture = null!;
    private List<TrameRequest> _batchRequests = null!;
    private const int BatchSize = 100;

    [GlobalSetup]
    public void Setup()
    {
        _fixture = new BenchmarkFixture();
        _fixture.Initialize();
        _batchRequests = new List<TrameRequest>();
        for (int i = 1; i <= BatchSize; i++)
        {
            _batchRequests.Add(new TrameRequest
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

    // ─── Trame REST (1 Roundtrip, 100 Aufrufe gebündelt) ─────────────

    [Benchmark(Description = "Trame REST: 100x batch parallel")]
    [BenchmarkCategory("Trame")]
    public async Task<object?> TrameRest_BatchParallel()
    {
        var multiRequest = new TrameMultiRequest
        {
            Mode = ExecutionMode.Parallel,
            Requests = _batchRequests
        };
        return await _fixture.TrameRestClient.Call(multiRequest);
    }

    [Benchmark(Description = "Trame REST: 100x batch serial")]
    [BenchmarkCategory("Trame")]
    public async Task<object?> TrameRest_BatchSerial()
    {
        var multiRequest = new TrameMultiRequest
        {
            Mode = ExecutionMode.Serial,
            Requests = _batchRequests.ToList()
        };
        return await _fixture.TrameRestClient.Call(multiRequest);
    }

    // ─── Trame WebSocket (1 Roundtrip über persistente Verbindung) ────

    [Benchmark(Description = "Trame WebSocket: 100x batch parallel")]
    [BenchmarkCategory("Trame")]
    public async Task<object?> TrameWs_BatchParallel()
    {
        var multiRequest = new TrameMultiRequest
        {
            Mode = ExecutionMode.Parallel,
            Requests = _batchRequests
        };
        return await _fixture.TrameWebSocketClient.Call(multiRequest);
    }

    [Benchmark(Description = "Trame WebSocket: 100x batch serial")]
    [BenchmarkCategory("Trame")]
    public async Task<object?> TrameWs_BatchSerial()
    {
        var multiRequest = new TrameMultiRequest
        {
            Mode = ExecutionMode.Serial,
            Requests = _batchRequests.ToList()
        };
        return await _fixture.TrameWebSocketClient.Call(multiRequest);
    }

    // ─── Trame SignalR (1 Roundtrip, MessagePack-Binär) ───────────────

    [Benchmark(Description = "Trame SignalR: 100x batch parallel")]
    [BenchmarkCategory("Trame")]
    public async Task<object?> TrameSignalR_BatchParallel()
    {
        var multiRequest = new TrameMultiRequest
        {
            Mode = ExecutionMode.Parallel,
            Requests = _batchRequests
        };
        return await _fixture.TrameSignalrClient.Call(multiRequest);
    }

    [Benchmark(Description = "Trame SignalR: 100x batch serial")]
    [BenchmarkCategory("Trame")]
    public async Task<object?> TrameSignalR_BatchSerial()
    {
        var multiRequest = new TrameMultiRequest
        {
            Mode = ExecutionMode.Serial,
            Requests = _batchRequests.ToList()
        };
        return await _fixture.TrameSignalrClient.Call(multiRequest);
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