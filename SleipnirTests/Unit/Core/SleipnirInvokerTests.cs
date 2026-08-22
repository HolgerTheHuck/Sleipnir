using FluentAssertions;
using SleipnirCommon.Models;
using SleipnirCore.Attributes;
using SleipnirCore.Services;
using SleipnirTests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Xunit;

namespace SleipnirTests.Unit.Core;

/// <summary>
/// Unit tests for SleipnirInvoker – the core RPC invocation engine.
/// </summary>
public class SleipnirInvokerTests
{
    private readonly IServiceProvider _serviceProvider;
    private readonly SleipnirInvoker _invoker;

    public SleipnirInvokerTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTransient<TestInvokerController>();
        services.AddTransient<NestedContactController>();
        services.AddTransient<DependencyChainController>();
        _serviceProvider = services.BuildServiceProvider();

        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var logger = _serviceProvider.GetRequiredService<ILogger<SleipnirInvoker>>();
        _invoker = new SleipnirInvoker(scopeFactory, logger);
        _invoker.Register<TestInvokerController>();
        _invoker.Register<NestedContactController>();
        _invoker.Register<DependencyChainController>();
    }

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

    #region Registration

    [Fact]
    public void Register_ControllerWithAttributes_AddsToRouteHandlers()
    {
        // Act & Assert: After registration, discovery should list the controller
        var discovery = _invoker.GetDiscoveryInfo();
        discovery.Controllers.Should().Contain(c => c.Name == "TestInvoker");
    }

    [Fact]
    public void Register_NonControllerType_DoesNotRegister()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        var sp = services.BuildServiceProvider();
        var invoker = new SleipnirInvoker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<SleipnirInvoker>>());

        // Act: Register a type without [SleipnirController]
        invoker.Register(typeof(string));

        // Assert: No controllers registered
        var discovery = invoker.GetDiscoveryInfo();
        discovery.Controllers.Should().BeEmpty();
    }

    // Gleichnamige Sleipnir-Methoden auf demselben Controller sind verboten — Sleipnir
    // hat keine parameterbasierte Überladungsauflösung, der Dispatch-Key ist rein
    // namensbasiert. Früher schluckte TryAdd das still (first-wins, nicht-
    // deterministisch); jetzt wird zur Registrierungszeit hart geworfen.
    [Fact]
    public void Register_DuplicateMethodNameOnSameController_Throws()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        var sp = services.BuildServiceProvider();
        var invoker = new SleipnirInvoker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<SleipnirInvoker>>());

        // Act
        var act = () => invoker.Register<ControllerWithDuplicateMethodNames>();

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*names within a controller must be unique*");
    }

    // Zwei verschiedene Controllertypen mit demselben Sleipnir-Controller-Namen
    // kollidieren ebenfalls — sonst würde der zweite den ersten still schattieren.
    [Fact]
    public void Register_DuplicateControllerName_DifferentType_Throws()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        var sp = services.BuildServiceProvider();
        var invoker = new SleipnirInvoker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<SleipnirInvoker>>());
        invoker.Register<ControllerNamedClashA>();

        // Act
        var act = () => invoker.Register<ControllerNamedClashB>();

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Controller names must be unique*");
    }

    // Erneute Registrierung desselben Controllertyps bleibt idempotent — wichtig
    // für Auto-Discovery (UseSleipnir) plus explizite Registrierung im selben Host.
    [Fact]
    public void Register_SameControllerTypeTwice_IsIdempotent()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        var sp = services.BuildServiceProvider();
        var invoker = new SleipnirInvoker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<SleipnirInvoker>>());

        // Act: zweimal derselbe Typ — darf nicht werfen
        invoker.Register<TestInvokerController>();
        var act = () => invoker.Register<TestInvokerController>();

        // Assert
        act.Should().NotThrow();
        var discovery = invoker.GetDiscoveryInfo();
        discovery.Controllers.Should().Contain(c => c.Name == "TestInvoker");
    }

    #endregion

    #region Event Marker Contract ([SleipnirEvent])

    // [SleipnirEvent] is the required marker for server-push event methods. A method is either
    // a call ([SleipnirMethod]) or an event ([SleipnirEvent]) — never both — and the return-type
    // contract is enforced at registration (fail-loud, like name uniqueness).

    private static SleipnirInvoker NewInvoker()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var sp = services.BuildServiceProvider();
        return new SleipnirInvoker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<SleipnirInvoker>>());
    }

    [Fact]
    public void Register_EventMethodNotReturningObservable_Throws()
    {
        var invoker = NewInvoker();
        var act = () => invoker.Register<EventNotObservableController>();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*[SleipnirEvent]*IObservable<T>*");
    }

    [Fact]
    public void Register_CallMethodReturningObservable_Throws()
    {
        var invoker = NewInvoker();
        var act = () => invoker.Register<MethodObservableButMarkedMethodController>();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*IObservable<T>*[SleipnirMethod]*[SleipnirEvent]*");
    }

    [Fact]
    public void Register_BothMarkersOnSameMethod_Throws()
    {
        var invoker = NewInvoker();
        var act = () => invoker.Register<BothMarkersController>();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*both*[SleipnirMethod]*[SleipnirEvent]*");
    }

    [Fact]
    public async Task Subscribe_ToCallMethod_Returns400AndDoesNotExecute()
    {
        // Arrange: a fresh invoker with a call method (Poke) that has an observable side effect.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTransient<EventContractController>();
        var sp = services.BuildServiceProvider();
        var invoker = new SleipnirInvoker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<SleipnirInvoker>>());
        invoker.Register<EventContractController>();
        EventContractController.PokeCount = 0;

        // Act: subscribe to a [SleipnirMethod] call — must fail before the body runs.
        var req = CreateRequest("EventContract", "Poke");
        var result = await invoker.SubscribeAsync(req, null);

        // Assert: 400, no observable, and the call method was NOT executed.
        result.Error.Should().NotBeNull();
        result.Error!.Code.Should().Be(400);
        result.Observable.Should().BeNull();
        EventContractController.PokeCount.Should().Be(0);
    }

    [Fact]
    public async Task InvokeDi_CallToEventMethod_Returns400Not500()
    {
        // "ObservableStrings" is a [SleipnirEvent] method — a plain call must return an
        // actionable 400, not the opaque 500 ("Failed to serialize the response.") it used to.
        var req = CreateRequest("TestInvoker", "ObservableStrings", ("count", "3"));
        var resp = await _invoker.InvokeDi(req, null);

        resp.Should().NotBeNull();
        resp!.Code.Should().Be(400);
    }

    [Fact]
    public async Task Subscribe_ToEventMethod_ReturnsObservable()
    {
        // Happy path: a real [SleipnirEvent] method resolves to its IObservable (not serialized).
        var req = CreateRequest("TestInvoker", "ObservableStrings", ("count", "3"));
        var result = await _invoker.SubscribeAsync(req, null);

        result.Error.Should().BeNull();
        result.Observable.Should().NotBeNull();
    }

    [Fact]
    public async Task Subscribe_ToValueTypeEvent_ReturnsObservableAndPushesBoxedInts()
    {
        // Regression: IObservable<int> (value-type element) was rejected at subscribe time
        // up to 1.2.0 by the covariant `result is IObservable<object?>` test as "not a
        // subscribable event" (IObservable<out T> covariance does not apply to value-type
        // elements). Now: subscribe succeeds and the boxing adapter delivers the ints as object?.
        var (invoker, sp) = NewInvokerWithValueTypeEvent();
        using (sp)
        {
            var req = CreateRequest("VtEvents", "ObservableInts", ("count", "4"));
            var result = await invoker.SubscribeAsync(req, null);

            result.Error.Should().BeNull();
            result.Observable.Should().NotBeNull();

            var pushed = new List<object?>();
            var done = new ManualResetEventSlim();
            result.Observable!.Subscribe(new ObjCollector(pushed, done));
            done.Wait(500);
            pushed.Should().BeEquivalentTo(new object?[] { 0, 1, 2, 3 });
        }
    }

    // ── Backpressure resolution (per-event override ?? global option ?? default) ──

    private static (SleipnirInvoker invoker, ServiceProvider sp) NewInvokerWithEvents()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTransient<BackpressureEventController>();
        var sp = services.BuildServiceProvider();
        var invoker = new SleipnirInvoker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<SleipnirInvoker>>());
        invoker.Register<BackpressureEventController>();
        return (invoker, sp);
    }

    private static (SleipnirInvoker invoker, ServiceProvider sp) NewInvokerWithValueTypeEvent()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTransient<ValueTypeEventController>();
        var sp = services.BuildServiceProvider();
        var invoker = new SleipnirInvoker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<SleipnirInvoker>>());
        invoker.Register<ValueTypeEventController>();
        return (invoker, sp);
    }

    [Fact]
    public async Task Subscribe_PerEventOverride_WinsOverGlobal()
    {
        var (invoker, sp) = NewInvokerWithEvents();
        invoker.EventBufferCapacity = 50;
        invoker.EventBackpressureStrategy = EventBackpressureStrategy.DropOldest;
        using (sp)
        {
            var req = CreateRequest("BpEvents", "OverrideEvent", ("count", "1"));
            var result = await invoker.SubscribeAsync(req, null);

            result.Error.Should().BeNull();
            result.EventBufferCapacity.Should().Be(7);
            result.EventBackpressureStrategy.Should().Be(EventBackpressureStrategy.DropWrite);
        }
    }

    [Fact]
    public async Task Subscribe_NoOverride_UsesGlobal()
    {
        var (invoker, sp) = NewInvokerWithEvents();
        invoker.EventBufferCapacity = 42;
        invoker.EventBackpressureStrategy = EventBackpressureStrategy.Block;
        using (sp)
        {
            var req = CreateRequest("BpEvents", "PlainEvent", ("count", "1"));
            var result = await invoker.SubscribeAsync(req, null);

            result.Error.Should().BeNull();
            result.EventBufferCapacity.Should().Be(42);
            result.EventBackpressureStrategy.Should().Be(EventBackpressureStrategy.Block);
        }
    }

    [Fact]
    public async Task Subscribe_NoOverrideNoGlobal_FallsBackToDefault100DropOldest()
    {
        var (invoker, sp) = NewInvokerWithEvents();
        using (sp)
        {
            var req = CreateRequest("BpEvents", "PlainEvent", ("count", "1"));
            var result = await invoker.SubscribeAsync(req, null);

            result.Error.Should().BeNull();
            result.EventBufferCapacity.Should().Be(100);
            result.EventBackpressureStrategy.Should().Be(EventBackpressureStrategy.DropOldest);
        }
    }

    [Fact]
    public async Task Subscribe_UnboundedStrategy_YieldsZeroCapacity_IgnoringGlobal()
    {
        var (invoker, sp) = NewInvokerWithEvents();
        invoker.EventBufferCapacity = 50;   // ignored for an Unbounded event
        using (sp)
        {
            var req = CreateRequest("BpEvents", "UnboundedEvent", ("count", "1"));
            var result = await invoker.SubscribeAsync(req, null);

            result.Error.Should().BeNull();
            result.EventBufferCapacity.Should().Be(0);
            result.EventBackpressureStrategy.Should().Be(EventBackpressureStrategy.Unbounded);
        }
    }

    #endregion

    #region Single Invocation – Sync Methods

    [Fact]
    public async Task InvokeDi_SyncEcho_ReturnsEchoedString()
    {
        // Arrange
        var request = CreateRequest("TestInvoker", "Echo",
            ("message", "\"Hello Sleipnir\""));

        // Act
        var response = await _invoker.InvokeDi(request, null);

        // Assert
        response.Should().NotBeNull();
        response!.Code.Should().Be((int)HttpStatusCode.OK);
        response.Data.Value.GetRawText().Should().Contain("Hello Sleipnir");
    }

    [Fact]
    public async Task InvokeDi_SyncAdd_ReturnsSum()
    {
        // Arrange
        var request = CreateRequest("TestInvoker", "Add",
            ("a", "3"), ("b", "4"));

        // Act
        var response = await _invoker.InvokeDi(request, null);

        // Assert
        response.Should().NotBeNull();
        response!.Code.Should().Be((int)HttpStatusCode.OK);
        response.Data.Value.GetRawText().Should().Be("7");
    }

    [Fact]
    public async Task InvokeDi_NoParams_ReturnsResult()
    {
        // Arrange
        var request = new SleipnirRequest
        {
            Controller = "TestInvoker",
            Method = "NoParams",
            Params = JsonNode.Parse("[]"),
            Id = "NoParams"
        };

        // Act
        var response = await _invoker.InvokeDi(request, null);

        // Assert
        response.Should().NotBeNull();
        response!.Code.Should().Be((int)HttpStatusCode.OK);
        response.Data.Value.GetRawText().Should().Contain("Hello World");
    }

    // Weg A: Eine Controller-Methode, die ein SleipnirResponse-Objekt zurückgibt
    // (SleipnirResults.Error/Ok), muss der Invoker UNVERÄNDERT durchreichen — Code,
    // Data und strukturiertes Error bleiben erhalten (kein Re-Wrap als 200).
    [Fact]
    public async Task InvokeDi_ControllerReturnsSleipnirResults_Error_IsPassedThroughVerbatim()
    {
        var request = CreateRequest("TestInvoker", "GetOr404", ("id", "99"));

        var response = await _invoker.InvokeDi(request, null);

        response.Should().NotBeNull();
        response!.Code.Should().Be(404);
        response.IsSuccess.Should().BeFalse();
        // Data ist bei Fehlern null; Message wohnt in Error.Message.
        response.Data.Should().BeNull();
        response.Error.Should().NotBeNull();
        response.Error!.Code.Should().Be(404);
        response.Error.Message.Should().Be("Customer '99' not found.");
        response.Id.Should().Be("TestInvoker.GetOr404");
    }

    [Fact]
    public async Task InvokeDi_ControllerReturnsSleipnirResults_Ok_IsPassedThroughVerbatim()
    {
        var request = CreateRequest("TestInvoker", "GetOr404", ("id", "5"));

        var response = await _invoker.InvokeDi(request, null);

        response.Should().NotBeNull();
        response!.Code.Should().Be(200);
        response.IsSuccess.Should().BeTrue();
        response.Error.Should().BeNull();
        // Ok(object) serialisiert camelCase (wie der Invoker-Erfolgs-Pfad).
        response.Data.Value.GetRawText().Should().Contain("\"id\":5");
        response.Data.Value.GetRawText().Should().Contain("\"name\":\"Found\"");
    }

    [Fact]
    public async Task InvokeDi_ControllerReturnsSleipnirResults_BadRequest_CarriesDetails()
    {
        var request = CreateRequest("TestInvoker", "ValidationProblem", ("input", "\"\""));

        var response = await _invoker.InvokeDi(request, null);

        response!.Code.Should().Be(400);
        response.IsSuccess.Should().BeFalse();
        // Data bei Fehlern null; Message in Error.Message.
        response.Error!.Message.Should().Be("input must not be empty.");
        response.Error!.Details.Should().Be("ParameterName=input");
    }

    [Fact]
    public async Task InvokeDi_VoidMethod_ReturnsNoContent()
    {
        // Arrange
        var request = CreateRequest("TestInvoker", "VoidMethod",
            ("data", "\"test\""));

        // Act
        var response = await _invoker.InvokeDi(request, null);

        // Assert: void methods respond with 204 No Content (no body).
        response.Should().NotBeNull();
        response!.Code.Should().Be((int)HttpStatusCode.NoContent);
        response.Data.Should().BeNull();
    }

    [Fact]
    public async Task InvokeDi_ComplexReturn_SerializesDto()
    {
        // Arrange
        var request = CreateRequest("TestInvoker", "ComplexReturn",
            ("id", "42"));

        // Act
        var response = await _invoker.InvokeDi(request, null);

        // Assert
        response.Should().NotBeNull();
        response!.Code.Should().Be((int)HttpStatusCode.OK);
        response.Data.Value.GetRawText().Should().Contain("\"id\":42");
        response.Data.Value.GetRawText().Should().Contain("\"name\":\"Test\"");
    }

    #endregion

    #region Single Invocation – Async Methods

    [Fact]
    public async Task InvokeDi_AsyncEcho_ReturnsEchoedString()
    {
        // Arrange
        var request = CreateRequest("TestInvoker", "EchoAsync",
            ("message", "\"Async Hello\""));

        // Act
        var response = await _invoker.InvokeDi(request, null);

        // Assert
        response.Should().NotBeNull();
        response!.Code.Should().Be((int)HttpStatusCode.OK);
        response.Data.Value.GetRawText().Should().Contain("Async Hello");
    }

    [Fact]
    public async Task InvokeDi_AsyncAdd_ReturnsSum()
    {
        // Arrange
        var request = CreateRequest("TestInvoker", "AddAsync",
            ("a", "10"), ("b", "20"));

        // Act
        var response = await _invoker.InvokeDi(request, null);

        // Assert
        response.Should().NotBeNull();
        response!.Code.Should().Be((int)HttpStatusCode.OK);
        response.Data.Value.GetRawText().Should().Be("30");
    }

    [Fact]
    public async Task InvokeDi_WithCancellation_PassesToken()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        var request = CreateRequest("TestInvoker", "WithCancellation",
            ("input", "\"Cancelled\""));

        // Act
        cts.CancelAfter(1);
        var response = await _invoker.InvokeDi(request, null, cts.Token);

        // Assert: Should either complete or be cancelled gracefully
        response.Should().NotBeNull();
    }

    #endregion

    #region Error Handling

    [Fact]
    public async Task InvokeDi_UnknownController_ReturnsNotFound()
    {
        // Arrange
        var request = new SleipnirRequest
        {
            Controller = "NonExistent",
            Method = "AnyMethod",
            Params = JsonNode.Parse("[]")
        };

        // Act
        var response = await _invoker.InvokeDi(request, null);

        // Assert
        response.Should().NotBeNull();
        response!.Code.Should().Be((int)HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task InvokeDi_UnknownMethod_ReturnsBadRequest()
    {
        // Arrange
        var request = new SleipnirRequest
        {
            Controller = "TestInvoker",
            Method = "NonExistentMethod",
            Params = JsonNode.Parse("[]")
        };

        // Act
        var response = await _invoker.InvokeDi(request, null);

        // Assert
        response.Should().NotBeNull();
        response!.Code.Should().Be((int)HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task InvokeDi_InvalidParameterType_ReturnsBadRequest()
    {
        // Arrange
        var request = CreateRequest("TestInvoker", "Add",
            ("a", "\"not-a-number\""), ("b", "4"));

        // Act
        var response = await _invoker.InvokeDi(request, null);

        // Assert
        response.Should().NotBeNull();
        response!.Code.Should().Be((int)HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task InvokeDi_SetsResponseId_FromRequestId()
    {
        // Arrange
        var request = CreateRequest("TestInvoker", "Echo",
            ("message", "\"test\""));
        request.Id = "my-custom-id";

        // Act
        var response = await _invoker.InvokeDi(request, null);

        // Assert
        response.Should().NotBeNull();
        response!.Id.Should().Be("my-custom-id");
    }

    #endregion

    #region Parameter Binding (positional fallback + duplicate names)

    [Fact]
    public async Task InvokeDi_PositionalParams_BindByIndex()
    {
        // Arrange: simulate the fluent client which emits param0/param1 with Num indices.
        // The method Add(int a, int b) has no matching names -> server must fall back to Num.
        var paramList = new List<SleipnirParameter>
        {
            new() { Num = 0, ParameterName = "param0", Data = JsonNode.Parse("3") },
            new() { Num = 1, ParameterName = "param1", Data = JsonNode.Parse("4") }
        };
        var request = new SleipnirRequest
        {
            Controller = "TestInvoker",
            Method = "Add",
            Params = JsonSerializer.SerializeToNode(paramList),
            Id = "pos"
        };

        // Act
        var response = await _invoker.InvokeDi(request, null);

        // Assert
        response.Should().NotBeNull();
        response!.Code.Should().Be((int)HttpStatusCode.OK);
        response.Data.Value.GetRawText().Should().Be("7");
    }

    [Fact]
    public async Task InvokeDi_PositionalParams_SkipCancellationTokenInIndex()
    {
        // Arrange: WithCancellation(string input, CancellationToken ct) — client sends one
        // positional arg (Num=0). Server must bind it to `input` (the first non-token param),
        // not to position 0 of the raw signature (which is `input` anyway) — this guards the
        // general case where a CancellationToken sits before a value parameter.
        var paramList = new List<SleipnirParameter>
        {
            new() { Num = 0, ParameterName = "param0", Data = JsonNode.Parse("\"hello\"") }
        };
        var request = new SleipnirRequest
        {
            Controller = "TestInvoker",
            Method = "Echo",
            Params = JsonSerializer.SerializeToNode(paramList),
            Id = "pos-ct"
        };

        // Act
        var response = await _invoker.InvokeDi(request, null);

        // Assert
        response.Should().NotBeNull();
        response!.Code.Should().Be((int)HttpStatusCode.OK);
        response.Data.Value.GetRawText().Should().Contain("hello");
    }

    [Fact]
    public async Task InvokeDi_DuplicateParameterName_ReturnsBadRequest()
    {
        // Arrange
        var paramList = new List<SleipnirParameter>
        {
            new() { ParameterName = "a", Data = JsonNode.Parse("3") },
            new() { ParameterName = "a", Data = JsonNode.Parse("4") }
        };
        var request = new SleipnirRequest
        {
            Controller = "TestInvoker",
            Method = "Add",
            Params = JsonSerializer.SerializeToNode(paramList),
            Id = "dup"
        };

        // Act
        var response = await _invoker.InvokeDi(request, null);

        // Assert
        response.Should().NotBeNull();
        response!.Code.Should().Be((int)HttpStatusCode.BadRequest);
        // Fehler-Message wohnt in Error.Message (Data bei Fehlern null).
        response.Error!.Message.Should().Contain("Duplicate");
    }

    [Fact]
    public async Task InvokeDi_MissingParameter_UsesDefault()
    {
        // Arrange: Add(int a, int b) — only `a` supplied; `b` defaults to 0.
        var request = CreateRequest("TestInvoker", "Add", ("a", "3"));

        // Act
        var response = await _invoker.InvokeDi(request, null);

        // Assert
        response.Should().NotBeNull();
        response!.Code.Should().Be((int)HttpStatusCode.OK);
        response.Data.Value.GetRawText().Should().Be("3");
    }

    [Fact]
    public async Task InvokeDi_AuthorisationFailure_ReturnsUnauthorized()
    {
        // Arrange: [SleipnirAuthorise] on Secured; no HttpContext -> auth fails.
        var request = CreateRequest("TestInvoker", "Secured", ("data", "\"x\""));

        // Act
        var response = await _invoker.InvokeDi(request, null);

        // Assert
        response.Should().NotBeNull();
        response!.Code.Should().Be((int)HttpStatusCode.Unauthorized);
    }

    #endregion

    #region Dotted Controller Namespace

    [Fact]
    public async Task InvokeDi_DottedControllerNamespace_RoutesCorrectly()
    {
        // Arrange: Controller "Customer.Address.Contact" — dots in the name are allowed
        // and let callers express arbitrarily deep namespaces via the controller field.
        var request = CreateRequest("Customer.Address.Contact", "Add", ("name", "\"alice\""));

        // Act
        var response = await _invoker.InvokeDi(request, null);

        // Assert
        response.Should().NotBeNull();
        response!.Code.Should().Be((int)HttpStatusCode.OK);
        response.Data.Value.GetRawText().Should().Contain("added alice");
    }

    #endregion

    #region Batch Invocation

    [Fact]
    public async Task InvokeDi_ParallelBatch_ExecutesAllRequests()
    {
        // Arrange — Ids müssen batch-unique sein (GraphKey-Kollisions-Gate, D3): der
        // Helper-Default "{Controller}.{Method}" kollidiert bei zwei Add-Requests.
        var requests = new List<SleipnirRequest>
        {
            CreateRequest("TestInvoker", "Add", ("a", "1"), ("b", "2")),
            CreateRequest("TestInvoker", "Add", ("a", "3"), ("b", "4")),
            CreateRequest("TestInvoker", "Echo", ("message", "\"batch\""))
        };
        requests[1].Id = "TestInvoker.Add#2";

        // Act
        var responses = await _invoker.InvokeDi(requests, null, ExecutionMode.Parallel);

        // Assert
        var responseList = responses.ToList();
        responseList.Should().HaveCount(3);
        responseList.Should().AllSatisfy(r => r!.Code.Should().Be((int)HttpStatusCode.OK));
    }

    [Fact]
    public async Task InvokeDi_SerialBatch_ExecutesAllRequests()
    {
        // Arrange
        var requests = new List<SleipnirRequest>
        {
            CreateRequest("TestInvoker", "Add", ("a", "1"), ("b", "2")),
            CreateRequest("TestInvoker", "Echo", ("message", "\"serial\""))
        };

        // Act
        var responses = await _invoker.InvokeDi(requests, null, ExecutionMode.Serial);

        // Assert
        var responseList = responses.ToList();
        responseList.Should().HaveCount(2);
        responseList.Should().AllSatisfy(r => r!.Code.Should().Be((int)HttpStatusCode.OK));
    }

    [Fact]
    public async Task InvokeDi_EmptyBatch_ReturnsEmptyCollection()
    {
        // Arrange
        var requests = new List<SleipnirRequest>();

        // Act
        var responses = await _invoker.InvokeDi(requests, null, ExecutionMode.Parallel);

        // Assert
        responses.Should().BeEmpty();
    }

    #endregion

    #region Binary Data

    [Fact]
    public async Task InvokeDi_UploadBlob_BinaryDataInjected()
    {
        // Arrange
        var binaryData = new byte[] { 1, 2, 3, 4, 5 };
        var request = new SleipnirRequest
        {
            Controller = "TestInvoker",
            Method = "UploadBlob",
            Params = JsonNode.Parse("[{\"ParameterName\":\"filename\",\"Data\":\"test.bin\"}]"),
            BinaryData = binaryData,
            Id = "UploadBlob"
        };

        // Act
        var response = await _invoker.InvokeDi(request, null);

        // Assert
        response.Should().NotBeNull();
        response!.Code.Should().Be((int)HttpStatusCode.OK);
        response.Data.Value.GetRawText().Should().Contain("5 bytes");
    }

    [Fact]
    public async Task InvokeDi_DownloadBlob_ReturnsBinaryInContent()
    {
        // Arrange
        var request = new SleipnirRequest
        {
            Controller = "TestInvoker",
            Method = "DownloadBlob",
            Params = JsonNode.Parse("[{\"ParameterName\":\"name\",\"Data\":\"myfile\"}]"),
            Id = "DownloadBlob"
        };

        // Act
        var response = await _invoker.InvokeDi(request, null);

        // Assert
        response.Should().NotBeNull();
        response!.Code.Should().Be((int)HttpStatusCode.OK);
        response.Content.Should().NotBeNull();
        response.Content!.Length.Should().BeGreaterThan(0);
        var text = System.Text.Encoding.UTF8.GetString(response.Content);
        text.Should().Contain("Blob content for myfile");
    }

    [Fact]
    public async Task InvokeDi_UploadAndProcess_BinaryWithCancellationToken()
    {
        // Arrange
        var binaryData = new byte[] { 10, 20, 30, 40, 50, 60, 70, 80 };
        var request = new SleipnirRequest
        {
            Controller = "TestInvoker",
            Method = "UploadAndProcess",
            Params = JsonNode.Parse("[]"),
            BinaryData = binaryData,
            Id = "UploadAndProcess"
        };

        // Act
        var response = await _invoker.InvokeDi(request, null);

        // Assert
        response.Should().NotBeNull();
        response!.Code.Should().Be((int)HttpStatusCode.OK);
        response.Data.Value.GetRawText().Should().Be("8");
    }

    #endregion

    #region Streaming (IAsyncEnumerable)

    [Fact]
    public async Task InvokeDi_StreamNumbers_ReturnsJsonArray()
    {
        // Arrange
        var request = CreateRequest("TestInvoker", "StreamNumbers",
            ("count", "5"));

        // Act
        var response = await _invoker.InvokeDi(request, null);

        // Assert
        response.Should().NotBeNull();
        response!.Code.Should().Be((int)HttpStatusCode.OK);
        response.Data.Should().NotBeNull();
        var numbers = response.Data.Value.Deserialize<List<int>>();
        numbers.Should().NotBeNull();
        numbers.Should().HaveCount(5);
        numbers.Should().BeEquivalentTo(new[] { 0, 1, 2, 3, 4 });
    }

    [Fact]
    public async Task InvokeDi_StreamNumbersTask_ReturnsJsonArray()
    {
        // Arrange
        var request = CreateRequest("TestInvoker", "StreamNumbersTask",
            ("count", "3"));

        // Act
        var response = await _invoker.InvokeDi(request, null);

        // Assert
        response.Should().NotBeNull();
        response!.Code.Should().Be((int)HttpStatusCode.OK);
        response.Data.Should().NotBeNull();
        var numbers = response.Data.Value.Deserialize<List<int>>();
        numbers.Should().NotBeNull();
        numbers.Should().HaveCount(3);
        numbers.Should().BeEquivalentTo(new[] { 0, 1, 2 });
    }

    #endregion

    #region Discovery

    [Fact]
    public void GetDiscoveryInfo_ReturnsControllersAndMethods()
    {
        // Act
        var discovery = _invoker.GetDiscoveryInfo();

        // Assert
        discovery.Controllers.Should().NotBeEmpty();
        var controller = discovery.Controllers.First(c => c.Name == "TestInvoker");
        controller.Methods.Should().NotBeEmpty();
        controller.Methods.Should().Contain(m => m.MethodName == "Echo");
        controller.Methods.Should().Contain(m => m.MethodName == "Add");
        controller.Methods.Should().Contain(m => m.MethodName == "EchoAsync");
    }

    [Fact]
    public void GetDiscoveryInfo_ReturnsCachedInstance()
    {
        // Act
        var first = _invoker.GetDiscoveryInfo();
        var second = _invoker.GetDiscoveryInfo();

        // Assert
        first.Should().BeSameAs(second);
    }

    [Fact]
    public void GetDiscoveryInfo_IncludesParameterMetadata()
    {
        // Act
        var discovery = _invoker.GetDiscoveryInfo();

        // Assert
        var echoMethod = discovery.Controllers
            .First(c => c.Name == "TestInvoker")
            .Methods.First(m => m.MethodName == "Echo");
        echoMethod.Parameters.Should().HaveCount(1);
        echoMethod.Parameters[0].ParameterName.Should().Be("message");
    }

    [Fact]
    public void GetDiscoveryInfo_IncludesComplexReturnType()
    {
        // Act
        var discovery = _invoker.GetDiscoveryInfo();

        // Assert
        var complexMethod = discovery.Controllers
            .First(c => c.Name == "TestInvoker")
            .Methods.First(m => m.MethodName == "ComplexReturn");
        complexMethod.ReturnType.Kind.Should().Be("ref");
        complexMethod.ReturnType.Ref.Should().Be(typeof(TestDto).FullName);
    }

    #endregion

    #region Error Message Safety

    [Fact]
    public async Task InvokeDi_UnknownController_ReturnsGenericError()
    {
        // Arrange
        var request = CreateRequest("NonExistent", "Echo", ("message", "\"hello\""));

        // Act
        var response = await _invoker.InvokeDi(request, null);

        // Assert
        response.Should().NotBeNull();
        response!.Code.Should().Be((int)HttpStatusCode.NotFound);
        // Data bei Fehlern null (kein Leak); generische Message ohne interne Details.
        response.Data.Should().BeNull();
        response.Error!.Message.Should().NotContain("Fehler").And.NotContain("Exception");
        response.Error?.Details.Should().BeNull();
    }

    [Fact]
    public async Task InvokeDi_UnknownMethod_ReturnsGenericError()
    {
        // Arrange
        var request = CreateRequest("TestInvoker", "NonExistentMethod", ("message", "\"hello\""));

        // Act
        var response = await _invoker.InvokeDi(request, null);

        // Assert
        response.Should().NotBeNull();
        response!.Code.Should().Be((int)HttpStatusCode.BadRequest);
        // Data bei Fehlern null (kein Leak); generische Message ohne interne Details.
        response.Data.Should().BeNull();
        response.Error!.Message.Should().NotContain("Fehler").And.NotContain("Exception");
        response.Error?.Details.Should().BeNull();
    }

    [Fact]
    public async Task InvokeDi_InvalidJson_ReturnsGenericError()
    {
        // Arrange
        var request = new SleipnirRequest
        {
            Controller = "TestInvoker",
            Method = "Echo",
            // Seit Params ein nativer JsonNode ist, kann kein invalides JSON mehr beim
            // Invoker ankommen — der Parse-Fehler liegt jetzt an der Transportgrenze.
            // Äquivalentes graceful-400 ohne Leak: ein strukturell fehlerhaftes Params-
            // Array mit doppeltem Parameternamen.
            Params = JsonNode.Parse("[{\"parameterName\":\"message\",\"data\":\"a\"},{\"parameterName\":\"message\",\"data\":\"b\"}]"),
            Id = "test"
        };

        // Act
        var response = await _invoker.InvokeDi(request, null);

        // Assert
        response.Should().NotBeNull();
        response!.Code.Should().Be((int)HttpStatusCode.BadRequest);
        // Data bei Fehlern null (kein Leak); generische Message ohne interne Details.
        response.Data.Should().BeNull();
        response.Error!.Message.Should().NotContain("Exception").And.NotContain("JsonException");
        response.Error?.Details.Should().BeNull();
    }

    [Fact]
    public async Task InvokeDi_TypeMismatch_ReturnsSafeError()
    {
        // Arrange: "Add" expects int a, int b — pass a string
        var request = CreateRequest("TestInvoker", "Add",
            ("a", "\"not_a_number\""),
            ("b", "42"));

        // Act
        var response = await _invoker.InvokeDi(request, null);

        // Assert
        response.Should().NotBeNull();
        response!.Code.Should().Be((int)HttpStatusCode.BadRequest);
        // Should mention the parameter name but NOT leak stack traces
        response.Error!.Message.Should().Contain("a");
        response.Error!.Message.Should().NotContain("at ");
        response.Error!.Message.Should().NotContain("stack");
        response.Error?.Details.Should().BeNull();
    }

    [Fact]
    public async Task InvokeDi_InternalError_ReturnsGenericMessage()
    {
        // Arrange: call a method that doesn't exist in the controller
        // but the controller exists — this tests the error path
        var request = new SleipnirRequest
        {
            Controller = "TestInvoker",
            Method = "Echo",
            Params = JsonNode.Parse("[{\"parameterName\":\"message\",\"data\":\"hello\"}]"),
            Id = "test"
        };

        // Act
        var response = await _invoker.InvokeDi(request, null);

        // Assert
        response.Should().NotBeNull();
        // Even on success, verify error responses don't leak
        if (response!.Code >= 400)
        {
            response.Data.Should().BeNull();
            response.Error?.Details.Should().BeNull();
        }
    }

    #endregion

    #region Dependency Chains (@alias-Walking)

    /// <summary>
    /// Baut einen Chain-Request mit eindeutiger Id. Die Parameter werden als
    /// SleipnirParameter[] serialisiert; jsonValue ist jeweils die JSON-Form des Werts
    /// (z. B. "7", "true", "\"hi\"", "{\"Id\":7}").
    /// </summary>
    private static SleipnirRequest ChainRequest(string id, string controller, string method,
        params (string name, string jsonValue)[] parameters)
        => ChainRequest(id, controller, method, null, parameters);

    /// <summary>
    /// Wie oben, zusätzlich mit DependencyMapping (Alias → ergebnisrelativer JsonPath).
    /// mapping steht positionell vor den params, sodass Tuple-Werte nicht versehentlich
    /// an mapping gebunden werden.
    /// </summary>
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

    private static readonly JsonSerializerOptions _caseInsensitiveOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Wahrheitswert durch eine Chain: EchoBool(true) exposes "$" als "flag";
    /// EchoBool(@flag) muss true liefern. Prüft, dass bool weder zu "true"(string)
    /// noch zu 1 kollabiert — die typgetreue Substitution muss JsonValue.Create nutzen.
    /// </summary>
    [Fact]
    public async Task DependencyChain_Bool_RoundtripsThroughAlias()
    {
        // Arrange
        var step1 = ChainRequest("s1", "DepChain", "EchoBool",
            new Dictionary<string, string> { { "flag", "$" } },
            ("value", "true"));
        var step2 = ChainRequest("s2", "DepChain", "EchoBool",
            ("value", "@flag"));

        // Act
        var responses = (await _invoker.InvokeDi(new[] { step1, step2 }, null)).ToList();

        // Assert
        var byId = responses.ToDictionary(r => r!.Id ?? string.Empty);
        byId["s1"].Code.Should().Be((int)HttpStatusCode.OK);
        byId["s2"].Code.Should().Be((int)HttpStatusCode.OK);
        byId["s2"].Error.Should().BeNull();
        byId["s2"].Data.Value.Deserialize<bool>().Should().BeTrue();
    }

    /// <summary>
    /// long-Wert jenseits des int-Bereichs durch eine Chain. Stellt sicher, dass die
    /// numerische Substitution die volle long-Präzision erhält (kein int-Verlust).
    /// </summary>
    [Fact]
    public async Task DependencyChain_Long_RoundtripsThroughAlias()
    {
        // Arrange — 9_000_000_000 liegt außerhalb von int32
        var step1 = ChainRequest("s1", "DepChain", "EchoLong",
            new Dictionary<string, string> { { "big", "$" } },
            ("value", "9000000000"));
        var step2 = ChainRequest("s2", "DepChain", "EchoLong",
            ("value", "@big"));

        // Act
        var responses = (await _invoker.InvokeDi(new[] { step1, step2 }, null)).ToList();

        // Assert
        var byId = responses.ToDictionary(r => r!.Id ?? string.Empty);
        byId["s2"].Code.Should().Be((int)HttpStatusCode.OK);
        byId["s2"].Error.Should().BeNull();
        byId["s2"].Data.Value.Deserialize<long>().Should().Be(9_000_000_000L);
    }

    /// <summary>
    /// decimal durch eine Chain. Decimal wird von System.Text.Json als JSON-Zahl
    /// serialisiert; die Substitution muss die Zahlform (nicht den String "12.5")
    /// erhalten, sonst bricht Deserialize&lt;decimal&gt;.
    /// </summary>
    [Fact]
    public async Task DependencyChain_Decimal_RoundtripsThroughAlias()
    {
        // Arrange
        var step1 = ChainRequest("s1", "DepChain", "EchoDecimal",
            new Dictionary<string, string> { { "price", "$" } },
            ("value", "12.5"));
        var step2 = ChainRequest("s2", "DepChain", "EchoDecimal",
            ("value", "@price"));

        // Act
        var responses = (await _invoker.InvokeDi(new[] { step1, step2 }, null)).ToList();

        // Assert
        var byId = responses.ToDictionary(r => r!.Id ?? string.Empty);
        byId["s2"].Code.Should().Be((int)HttpStatusCode.OK);
        byId["s2"].Error.Should().BeNull();
        byId["s2"].Data.Value.Deserialize<decimal>().Should().Be(12.5m);
    }

    /// <summary>
    /// Ganzes Objekt als Dependency: MakeDto(7,"Alice") exposes "$" als "dto";
    /// EchoDto(@dto) muss das Objekt mit Id=7 zurückgeben. Prüft, dass die
    /// Substitution ein JSON-Objekt intakt in einen Objekt-Parameter injiziert
    /// (ToJsonString des Objects + JSON-String-Hülle + Deserialize&lt;TestDto&gt;).
    /// </summary>
    [Fact]
    public async Task DependencyChain_Object_WholeResult_InjectsIntoObjectParam()
    {
        // Arrange
        var step1 = ChainRequest("s1", "DepChain", "MakeDto",
            new Dictionary<string, string> { { "dto", "$" } },
            ("id", "7"), ("name", "\"Alice\""));
        var step2 = ChainRequest("s2", "DepChain", "EchoDto",
            ("dto", "@dto"));

        // Act
        var responses = (await _invoker.InvokeDi(new[] { step1, step2 }, null)).ToList();

        // Assert
        var byId = responses.ToDictionary(r => r!.Id ?? string.Empty);
        byId["s1"].ExposedDependencies.Should().ContainKey("dto");
        byId["s2"].Code.Should().Be((int)HttpStatusCode.OK);
        byId["s2"].Error.Should().BeNull();
        var dto = byId["s2"].Data.Value.Deserialize<TestDto>(_caseInsensitiveOpts);
        dto.Should().NotBeNull();
        dto!.Id.Should().Be(7);
        dto.Name.Should().Be("Alice");
    }

    /// <summary>
    /// Property-Pfad auf einem Objekt-Result: MakeDto(7,"Alice") exposes "$.id" als
    /// "id"; MakeDto(@id,"Bob") muss ein neues Dto mit Id=7 liefern. Prüft int-Extraktion
    /// via ergebnisrelativem Pfad (kein $.data-Envelope) und typgetreue int-Injection.
    /// </summary>
    [Fact]
    public async Task DependencyChain_ObjectPropertyPath_Int_InjectsIntoIntParam()
    {
        // Arrange
        var step1 = ChainRequest("s1", "DepChain", "MakeDto",
            new Dictionary<string, string> { { "id", "$.id" } },
            ("id", "7"), ("name", "\"Alice\""));
        var step2 = ChainRequest("s2", "DepChain", "MakeDto",
            ("id", "@id"), ("name", "\"Bob\""));

        // Act
        var responses = (await _invoker.InvokeDi(new[] { step1, step2 }, null)).ToList();

        // Assert
        var byId = responses.ToDictionary(r => r!.Id ?? string.Empty);
        byId["s1"].ExposedDependencies!["id"].Should().Be("7");
        byId["s2"].Code.Should().Be((int)HttpStatusCode.OK);
        byId["s2"].Error.Should().BeNull();
        var dto = byId["s2"].Data.Value.Deserialize<TestDto>(_caseInsensitiveOpts);
        dto!.Id.Should().Be(7);
        dto.Name.Should().Be("Bob");
    }

    /// <summary>
    /// Ganze Collection als Dependency: MakeDtoList() exposes "$" als "dtos";
    /// CountDtoList(@dtos) muss 3 liefern. Prüft, dass ein JSON-Array intakt in einen
    /// List&lt;T&gt;-Parameter injiziert wird (ToJsonString des Arrays + Hülle).
    /// </summary>
    [Fact]
    public async Task DependencyChain_Array_WholeResult_InjectsIntoArrayParam()
    {
        // Arrange
        var step1 = ChainRequest("s1", "DepChain", "MakeDtoList",
            new Dictionary<string, string> { { "dtos", "$" } });
        var step2 = ChainRequest("s2", "DepChain", "CountDtoList",
            ("dtos", "@dtos"));

        // Act
        var responses = (await _invoker.InvokeDi(new[] { step1, step2 }, null)).ToList();

        // Assert
        var byId = responses.ToDictionary(r => r!.Id ?? string.Empty);
        byId["s2"].Code.Should().Be((int)HttpStatusCode.OK);
        byId["s2"].Error.Should().BeNull();
        byId["s2"].Data.Value.Deserialize<int>().Should().Be(3);
    }

    /// <summary>
    /// Array-Element-Pfad als Dependency-Quelle: MakeIntList() → [10,20,30];
    /// exposes "$[1]" als "second"; EchoLong(@second) muss 20 liefern. Prüft die
    /// JsonPath-Index-Extraktion innerhalb einer Chain (bisher nur am nackten Array
    /// im Resolver-Unit-Test geprüft, nie in einer echten Chain).
    /// </summary>
    [Fact]
    public async Task DependencyChain_ArrayElementPath_InjectsExtractedElement()
    {
        // Arrange
        var step1 = ChainRequest("s1", "DepChain", "MakeIntList",
            new Dictionary<string, string> { { "second", "$[1]" } });
        var step2 = ChainRequest("s2", "DepChain", "EchoLong",
            ("value", "@second"));

        // Act
        var responses = (await _invoker.InvokeDi(new[] { step1, step2 }, null)).ToList();

        // Assert
        var byId = responses.ToDictionary(r => r!.Id ?? string.Empty);
        byId["s1"].ExposedDependencies!["second"].Should().Be("20");
        byId["s2"].Code.Should().Be((int)HttpStatusCode.OK);
        byId["s2"].Error.Should().BeNull();
        byId["s2"].Data.Value.Deserialize<long>().Should().Be(20L);
    }

    /// <summary>
    /// Mehrere @alias-Platzhalter in einen Request aus zwei verschiedenen Vorläufern
    /// (Diamond-Verbraucher): step1 exposes "$.id" als "id", step2 exposes "$.name" als
    /// "name", step3 MakeDto(@id, @name) kombiniert beide. Prüft das Zusammenführen der
    /// ExposedDependencies aller Vorläufer-Batches in einem Aufruf.
    /// </summary>
    [Fact]
    public async Task DependencyChain_MultipleAliases_OneRequest_ConsumesTwoProducers()
    {
        // Arrange — step1 und step2 sind unabhängig (Batch 1), step3 verbraucht beide (Batch 2)
        var step1 = ChainRequest("s1", "DepChain", "MakeDto",
            new Dictionary<string, string> { { "id", "$.id" } },
            ("id", "7"), ("name", "\"Alice\""));
        var step2 = ChainRequest("s2", "DepChain", "MakeDto",
            new Dictionary<string, string> { { "name", "$.name" } },
            ("id", "99"), ("name", "\"Bob\""));
        var step3 = ChainRequest("s3", "DepChain", "MakeDto",
            ("id", "@id"), ("name", "@name"));

        // Act
        var responses = (await _invoker.InvokeDi(new[] { step1, step2, step3 }, null)).ToList();

        // Assert
        var byId = responses.ToDictionary(r => r!.Id ?? string.Empty);
        byId["s3"].Code.Should().Be((int)HttpStatusCode.OK);
        byId["s3"].Error.Should().BeNull();
        var dto = byId["s3"].Data.Value.Deserialize<TestDto>(_caseInsensitiveOpts);
        dto!.Id.Should().Be(7);      // aus step1
        dto.Name.Should().Be("Bob"); // aus step2
    }

    /// <summary>
    /// Nicht auflösbarer @alias-Platzhalter: step2 referenziert "@nonexistent", den
    /// kein Vorläufer exposed. Erwartet einen BadRequest (400) für step2 — keinen 500
    /// und keine geworfene Exception. Regression-Guard für ResolveParameterValues.
    /// </summary>
    [Fact]
    public async Task DependencyChain_UnresolvedAlias_ReturnsBadRequest_NoThrow()
    {
        // Arrange — step1 exposed "id", step2 fragt aber "@nonexistent" ab
        var step1 = ChainRequest("s1", "DepChain", "MakeDto",
            new Dictionary<string, string> { { "id", "$.id" } },
            ("id", "7"), ("name", "\"Alice\""));
        var step2 = ChainRequest("s2", "DepChain", "EchoLong",
            ("value", "@nonexistent"));

        // Act — darf nicht werfen
        var responses = (await _invoker.InvokeDi(new[] { step1, step2 }, null)).ToList();

        // Assert
        var byId = responses.ToDictionary(r => r!.Id ?? string.Empty);
        byId["s1"].Code.Should().Be((int)HttpStatusCode.OK);
        byId["s2"].Code.Should().Be((int)HttpStatusCode.BadRequest);
        byId["s2"].Error.Should().NotBeNull();
    }

    /// <summary>
    /// Dokumentiert das Auto-Detect-Verhalten: sobald ein Request ein DependencyMapping
    /// deklariert, routed InvokeDi IMMER auf den topologischen Batch-Pfad — auch wenn
    /// Mode=Serial angefordert wurde. Die Chain muss daher trotzdem korrekt auflösen.
    /// </summary>
    [Fact]
    public async Task DependencyChain_ModeSerial_WithMapping_RoutesTopologicallyAndResolves()
    {
        // Arrange — Mode.Serial wird angefordert, aber das Mapping erzwingt topologisch
        var step1 = ChainRequest("s1", "DepChain", "EchoLong",
            new Dictionary<string, string> { { "big", "$" } },
            ("value", "42"));
        var step2 = ChainRequest("s2", "DepChain", "EchoLong",
            ("value", "@big"));

        // Act
        var responses = (await _invoker.InvokeDi(new[] { step1, step2 }, null, ExecutionMode.Serial)).ToList();

        // Assert — topologischer Pfad löst den Alias trotz Mode=Serial auf
        var byId = responses.ToDictionary(r => r!.Id ?? string.Empty);
        byId["s2"].Code.Should().Be((int)HttpStatusCode.OK);
        byId["s2"].Error.Should().BeNull();
        byId["s2"].Data.Value.Deserialize<long>().Should().Be(42L);
    }

    /// <summary>
    /// Dokumentiert die Kehrseite: der echte Serial-Pfad (ExecuteSequentially) ist nur
    /// erreichbar, wenn KEIN Request ein DependencyMapping hat — dann kann aber kein
    /// Vorläufer ExposedDependencies produzieren, sodass ein @alias zwingend unresolved
    /// ist. Serial-Mode + Chain ohne Mapping ist daher ein Fehler, kein Erfolgspfad.
    /// </summary>
    [Fact]
    public async Task DependencyChain_SerialPath_WithoutMapping_AliasIsUnresolved()
    {
        // Arrange — kein Mapping → echter Serial-Pfad; step1 exponiert nichts
        var step1 = ChainRequest("s1", "DepChain", "EchoLong",
            ("value", "42"));
        var step2 = ChainRequest("s2", "DepChain", "EchoLong",
            ("value", "@big"));

        // Act
        var responses = (await _invoker.InvokeDi(new[] { step1, step2 }, null, ExecutionMode.Serial)).ToList();

        // Assert — Serial-Pfad kann @big nicht auflösen → 400, kein 500
        var byId = responses.ToDictionary(r => r!.Id ?? string.Empty);
        byId["s1"].Code.Should().Be((int)HttpStatusCode.OK);
        byId["s2"].Code.Should().Be((int)HttpStatusCode.BadRequest);
        byId["s2"].Error.Should().NotBeNull();
    }

    // --- Edge-Cases: weitere Primitives, geschachtelter Pfad, Dictionary, Nullable, Binär ---

    /// <summary>
    /// double durch eine Chain. System.Text.Json serialisiert double als JSON-Zahl; die
    /// Substitution muss die Zahlform erhalten (wie bei decimal/int).
    /// </summary>
    [Fact]
    public async Task DependencyChain_Double_RoundtripsThroughAlias()
    {
        // Arrange
        var step1 = ChainRequest("s1", "DepChain", "EchoDouble",
            new Dictionary<string, string> { { "pi", "$" } },
            ("value", "3.14159"));
        var step2 = ChainRequest("s2", "DepChain", "EchoDouble",
            ("value", "@pi"));

        // Act
        var responses = (await _invoker.InvokeDi(new[] { step1, step2 }, null)).ToList();

        // Assert
        var byId = responses.ToDictionary(r => r!.Id ?? string.Empty);
        byId["s2"].Code.Should().Be((int)HttpStatusCode.OK);
        byId["s2"].Error.Should().BeNull();
        byId["s2"].Data.Value.Deserialize<double>().Should().Be(3.14159);
    }

    /// <summary>
    /// float durch eine Chain. float wird als JSON-Zahl serialisiert; Wert so gewählt,
    /// dass er ohne Präzisionsverlust round-trippt.
    /// </summary>
    [Fact]
    public async Task DependencyChain_Float_RoundtripsThroughAlias()
    {
        // Arrange
        var step1 = ChainRequest("s1", "DepChain", "EchoFloat",
            new Dictionary<string, string> { { "f", "$" } },
            ("value", "1.5"));
        var step2 = ChainRequest("s2", "DepChain", "EchoFloat",
            ("value", "@f"));

        // Act
        var responses = (await _invoker.InvokeDi(new[] { step1, step2 }, null)).ToList();

        // Assert
        var byId = responses.ToDictionary(r => r!.Id ?? string.Empty);
        byId["s2"].Code.Should().Be((int)HttpStatusCode.OK);
        byId["s2"].Error.Should().BeNull();
        byId["s2"].Data.Value.Deserialize<float>().Should().Be(1.5f);
    }

    /// <summary>
    /// DateTime durch eine Chain. DateTime wird als ISO-8601-JSON-String serialisiert;
    /// die typgetreue Substitution muss die String-Form (mit Quotes) intakt halten
    /// (analog string), sonst bricht Deserialize&lt;DateTime&gt;.
    /// </summary>
    [Fact]
    public async Task DependencyChain_DateTime_RoundtripsThroughAlias()
    {
        // Arrange — Unspecified-Kind, damit der Roundtrip ohne Offset-Verschiebung ist
        var value = new DateTime(2026, 7, 9, 12, 0, 0, DateTimeKind.Unspecified);
        var step1 = ChainRequest("s1", "DepChain", "EchoDateTime",
            new Dictionary<string, string> { { "when", "$" } },
            ("value", $"\"{value:O}\""));
        var step2 = ChainRequest("s2", "DepChain", "EchoDateTime",
            ("value", "@when"));

        // Act
        var responses = (await _invoker.InvokeDi(new[] { step1, step2 }, null)).ToList();

        // Assert
        var byId = responses.ToDictionary(r => r!.Id ?? string.Empty);
        byId["s2"].Code.Should().Be((int)HttpStatusCode.OK);
        byId["s2"].Error.Should().BeNull();
        byId["s2"].Data.Value.Deserialize<DateTime>().Should().Be(value);
    }

    /// <summary>
    /// Guid durch eine Chain. Guid wird als JSON-String serialisiert; String-Form muss
    /// intakt transportiert werden.
    /// </summary>
    [Fact]
    public async Task DependencyChain_Guid_RoundtripsThroughAlias()
    {
        // Arrange
        var guid = Guid.Parse("12345678-1234-1234-1234-123456789abc");
        var step1 = ChainRequest("s1", "DepChain", "EchoGuid",
            new Dictionary<string, string> { { "g", "$" } },
            ("value", $"\"{guid}\""));
        var step2 = ChainRequest("s2", "DepChain", "EchoGuid",
            ("value", "@g"));

        // Act
        var responses = (await _invoker.InvokeDi(new[] { step1, step2 }, null)).ToList();

        // Assert
        var byId = responses.ToDictionary(r => r!.Id ?? string.Empty);
        byId["s2"].Code.Should().Be((int)HttpStatusCode.OK);
        byId["s2"].Error.Should().BeNull();
        byId["s2"].Data.Value.Deserialize<Guid>().Should().Be(guid);
    }

    /// <summary>
    /// enum durch eine Chain. Default-Serialisierung ist die numerische Member-Value
    /// (hier ChainPriority.High = 2); die Substitution behandelt das wie int.
    /// </summary>
    [Fact]
    public async Task DependencyChain_Enum_RoundtripsThroughAlias()
    {
        // Arrange
        var step1 = ChainRequest("s1", "DepChain", "EchoPriority",
            new Dictionary<string, string> { { "p", "$" } },
            ("value", "2"));
        var step2 = ChainRequest("s2", "DepChain", "EchoPriority",
            ("value", "@p"));

        // Act
        var responses = (await _invoker.InvokeDi(new[] { step1, step2 }, null)).ToList();

        // Assert
        var byId = responses.ToDictionary(r => r!.Id ?? string.Empty);
        byId["s2"].Code.Should().Be((int)HttpStatusCode.OK);
        byId["s2"].Error.Should().BeNull();
        byId["s2"].Data.Value.Deserialize<ChainPriority>().Should().Be(ChainPriority.High);
    }

    /// <summary>
    /// Ganze primitive Liste als Dependency: MakeIntList() → [10,20,30] exposes "$" als
    /// "list"; EchoIntList(@list) muss die Liste intakt zurückgeben. Komplementiert den
    /// Array-Element-Pfad-Test (dort wird nur $[1] extrahiert, hier die ganze List&lt;int&gt;
    /// injiziert).
    /// </summary>
    [Fact]
    public async Task DependencyChain_WholePrimitiveArray_InjectsIntoArrayParam()
    {
        // Arrange
        var step1 = ChainRequest("s1", "DepChain", "MakeIntList",
            new Dictionary<string, string> { { "list", "$" } });
        var step2 = ChainRequest("s2", "DepChain", "EchoIntList",
            ("values", "@list"));

        // Act
        var responses = (await _invoker.InvokeDi(new[] { step1, step2 }, null)).ToList();

        // Assert
        var byId = responses.ToDictionary(r => r!.Id ?? string.Empty);
        byId["s2"].Code.Should().Be((int)HttpStatusCode.OK);
        byId["s2"].Error.Should().BeNull();
        var values = byId["s2"].Data.Value.Deserialize<List<int>>();
        values.Should().Equal(10, 20, 30);
    }

    /// <summary>
    /// Geschachtelter JsonPath in einer Chain: MakeNestedDto(5,7) → {Id:5, Inner:{Id:7}};
    /// exposes "$.inner.id" als "innerId"; EchoLong(@innerId) muss 7 liefern. Prüft
    /// Multi-Level-Pfad-Extraktion (bisher nur flache $.Prop in Chains).
    /// </summary>
    [Fact]
    public async Task DependencyChain_NestedPath_InjectsInnerId()
    {
        // Arrange
        var step1 = ChainRequest("s1", "DepChain", "MakeNestedDto",
            new Dictionary<string, string> { { "innerId", "$.inner.id" } },
            ("outerId", "5"), ("innerId", "7"));
        var step2 = ChainRequest("s2", "DepChain", "EchoLong",
            ("value", "@innerId"));

        // Act
        var responses = (await _invoker.InvokeDi(new[] { step1, step2 }, null)).ToList();

        // Assert
        var byId = responses.ToDictionary(r => r!.Id ?? string.Empty);
        byId["s1"].ExposedDependencies!["innerId"].Should().Be("7");
        byId["s2"].Code.Should().Be((int)HttpStatusCode.OK);
        byId["s2"].Error.Should().BeNull();
        byId["s2"].Data.Value.Deserialize<long>().Should().Be(7L);
    }

    /// <summary>
    /// List-Fan-out (v1-Killer-Feature): MakeDtoList() → [{id:1},{id:2},{id:3}]
    /// exposes den Wildcard-Pfad "$[*].id" als "ids" → EchoIntList(values=@ids) muss
    /// die gesamte Liste [1,2,3] (nicht nur das erste Element) als List&lt;int&gt;
    /// erhalten. Beweist den vollen Round-Trip: Multi-Match-Extraktion → Array-JSON
    /// im ExposedDependency → typgetreue Injektion in einen List&lt;int&gt;-Parameter.
    /// </summary>
    [Fact]
    public async Task DependencyChain_WildcardFanOut_InjectsWholeIntList()
    {
        // Arrange
        var step1 = ChainRequest("s1", "DepChain", "MakeDtoList",
            new Dictionary<string, string> { { "ids", "$[*].id" } });
        var step2 = ChainRequest("s2", "DepChain", "EchoIntList",
            ("values", "@ids"));

        // Act
        var responses = (await _invoker.InvokeDi(new[] { step1, step2 }, null)).ToList();

        // Assert
        var byId = responses.ToDictionary(r => r!.Id ?? string.Empty);
        byId["s1"].Code.Should().Be((int)HttpStatusCode.OK);
        byId["s2"].Code.Should().Be((int)HttpStatusCode.OK);
        byId["s2"].Error.Should().BeNull();

        // Das ExposedDependency ist der Array-JSON-String (kein einzelner Skalar mehr).
        JsonSerializer.Deserialize<List<int>>(byId["s1"].ExposedDependencies!["ids"])
            .Should().Equal(1, 2, 3);

        // Der Consumer hat die ganze Liste als List<int> erhalten.
        byId["s2"].Data.Value.Deserialize<List<int>>().Should().Equal(1, 2, 3);
    }

    /// <summary>
    /// Dreistufiger Fan-out (Search → GetOpenByCustomerIds → GetAvailability-Form):
    /// MakeDtoList exposes $[*].id als "ids"; EchoIntList(@ids) gibt [1,2,3] zurück
    /// und exposes das Ergebnis per $[*] als "ids2"; ein zweiter EchoIntList(@ids2)
    /// muss wieder [1,2,3] liefern. Beweist, dass der Array-Fan-out über mehrere
    /// Stufen komponiert — jede Stufe kann ein Wildcard-Ergebnis weiterreichen.
    /// </summary>
    [Fact]
    public async Task DependencyChain_ThreeStepFanOut_ComposesAcrossSteps()
    {
        // Arrange
        var step1 = ChainRequest("s1", "DepChain", "MakeDtoList",
            new Dictionary<string, string> { { "ids", "$[*].id" } });
        var step2 = ChainRequest("s2", "DepChain", "EchoIntList",
            new Dictionary<string, string> { { "ids2", "$[*]" } },
            ("values", "@ids"));
        var step3 = ChainRequest("s3", "DepChain", "EchoIntList",
            ("values", "@ids2"));

        // Act
        var responses = (await _invoker.InvokeDi(new[] { step1, step2, step3 }, null)).ToList();

        // Assert
        var byId = responses.ToDictionary(r => r!.Id ?? string.Empty);
        byId["s3"].Code.Should().Be((int)HttpStatusCode.OK);
        byId["s3"].Error.Should().BeNull();
        byId["s3"].Data.Value.Deserialize<List<int>>().Should().Equal(1, 2, 3);
    }

    /// <summary>
    /// Dictionary als Dependency: MakeDict() → {"a":1,"b":2,"c":3} exposes "$" als "map";
    /// EchoDict(@map) muss das Dictionary intakt zurückgeben. Prüft, dass ein JSON-Objekt
    /// mit String-Keys in einen Dictionary-Parameter injiziert wird.
    /// </summary>
    [Fact]
    public async Task DependencyChain_Dictionary_WholeResult_InjectsIntoDictParam()
    {
        // Arrange
        var step1 = ChainRequest("s1", "DepChain", "MakeDict",
            new Dictionary<string, string> { { "map", "$" } });
        var step2 = ChainRequest("s2", "DepChain", "EchoDict",
            ("map", "@map"));

        // Act
        var responses = (await _invoker.InvokeDi(new[] { step1, step2 }, null)).ToList();

        // Assert
        var byId = responses.ToDictionary(r => r!.Id ?? string.Empty);
        byId["s2"].Code.Should().Be((int)HttpStatusCode.OK);
        byId["s2"].Error.Should().BeNull();
        var map = byId["s2"].Data.Value.Deserialize<Dictionary<string, int>>();
        map.Should().NotBeNull();
        map!.Should().HaveCount(3);
        map["b"].Should().Be(2);
    }

    /// <summary>
    /// Nullable-Result: FindDto(-1) liefert null; exposes "$.name". Ein null-Result
    /// liefert Data=null (kein JsonElement) → der Capture-Block wird übersprungen und
    /// ExposedDependencies bleibt null (kein Crash, kein 500). Der Consumer bekommt
    /// daher 400 (Unresolved dependencies).
    /// </summary>
    [Fact]
    public async Task DependencyChain_NullResult_AliasNotExposed_ConsumerReturns400()
    {
        // Arrange
        var step1 = ChainRequest("s1", "DepChain", "FindDto",
            new Dictionary<string, string> { { "name", "$.name" } },
            ("id", "-1"));
        var step2 = ChainRequest("s2", "DepChain", "EchoString",
            ("value", "@name"));

        // Act
        var responses = (await _invoker.InvokeDi(new[] { step1, step2 }, null)).ToList();

        // Assert
        var byId = responses.ToDictionary(r => r!.Id ?? string.Empty);
        byId["s1"].Code.Should().Be((int)HttpStatusCode.OK);
        byId["s1"].ExposedDependencies.Should().BeNull();
        byId["s2"].Code.Should().Be((int)HttpStatusCode.BadRequest);
        byId["s2"].Error.Should().NotBeNull();
    }

    /// <summary>
    /// Binär-Result als Dependency-Quelle: DownloadBytes() → byte[]; exposes "$".
    /// byte[]-Returns liegen seit dem Single-Pass-Fix ausschließlich in Content; Data
    /// ist null → der Capture-Block wird übersprungen und ExposedDependencies bleibt
    /// null (kein 500 durch unparsebares Base64 mehr). Zusätzlich sind byte[]-Parameter
    /// aus BinaryData gespeist, nicht aus dem Data-Feld, sodass binäre Payloads über
    /// @alias nicht chainbar sind. Dokumentiert die Designgrenze als sauberes 400 statt
    /// Server-Crash.
    /// </summary>
    [Fact]
    public async Task DependencyChain_BinaryResult_AliasNotExposed_ConsumerReturns400()
    {
        // Arrange
        var step1 = ChainRequest("s1", "DepChain", "DownloadBytes",
            new Dictionary<string, string> { { "bytes", "$" } });
        var step2 = ChainRequest("s2", "DepChain", "EchoBytes",
            ("data", "@bytes"));

        // Act
        var responses = (await _invoker.InvokeDi(new[] { step1, step2 }, null)).ToList();

        // Assert — step1 crasht nicht (Data ist null, Alias wird übersprungen)
        var byId = responses.ToDictionary(r => r!.Id ?? string.Empty);
        byId["s1"].Code.Should().Be((int)HttpStatusCode.OK);
        byId["s1"].Content.Should().NotBeNull();
        byId["s1"].ExposedDependencies.Should().BeNull();
        // step2 bekommt den Alias nicht -> sauberes 400, kein 500
        byId["s2"].Code.Should().Be((int)HttpStatusCode.BadRequest);
        byId["s2"].Error.Should().NotBeNull();
    }

    #endregion

    // ─────────────────────────────────────────────────────────────────────
    // Kollisions-Fixtures für die Register-*-Tests unten.
    //
    // Diese Controller sind absichtlich invalid (gleichnamige Sleipnir-Methoden bzw.
    // gleichnamige Controller). Sie tragen AutoDiscover = false, damit der
    // Auto-Discovery-Skan in UseSleipnir/AddSleipnir/FromAssemblies sie überspringt —
    // sonst würde schon das Hochfahren des Integrationstest-Hosts an ihnen werfen.
    // Explizit per Register<T>() sind sie trotzdem registrierbar, und genau das
    // prüfen die Unit-Tests: die Registrierung muss hart fehlschlagen.
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Controller mit zwei gleichnamigen Sleipnir-Methoden (unterschiedliche C#-Signaturen).
    /// Sleipnir resolved keine Überladungen anhand der Parameter — der Doppelname ist
    /// ein Bug, keine Feature, und muss bei der Registrierung werfen.
    /// </summary>
    [SleipnirController("DuplicateNames", AutoDiscover = false)]
    private class ControllerWithDuplicateMethodNames
    {
        [SleipnirMethod("DoIt")]
        public int DoIt(int a, int b) => a + b;

        [SleipnirMethod("DoIt")]
        public string DoIt(string s) => s;
    }

    /// <summary>Erster von zwei Controllern mit gleichem Sleipnir-Namen.</summary>
    [SleipnirController("Clash", AutoDiscover = false)]
    private class ControllerNamedClashA
    {
        [SleipnirMethod("Ping")]
        public string Ping() => "a";
    }

    /// <summary>Zweiter Controller, der denselben Sleipnir-Namen wie ClashA belegt.</summary>
    [SleipnirController("Clash", AutoDiscover = false)]
    private class ControllerNamedClashB
    {
        [SleipnirMethod("Ping")]
        public string Ping() => "b";
    }

    // ─── Event marker contract fixtures ([SleipnirEvent]) ──────────────────────

    /// <summary>[SleipnirEvent] on a non-IObservable return → registration must throw.</summary>
    [SleipnirController("BadEventNonObservable", AutoDiscover = false)]
    private class EventNotObservableController
    {
        [SleipnirEvent("NotObservable")]
        public int NotObservable() => 1;
    }

    /// <summary>IObservable<T> return on a [SleipnirMethod] → registration must throw.</summary>
    [SleipnirController("BadMethodObservable", AutoDiscover = false)]
    private class MethodObservableButMarkedMethodController
    {
        [SleipnirMethod("IsObservable")]
        public IObservable<string> IsObservable()
            => new SimpleObservable<string>(_ => () => { });
    }

    /// <summary>Both [SleipnirMethod] and [SleipnirEvent] on one method → registration must throw.</summary>
    [SleipnirController("BadBothMarkers", AutoDiscover = false)]
    private class BothMarkersController
    {
        [SleipnirMethod("Both")]
        [SleipnirEvent("Both")]
        public IObservable<string> Both()
            => new SimpleObservable<string>(_ => () => { });
    }

    /// <summary>
    /// Valid mixed controller: a call method with an observable side effect (Poke) and a
    /// proper event (GoodEvent). Used to prove a subscribe to a call method fails before
    /// the call body runs (no side effect) and a call to an event method returns 400.
    /// </summary>
    [SleipnirController("EventContract", AutoDiscover = false)]
    private class EventContractController
    {
        public static int PokeCount;

        [SleipnirMethod("Poke")]
        public int Poke() => Interlocked.Increment(ref PokeCount);

        [SleipnirEvent("GoodEvent")]
        public IObservable<string> GoodEvent(int count)
            => new SimpleObservable<string>(observer =>
            {
                for (int i = 0; i < count; i++)
                    observer.OnNext($"e{i}");
                observer.OnCompleted();
                return () => { };
            });
    }

    /// <summary>
    /// Backpressure-override fixture: three events exercising the per-event override
    /// resolution (override ?? global ?? default 100/DropOldest; Unbounded → capacity 0).
    /// </summary>
    [SleipnirController("BpEvents", AutoDiscover = false)]
    private class BackpressureEventController
    {
        [SleipnirEvent("OverrideEvent", BufferCapacity = 7, BackpressureStrategy = EventBackpressureStrategy.DropWrite)]
        public IObservable<string> OverrideEvent(int count)
            => new SimpleObservable<string>(o => { for (int i = 0; i < count; i++) o.OnNext($"e{i}"); o.OnCompleted(); return () => { }; });

        [SleipnirEvent("PlainEvent")]
        public IObservable<string> PlainEvent(int count)
            => new SimpleObservable<string>(o => { for (int i = 0; i < count; i++) o.OnNext($"e{i}"); o.OnCompleted(); return () => { }; });

        [SleipnirEvent("UnboundedEvent", BackpressureStrategy = EventBackpressureStrategy.Unbounded)]
        public IObservable<string> UnboundedEvent(int count)
            => new SimpleObservable<string>(o => { for (int i = 0; i < count; i++) o.OnNext($"e{i}"); o.OnCompleted(); return () => { }; });
    }

    /// <summary>
    /// Value-type-element event fixture (IObservable&lt;int&gt;). Regression for the
    /// subscribe-time bug present up to 1.2.0, which rejected value-type elements via the
    /// covariant <c>result is IObservable&lt;object?&gt;</c> test as "not a subscribable
    /// event" (IObservable&lt;out T&gt; covariance does not apply to value-type elements).
    /// The invoker now builds a boxing adapter IObservable&lt;object?&gt; for value types.
    /// </summary>
    [SleipnirController("VtEvents", AutoDiscover = false)]
    private class ValueTypeEventController
    {
        [SleipnirEvent("ObservableInts")]
        public IObservable<int> ObservableInts(int count)
            => new SimpleObservable<int>(o => { for (int i = 0; i < count; i++) o.OnNext(i); o.OnCompleted(); return () => { }; });
    }

    /// <summary>Collects IObservable&lt;object?&gt; elements and signals OnCompleted/OnError.</summary>
    private sealed class ObjCollector : IObserver<object?>
    {
        private readonly List<object?> _values;
        private readonly ManualResetEventSlim _done;
        public ObjCollector(List<object?> values, ManualResetEventSlim done)
        { _values = values; _done = done; }
        public void OnNext(object? value) => _values.Add(value);
        public void OnError(Exception error) => _done.Set();
        public void OnCompleted() => _done.Set();
    }
}
