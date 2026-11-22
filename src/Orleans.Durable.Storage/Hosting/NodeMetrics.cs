using System.Diagnostics.Metrics;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Hosting;

/// <summary>
/// The node's resources and its primaries on the composition's meter (orleans.md §5.9, §8), each measurement tagged with
/// <c>node</c>, and a ledger's also with <c>ledger</c>. Two clocks feed them. The resource gauges, and each primary's
/// undrained bytes and age, publish the governance loop's last record, which <see cref="DurableStorageStatus"/> answers
/// too, and nothing before the first: its primaries are the open ledgers whose writer the node holds. The pressure gauges,
/// <c>orleans.durable.storage.node.disk.pressure</c>, <c>orleans.durable.storage.node.primaries</c>, and <c>orleans.durable.storage.ledger.throttled</c>, publish the pressure state
/// as it stands: its primaries are the open ledgers the node's map view makes it primary of, served or not.
/// </summary>
/// <remarks>
/// Each runtime owns a meter of its own, which ends with it: a meter from <c>IMeterFactory</c> is cached by name and
/// ignores its disposal, so a stopped runtime's gauges would go on publishing. The meter carries the <c>node</c> tag too,
/// so a listener in a process which runs several silos tells each runtime's instruments apart.
/// </remarks>
internal sealed class NodeMetrics : IDisposable
{
    private readonly Meter _meter;
    private readonly KeyValuePair<string, object?> _node;
    private DurableStorageNodeStatus? _lastStatus;

    /// <summary>Initializes a new instance of the <see cref="NodeMetrics"/> class, publishing <paramref name="node"/>'s instruments.</summary>
    /// <param name="node">The node.</param>
    /// <param name="pressure">The node's pressure state, which the pressure gauges publish.</param>
    public NodeMetrics(NodeId node, NodePressure pressure)
    {
        ArgumentNullException.ThrowIfNull(pressure);
        _node = new("node", node.ToString());
        _meter = new(new MeterOptions(DurableStorageMeter.Name) { Tags = [_node] });
        _ = _meter.CreateObservableGauge("orleans.durable.storage.node.disk.used", () => Resource(static resources => resources.DiskUsedBytes), "By", "The bytes of the node's LedgerStore files.");
        _ = _meter.CreateObservableGauge("orleans.durable.storage.node.disk.free", () => Resource(static resources => resources.DiskFreeBytes), "By", "The least free space of the volumes holding the node's LedgerStore.");
        _ = _meter.CreateObservableGauge("orleans.durable.storage.node.disk.budget", () => Resource(static resources => resources.DiskBudgetBytes), "By", "The node's disk budget.");
        _ = _meter.CreateObservableGauge("orleans.durable.storage.node.wal.retained_files", () => Resource(static resources => (long)resources.RetainedWalFiles), "{file}", "The WAL files the node keeps, from the oldest live replica's first file.");
        _ = _meter.CreateObservableGauge("orleans.durable.storage.node.wal.live_replicas", () => Resource(static resources => resources.LiveReplicas), "{replica}", "The node's live replicas.");
        _ = _meter.CreateObservableGauge("orleans.durable.storage.node.disk.pressure", () => new Measurement<int>(pressure.UnderPressure ? 1 : 0, _node), "1", "1 while the node is under disk pressure.");
        _ = _meter.CreateObservableGauge("orleans.durable.storage.node.primaries", () => new Measurement<int>(pressure.ThrottleCauses.Count, _node), "{ledger}", "The open ledgers the node's map view makes it primary of.");
        _ = _meter.CreateObservableGauge(
            "orleans.durable.storage.ledger.throttled",
            () => pressure.ThrottleCauses.Select(primary => new Measurement<int>(primary.Value.IsEmpty ? 0 : 1, _node, new("ledger", primary.Key.ToString()))),
            "1",
            "1 while the ledger's primary on the node is throttled.");
        _ = _meter.CreateObservableCounter(
            "orleans.durable.storage.ledger.throttled_writes",
            () => pressure.ThrottledWrites.Select(ledger => new Measurement<long>(ledger.Value, _node, new("ledger", ledger.Key.ToString()))),
            "{write}",
            "The journal writes the ledger's primary on the node answered Throttled.");
        _ = _meter.CreateObservableGauge(
            "orleans.durable.storage.ledger.age",
            () => PerPrimary(static ledger => ledger.Undrained.Age?.TotalSeconds ?? 0),
            "s",
            "The age of the oldest undrained batch with rows of the ledger whose primary is the node, 0 when none is.");
        _ = _meter.CreateObservableGauge(
            "orleans.durable.storage.ledger.undrained",
            () => PerPrimary(static ledger => ledger.Undrained.Bytes),
            "By",
            "The bytes the ledger's primary on the node proposed at or above the ledger's watermark.");
    }

    /// <summary>Gets the runtime's meter, on which the node's other instruments, its archive-log drains' among them, are published.</summary>
    internal Meter Meter => _meter;

    /// <summary>Gets the node's last recorded status, or <see langword="null"/> before the first record.</summary>
    public DurableStorageNodeStatus? LastStatus => Volatile.Read(ref _lastStatus);

    /// <summary>Records the node's status, which the gauges publish until the next record.</summary>
    public void Record(DurableStorageNodeStatus status) => Volatile.Write(ref _lastStatus, status);

    /// <inheritdoc/>
    public void Dispose() => _meter.Dispose();

    private IEnumerable<Measurement<long>> Resource(Func<NodeResources, long> value) =>
        LastStatus?.Resources is { } resources ? [new Measurement<long>(value(resources), _node)] : [];

    private IEnumerable<Measurement<T>> PerPrimary<T>(Func<DurableStorageLedgerStatus, T> value)
        where T : struct =>
        LastStatus is { } status ? status.Primaries.Select(ledger => new Measurement<T>(value(ledger), _node, new("ledger", ledger.Ledger.ToString()))) : [];
}

