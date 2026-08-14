// Tests that SleipnirDiscoveryService serializes the server-side [SleipnirNavigation] (SleipnirCommon)
// into the discovery JSON `navigation` field — the producer half of the one-declaration pipeline. The
// codegen (ContractsEmitterNavigationTests) consumes that field and re-emits the client attribute.
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SleipnirCore.Model.Messages.Mex;
using SleipnirCore.Services;
using SleipnirTests.Fixtures;
using System.Text.Json;
using Xunit;

namespace SleipnirTests.Unit.Core;

public class SleipnirDiscoveryServiceNavigationTests
{
    private readonly SleipnirInvoker _invoker;

    public SleipnirDiscoveryServiceNavigationTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTransient<NavFetchController>();
        var sp = services.BuildServiceProvider();
        _invoker = new SleipnirInvoker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<SleipnirInvoker>>());
        _invoker.Register<NavFetchController>();
    }

    [Fact]
    public void Property_without_attribute_has_null_navigation()
    {
        var discovery = _invoker.GetDiscoveryInfo();
        var root = discovery.Types[typeof(NavRootDto).FullName!];

        var id = root.Properties.First(p => p.PropertyName == "Id");
        id.Navigation.Should().BeNull();
    }

    [Fact]
    public void Property_with_attribute_serializes_navigation_edge()
    {
        var discovery = _invoker.GetDiscoveryInfo();
        var root = discovery.Types[typeof(NavRootDto).FullName!];

        var owner = root.Properties.First(p => p.PropertyName == "Owner");
        owner.Navigation.Should().NotBeNull();
        owner.Navigation!.Fetch.Should().Be("NavFetch.GetOwners");
        owner.Navigation.Key.Should().Be("ownerId");
        owner.Navigation.ChildKey.Should().Be("id");
        owner.Navigation.Param.Should().Be("ownerIds");
    }

    [Fact]
    public void Navigation_is_emitted_as_camelcase_json_and_omitted_when_absent()
    {
        var discovery = _invoker.GetDiscoveryInfo();
        var json = JsonSerializer.Serialize(discovery, DiscoverySerialization.Options);

        // camelCase wire shape with all four fields, on the Owner property's entry.
        json.Should().Contain("\"navigation\":{\"fetch\":\"NavFetch.GetOwners\",\"key\":\"ownerId\",\"childKey\":\"id\",\"param\":\"ownerIds\"}");
        // A property without the attribute must not carry a `navigation` key (WhenWritingNull omits null).
        // The Id property is a scalar int with no nav → its entry is {"propertyName":"Id","propertyType":{...}}
        // with no navigation sibling. Assert by counting: exactly one `navigation` occurrence in the whole doc.
        var count = 0;
        var idx = 0;
        while ((idx = json.IndexOf("\"navigation\"", idx, System.StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx++;
        }
        count.Should().Be(1, "only the Owner property declares a navigation edge");
    }
}