using System.Threading.Tasks;
using FluentAssertions;
using SleipnirCore.Tracing;
using Xunit;

namespace SleipnirTests.Unit.Telemetry;

/// <summary>
/// Tests für <see cref="SleipnirConnectionRegistry"/> (lock-free Interlocked-Zähler).
/// Die Gauge-Auslese-Tests (MeterListener) und die process-globalen
/// Meter-/ActivitySource-Tests teilen die <c>sleipnir-tracing</c>-Collection —
/// serialize-only untereinander, Rest parallel.
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

    // Gauges_Read_Current_Registry_Values moved to SleipnirGaugeCurrentTests (its own
    // DisableParallelization collection — see that file for the race rationale).
}