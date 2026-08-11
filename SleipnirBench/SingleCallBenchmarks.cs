using BenchmarkDotNet.Attributes;
using System.Text.Json.Nodes;
using SleipnirCommon.Models;

namespace SleipnirBench;

/// <summary>
/// Single-Call comparison: REST vs Sleipnir REST vs Sleipnir WebSocket vs Sleipnir SignalR.
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

    // ─── Sleipnir REST (HTTP/JSON via Sleipnir) ────────────────────────────

    [Benchmark(Description = "Sleipnir REST: GetAllCustomers")]
    [BenchmarkCategory("Sleipnir")]
    public async Task<object?> SleipnirRest_GetAllCustomers()
    {
        var request = new SleipnirRequest
        {
            Controller = "Customer",
            Method = "GetAllCustomers",
            Params = JsonNode.Parse("[]"),
            Id = "Customer.GetAllCustomers"
        };
        return await _fixture.SleipnirRestClient.Call(request);
    }

    [Benchmark(Description = "Sleipnir REST: GetCustomerById")]
    [BenchmarkCategory("Sleipnir")]
    public async Task<object?> SleipnirRest_GetCustomerById()
    {
        var request = new SleipnirRequest
        {
            Controller = "Customer",
            Method = "GetCustomerById",
            Params = JsonNode.Parse("[{\"" + "ParameterName" + "\":\"" + "id" + "\",\"" + "Data" + "\":" + "1" + "}]"),
            Id = "Customer.GetCustomerById"
        };
        return await _fixture.SleipnirRestClient.Call(request);
    }

    // ─── Sleipnir WebSocket (persistent connection, no HTTP overhead) ──

    [Benchmark(Description = "Sleipnir WebSocket: GetAllCustomers")]
    [BenchmarkCategory("Sleipnir")]
    public async Task<object?> SleipnirWs_GetAllCustomers()
    {
        var request = new SleipnirRequest
        {
            Controller = "Customer",
            Method = "GetAllCustomers",
            Params = JsonNode.Parse("[]"),
            Id = "Customer.GetAllCustomers"
        };
        return await _fixture.SleipnirWebSocketClient.Call(request);
    }

    [Benchmark(Description = "Sleipnir WebSocket: GetCustomerById")]
    [BenchmarkCategory("Sleipnir")]
    public async Task<object?> SleipnirWs_GetCustomerById()
    {
        var request = new SleipnirRequest
        {
            Controller = "Customer",
            Method = "GetCustomerById",
            Params = JsonNode.Parse("[{\"" + "ParameterName" + "\":\"" + "id" + "\",\"" + "Data" + "\":" + "1" + "}]"),
            Id = "Customer.GetCustomerById"
        };
        return await _fixture.SleipnirWebSocketClient.Call(request);
    }

    // ─── Sleipnir SignalR (persistent connection, MessagePack) ─────────

    [Benchmark(Description = "Sleipnir SignalR: GetAllCustomers")]
    [BenchmarkCategory("Sleipnir")]
    public async Task<object?> SleipnirSignalR_GetAllCustomers()
    {
        var request = new SleipnirRequest
        {
            Controller = "Customer",
            Method = "GetAllCustomers",
            Params = JsonNode.Parse("[]"),
            Id = "Customer.GetAllCustomers"
        };
        return await _fixture.SleipnirSignalrClient.Call(request);
    }

    [Benchmark(Description = "Sleipnir SignalR: GetCustomerById")]
    [BenchmarkCategory("Sleipnir")]
    public async Task<object?> SleipnirSignalR_GetCustomerById()
    {
        var request = new SleipnirRequest
        {
            Controller = "Customer",
            Method = "GetCustomerById",
            Params = JsonNode.Parse("[{\"" + "ParameterName" + "\":\"" + "id" + "\",\"" + "Data" + "\":" + "1" + "}]"),
            Id = "Customer.GetCustomerById"
        };
        return await _fixture.SleipnirSignalrClient.Call(request);
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
