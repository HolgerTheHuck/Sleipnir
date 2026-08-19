using System.Diagnostics.Metrics;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SleipnirCore.Tracing;
using Xunit;

namespace SleipnirTests.Unit.Telemetry;

/// <summary>
/// Tests für <see cref="SleipnirConnectionRegistry"/> (lock-free Interlocked-Zähler) und
/// die <see cref="SleipnirMetrics"/>-Gauges <c>sleipnir.ws.connections</c> /
/// <c>sleipnir.subscriptions.active</c>. Die Gauges werden über einen
/// <see cref="MeterListener"/> ausgelesen (kostengünstig, ohne OTel-SDK), daher teilt
/// diese Klasse die <c>sleipnir-tracing</c>-Collection mit den anderen prozess-globalen
/// Meter-/ActivitySource-Tests — serialize-only untereinander, Rest parallel.
/// </summary>
[Collection("sleipnir-tracing")]
public class SleipnirConnectionRegistryTests
{
    [Fact]
    public void IncDec_Connections_Concurrent_ReturnsToBaseline()
    {
        var registry = new SleipnirConnectionRegistry();
        const int threads = 16;
        const int perThread = 500;

        Parallel.For(0, threads, _ =>
        {
            for (var i = 0; i < perThread; i++)
            {
                registry.IncConnection();
                registry.DecConnection();
            }
        });

        registry.Connections.Should().Be(0);
    }

    [Fact]
    public void IncDec_Subscriptions_Concurrent_ReturnsToBaseline()
    {
        var registry = new SleipnirConnectionRegistry();
        const int threads = 16;
        const int perThread = 500;

        Parallel.For(0, threads, _ =>
        {
            for (var i = 0; i < perThread; i++)
            {
                registry.IncSubscription();
                registry.DecSubscription();
            }
        });

        registry.Subscriptions.Should().Be(0);
    }

    [Fact]
    public void IncSubscription_WithoutDec_ReflectedInCount()
    {
        var registry = new SleipnirConnectionRegistry();
        registry.IncSubscription();
        registry.IncSubscription();
        registry.Subscriptions.Should().Be(2);
        registry.DecSubscription();
        registry.Subscriptions.Should().Be(1);
    }

    [Fact]
    public void RecordCall_Success_BumpsCallCountNotErrorCount()
    {
        var registry = new SleipnirConnectionRegistry();
        registry.RecordCall(success: true);
        registry.RecordCall(success: true);
        registry.CallCount.Should().Be(2);
        registry.ErrorCount.Should().Be(0);
    }

    [Fact]
    public void RecordCall_Failure_BumpsBothCallAndErrorCount()
    {
        var registry = new SleipnirConnectionRegistry();
        registry.RecordCall(success: false);
        registry.CallCount.Should().Be(1);
        registry.ErrorCount.Should().Be(1);
    }

    [Fact]
    public void RecordBatch_And_EventDrop_Accumulate()
    {
        var registry = new SleipnirConnectionRegistry();
        registry.RecordBatch();
        registry.RecordBatch();
        registry.BatchCount.Should().Be(2);
        registry.RecordEventDrop();
        registry.RecordEventDrop();
        registry.RecordEventDrop();
        registry.EventDroppedTotal.Should().Be(3);
    }

    [Fact]
    public void GetSnapshot_Reflects_Current_State()
    {
        var registry = new SleipnirConnectionRegistry();
        registry.IncConnection();
        registry.IncSubscription();
        registry.IncSubscription();
        registry.RecordCall(success: false);
        registry.RecordBatch();

        var snap = registry.GetSnapshot();
        snap.ActiveConnections.Should().Be(1);
        snap.ActiveSubscriptions.Should().Be(2);
        snap.CallCount.Should().Be(1);
        snap.ErrorCount.Should().Be(1);
        snap.BatchCount.Should().Be(1);
        snap.EventDroppedTotal.Should().Be(0);
    }

    [Fact]
    public void StartedAtUtc_IsRecent()
    {
        var registry = new SleipnirConnectionRegistry();
        var delta = DateTimeOffset.UtcNow - registry.StartedAtUtc;
        delta.Should().BeLessOrEqualTo(TimeSpan.FromSeconds(5));
        delta.Should().BeGreaterOrEqualTo(TimeSpan.FromSeconds(-5));
    }

    /// <summary>
    /// Verifiziert, dass die Gauge-Callbacks den Wert der *aktuellen* Registry
    /// (<see cref="SleipnirConnectionRegistry.Current"/>) liefern — nicht der beim ersten
    /// <see cref="SleipnirMetrics.SetConnectionRegistry"/>-Aufruf übergebenen (die in einem
    /// Testprozess mit mehreren Hosts sonst eingefroren würden). Ausgelesen via
    /// <see cref="MeterListener"/>, der nur den Sleipnir-Meter beobachtet.
    /// </summary>
    [Fact]
    public void Gauges_Read_Current_Registry_Values()
    {
        var registry = new SleipnirConnectionRegistry();
        registry.IncConnection();
        registry.IncConnection();
        registry.IncSubscription();
        registry.IncSubscription();
        registry.IncSubscription();
        // Install as the process-wide current so the gauge callbacks (which read Current)
        // observe this instance.
        SleipnirConnectionRegistry.SetInstance(registry);
        // Ensure the ObservableGauges exist on the Sleipnir meter.
        SleipnirMetrics.SetConnectionRegistry(registry);

        int? connections = null;
        int? subscriptions = null;

        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == SleipnirMetrics.MeterName)
                    l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<int>((inst, value, tags, state) =>
        {
            if (inst.Name == "sleipnir.ws.connections") connections = value;
            else if (inst.Name == "sleipnir.subscriptions.active") subscriptions = value;
        });
        listener.Start();
        // ObservableGauges are polled on RecordObservableInstruments.
        listener.RecordObservableInstruments();

        connections.Should().Be(2);
        subscriptions.Should().Be(3);

        listener.Dispose();
    }
}