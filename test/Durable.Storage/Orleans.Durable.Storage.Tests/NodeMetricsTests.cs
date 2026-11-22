using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Hosting;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// The node's instruments (orleans.md §8): each publishes its own value tagged with the node, the resource gauges and each
/// primary's undrained bytes and age only from the governance loop's last record, so a ledger or resources a record leaves
/// out are no longer published, the pressure gauges from the pressure state as it stands, and none once the runtime
/// disposed them.
/// </summary>
[NoPageLeakCheck("The instruments rent no pages.")]
public sealed class NodeMetricsTests
{
    [Fact]
    public void EveryInstrumentPublishesItsOwnValueUntilDisposed()
    {
        var node = new NodeId(Guid.NewGuid());
        var pressure = new NodePressure(TimeSpan.FromSeconds(1), new FakeTimeProvider(), new RecordingLogger<NodePressure>());
        var values = new ConcurrentDictionary<(string Instrument, string? Ledger), long>();
        using var listener = Listen(node, values);
        var metrics = new NodeMetrics(node, pressure);

        listener.RecordObservableInstruments();
        Assert.Equal(
            new Dictionary<(string, string?), long> { [("orleans.durable.storage.node.disk.pressure", null)] = 0, [("orleans.durable.storage.node.primaries", null)] = 0 },
            values);

        var (throttled, quiet) = (new LedgerId(7), new LedgerId(8));
        var (lower, upper) = KeyRange.Root.Children;
        var reporter = new NodeId(Guid.NewGuid());
        pressure.Report(reporter);
        pressure.SetUnderPressure(true);
        pressure.Observe(node, new LedgerMapValue(1, new LedgerId(10), 0, [Ledger(throttled, lower, node), Ledger(quiet, upper, node)]));
        pressure.SetUnderPressure(false);
        pressure.Observe(node, new LedgerMapValue(1, new LedgerId(10), 0, [Ledger(throttled, lower, node, reporter), Ledger(quiet, upper, node)]));
        var resources = new NodeResources(DateTimeOffset.UnixEpoch, DiskUsedBytes: 11, DiskFreeBytes: 12, DiskBudgetBytes: 13, UnderPressure: false, RetainedWalFiles: 14, LiveReplicas: 15);
        var quietStatus = new DurableStorageLedgerStatus(quiet, AppliedBelow: null, Slot.Zero, Slot.Zero, new LedgerUndrained(0, Age: null), []);
        metrics.Record(new DurableStorageNodeStatus(
            node,
            DateTimeOffset.UnixEpoch,
            resources,
            [new DurableStorageLedgerStatus(throttled, new Slot(5), new Slot(6), new Slot(2), new LedgerUndrained(300, TimeSpan.FromSeconds(90)), [reporter]), quietStatus]));
        values.Clear();
        listener.RecordObservableInstruments();

        Assert.Equal(
            new Dictionary<(string, string?), long>
            {
                [("orleans.durable.storage.node.disk.used", null)] = 11,
                [("orleans.durable.storage.node.disk.free", null)] = 12,
                [("orleans.durable.storage.node.disk.budget", null)] = 13,
                [("orleans.durable.storage.node.wal.retained_files", null)] = 14,
                [("orleans.durable.storage.node.wal.live_replicas", null)] = 15,
                [("orleans.durable.storage.node.disk.pressure", null)] = 0,
                [("orleans.durable.storage.node.primaries", null)] = 2,
                [("orleans.durable.storage.ledger.throttled", throttled.ToString())] = 1,
                [("orleans.durable.storage.ledger.throttled", quiet.ToString())] = 0,
                [("orleans.durable.storage.ledger.undrained", throttled.ToString())] = 300,
                [("orleans.durable.storage.ledger.undrained", quiet.ToString())] = 0,
                [("orleans.durable.storage.ledger.age", throttled.ToString())] = 90,
                [("orleans.durable.storage.ledger.age", quiet.ToString())] = 0,
            },
            values);

        // A ledger the next record leaves out, as one handed off, is no longer published; nor are resources it lacks.
        metrics.Record(new DurableStorageNodeStatus(node, DateTimeOffset.UnixEpoch.AddSeconds(1), Resources: null, [quietStatus]));
        values.Clear();
        listener.RecordObservableInstruments();
        Assert.Equal(
            new Dictionary<(string, string?), long>
            {
                [("orleans.durable.storage.node.disk.pressure", null)] = 0,
                [("orleans.durable.storage.node.primaries", null)] = 2,
                [("orleans.durable.storage.ledger.throttled", throttled.ToString())] = 1,
                [("orleans.durable.storage.ledger.throttled", quiet.ToString())] = 0,
                [("orleans.durable.storage.ledger.undrained", quiet.ToString())] = 0,
                [("orleans.durable.storage.ledger.age", quiet.ToString())] = 0,
            },
            values);

        metrics.Dispose();
        values.Clear();
        listener.RecordObservableInstruments();
        Assert.Empty(values);
    }

    private static LedgerMetadata Ledger(LedgerId id, KeyRange range, NodeId primary, NodeId? replica = null) =>
        new(id, range, [replica ?? primary], 4, new PrimaryClaim(primary, SequencerInstanceId.New()), LedgerStatus.Open.Instance);

    /// <summary>Listens to the composition's meter, recording each of <paramref name="node"/>'s measurements by instrument and ledger.</summary>
    private static MeterListener Listen(NodeId node, ConcurrentDictionary<(string Instrument, string? Ledger), long> values)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = static (instrument, listener) =>
            {
                if (instrument.Meter.Name == DurableStorageMeter.Name)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };

        void Record<T>(Instrument instrument, T value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
            where T : struct
        {
            var (named, ledger) = (false, (string?)null);
            foreach (var tag in tags)
            {
                named |= tag.Key == "node" && Equals(tag.Value, node.ToString());
                ledger = tag.Key == "ledger" ? tag.Value as string : ledger;
            }

            if (named)
            {
                values[(instrument.Name, ledger)] = Convert.ToInt64(value, CultureInfo.InvariantCulture);
            }
        }

        listener.SetMeasurementEventCallback<long>(Record);
        listener.SetMeasurementEventCallback<int>(Record);
        listener.SetMeasurementEventCallback<double>(Record);
        listener.Start();
        return listener;
    }
}
