using Microsoft.Extensions.Logging;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Store;

namespace Orleans.Durable.Storage.Hosting;

/// <summary>
/// The node's disk policy (orleans.md §5.9): from each measurement of the node's LedgerStore
/// (<see cref="LedgerStore.MeasureDiskAsync"/>), whether the node is under disk pressure. It is while the store's bytes
/// exceed the budget or a volume holding the store has less free space than the minimum, and pressure ends once both are
/// clear by one WAL file, so a node near either bound does not flap. Pressure starting and ending are each logged once
/// (events 6237, 6238).
/// </summary>
internal sealed partial class NodeDisk
{
    private readonly NodeId _node;
    private readonly long _minimumFreeBytes;
    private readonly long _walFileSize;
    private readonly ILogger _logger;
    private bool _underPressure;

    /// <summary>Initializes a new instance of the <see cref="NodeDisk"/> class.</summary>
    /// <param name="node">The node, which the log events name.</param>
    /// <param name="budgetBytes">The node's disk budget, the bytes of its LedgerStore's files.</param>
    /// <param name="minimumFreeBytes">The free space of a volume holding the store below which the node is under pressure.</param>
    /// <param name="walFileSize">The size of a WAL file, by which both bounds must be clear for pressure to end.</param>
    /// <param name="logger">Logs pressure starting and ending.</param>
    public NodeDisk(NodeId node, long budgetBytes, long minimumFreeBytes, long walFileSize, ILogger<NodeDisk> logger)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budgetBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(minimumFreeBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(walFileSize);
        ArgumentNullException.ThrowIfNull(logger);
        _node = node;
        BudgetBytes = budgetBytes;
        _minimumFreeBytes = minimumFreeBytes;
        _walFileSize = walFileSize;
        _logger = logger;
    }

    /// <summary>Gets the node's disk budget.</summary>
    public long BudgetBytes { get; }

    /// <summary>
    /// Creates the node's disk policy from its options and the store's first measurement: the budget, by default 80% of the
    /// volume holding the WAL, once the bounds leave pressure a way to end (orleans.md §5.9).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The room the store has, the smaller of the budget and the WAL's volume less the minimum free space, is at most
    /// <c>T</c> and three WAL files: a node holds its WAL near <c>T</c> past its oldest replica, with the current and the
    /// next WAL file, pressure ends only one WAL file below the budget, and a node whose volume fills first is throttled
    /// before its WAL reaches <c>F</c>, so no roll releases it. Or the minimum free space and one WAL file reach the size
    /// of a volume holding the store, which no free space can clear.
    /// </exception>
    public static NodeDisk Create(NodeId node, DurableStorageOptions options, LedgerStoreDiskUsage usage, ILogger<NodeDisk> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(usage);
        var budget = options.DiskBudgetBytes ?? usage.WalVolumeBytes / 5 * 4;
        var retention = WalRelease.Retention(options, budget);
        var room = Math.Min(budget, usage.WalVolumeBytes - options.MinimumFreeDiskBytes);
        if ((Int128)retention + 3 * (Int128)options.WalFileSize >= room)
        {
            throw new InvalidOperationException(
                $"Node {node}'s disk leaves disk pressure no way to end: the smaller of its budget, {budget} bytes, and its WAL's volume of {usage.WalVolumeBytes} bytes less the minimum free space of {options.MinimumFreeDiskBytes} must exceed the WAL retention target T, {retention} bytes, and three WAL files of {options.WalFileSize} bytes, the least the node holds past its oldest replica and the one file of hysteresis by which pressure ends (DiskBudgetBytes, MinimumFreeDiskBytes, WalRetentionBytes, WalFileSize).");
        }

        if ((Int128)options.MinimumFreeDiskBytes + options.WalFileSize >= usage.SmallestVolumeBytes)
        {
            throw new InvalidOperationException(
                $"Node {node}'s minimum free disk space of {options.MinimumFreeDiskBytes} bytes and one WAL file of {options.WalFileSize} bytes reach the size of a volume holding its LedgerStore, {usage.SmallestVolumeBytes} bytes, so disk pressure could never end (MinimumFreeDiskBytes, WalFileSize).");
        }

        return new NodeDisk(node, budget, options.MinimumFreeDiskBytes, options.WalFileSize, logger);
    }

    /// <summary>Returns the node's disk measurement from the store's, logging the start or end of its disk pressure.</summary>
    public NodeDiskMeasurement Judge(LedgerStoreDiskUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        var under = _underPressure
            ? usage.UsedBytes > BudgetBytes - _walFileSize || usage.FreeBytes < _minimumFreeBytes + _walFileSize
            : usage.UsedBytes > BudgetBytes || usage.FreeBytes < _minimumFreeBytes;
        if (under != _underPressure)
        {
            _underPressure = under;
            if (under)
            {
                LogPressureStarted(_logger, _node, usage.UsedBytes, BudgetBytes, usage.FreeBytes, _minimumFreeBytes);
            }
            else
            {
                LogPressureEnded(_logger, _node, usage.UsedBytes, BudgetBytes, usage.FreeBytes, _minimumFreeBytes);
            }
        }

        return new NodeDiskMeasurement(usage.UsedBytes, usage.FreeBytes, under);
    }

    [LoggerMessage(EventId = 6237, Level = LogLevel.Warning, Message = "Node {Node} is under disk pressure: {Used} bytes in its LedgerStore against a budget of {Budget}, {Free} bytes free on its least free volume against a minimum of {Minimum}. Its primaries and the primaries of the ledgers it replicates are throttled.")]
    private static partial void LogPressureStarted(ILogger logger, NodeId node, long used, long budget, long free, long minimum);

    [LoggerMessage(EventId = 6238, Level = LogLevel.Information, Message = "Node {Node}'s disk pressure ended: {Used} bytes in its LedgerStore against a budget of {Budget}, {Free} bytes free on its least free volume against a minimum of {Minimum}.")]
    private static partial void LogPressureEnded(ILogger logger, NodeId node, long used, long budget, long free, long minimum);
}

/// <summary>A measurement of the node's disk, as its policy judged it (orleans.md §5.9).</summary>
/// <param name="UsedBytes">The bytes of the node's LedgerStore files.</param>
/// <param name="FreeBytes">The least free space of the volumes holding the store.</param>
/// <param name="UnderPressure">Whether the node is under disk pressure.</param>
internal readonly record struct NodeDiskMeasurement(long UsedBytes, long FreeBytes, bool UnderPressure);
