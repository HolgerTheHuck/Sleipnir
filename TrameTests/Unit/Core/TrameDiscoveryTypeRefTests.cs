using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using TrameCore.Model.Messages.Mex;
using TrameCore.Services;
using TrameTests.Fixtures;
using Xunit;

namespace TrameTests.Unit.Core;

/// <summary>
/// Structural TypeRef edge-case coverage for <see cref="TrameDiscoveryService"/>. The base
/// <see cref="TrameDiscoveryServiceTests"/> exercise the common kinds (scalar, array, map,
/// stream, bytes, ref, opaque, enum) and the Weg-C inference rules. These tests cover the
/// branches that were previously untested: <c>set</c> collections, the <c>any</c> scalar
/// (object / JSON-DOM), <c>Nullable&lt;T&gt;</c> value-type unwrap, native arrays, nested
/// collections, a present parameter default, an enum with a non-<c>int</c> underlying type,
/// <c>[TrameExample]</c> example population, a self-referential type (cycle-safe placeholder),
/// bare <c>Task</c> → <c>void</c>, and the deterministic <see cref="DiscoverySerialization"/>
/// wire contract (camelCase + null omission).
/// </summary>
public class TrameDiscoveryTypeRefTests
{
    private static TrameInvoker CreateInvoker<T>() where T : class
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTransient<T>();
        var sp = services.BuildServiceProvider();
        var invoker = new TrameInvoker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<TrameInvoker>>());
        invoker.Register<T>();
        return invoker;
    }

    private static MethodMeta Method(DiscoveryInfo d, string name)
        => d.Controllers[0].Methods.First(m => m.MethodName == name);

    // --- set kind -------------------------------------------------------------------------

    [Fact]
    public void HashSet_IsEmittedAsSet_WithScalarElement()
    {
        var d = CreateInvoker<DiscoveryEdgeCasesController>().GetDiscoveryInfo();
        var m = Method(d, "EchoHashSet");

        m.ReturnType.Kind.Should().Be("set");
        m.ReturnType.Element.Should().NotBeNull();
        m.ReturnType.Element!.Kind.Should().Be("scalar");
        m.ReturnType.Element!.Name.Should().Be("string");

        m.Parameters[0].ParameterType.Kind.Should().Be("set");
        m.Parameters[0].ParameterType.Element!.Name.Should().Be("string");
    }

    [Fact]
    public void SortedSet_IsEmittedAsSet()
    {
        var d = CreateInvoker<DiscoveryEdgeCasesController>().GetDiscoveryInfo();
        var m = Method(d, "EchoSortedSet");

        m.ReturnType.Kind.Should().Be("set");
        m.ReturnType.Element!.Kind.Should().Be("scalar");
        m.ReturnType.Element!.Name.Should().Be("int");
    }

    [Fact]
    public void Set_OfRefTypes_EmitsRefElement()
    {
        var d = CreateInvoker<DiscoveryEdgeCasesController>().GetDiscoveryInfo();
        var m = Method(d, "EchoDtoSet");

        m.ReturnType.Kind.Should().Be("set");
        m.ReturnType.Element!.Kind.Should().Be("ref");
        m.ReturnType.Element!.Ref.Should().Be(typeof(TestDto).FullName);
    }

    // --- scalar "any" (object / JSON-DOM) -------------------------------------------------

    [Theory]
    [InlineData("EchoObject")]
    [InlineData("EchoJsonElement")]
    [InlineData("EchoJsonNode")]
    public void ObjectAndJsonDom_AreEmittedAsAnyScalar(string method)
    {
        var d = CreateInvoker<DiscoveryEdgeCasesController>().GetDiscoveryInfo();
        var m = Method(d, method);

        m.ReturnType.Kind.Should().Be("scalar");
        m.ReturnType.Name.Should().Be("any");
        m.Parameters[0].ParameterType.Kind.Should().Be("scalar");
        m.Parameters[0].ParameterType.Name.Should().Be("any");
    }

    // --- Nullable<T> value-type unwrap ----------------------------------------------------

    [Fact]
    public void NullableValueType_UnwrapsToScalar_AndMarksNullable()
    {
        var d = CreateInvoker<DiscoveryEdgeCasesController>().GetDiscoveryInfo();

        var intM = Method(d, "EchoNullableInt");
        intM.ReturnType.Kind.Should().Be("scalar");
        intM.ReturnType.Name.Should().Be("int");
        intM.ReturnType.Nullable.Should().BeTrue();
        intM.Parameters[0].ParameterType.Name.Should().Be("int");
        intM.Parameters[0].ParameterType.Nullable.Should().BeTrue();

        var guidM = Method(d, "EchoNullableGuid");
        guidM.ReturnType.Name.Should().Be("guid");
        guidM.ReturnType.Nullable.Should().BeTrue();
    }

    // --- native arrays --------------------------------------------------------------------

    [Fact]
    public void NativeArray_IsEmittedAsArray_WithElement()
    {
        var d = CreateInvoker<DiscoveryEdgeCasesController>().GetDiscoveryInfo();
        var m = Method(d, "EchoLongArray");

        m.ReturnType.Kind.Should().Be("array");
        m.ReturnType.Element!.Kind.Should().Be("scalar");
        m.ReturnType.Element!.Name.Should().Be("long");
    }

    [Fact]
    public void NativeArray_OfRefTypes_EmitsRefElement()
    {
        var d = CreateInvoker<DiscoveryEdgeCasesController>().GetDiscoveryInfo();
        var m = Method(d, "EchoDtoArray");

        m.ReturnType.Kind.Should().Be("array");
        m.ReturnType.Element!.Kind.Should().Be("ref");
        m.ReturnType.Element!.Ref.Should().Be(typeof(TestDto).FullName);
    }

    // --- nested collections ---------------------------------------------------------------

    [Fact]
    public void NestedList_EmitsArrayOfArray()
    {
        var d = CreateInvoker<DiscoveryEdgeCasesController>().GetDiscoveryInfo();
        var m = Method(d, "MakeNestedList");

        m.ReturnType.Kind.Should().Be("array");
        m.ReturnType.Element!.Kind.Should().Be("array");
        m.ReturnType.Element!.Element!.Kind.Should().Be("scalar");
        m.ReturnType.Element!.Element!.Name.Should().Be("int");
    }

    [Fact]
    public void MapOfLists_EmitsMapWithArrayValue()
    {
        var d = CreateInvoker<DiscoveryEdgeCasesController>().GetDiscoveryInfo();
        var m = Method(d, "MakeMapOfLists");

        m.ReturnType.Kind.Should().Be("map");
        m.ReturnType.Key!.Name.Should().Be("string");
        m.ReturnType.Value!.Kind.Should().Be("array");
        m.ReturnType.Value!.Element!.Name.Should().Be("int");
    }

    [Fact]
    public void SetOfArrays_EmitsSetWithArrayElement()
    {
        var d = CreateInvoker<DiscoveryEdgeCasesController>().GetDiscoveryInfo();
        var m = Method(d, "MakeSetOfArrays");

        m.ReturnType.Kind.Should().Be("set");
        m.ReturnType.Element!.Kind.Should().Be("array");
        m.ReturnType.Element!.Element!.Name.Should().Be("string");
    }

    // --- present parameter default -------------------------------------------------------

    [Fact]
    public void ParameterDefaultValue_IsCarriedWhenPresent()
    {
        var d = CreateInvoker<DiscoveryEdgeCasesController>().GetDiscoveryInfo();

        var intM = Method(d, "EchoWithDefault");
        var xParam = intM.Parameters.First(p => p.ParameterName == "x");
        xParam.DefaultValue.Should().NotBeNull();
        xParam.DefaultValue.Should().Be(42);

        var strM = Method(d, "EchoStringDefault");
        var sParam = strM.Parameters.First(p => p.ParameterName == "s");
        sParam.DefaultValue.Should().Be("hi");
    }

    // --- enum with non-int underlying ----------------------------------------------------

    [Fact]
    public void EnumWithByteUnderlying_RegistersByteMembers()
    {
        var d = CreateInvoker<DiscoveryEdgeCasesController>().GetDiscoveryInfo();

        d.Types.Should().ContainKey(typeof(ByteFlag).FullName!);
        var enumMeta = d.Types[typeof(ByteFlag).FullName!];
        enumMeta.Kind.Should().Be("enum");
        enumMeta.Members.Should().HaveCount(3);
        enumMeta.Members.Should().Contain(m => m.Name == "None" && (byte)m.Value! == 0);
        enumMeta.Members.Should().Contain(m => m.Name == "A" && (byte)m.Value! == 1);
        enumMeta.Members.Should().Contain(m => m.Name == "B" && (byte)m.Value! == 2);

        var m = Method(d, "EchoByteFlag");
        m.ReturnType.Kind.Should().Be("ref");
        m.ReturnType.Ref.Should().Be(typeof(ByteFlag).FullName);
    }

    // --- [TrameExample] population -------------------------------------------------------

    [Fact]
    public void TrameExample_PopulatesTypeExample()
    {
        var d = CreateInvoker<DiscoveryEdgeCasesController>().GetDiscoveryInfo();

        d.Types.Should().ContainKey(typeof(ExampledDto).FullName!);
        var meta = d.Types[typeof(ExampledDto).FullName!];
        meta.Example.Should().NotBeNull();
        var example = (ExampledDto)meta.Example!;
        example.Id.Should().Be(7);
        example.Name.Should().Be("sample");
    }

    // --- self-referential type (cycle-safe) ----------------------------------------------

    [Fact]
    public void SelfReferentialType_RegistersAndResolvesCycle()
    {
        var d = CreateInvoker<DiscoveryEdgeCasesController>().GetDiscoveryInfo();

        d.Types.Should().ContainKey(typeof(TreeNode).FullName!);
        var meta = d.Types[typeof(TreeNode).FullName!];
        meta.Properties.Should().Contain(p => p.PropertyName == "Value");
        var next = meta.Properties.First(p => p.PropertyName == "Next");
        next.PropertyType.Kind.Should().Be("ref");
        next.PropertyType.Ref.Should().Be(typeof(TreeNode).FullName);
        next.PropertyType.Nullable.Should().BeTrue();

        var m = Method(d, "MakeNode");
        m.ReturnType.Kind.Should().Be("ref");
        m.ReturnType.Ref.Should().Be(typeof(TreeNode).FullName);
    }

    // --- bare Task -> void ----------------------------------------------------------------

    [Fact]
    public void BareTask_ReturnsVoid()
    {
        var d = CreateInvoker<DiscoveryEdgeCasesController>().GetDiscoveryInfo();
        var m = Method(d, "Fire");
        m.ReturnType.Kind.Should().Be("void");
    }

    // --- scalar name coverage -------------------------------------------------------------

    [Theory]
    [InlineData("EchoLong", "long")]
    [InlineData("EchoBool", "bool")]
    [InlineData("EchoDouble", "double")]
    [InlineData("EchoDecimal", "decimal")]
    [InlineData("EchoTimeSpan", "timespan")]
    [InlineData("EchoDateTimeOffset", "datetimeoffset")]
    [InlineData("EchoDateTime", "datetime")]
    [InlineData("EchoGuid", "guid")]
    public void ScalarVariants_MapToNeutralScalarNames(string method, string expectedScalar)
    {
        var d = CreateInvoker<DiscoveryEdgeCasesController>().GetDiscoveryInfo();
        var m = Method(d, method);
        m.ReturnType.Kind.Should().Be("scalar");
        m.ReturnType.Name.Should().Be(expectedScalar);
    }

    // --- DiscoverySerialization wire contract ---------------------------------------------

    [Fact]
    public void DiscoverySerialization_CamelCaseAndOmitsNullOptionals()
    {
        // camelCase keys on the top-level contract object.
        var info = new DiscoveryInfo { Controllers = { new ControllerMeta { Name = "Edge" } } };
        var json = JsonSerializer.Serialize(info, DiscoverySerialization.Options);
        json.Should().Contain("\"discoveryVersion\"");
        json.Should().Contain("\"controllers\"");
        json.Should().NotContain("DiscoveryVersion");  // no PascalCase leakage

        // A scalar TypeRef carries kind + name; every unset optional (element/key/value/ref/
        // nativeName/nullable) must be omitted so the wire matches the contract.
        var scalar = new TypeRef { Kind = "scalar", Name = "int" };
        var scalarJson = JsonSerializer.Serialize(scalar, DiscoverySerialization.Options);
        scalarJson.Should().Contain("\"kind\":\"scalar\"");
        scalarJson.Should().Contain("\"name\":\"int\"");
        scalarJson.Should().NotContain("\"nullable\"");
        scalarJson.Should().NotContain("\"element\"");
        scalarJson.Should().NotContain("\"key\"");
        scalarJson.Should().NotContain("\"value\"");
        scalarJson.Should().NotContain("\"ref\"");
        scalarJson.Should().NotContain("\"nativeName\"");
    }

    [Fact]
    public void DiscoverySerialization_EmitsNullableWhenPresent()
    {
        var tr = new TypeRef { Kind = "scalar", Name = "string", Nullable = true };
        var json = JsonSerializer.Serialize(tr, DiscoverySerialization.Options);
        json.Should().Contain("\"nullable\":true");
    }

    [Fact]
    public void DiscoverySerialization_RefPropertySerializesAsLowerCaseRef()
    {
        // The C# property is `Ref` (keyword collision); the wire field is `ref` (camelCase).
        var tr = new TypeRef { Kind = "ref", Ref = "Some.Type" };
        var json = JsonSerializer.Serialize(tr, DiscoverySerialization.Options);
        json.Should().Contain("\"ref\":\"Some.Type\"");
        json.Should().NotContain("\"Ref\"");
    }

    // --- discovery cache ------------------------------------------------------------------

    [Fact]
    public void GetDiscoveryInfo_CachesUntilInvalidated()
    {
        var invoker = CreateInvoker<DiscoveryEdgeCasesController>();

        var first = invoker.GetDiscoveryInfo();
        var second = invoker.GetDiscoveryInfo();
        second.Should().BeSameAs(first);  // cached instance reused

        // A re-registration invalidates the cache (TrameInvoker.Register wires InvalidateCache).
        invoker.Register<DiscoveryEdgeCasesController>();
        var third = invoker.GetDiscoveryInfo();
        third.Should().NotBeSameAs(first);
        third.Controllers.Should().HaveCount(1);  // still valid, just rebuilt
    }
}