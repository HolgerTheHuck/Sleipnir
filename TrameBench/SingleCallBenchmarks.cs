using BenchmarkDotNet.Attributes;
using System.Text.Json.Nodes;
using TrameCommon.Models;

namespace TrameBench;

/// <summary>
/// Single-Call comparison: REST vs Trame REST vs Trame WebSocket vs Trame SignalR.
/// Uses a real Kestrel server for WebSocket/SignalR (needs TCP connections).
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 10, iterationCount: 15)]
[HideColumns("Error", "StdDev")]
public class SingleCallBenchmarks
{
    private BenchmarkFixture _fixture = null!;

    [GlobalSetup]
    public void Setup()
    {
        _fixture = new BenchmarkFixture();
        _fixture.Initialize();
    }

    [GlobalCleanup]
    public void Cleanup() => _fixture.Dispose();

    // ─── REST (HTTP/JSON) ──────────────────────────────────────────

    [Benchmark(Description = "REST: GetAllCustomers")]
    [BenchmarkCategory("REST")]
    public async Task<object?> Rest_GetAllCustomers()
    {
        return await _fixture.RestGetAllCustomers();
    }

    [Benchmark(Description = "REST: GetCustomerById")]
    [BenchmarkCategory("REST")]
    public async Task<object?> Rest_GetCustomerById()
    {
        return await _fixture.RestGetCustomerById(1);
    }

    // ─── Trame REST (HTTP/JSON via Trame) ────────────────────────────

    [Benchmark(Description = "Trame REST: GetAllCustomers")]
    [BenchmarkCategory("Trame")]
    public async Task<object?> TrameRest_GetAllCustomers()
    {
        var request = new TrameRequest
        {
            Controller = "Customer",
            Method = "GetAllCustomers",
            Params = JsonNode.Parse("[]"),
            Id = "Customer.GetAllCustomers"
        };
        return await _fixture.TrameRestClient.Call(request);
    }

    [Benchmark(Description = "Trame REST: GetCustomerById")]
    [BenchmarkCategory("Trame")]
    public async Task<object?> TrameRest_GetCustomerById()
    {
        var request = new TrameRequest
        {
            Controller = "Customer",
            Method = "GetCustomerById",
            Params = JsonNode.Parse("[{\"" + "ParameterName" + "\":\"" + "id" + "\",\"" + "Data" + "\":" + "1" + "}]"),
            Id = "Customer.GetCustomerById"
        };
        return await _fixture.TrameRestClient.Call(request);
    }

    // ─── Trame WebSocket (persistent connection, no HTTP overhead) ──

    [Benchmark(Description = "Trame WebSocket: GetAllCustomers")]
    [BenchmarkCategory("Trame")]
    public async Task<object?> TrameWs_GetAllCustomers()
    {
        var request = new TrameRequest
        {
            Controller = "Customer",
            Method = "GetAllCustomers",
            Params = JsonNode.Parse("[]"),
            Id = "Customer.GetAllCustomers"
        };
        return await _fixture.TrameWebSocketClient.Call(request);
    }

    [Benchmark(Description = "Trame WebSocket: GetCustomerById")]
    [BenchmarkCategory("Trame")]
    public async Task<object?> TrameWs_GetCustomerById()
    {
        var request = new TrameRequest
        {
            Controller = "Customer",
            Method = "GetCustomerById",
            Params = JsonNode.Parse("[{\"" + "ParameterName" + "\":\"" + "id" + "\",\"" + "Data" + "\":" + "1" + "}]"),
            Id = "Customer.GetCustomerById"
        };
        return await _fixture.TrameWebSocketClient.Call(request);
    }

    // ─── Trame SignalR (persistent connection, MessagePack) ─────────

    [Benchmark(Description = "Trame SignalR: GetAllCustomers")]
    [BenchmarkCategory("Trame")]
    public async Task<object?> TrameSignalR_GetAllCustomers()
    {
        var request = new TrameRequest
        {
            Controller = "Customer",
            Method = "GetAllCustomers",
            Params = JsonNode.Parse("[]"),
            Id = "Customer.GetAllCustomers"
        };
        return await _fixture.TrameSignalrClient.Call(request);
    }

    [Benchmark(Description = "Trame SignalR: GetCustomerById")]
    [BenchmarkCategory("Trame")]
    public async Task<object?> TrameSignalR_GetCustomerById()
    {
        var request = new TrameRequest
        {
            Controller = "Customer",
            Method = "GetCustomerById",
            Params = JsonNode.Parse("[{\"" + "ParameterName" + "\":\"" + "id" + "\",\"" + "Data" + "\":" + "1" + "}]"),
            Id = "Customer.GetCustomerById"
        };
        return await _fixture.TrameSignalrClient.Call(request);
    }

    // ─── Natives gRPC (Protobuf, HTTP/2) — Binär-Baseline ───────────

    [Benchmark(Description = "gRPC: GetAllCustomers")]
    [BenchmarkCategory("gRPC")]
    public async Task<object?> Grpc_GetAllCustomers()
    {
        return await _fixture.GrpcGetAllCustomers();
    }

    [Benchmark(Description = "gRPC: GetCustomerById")]
    [BenchmarkCategory("gRPC")]
    public async Task<object?> Grpc_GetCustomerById()
    {
        return await _fixture.GrpcGetCustomerById(1);
    }
}
