using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SleipnirClient.Sleipnir;
using SleipnirCommon.Models;
using SleipnirCore.Attributes;
using SleipnirCore.Model.Messages.Mex;
using SleipnirCore.Services;
using SleipnirCore.Services.Helper;
using SleipnirCore.Tracing;
using Xunit;

namespace SleipnirTests.Unit.Core;

/// <summary>
/// Contract identity (CONTRACT_EVOLUTION.md §7): the contract hash is stable for the same wire
/// contract and changes with it; the server records which contract each call came from and who
/// still calls deprecated methods; the C# client sends its hash.
/// </summary>
public class ContractIdentityTests
{
    private const string HashA = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HashB = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private static DiscoveryInfo Contract(bool reversed = false, string? doc = null, string paramType = "int", string? deprecated = null)
    {
        var order = new ControllerMeta
        {
            Name = "Order",
            Methods =
            {
                new MethodMeta
                {
                    MethodName = "Get",
                    Documentation = doc,
                    Deprecated = deprecated,
                    ReturnType = new TypeRef { Kind = "scalar", Name = "string" },
                    Parameters = { new ParameterMeta { ParameterName = "id", ParameterType = new TypeRef { Kind = "scalar", Name = paramType } } },
                },
                new MethodMeta { MethodName = "Count", ReturnType = new TypeRef { Kind = "scalar", Name = "int" } },
            },
        };
        var customer = new ControllerMeta { Name = "Customer", Methods = { new MethodMeta { MethodName = "Ping" } } };
        var info = new DiscoveryInfo();
        info.Controllers.AddRange(reversed ? new[] { customer, order } : new[] { order, customer });
        if (reversed)
            order.Methods.Reverse();
        return info;
    }

    #region Hash

    [Fact]
    public void Hash_IsIndependentOfRegistrationOrder_AndHasTheDocumentedShape()
    {
        var a = ContractHash.Compute(Contract());
        a.Should().MatchRegex("^sha256:[0-9a-f]{64}$");
        ContractHash.Compute(Contract(reversed: true)).Should().Be(a);
    }

    [Fact]
    public void Hash_IgnoresDocumentationAndDeprecation()
    {
        var a = ContractHash.Compute(Contract());
        ContractHash.Compute(Contract(doc: "Returns the order.")).Should().Be(a);
        ContractHash.Compute(Contract(deprecated: "Use v2.")).Should().Be(a);
    }

    [Fact]
    public void Hash_ChangesWithTheWireContract()
    {
        ContractHash.Compute(Contract(paramType: "long")).Should().NotBe(ContractHash.Compute(Contract()));
    }

    [Fact]
    public void Discovery_CarriesHashAndDeprecation_MethodLevelOverridesControllerLevel()
    {
        var invoker = Invoker(out _);

        var discovery = invoker.GetDiscoveryInfo();

        discovery.ContractHash.Should().MatchRegex("^sha256:[0-9a-f]{64}$");
        var legacy = discovery.Controllers.Single(c => c.Name == "Legacy").Methods.ToDictionary(m => m.MethodName);
        legacy["New"].Deprecated.Should().BeNull();
        legacy["Old"].Deprecated.Should().Be("Use Legacy.New.");
        var sunset = discovery.Controllers.Single(c => c.Name == "Sunset").Methods.ToDictionary(m => m.MethodName);
        sunset["Any"].Deprecated.Should().Be("The whole controller goes away in 2.0.");
        sunset["Special"].Deprecated.Should().Be("Use Legacy.New.");
    }

    #endregion

    #region Registry

    private static HttpContext Context(string? header = null, string? query = null)
    {
        var ctx = new DefaultHttpContext();
        if (header != null)
            ctx.Request.Headers[SleipnirClientContractRegistry.HeaderName] = header;
        if (query != null)
            ctx.Request.QueryString = new QueryString("?" + SleipnirClientContractRegistry.QueryName + "=" + Uri.EscapeDataString(query));
        return ctx;
    }

    [Fact]
    public void Resolve_ReadsHeaderThenQuery_AndValidates()
    {
        SleipnirClientContractRegistry.Resolve(Context(header: HashA)).Should().Be(HashA);
        SleipnirClientContractRegistry.Resolve(Context(query: HashB)).Should().Be(HashB);
        SleipnirClientContractRegistry.Resolve(Context(header: HashA, query: HashB)).Should().Be(HashA);
        SleipnirClientContractRegistry.Resolve(Context()).Should().Be(SleipnirClientContractRegistry.Unknown);
        SleipnirClientContractRegistry.Resolve(Context(header: "v1.2")).Should().Be(SleipnirClientContractRegistry.Invalid);
        SleipnirClientContractRegistry.Resolve(null).Should().Be(SleipnirClientContractRegistry.Unknown);
    }

    [Fact]
    public void Registry_CapsDistinctContracts()
    {
        var registry = new SleipnirClientContractRegistry();
        for (var i = 0; i < SleipnirClientContractRegistry.MaxTrackedContracts + 10; i++)
            registry.RecordCall("sha256:" + i.ToString("x64"), currentContract: null);

        var snapshot = registry.GetSnapshot(null);
        snapshot.Clients.Count(c => c.Contract.StartsWith("sha256:")).Should().Be(SleipnirClientContractRegistry.MaxTrackedContracts);
        snapshot.Clients.Single(c => c.Contract == SleipnirClientContractRegistry.Other).Calls.Should().Be(10);
    }

    #endregion

    #region Invoker

    private static SleipnirInvoker Invoker(out SleipnirClientContractRegistry registry)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTransient<LegacyController>();
        services.AddTransient<SunsetController>();
        var provider = services.BuildServiceProvider();
        registry = new SleipnirClientContractRegistry();
        var invoker = new SleipnirInvoker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<ILogger<SleipnirInvoker>>())
        {
            ClientContracts = registry,
        };
        invoker.Register<LegacyController>();
        invoker.Register<SunsetController>();
        return invoker;
    }

    private static SleipnirRequest Call(string method) => new() { Controller = "Legacy", Method = method, Id = method };

    [Fact]
    public async Task Invoker_RecordsCallsPerContract_AndDeprecatedCallsPerRoute()
    {
        var invoker = Invoker(out var registry);
        var current = invoker.GetDiscoveryInfo().ContractHash;

        await invoker.InvokeDi(Call("New"), Context(header: current));
        await invoker.InvokeDi(Call("Old"), Context(header: HashA));
        await invoker.InvokeDi(new[] { Call("Old"), Call("New") }, Context(header: HashA), ExecutionMode.Parallel);
        await invoker.InvokeDi(Call("Old"), Context());

        var snapshot = registry.GetSnapshot(current);
        snapshot.Current.Should().Be(current);
        snapshot.Clients.Should().ContainSingle(c => c.Contract == current && c.Current && c.Calls == 1);
        snapshot.Clients.Should().ContainSingle(c => c.Contract == HashA && !c.Current && c.Calls == 3);
        snapshot.Clients.Should().ContainSingle(c => c.Contract == SleipnirClientContractRegistry.Unknown && c.Calls == 1);
        snapshot.DeprecatedCalls.Select(d => (d.Route, d.Contract, d.Calls)).Should().BeEquivalentTo(new[]
        {
            ("Legacy.Old", HashA, 2L),
            ("Legacy.Old", SleipnirClientContractRegistry.Unknown, 1L),
        });
    }

    [Fact]
    public async Task Invoker_DoesNotRecordUnresolvedRoutes()
    {
        var invoker = Invoker(out var registry);

        var response = await invoker.InvokeDi(Call("Nope"), Context(header: HashA));

        response!.Code.Should().Be((int)HttpStatusCode.BadRequest);
        registry.GetSnapshot(null).Clients.Should().BeEmpty();
    }

    #endregion

    #region C# client

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Requests = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"code\":200,\"data\":1,\"id\":\"C.M\"}"),
            });
        }
    }

    [Fact]
    public async Task Router_SendsContractHeaderOnRest_AndQueryOnWebSocketAndSignalR()
    {
        var handler = new CapturingHandler();
        await using var router = new SleipnirTransportRouter(new SleipnirRouterOptions
        {
            BaseUrl = "https://host",
            Capability = SleipnirBundleCapability.Signalr,
            DefaultTransport = SleipnirTransport.Rest,
            ContractHash = HashA,
            RestHttpClient = new HttpClient(handler),
        });

        await router.Call(new SleipnirRequest { Controller = "C", Method = "M", Id = "C.M" });

        handler.Requests.Single().Headers.GetValues(SleipnirContract.HeaderName).Should().Equal(HashA);
        SleipnirContract.WithQuery("sleipnirws", HashA).Should().Be("sleipnirws?sleipnir-contract=" + Uri.EscapeDataString(HashA));
        SleipnirContract.WithQuery("hub?x=1", HashA).Should().Be("hub?x=1&sleipnir-contract=" + Uri.EscapeDataString(HashA));
        SleipnirContract.WithQuery("sleipnirws", null).Should().Be("sleipnirws");
    }

    #endregion

    [SleipnirController("Legacy", AutoDiscover = false)]
    public class LegacyController
    {
        [SleipnirMethod("New")]
        public int New() => 1;

        [SleipnirMethod("Old")]
        [SleipnirDeprecated("Use Legacy.New.")]
        public int Old() => New();
    }

    [SleipnirController("Sunset", AutoDiscover = false)]
    [SleipnirDeprecated("The whole controller goes away in 2.0.")]
    public class SunsetController
    {
        [SleipnirMethod("Any")]
        public int Any() => 1;

        [SleipnirMethod("Special")]
        [SleipnirDeprecated("Use Legacy.New.")]
        public int Special() => 2;
    }
}
