using System.Net;
using FluentAssertions;
using SleipnirCommon.Models;
using SleipnirCore.Attributes;
using SleipnirCore.Services;
using SleipnirCore.Services.Helper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace SleipnirTests.Unit.Core;

/// <summary>
/// The opaque-site scan behind the UseSleipnir startup warning: a type discovery cannot model
/// (here: System.Net.IPAddress, declared outside the controller assembly) must be reported at
/// every usage site — return, parameter, event payload, nested property — while modelled types
/// (scalars, contract DTOs, the typed envelope SleipnirResponse&lt;T&gt;) are not.
/// </summary>
public class DiscoveryOpaqueScanTests
{
    private static IReadOnlyList<DiscoveryOpaqueScan.OpaqueUsage> Scan<TController>() where TController : class
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTransient<TController>();
        var provider = services.BuildServiceProvider();
        var invoker = new SleipnirInvoker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<ILogger<SleipnirInvoker>>());
        invoker.Register<TController>();
        return DiscoveryOpaqueScan.Find(invoker.GetDiscoveryInfo());
    }

    [Fact]
    public void ReportsEveryUsageSiteOfAnUnmodelledType()
    {
        var usages = Scan<OpaqueSitesController>();

        usages.Should().BeEquivalentTo(new[]
        {
            new DiscoveryOpaqueScan.OpaqueUsage("OpaqueSites.Lookup → return", "IPAddress"),
            new DiscoveryOpaqueScan.OpaqueUsage("OpaqueSites.Ping(target)", "IPAddress"),
            new DiscoveryOpaqueScan.OpaqueUsage("OpaqueSites.Changes → event payload", "IPAddress"),
            new DiscoveryOpaqueScan.OpaqueUsage("OpaqueSites.Raw → return", "SleipnirResponse"),
            new DiscoveryOpaqueScan.OpaqueUsage($"{typeof(HostDto).FullName}.Addresses", "IPAddress"),
        });
    }

    [Fact]
    public void ModelledContractReportsNothing()
    {
        Scan<ModelledOnlyController>().Should().BeEmpty();
    }

    public class HostDto
    {
        public string Name { get; set; } = "";
        public List<IPAddress> Addresses { get; set; } = new();
    }

    [SleipnirController("OpaqueSites", AutoDiscover = false)]
    public class OpaqueSitesController
    {
        [SleipnirMethod("Lookup")]
        public IPAddress Lookup(string host) => IPAddress.Loopback;

        [SleipnirMethod("Ping")]
        public bool Ping(IPAddress target) => true;

        [SleipnirMethod("Host")]
        public HostDto Host() => new();

        [SleipnirEvent("Changes")]
        public IObservable<IPAddress> Changes() => throw new NotSupportedException();

        [SleipnirMethod("Raw")]
        public SleipnirResponse Raw() => new();
    }

    [SleipnirController("ModelledOnly", AutoDiscover = false)]
    public class ModelledOnlyController
    {
        [SleipnirMethod("Get")]
        public Task<SleipnirResponse<HostName>> Get(int id, Guid tenant) => throw new NotSupportedException();
    }

    public class HostName
    {
        public string Name { get; set; } = "";
        public Dictionary<string, int> Ports { get; set; } = new();
    }
}
