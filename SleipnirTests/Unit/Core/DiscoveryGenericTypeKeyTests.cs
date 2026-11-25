// Closed generic DTOs (Page<TicketDto>) must appear in discovery.types under a flat contract key
// ({Namespace}.{Name}Of{Args}) instead of the assembly-qualified CLR FullName
// ("Page`1[[TicketDto, Asm, Version=…, Culture=…, PublicKeyToken=…]]"), which the codegen
// split into garbage identifiers.
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SleipnirCore.Attributes;
using SleipnirCore.Services;
using System.Text.Json;
using Xunit;

namespace SleipnirTests.Unit.Core;

public class DiscoveryGenericTypeKeyTests
{
    private const string Ns = "SleipnirTests.Unit.Core";

    private static SleipnirInvoker CreateInvoker<T>() where T : class
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTransient<T>();
        var sp = services.BuildServiceProvider();
        var invoker = new SleipnirInvoker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<SleipnirInvoker>>());
        invoker.Register<T>();
        return invoker;
    }

    [Fact]
    public void ClosedGeneric_IsKeyedByFlatName_AndExpanded()
    {
        var discovery = CreateInvoker<GenericKeyController>().GetDiscoveryInfo();

        var key = $"{Ns}.GenericPageOfGenericTicketDto";
        discovery.Types.Should().ContainKey(key);
        var meta = discovery.Types[key];
        meta.TypeName.Should().Be(key);
        meta.Properties.Select(p => p.PropertyName).Should().BeEquivalentTo("Items", "NextCursor", "Total");

        var items = meta.Properties.First(p => p.PropertyName == "Items").PropertyType;
        items.Kind.Should().Be("array");
        items.Element!.Ref.Should().Be(typeof(GenericTicketDto).FullName);

        var method = discovery.Controllers[0].Methods.First(m => m.MethodName == "listTickets");
        method.ReturnType.Kind.Should().Be("ref");
        method.ReturnType.Ref.Should().Be(key);
    }

    [Fact]
    public void ClosedGeneric_NestedAndMultipleArguments_AreFlattenedInDeclarationOrder()
    {
        var discovery = CreateInvoker<GenericKeyController>().GetDiscoveryInfo();

        discovery.Types.Should().ContainKey($"{Ns}.GenericPairOfGenericTicketDtoAndGenericPageOfInt32");
        discovery.Types.Should().ContainKey($"{Ns}.GenericPageOfInt32");
        discovery.Types.Should().ContainKey($"{Ns}.GenericPageOfGenericTicketDtoArray");
    }

    [Fact]
    public void ClosedGeneric_FlatNameClashingWithDeclaredType_TakesQualifiedForm()
    {
        var discovery = CreateInvoker<GenericKeyController>().GetDiscoveryInfo();

        // The declared non-generic GenericPageOfString keeps its name; Page<string> is qualified.
        discovery.Types.Should().ContainKey(typeof(GenericPageOfString).FullName!);
        discovery.Types[typeof(GenericPageOfString).FullName!].Properties
            .Should().ContainSingle(p => p.PropertyName == "Marker");
        discovery.Types.Should().ContainKey($"{Ns}.GenericPageOfSystemString");
    }

    [Fact]
    public void SerializedDiscovery_ContainsNoClrGenericOrAssemblyQualifiedNames()
    {
        var discovery = CreateInvoker<GenericKeyController>().GetDiscoveryInfo();
        var json = JsonSerializer.Serialize(discovery);

        json.Should().NotContain("\\u0060").And.NotContain("`");
        json.Should().NotContain("Version=").And.NotContain("PublicKeyToken");
    }
}

public class GenericTicketDto
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
}

public class GenericPage<T>
{
    public List<T> Items { get; set; } = new();
    public string? NextCursor { get; set; }
    public int Total { get; set; }
}

public class GenericPair<TLeft, TRight>
{
    public TLeft? Left { get; set; }
    public TRight? Right { get; set; }
}

/// <summary>A declared type whose name equals the flat name of GenericPage&lt;string&gt;.</summary>
public class GenericPageOfString
{
    public string Marker { get; set; } = "";
}

[SleipnirController("GenericKeys")]
public class GenericKeyController
{
    [SleipnirMethod("listTickets")]
    public GenericPage<GenericTicketDto> ListTickets() => new();

    [SleipnirMethod("pair")]
    public GenericPair<GenericTicketDto, GenericPage<int>> Pair() => new();

    [SleipnirMethod("pageOfArrays")]
    public GenericPage<GenericTicketDto[]> PageOfArrays() => new();

    [SleipnirMethod("declared")]
    public GenericPageOfString Declared() => new();

    [SleipnirMethod("pageOfString")]
    public GenericPage<string> PageOfString() => new();
}
