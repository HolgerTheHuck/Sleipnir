using System.Diagnostics.Metrics;
using FluentAssertions;
using SleipnirCore.Tracing;
using Xunit;

namespace SleipnirTests.Unit.Telemetry;

/// <summary>
/// Dedicated collection for <see cref="SleipnirGaugeCurrentTests"/>. The gauge test writes
/// AND asserts on process-global state, so it must not run in parallel with anything that
/// touches <see cref="SleipnirConnectionRegistry.Current"/>.
/// </summary>
/// <remarks>
/// Every test host that calls <c>AddSleipnir</c> (the TransportTestFixture-based
/// integration suites — 15+ classes) eagerly overwrites the process-global
/// <c>Current</c> via <c>SleipnirConnectionRegistry.SetInstance</c>. Plain membership in
/// the <c>sleipnir-tracing</c> collection does not help: collection membership only
/// serializes tests *within* the collection, and those integration classes live outside
/// it. This is how the 2026-09-13 audit flake
/// (<c>Gauges_Read_Current_Registry_Values</c>) reproduced — a parallel host flipped
/// <c>Current</c> between the test's <c>SetInstance</c> and its gauge poll.
/// <para>
/// <c>DisableParallelization = true</c> opts this collection out of cross-collection
/// parallelism entirely, making the race structurally impossible. Cost is negligible:
/// the test itself runs in milliseconds, it merely runs outside the parallel window.
/// </para>
/// </remarks>
[CollectionDefinition("gauge-current", DisableParallelization = true)]
public class GaugeCurrentCollectionDefinition { }

/// <summary>
/// Verifies that the <see cref="SleipnirMetrics"/> gauges
/// (<c>sleipnir.ws.connections</c> / <c>sleipnir.subscriptions.active</c>) read the
/// *current* registry (<see cref="SleipnirConnectionRegistry.Current"/>) — not the
/// instance frozen at the first <see cref="SleipnirMetrics.SetConnectionRegistry"/> call.
/// Values are polled via a <see cref="MeterListener"/> (cheap, no OTel SDK), listening
/// only to the Sleipnir meter.
/// </summary>
[Collection("gauge-current")]
public class SleipnirGaugeCurrentTests
{
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