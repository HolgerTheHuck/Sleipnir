using Grpc.Net.Client;
using Sleipnir.Grpc;
using Sleipnir.Api;
using Sleipnir.Services;
using SleipnirClient.Sleipnir;
using SleipnirHub.Extensions;
using SleipnirRest;
using SleipnirWebSocket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using System.Text;
using System.Text.Json;

namespace SleipnirBench;

/// <summary>
/// Shared fixture that starts a real Kestrel server for WebSocket/SignalR benchmarks
/// and a TestServer for REST/Sleipnir-REST benchmarks.
/// </summary>
public class BenchmarkFixture
{
    private TestServer _testServer = null!;
    private Microsoft.AspNetCore.Hosting.IWebHost? _realServer;
    private int _port;
    private int _grpcPort;

    public HttpClient RestClient { get; private set; } = null!;
    public SleipnirRestJsonClient SleipnirRestClient { get; private set; } = null!;
    public SleipnirWebSocketClient SleipnirWebSocketClient { get; private set; } = null!;
    public SleipnirSignalrClient SleipnirSignalrClient { get; private set; } = null!;
    public CustomerService CustomerService { get; private set; } = null!;
    public GrpcChannel GrpcChannel { get; private set; } = null!;
    public CustomerGrpc.CustomerGrpcClient GrpcClient { get; private set; } = null!;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public string ServerUrl { get; private set; } = string.Empty;
    public string GrpcServerUrl { get; private set; } = string.Empty;

    private static int AllocateFreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Initialize()
    {
        // Zwei freie Ports: REST/WebSocket/SignalR über HTTP/1.1, gRPC über einen
        // DEDIZIERTEN HTTP/2-Endpoint (h2c prior-knowledge). Kestrel bedient gRPC auf
        // Plaintext nur, wenn der Endpoint reines HTTP/2 ist — ein gemischter
        // Http1AndHttp2-Endpoint lehnt gRPC-Prior-Knowledge mit 'HTTP_1_1_REQUIRED' ab
        // (das offizielle gRPC-Template nutzt deshalb ebenfalls getrennte Endpoints).
        _port = AllocateFreePort();
        _grpcPort = AllocateFreePort();
        ServerUrl = $"http://localhost:{_port}/";
        GrpcServerUrl = $"http://localhost:{_grpcPort}/";

        _realServer = Microsoft.AspNetCore.WebHost.CreateDefaultBuilder()
            .ConfigureKestrel(serverOptions =>
            {
                // REST/WebSocket/SignalR — HTTP/1.1 (WebSocket-Upgrade braucht HTTP/1.1).
                serverOptions.Listen(System.Net.IPAddress.Loopback, _port, listen =>
                    listen.Protocols = HttpProtocols.Http1);
                // gRPC — dediziertes HTTP/2 (Plaintext h2c prior-knowledge).
                serverOptions.Listen(System.Net.IPAddress.Loopback, _grpcPort, listen =>
                    listen.Protocols = HttpProtocols.Http2);
            })
            .ConfigureServices(services =>
            {
                services.AddControllers()
                    .AddApplicationPart(typeof(Sleipnir.Api.CustomerHandler).Assembly);
                // Natives gRPC (Protobuf über HTTP/2) als Binär-Baseline. Server-Implementierung
                // CustomerGrpcService liegt in derselben Assembly wie CustomerHandler.
                services.AddGrpc();
                services.AddEndpointsApiExplorer();
                services.AddSwaggerGen();

                services.AddCors(options =>
                {
                    options.AddDefaultPolicy(policy => policy
                        .AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
                });

                services.AddSleipnir(new SleipnirHub.Extensions.SleipnirOptions
                {
                    EnableDetailedErrors = true,
                    UseMessagePack = true,
                    UseSignalR = true,
                    MaximumParallelInvocationsPerClient = 100,
                    MaximumReceiveMessageSize = 102400,
                    StreamBufferCapacity = 100
                });

                services.AddSingleton<CustomerService>();
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseCors();
                app.UseAuthorization();
                app.UseSleipnir();
                app.UseWebSockets();
                app.UseSleipnirWebSocket("/sleipnirws");
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapControllers();
                    endpoints.MapSleipnirEndpoints();
                    endpoints.MapHub<SleipnirHub.Hub.SleipnirHub>("/sleipnirhub");
                    // gRPC über h2c (Kestrel-Default Http1AndHttp2 verhandelt HTTP/2).
                    endpoints.MapGrpcService<CustomerGrpcService>();
                });
            })
            .Build();

        _realServer.Start();

        // Use the real server's HTTP client for REST and Sleipnir REST
        RestClient = new HttpClient { BaseAddress = new Uri(ServerUrl) };
        CustomerService = _realServer.Services.GetRequiredService<CustomerService>();
        SleipnirRestClient = new SleipnirRestJsonClient(ServerUrl, RestClient);

        // Connect WebSocket client. callTimeout schützt vor Endlos-Hang: wenn der Server
        // bei großen Batches eine Fehler-Antwort ohne matchbare Id sendet (oder gar keine),
        // wirft der Client nach 15 s statt den wartenden TCS nie zu komplettieren.
        SleipnirWebSocketClient = new SleipnirWebSocketClient(ServerUrl, callTimeout: TimeSpan.FromSeconds(15));
        SleipnirWebSocketClient.ConnectAsync().GetAwaiter().GetResult();

        // Connect SignalR client
        SleipnirSignalrClient = new SleipnirSignalrClient(ServerUrl);

        // Connect native gRPC client (HTTP/2 über h2c, kein TLS, dedizierter Port)
        GrpcChannel = GrpcChannel.ForAddress(GrpcServerUrl);
        GrpcClient = new CustomerGrpc.CustomerGrpcClient(GrpcChannel);

        // Seed data
        SeedData().GetAwaiter().GetResult();
    }

    private async Task SeedData()
    {
        for (int i = 0; i < 100; i++)
        {
            await CustomerService.AddCustomer($"Customer-{i}");
        }
    }

    public async Task<List<Sleipnir.Model.Customer>?> RestGetAllCustomers()
    {
        var response = await RestClient.GetAsync("api/customer/all");
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<List<Sleipnir.Model.Customer>>(await response.Content.ReadAsStringAsync(), JsonOpts);
    }

    public async Task<Sleipnir.Model.Customer?> RestGetCustomerById(int id)
    {
        var response = await RestClient.GetAsync($"api/customer/{id}");
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<Sleipnir.Model.Customer>(await response.Content.ReadAsStringAsync(), JsonOpts);
    }

    public async Task<List<Sleipnir.Model.Order>?> RestGetOrders(int orderId)
    {
        var response = await RestClient.GetAsync($"api/customer/{orderId}/orders");
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<List<Sleipnir.Model.Order>>(await response.Content.ReadAsStringAsync(), JsonOpts);
    }

    public async Task<int> RestAddCustomer(string name)
    {
        var json = JsonSerializer.Serialize(new { Name = name }, JsonOpts);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await RestClient.PostAsync("api/customer", content);
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<int>(await response.Content.ReadAsStringAsync(), JsonOpts);
    }

    // ─── Natives gRPC (Protobuf, HTTP/2) — Binär-Baseline ───────────

    public Task<CustomerList> GrpcGetAllCustomers()
        => GrpcClient.GetAllCustomersAsync(new Empty()).ResponseAsync;

    public Task<Customer> GrpcGetCustomerById(int id)
        => GrpcClient.GetCustomerByIdAsync(new CustomerRequest { Id = id }).ResponseAsync;

    public Task<AddCustomerResponse> GrpcAddCustomer(string name)
        => GrpcClient.AddCustomerAsync(new AddCustomerRequest { Name = name }).ResponseAsync;

    public Task<OrderList> GrpcGetOrdersByOrderId(int orderId)
        => GrpcClient.GetOrdersByOrderIdAsync(new OrderRequest { Id = orderId }).ResponseAsync;

    public void Dispose()
    {
        SleipnirWebSocketClient?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        SleipnirRestClient?.Dispose();
        RestClient?.Dispose();
        GrpcChannel?.Dispose();
        _realServer?.Dispose();
    }
}
