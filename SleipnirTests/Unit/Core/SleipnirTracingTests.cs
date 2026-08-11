using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using SleipnirCommon.Models;
using SleipnirCore.Attributes;
using SleipnirCore.Services;
using SleipnirCore.Tracing;
using SleipnirTests.Fixtures;
using Xunit;

namespace SleipnirTests.Unit.Core;

/// <summary>
/// Tracing-Tests für die always-on OpenTelemetry-Instrumentierung des Sleipnir-Motors.
/// Beobachtet die emittierten Activities über die public Oberfläche (ActivitySource-Name
/// <c>"Sleipnir"</c> + in-box <see cref="ActivityListener"/>) — ohne OTel-SDK, ohne
/// InternalsVisibleTo. Übt den vollen Aufruf-Pfad (Single-Call, Batch, Auto-Detect,
/// Binär, Domain-Fehler, Exception) über den registrierten Invoker aus.
/// </summary>
/// <remarks>
/// Der <see cref="ActivityListener"/> ist prozess-global — erfasst auch Activities, die
/// andere Invoker-basierte Tests (SleipnirInvokerTests, Integrationstests) parallel
/// emittieren. Jeder Test startet daher einen Test-Harness-Activity (eigene Quelle
/// <c>"Sleipnir.Tests.Harness"</c>); <see cref="ActivityCapture.Mine"/> liefert nur die
/// „Sleipnir"-Activities, die vom eigenen Harness abstammen. Fremde Tests haben einen
/// anderen (oder keinen) Parent und werden herausgefiltert — volle Parallelität bleibt.
///
/// Die Telemetry-Tests starten hingegen die OTel-SDK-Subscription, die ihrerseits „Sleipnir"
/// abonniert und den <c>NoListener</c>-Test sowie die <c>probe != null</c>-Assertions
/// verfälschen könnte. Daher teilen sich Tracing- und Telemetry-Tests die Collection
/// „sleipnir-tracing“ (serialisiert nur diese untereinander); der Rest der Assembly
/// parallelisiert normal weiter.
/// </remarks>
[Collection("sleipnir-tracing")]
public class SleipnirTracingTests
{
    private readonly IServiceProvider _serviceProvider;
    private readonly SleipnirInvoker _invoker;

    public SleipnirTracingTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTransient<TestInvokerController>();
        services.AddTransient<DependencyChainController>();
        services.AddTransient<ThrowingSleipnirController>();
        _serviceProvider = services.BuildServiceProvider();

        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var logger = _serviceProvider.GetRequiredService<ILogger<SleipnirInvoker>>();
        _invoker = new SleipnirInvoker(scopeFactory, logger);
        _invoker.Register<TestInvokerController>();
        _invoker.Register<DependencyChainController>();
        _invoker.Register<ThrowingSleipnirController>();
    }

    #region Single Call

    [Fact]
    public async Task SingleCall_EmitsActivityWithRpcTags_AndOkStatus()
    {
        using var capture = new ActivityCapture();
        var request = CreateRequest("TestInvoker", "Echo", ("message", "\"hi\""));

        var response = await _invoker.InvokeDi(request, null);

        response!.Code.Should().Be((int)HttpStatusCode.OK);
        capture.Mine().Should().ContainSingle(a => a.OperationName == "SleipnirCall");
        var call = capture.Mine().Single(a => a.OperationName == "SleipnirCall");
        ActivityCapture.Tag(call, "rpc.system").Should().Be("sleipnir");
        ActivityCapture.Tag(call, "rpc.service").Should().Be("TestInvoker");
        ActivityCapture.Tag(call, "rpc.method").Should().Be("Echo");
        ActivityCapture.Tag(call, "sleipnir.request_id").Should().Be("TestInvoker.Echo");
        call.Status.Should().Be(ActivityStatusCode.Ok);
    }

    [Fact]
    public async Task SingleCall_DomainError_SetsErrorStatus_WithoutExceptionTags()
    {
        using var capture = new ActivityCapture();
        // GetOr404(99) liefert einen strukturierten Domain-Fehler (SleipnirResults.NotFound),
        // KEIN throw → Error-Status aus der Response, aber kein exception.type-Tag.
        var request = CreateRequest("TestInvoker", "GetOr404", ("id", "99"));

        var response = await _invoker.InvokeDi(request, null);

        response!.Code.Should().Be(404);
        var call = capture.Mine().Single(a => a.OperationName == "SleipnirCall");
        call.Status.Should().Be(ActivityStatusCode.Error);
        ActivityCapture.Tag(call, "exception.type").Should().BeNull();
    }

    [Fact]
    public async Task SingleCall_EmptyId_OmitsRequestIdTag()
    {
        using var capture = new ActivityCapture();
        var request = new SleipnirRequest
        {
            Controller = "TestInvoker",
            Method = "NoParams",
            Params = JsonNode.Parse("[]")
            // Id absichtlich nicht gesetzt → kein sleipnir.request_id-Tag.
        };

        await _invoker.InvokeDi(request, null);

        var call = capture.Mine().Single(a => a.OperationName == "SleipnirCall");
        ActivityCapture.Tag(call, "sleipnir.request_id").Should().BeNull();
        ActivityCapture.Tag(call, "rpc.method").Should().Be("NoParams");
    }

    [Fact]
    public async Task SingleCall_BinaryData_RecordsLengthTag()
    {
        using var capture = new ActivityCapture();
        var request = new SleipnirRequest
        {
            Controller = "TestInvoker",
            Method = "UploadBlob",
            Params = JsonNode.Parse("[{\"ParameterName\":\"filename\",\"Data\":\"test.bin\"}]"),
            BinaryData = new byte[] { 1, 2, 3, 4, 5 },
            Id = "UploadBlob"
        };

        var response = await _invoker.InvokeDi(request, null);

        response!.Code.Should().Be((int)HttpStatusCode.OK);
        var call = capture.Mine().Single(a => a.OperationName == "SleipnirCall");
        ActivityCapture.Tag(call, "sleipnir.binary.length").Should().Be("5");
    }

    #endregion

    #region No Listener (cost neutrality)

    [Fact]
    public void NoListener_StartActivityReturnsNull_IsCostNeutral()
    {
        // Ohne ActivityListener muss StartActivity null liefern — die always-on
        // Instrumentierung ist kostenneutral, solange niemand abonniert. Der Test läuft
        // in der „sleipnir-tracing“-Collection, sodass kein paralleler Telemetry-SDK-Listener
        // (der „Sleipnir“ abonniert) das Ergebnis verfälscht.
        var probe = new ActivitySource(SleipnirTracing.ActivitySourceName);

        probe.StartActivity("probe").Should().BeNull();
    }

    #endregion

    #region Batch

    [Fact]
    public async Task Batch_EmitsParentAndChildActivities()
    {
        using var capture = new ActivityCapture();
        var requests = new List<SleipnirRequest>
        {
            CreateRequest("TestInvoker", "Add", ("a", "1"), ("b", "2")),
            CreateRequest("TestInvoker", "Echo", ("message", "\"batch\""))
        };

        var responses = (await _invoker.InvokeDi(requests, null, ExecutionMode.Parallel)).ToList();

        responses.Should().HaveCount(2);
        responses.Should().AllSatisfy(r => r!.Code.Should().Be((int)HttpStatusCode.OK));

        var mine = capture.Mine();
        mine.Should().ContainSingle(a => a.OperationName == "SleipnirBatch");
        var batch = mine.Single(a => a.OperationName == "SleipnirBatch");
        ActivityCapture.Tag(batch, "sleipnir.batch.count").Should().Be("2");
        ActivityCapture.Tag(batch, "sleipnir.batch.mode").Should().Be("Parallel");

        var calls = mine.Where(a => a.OperationName == "SleipnirCall").ToList();
        calls.Should().HaveCount(2);
        // Jeder Call ist via Activity.Current ein Kind des Batch-Parents (und damit des Harness).
        calls.Should().AllSatisfy(c => c.Parent?.OperationName.Should().Be("SleipnirBatch"));
    }

    [Fact]
    public async Task Batch_WithDependencyMapping_SetsDependencyBatchesMode()
    {
        using var capture = new ActivityCapture();
        var step1 = ChainRequest("s1", "DepChain", "MakeDto",
            new Dictionary<string, string> { { "id", "$.id" } },
            ("id", "7"), ("name", "\"Alice\""));
        var step2 = ChainRequest("s2", "DepChain", "MakeDto", null,
            ("id", "@id"), ("name", "\"Bob\""));

        await _invoker.InvokeDi(new[] { step1, step2 }, null);

        var mine = capture.Mine();
        mine.Should().ContainSingle(a => a.OperationName == "SleipnirBatch");
        var batch = mine.Single(a => a.OperationName == "SleipnirBatch");
        // Auto-Detect überschreibt den Mode-Tag, sobald ein Request ein Mapping deklariert.
        ActivityCapture.Tag(batch, "sleipnir.batch.mode").Should().Be("DependencyBatches");
    }

    [Fact]
    public async Task Batch_ThrowingStream_RecordsExceptionAndErrorStatus()
    {
        using var capture = new ActivityCapture();
        // Ein Streaming-Throw entwischt ExecuteMethod (dessen innerer Catch nur
        // ResultCardinalityExceededException fängt und der äußere nur TargetInvocationException)
        // und erreicht den Batch-Catch → RecordException setzt exception.type.
        var request = new SleipnirRequest
        {
            Controller = "Throwing",
            Method = "BoomStream",
            Params = JsonNode.Parse("[]"),
            Id = "boom"
        };

        var responses = (await _invoker.InvokeDi(new[] { request }, null, ExecutionMode.Parallel)).ToList();

        responses[0]!.Code.Should().Be((int)HttpStatusCode.InternalServerError);
        var call = capture.Mine().Single(a => a.OperationName == "SleipnirCall");
        call.Status.Should().Be(ActivityStatusCode.Error);
        ActivityCapture.Tag(call, "exception.type").Should().Be("System.InvalidOperationException");
    }

    #endregion

    #region Helpers

    // jsonValue ist entweder ein roher @alias-Platzhalter (C#-String ab "@") oder ein
    // JSON-kodierter Wert ("42", "\"alice\"", "true", …). Seit Data ein nativer JsonNode
    // ist, wandelt dieser Guard beides in die richtige Knotenform.
    private static JsonNode? ToData(string jsonValue)
        => jsonValue.StartsWith("@") ? JsonValue.Create(jsonValue) : JsonNode.Parse(jsonValue);

    private static SleipnirRequest CreateRequest(string controller, string method,
        params (string name, string jsonValue)[] parameters)
    {
        var paramList = parameters.Select(p => new SleipnirParameter
        {
            ParameterName = p.name,
            Data = ToData(p.jsonValue)
        }).ToList();
        return new SleipnirRequest
        {
            Controller = controller,
            Method = method,
            Params = JsonSerializer.SerializeToNode(paramList),
            Id = $"{controller}.{method}"
        };
    }

    private static SleipnirRequest ChainRequest(string id, string controller, string method,
        Dictionary<string, string>? mapping,
        params (string name, string jsonValue)[] parameters)
    {
        var paramList = parameters.Select(p => new SleipnirParameter
        {
            ParameterName = p.name,
            Data = ToData(p.jsonValue)
        }).ToList();
        return new SleipnirRequest
        {
            Controller = controller,
            Method = method,
            Params = JsonSerializer.SerializeToNode(paramList),
            Id = id,
            DependencyMapping = mapping
        };
    }

    // Convenience-Wrapper ohne Mapping — delegiert mit null. Eigener Name vermeidet die
    // params-Überladungs-Kollision (Tupel vs. Dictionary als 4. Argument).
    private static SleipnirRequest ChainRequest(string id, string controller, string method,
        params (string name, string jsonValue)[] parameters)
        => ChainRequest(id, controller, method, null, parameters);

    /// <summary>
    /// Hängt einen <see cref="ActivityListener"/> an den Sleipnir-ActivitySource-Namen und
    /// sammelt jede gestartete „Sleipnir"-Activity. Parallel dazu startet ein Test-Harness-Activity
    /// (eigene Quelle <c>Sleipnir.Tests.Harness</c>), der alle vom Test ausgelösten Activities
    /// als Parent dient. <see cref="Mine"/> liefert nur diese — fremde Tests (parallele
    /// Invoker-Tests) haben einen anderen/noch keinen Parent und werden herausgefiltert.
    /// Dispose stoppt Harness und Listener wieder.
    /// </summary>
    private sealed class ActivityCapture : IDisposable
    {
        private const string HarnessSourceName = "Sleipnir.Tests.Harness";

        private readonly ActivityListener _listener;
        private readonly ActivitySource _harnessSource;
        private readonly Activity _harness;
        public List<Activity> Activities { get; } = new();

        public ActivityCapture()
        {
            // Listener zuerst anhängen, damit StartActivity für die Harness-Quelle nicht null ist.
            _listener = new ActivityListener
            {
                // „Sleipnir“ sammeln; „Sleipnir.Tests.Harness“ nur abonnieren, damit der Harness
                // wirklich erzeugt wird (aber nicht in Activities aufnehmen).
                ShouldListenTo = source =>
                    source.Name == SleipnirTracing.ActivitySourceName || source.Name == HarnessSourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                    ActivitySamplingResult.AllDataAndRecorded,
                SampleUsingParentId = (ref ActivityCreationOptions<string> _) =>
                    ActivitySamplingResult.AllDataAndRecorded,
                ActivityStarted = activity =>
                {
                    if (activity.Source.Name == SleipnirTracing.ActivitySourceName)
                        Activities.Add(activity);
                }
            };
            ActivitySource.AddActivityListener(_listener);

            _harnessSource = new ActivitySource(HarnessSourceName);
            // Wird Current — alle Sleipnir-Activities des Tests werden dessen Kinder.
            _harness = _harnessSource.StartActivity("test-harness", ActivityKind.Internal)!;
        }

        /// <summary>Nur die „Sleipnir"-Activities, die vom eigenen Test-Harness abstammen.</summary>
        public List<Activity> Mine() =>
            Activities.Where(a => IsDescendantOf(a, _harness)).ToList();

        /// <summary>Liest einen String-Tag aus der Activity (null wenn nicht gesetzt).</summary>
        public static string? Tag(Activity activity, string name)
        {
            foreach (var kv in activity.TagObjects)
                if (kv.Key == name)
                    return kv.Value?.ToString();
            return null;
        }

        // Walkt die Parent-Kette bis zum Harness (true) oder bis zum Root (false).
        private static bool IsDescendantOf(Activity? activity, Activity ancestor)
        {
            for (var current = activity; current is not null; current = current.Parent)
                if (ReferenceEquals(current, ancestor))
                    return true;
            return false;
        }

        public void Dispose()
        {
            _harness.Dispose();
            _listener.Dispose();
            _harnessSource.Dispose();
        }
    }

    #endregion
}

/// <summary>
/// Eigener werfender Controller für den Exception-Tracing-Test. Getrennt von
/// TestInvokerController, damit deren 18-Methoden-Discovery-Assertion unangetastet bleibt.
/// Der Streaming-Throw entwischt ExecuteMethod und erreicht den Batch-Catch, der
/// RecordException aufruft (ein synchroner throw würde vom kompilierten Delegate-Pfad
/// ebenfalls entkommen — der Stream ist der deterministischste Weg).
/// </summary>
[SleipnirController("Throwing")]
public sealed class ThrowingSleipnirController
{
    [SleipnirMethod("BoomStream")]
    public async IAsyncEnumerable<int> BoomStream(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return 1;
        throw new InvalidOperationException("sleipnir-stream-boom");
    }
}

/// <summary>
/// Serialisiert die Tracing- und Telemetry-Tests untereinander (prozess-globaler
/// ActivityListener / OTel-SDK-Subscription); der Rest der Assembly parallelisiert normal.
/// </summary>
[CollectionDefinition("sleipnir-tracing")]
public class SleipnirTracingCollectionDefinition { }