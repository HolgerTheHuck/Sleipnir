using BenchmarkDotNet.Attributes;
using System.Text.Json;
using System.Text.Json.Nodes;
using SleipnirCommon.Models;

namespace SleipnirBench;

[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
[HideColumns("Error", "StdDev")]
public class DependencyChainBenchmarks
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

    [Benchmark(Description = "REST: 3 sequential calls (N+1 pattern)", Baseline = true)]
    public async Task<object?> Rest_ThreeSequentialCalls()
    {
        var customerId = await _fixture.RestAddCustomer("Bench-Chain");
        var customer = await _fixture.RestGetCustomerById(customerId);
        if (customer == null) return null;
        return await _fixture.RestGetOrders(customer.OrderId);
    }

    [Benchmark(Description = "gRPC: 3 sequential calls (N+1 pattern, binary)")]
    public async Task<object?> Grpc_ThreeSequentialCalls()
    {
        // gRPC hat kein serverseitiges Dependency-Chaining → 3 Roundtrips wie REST,
        // nur über Protobuf/HTTP·2 statt JSON/HTTP·1.1. Das ist die Binär-Baseline,
        // gegen die Sleipnirs 1-Roundtrip-Batch-Chain verglichen wird.
        var addResp = await _fixture.GrpcAddCustomer("Bench-Grpc");
        var customer = await _fixture.GrpcGetCustomerById(addResp.Id);
        if (customer == null) return null;
        return await _fixture.GrpcGetOrdersByOrderId(customer.OrderId);
    }

    [Benchmark(Description = "Sleipnir: 1 batch call (dependency chain, 3 reqs)")]
    public async Task<object?> Sleipnir_DependencyChain_SingleBatch()
    {
        var multiRequest = new SleipnirMultiRequest
        {
            Mode = ExecutionMode.Serial,
            Requests = new List<SleipnirRequest>
            {
                new SleipnirRequest
                {
                    Controller = "Customer",
                    Method = "AddCustomer",
                    Params = JsonNode.Parse("[{\"ParameterName\":\"name\",\"Data\":\"Bench-Sleipnir\"}]"),
                    Id = "step1",
                    DependencyMapping = new Dictionary<string, string>
                    {
                        { "newCustomerId", "$" }
                    }
                },
                new SleipnirRequest
                {
                    Controller = "Customer",
                    Method = "GetCustomerById",
                    Params = JsonNode.Parse("[{\"ParameterName\":\"id\",\"Data\":\"@newCustomerId\"}]"),
                    Id = "step2",
                    DependencyMapping = new Dictionary<string, string>
                    {
                        { "orderId", "$.orderId" }
                    }
                },
                new SleipnirRequest
                {
                    Controller = "Customer",
                    Method = "GetOrderByOrderId",
                    Params = JsonNode.Parse("[{\"ParameterName\":\"id\",\"Data\":\"@orderId\"}]"),
                    Id = "step3"
                }
            }
        };
        return await _fixture.SleipnirRestClient.Call(multiRequest);
    }

    [Benchmark(Description = "Sleipnir WebSocket: 1 batch call (dependency chain)")]
    public async Task<object?> SleipnirWs_DependencyChain_SingleBatch()
    {
        var multiRequest = new SleipnirMultiRequest
        {
            Mode = ExecutionMode.Serial,
            Requests = new List<SleipnirRequest>
            {
                new SleipnirRequest
                {
                    Controller = "Customer",
                    Method = "AddCustomer",
                    Params = JsonNode.Parse("[{\"ParameterName\":\"name\",\"Data\":\"Bench-Ws\"}]"),
                    Id = "step1",
                    DependencyMapping = new Dictionary<string, string>
                    {
                        { "newCustomerId", "$" }
                    }
                },
                new SleipnirRequest
                {
                    Controller = "Customer",
                    Method = "GetCustomerById",
                    Params = JsonNode.Parse("[{\"ParameterName\":\"id\",\"Data\":\"@newCustomerId\"}]"),
                    Id = "step2",
                    DependencyMapping = new Dictionary<string, string>
                    {
                        { "orderId", "$.orderId" }
                    }
                },
                new SleipnirRequest
                {
                    Controller = "Customer",
                    Method = "GetOrderByOrderId",
                    Params = JsonNode.Parse("[{\"ParameterName\":\"id\",\"Data\":\"@orderId\"}]"),
                    Id = "step3"
                }
            }
        };
        return await _fixture.SleipnirWebSocketClient.Call(multiRequest);
    }

    [Benchmark(Description = "Sleipnir: 3 sequential single calls (no chaining)")]
    public async Task<object?> Sleipnir_ThreeSequentialCalls_NoChaining()
    {
        var resp1 = await _fixture.SleipnirRestClient.Call(new SleipnirRequest
        {
            Controller = "Customer",
            Method = "AddCustomer",
            Params = JsonNode.Parse("[{\"ParameterName\":\"name\",\"Data\":\"Bench-Seq\"}]"),
            Id = "s1"
        });
        var customerId = resp1!.Data!.Value.GetRawText();

        var resp2 = await _fixture.SleipnirRestClient.Call(new SleipnirRequest
        {
            Controller = "Customer",
            Method = "GetCustomerById",
            // SleipnirParameter.Data ist nun ein JsonNode → der Wert wird nativ als JSON-String
            // eingebettet (der Parameter id ist string-typisiert), nicht als nackte Zahl.
            // Sonst schlägt das serverseitige Deserialisieren der Zahl in string fehl.
            Params = JsonNode.Parse("[{\"ParameterName\":\"id\",\"Data\":\"" + customerId + "\"}]"),
            Id = "s2"
        });
        // Data ist seit dem Single-Pass-Fix ein JsonElement (camelCase, kein JSON-String mehr).
        var customerEl = resp2!.Data!.Value;
        // Server serialisiert camelCase ("orderId"); JsonElement ist case-sensitiv.
        var orderId = customerEl.GetProperty("orderId").GetRawText();

        var resp3 = await _fixture.SleipnirRestClient.Call(new SleipnirRequest
        {
            Controller = "Customer",
            Method = "GetOrderByOrderId",
            Params = JsonNode.Parse("[{\"ParameterName\":\"id\",\"Data\":\"" + orderId + "\"}]"),
            Id = "s3"
        });
        return resp3;
    }
}
