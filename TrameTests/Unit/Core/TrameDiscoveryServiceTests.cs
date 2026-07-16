using FluentAssertions;
using TrameCommon.Models;
using TrameCore.Services;
using TrameTests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;
using Xunit;

namespace TrameTests.Unit.Core;

/// <summary>
/// Unit tests for TrameDiscoveryService metadata generation.
/// </summary>
public class TrameDiscoveryServiceTests
{
    private readonly TrameInvoker _invoker;

    public TrameDiscoveryServiceTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTransient<TestInvokerController>();
        var sp = services.BuildServiceProvider();
        _invoker = new TrameInvoker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<TrameInvoker>>());
        _invoker.Register<TestInvokerController>();
    }

    [Fact]
    public void GetDiscoveryInfo_ReturnsAllControllers()
    {
        // Act
        var discovery = _invoker.GetDiscoveryInfo();

        // Assert
        discovery.Controllers.Should().HaveCount(1);
        discovery.Controllers[0].Name.Should().Be("TestInvoker");
    }

    [Fact]
    public void GetDiscoveryInfo_ReturnsAllMethods()
    {
        // Act
        var discovery = _invoker.GetDiscoveryInfo();
        var controller = discovery.Controllers[0];

        // Assert
        controller.Methods.Should().HaveCount(18); // All methods decorated with [TrameMethod]
        controller.Methods.Select(m => m.MethodName).Should().Contain(new[]
        {
            "Echo", "Add", "EchoAsync", "AddAsync", "VoidMethod",
            "WithCancellation", "ComplexReturn", "NoParams", "Secured", "SecuredWithRole",
            "StreamNumbers", "StreamNumbersTask", "UploadBlob", "DownloadBlob", "UploadAndProcess", "DownloadStream",
            "GetOr404", "ValidationProblem"
        });
    }

    [Fact]
    public void GetDiscoveryInfo_ExcludesCancellationTokenFromParameters()
    {
        // Act
        var discovery = _invoker.GetDiscoveryInfo();
        var method = discovery.Controllers[0]
            .Methods.First(m => m.MethodName == "WithCancellation");

        // Assert
        method.Parameters.Should().HaveCount(1);
        method.Parameters[0].ParameterName.Should().Be("input");
    }

    [Fact]
    public void GetDiscoveryInfo_FriendlyTypeNamesForPrimitives()
    {
        // Act
        var discovery = _invoker.GetDiscoveryInfo();
        var addMethod = discovery.Controllers[0]
            .Methods.First(m => m.MethodName == "Add");

        // Assert
        addMethod.Parameters.Should().HaveCount(2);
        addMethod.Parameters[0].ParameterType.Kind.Should().Be("scalar");
        addMethod.Parameters[0].ParameterType.Name.Should().Be("int");
        addMethod.Parameters[1].ParameterType.Kind.Should().Be("scalar");
        addMethod.Parameters[1].ParameterType.Name.Should().Be("int");
        addMethod.ReturnType.Kind.Should().Be("scalar");
        addMethod.ReturnType.Name.Should().Be("int");
    }

    [Fact]
    public void GetDiscoveryInfo_StringReturnType_ForEchoMethod()
    {
        // Act
        var discovery = _invoker.GetDiscoveryInfo();
        var echoMethod = discovery.Controllers[0]
            .Methods.First(m => m.MethodName == "Echo");

        // Assert
        echoMethod.ReturnType.Kind.Should().Be("scalar");
        echoMethod.ReturnType.Name.Should().Be("string");
    }

    [Fact]
    public void GetDiscoveryInfo_VoidReturnType_ForVoidMethod()
    {
        // Act
        var discovery = _invoker.GetDiscoveryInfo();
        var voidMethod = discovery.Controllers[0]
            .Methods.First(m => m.MethodName == "VoidMethod");

        // Assert
        voidMethod.ReturnType.Kind.Should().Be("void");
    }

    [Fact]
    public void GetDiscoveryInfo_RegistersDataContractTypes()
    {
        // Act
        var discovery = _invoker.GetDiscoveryInfo();

        // Assert
        discovery.Types.Should().NotBeEmpty();
        discovery.Types.Should().ContainKey(typeof(TestDto).FullName!);
    }

    [Fact]
    public void GetDiscoveryInfo_TypeMeta_HasProperties()
    {
        // Act
        var discovery = _invoker.GetDiscoveryInfo();
        var typeMeta = discovery.Types[typeof(TestDto).FullName!];

        // Assert
        typeMeta.Properties.Should().HaveCount(2);
        typeMeta.Properties.Should().Contain(p => p.PropertyName == "Id");
        typeMeta.Properties.Should().Contain(p => p.PropertyName == "Name");
    }

    // --- Signatur-Inferenz (Weg C) -------------------------------------------------

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

    /// <summary>Regel 4: unmarkierter Typ aus der Controller-Assembly wird per Inference expandiert.</summary>
    [Fact]
    public void GetDiscoveryInfo_Inference_ExpandsUnmarkedOwnAssemblyType()
    {
        var invoker = CreateInvoker<DiscoveryInferenceController>();
        var discovery = invoker.GetDiscoveryInfo();

        // UnmarkedDto (Test-Assembly, kein Attribute) muss via Inference expandiert werden.
        discovery.Types.Should().ContainKey(typeof(UnmarkedDto).FullName!);
        var typeMeta = discovery.Types[typeof(UnmarkedDto).FullName!];
        typeMeta.Properties.Should().HaveCount(2);
        typeMeta.Properties.Should().Contain(p => p.PropertyName == "Id");
        typeMeta.Properties.Should().Contain(p => p.PropertyName == "Name");

        // …und am Methoden-Rückgabewert als ref verlinkt sein.
        var method = discovery.Controllers[0].Methods.First(m => m.MethodName == "ReturnUnmarked");
        method.ReturnType.Kind.Should().Be("ref");
        method.ReturnType.Ref.Should().Be(typeof(UnmarkedDto).FullName);
        method.ReturnType.Nullable.Should().NotHaveValue(); // not-nullable is absent
    }

    /// <summary>Regel 5: Framework-Envelope aus fremder Assembly bleibt opaque (keine Property-Expansion).</summary>
    [Fact]
    public void GetDiscoveryInfo_Inference_KeepsFrameworkEnvelopeOpaque()
    {
        var invoker = CreateInvoker<DiscoveryInferenceController>();
        var discovery = invoker.GetDiscoveryInfo();

        // TrameResponse liegt in TrameCommon.dll (fremde Assembly) → nicht expandieren (opaque).
        discovery.Types.Should().NotContainKey(typeof(TrameResponse).FullName!);
        var method = discovery.Controllers[0].Methods.First(m => m.MethodName == "ReturnFrameworkType");
        method.ReturnType.Kind.Should().Be("opaque");
        method.ReturnType.NativeName.Should().Be("TrameResponse");
    }

    /// <summary>Regel 2: Own-Assembly-Typ mit [TrameDataContract(Exclude = true)] bleibt force-opaque.</summary>
    [Fact]
    public void GetDiscoveryInfo_Inference_ExcludeKeepsOwnAssemblyTypeOpaque()
    {
        var invoker = CreateInvoker<DiscoveryInferenceController>();
        var discovery = invoker.GetDiscoveryInfo();

        // ExcludedDto ist in der Test-Assembly, aber Exclude-Override → nicht expandieren (opaque).
        discovery.Types.Should().NotContainKey(typeof(ExcludedDto).FullName!);
        var method = discovery.Controllers[0].Methods.First(m => m.MethodName == "TakeExcluded");
        var param = method.Parameters.First(p => p.ParameterName == "d");
        param.ParameterType.Kind.Should().Be("opaque");
        param.ParameterType.NativeName.Should().Be("ExcludedDto");
        method.ReturnType.Kind.Should().Be("scalar");
        method.ReturnType.Name.Should().Be("int");
    }

    // --- Neue strukturelle TypeRef-Kinds (enum / map / set / stream / bytes / nullable / default) -

    /// <summary>Enums werden mit Membern registriert und per ref referenziert.</summary>
    [Fact]
    public void GetDiscoveryInfo_EnumRegisteredWithMembers()
    {
        var invoker = CreateInvoker<DependencyChainController>();
        var discovery = invoker.GetDiscoveryInfo();

        discovery.Types.Should().ContainKey(typeof(ChainPriority).FullName!);
        var enumMeta = discovery.Types[typeof(ChainPriority).FullName!];
        enumMeta.Kind.Should().Be("enum");
        enumMeta.Members.Should().NotBeNullOrEmpty();
        enumMeta.Members.Should().HaveCount(3);
        enumMeta.Members.Should().Contain(m => m.Name == "Low" && (int)m.Value! == 0);
        enumMeta.Members.Should().Contain(m => m.Name == "Medium" && (int)m.Value! == 1);
        enumMeta.Members.Should().Contain(m => m.Name == "High" && (int)m.Value! == 2);

        var method = discovery.Controllers[0].Methods.First(m => m.MethodName == "EchoPriority");
        method.ReturnType.Kind.Should().Be("ref");
        method.ReturnType.Ref.Should().Be(typeof(ChainPriority).FullName);
        method.Parameters[0].ParameterType.Kind.Should().Be("ref");
        method.Parameters[0].ParameterType.Ref.Should().Be(typeof(ChainPriority).FullName);
    }

    /// <summary>Dictionary&lt;K,V&gt; wird als map mit key+value TypeRefs emittiert (kein unknown mehr).</summary>
    [Fact]
    public void GetDiscoveryInfo_DictionaryIsMap()
    {
        var invoker = CreateInvoker<DependencyChainController>();
        var discovery = invoker.GetDiscoveryInfo();
        var method = discovery.Controllers[0].Methods.First(m => m.MethodName == "MakeDict");

        method.ReturnType.Kind.Should().Be("map");
        method.ReturnType.Key.Should().NotBeNull();
        method.ReturnType.Key!.Kind.Should().Be("scalar");
        method.ReturnType.Key!.Name.Should().Be("string");
        method.ReturnType.Value.Should().NotBeNull();
        method.ReturnType.Value!.Kind.Should().Be("scalar");
        method.ReturnType.Value!.Name.Should().Be("int");
    }

    /// <summary>List&lt;T&gt; wird als array mit element TypeRef emittiert.</summary>
    [Fact]
    public void GetDiscoveryInfo_ListIsArray()
    {
        var invoker = CreateInvoker<DependencyChainController>();
        var discovery = invoker.GetDiscoveryInfo();
        var intList = discovery.Controllers[0].Methods.First(m => m.MethodName == "MakeIntList");

        intList.ReturnType.Kind.Should().Be("array");
        intList.ReturnType.Element.Should().NotBeNull();
        intList.ReturnType.Element!.Kind.Should().Be("scalar");
        intList.ReturnType.Element!.Name.Should().Be("int");

        var dtoList = discovery.Controllers[0].Methods.First(m => m.MethodName == "MakeDtoList");
        dtoList.ReturnType.Kind.Should().Be("array");
        dtoList.ReturnType.Element!.Kind.Should().Be("ref");
        dtoList.ReturnType.Element!.Ref.Should().Be(typeof(TestDto).FullName);
    }

    /// <summary>IAsyncEnumerable&lt;T&gt; wird als stream deklariert (Vertrag), nicht als array.</summary>
    [Fact]
    public void GetDiscoveryInfo_AsyncEnumerableIsStream()
    {
        var discovery = _invoker.GetDiscoveryInfo();
        var method = discovery.Controllers[0].Methods.First(m => m.MethodName == "StreamNumbers");

        method.ReturnType.Kind.Should().Be("stream");
        method.ReturnType.Element.Should().NotBeNull();
        method.ReturnType.Element!.Kind.Should().Be("scalar");
        method.ReturnType.Element!.Name.Should().Be("int");
    }

    /// <summary>byte[] wird als scalar "bytes" emittiert (binär), nicht als int-Array.</summary>
    [Fact]
    public void GetDiscoveryInfo_ByteArrayIsBytesScalar()
    {
        var discovery = _invoker.GetDiscoveryInfo();
        var download = discovery.Controllers[0].Methods.First(m => m.MethodName == "DownloadBlob");
        download.ReturnType.Kind.Should().Be("scalar");
        download.ReturnType.Name.Should().Be("bytes");

        var upload = discovery.Controllers[0].Methods.First(m => m.MethodName == "UploadBlob");
        var dataParam = upload.Parameters.First(p => p.ParameterName == "data");
        dataParam.ParameterType.Kind.Should().Be("scalar");
        dataParam.ParameterType.Name.Should().Be("bytes");
    }

    /// <summary>Nullability aus C# NRT wird occurrence-level auf den TypeRef gelegt.</summary>
    [Fact]
    public void GetDiscoveryInfo_NullableReferenceReturn()
    {
        var invoker = CreateInvoker<DependencyChainController>();
        var discovery = invoker.GetDiscoveryInfo();
        var method = discovery.Controllers[0].Methods.First(m => m.MethodName == "FindDto");

        method.ReturnType.Kind.Should().Be("ref");
        method.ReturnType.Ref.Should().Be(typeof(TestDto).FullName);
        method.ReturnType.Nullable.Should().BeTrue();
    }

    /// <summary>Ein Default-Wert wird auf dem Parameter getragen; ohne Default bleibt er absent.</summary>
    [Fact]
    public void GetDiscoveryInfo_ParameterDefaultValue()
    {
        var discovery = _invoker.GetDiscoveryInfo();
        // StreamNumbers(int count, CancellationToken ct = default) — ct ist gedroppt, count ohne Default.
        var method = discovery.Controllers[0].Methods.First(m => m.MethodName == "StreamNumbers");
        var countParam = method.Parameters.First(p => p.ParameterName == "count");
        countParam.DefaultValue.Should().BeNull();
    }

    /// <summary>Die Schema-Version wird auf der DiscoveryInfo getragen.</summary>
    [Fact]
    public void GetDiscoveryInfo_HasSchemaVersion()
    {
        var discovery = _invoker.GetDiscoveryInfo();
        discovery.DiscoveryVersion.Should().NotBeNullOrEmpty();
    }
}
