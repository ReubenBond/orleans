using System.Diagnostics;
using System.Globalization;

namespace Orleans.Testing.Simulation;

/// <summary>
/// Base class for scheduled lane work.
/// </summary>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
internal abstract class ScheduledItem : IDisposable
{
    private SimulationSchedulerLane? _lane;
    private IDisposable? _registration;
    private bool _disposed;

    /// <summary>
    /// Gets the comparer for ordering scheduled items.
    /// </summary>
    internal static IComparer<ScheduledItem> Comparer { get; } = ScheduledItemComparer.Instance;

    /// <summary>
    /// Gets the absolute time when this item is due.
    /// Set internally by <see cref="SimulationSchedulerLane"/> when the item is scheduled.
    /// </summary>
    internal DateTimeOffset DueTime { get; private set; }

    /// <summary>
    /// Gets the sequence number for ordering items with the same due time.
    /// Set internally by <see cref="SimulationSchedulerLane"/> when the item is scheduled.
    /// </summary>
    internal long SequenceNumber { get; private set; }

    internal abstract string Kind { get; }

    internal abstract string Description { get; }

    /// <summary>
    /// Called by <see cref="SimulationSchedulerLane"/> when the item is added to the queue.
    /// Sets the queue reference, due time, and sequence number.
    /// </summary>
    /// <param name="lane">The scheduler lane this item belongs to.</param>
    /// <param name="dueTime">The absolute time when this item is due.</param>
    /// <param name="sequenceNumber">The sequence number for ordering.</param>
    internal void OnScheduled(SimulationSchedulerLane lane, DateTimeOffset dueTime, long sequenceNumber)
    {
        if (_lane is not null)
        {
            throw new InvalidOperationException("Item has already been scheduled.");
        }

        _lane = lane;
        DueTime = dueTime;
        SequenceNumber = sequenceNumber;
    }

    internal void SetCancellation(IDisposable registration) => _registration = registration;

    internal void OnInvoking()
    {
        _lane = null;
        _registration = null;
    }

    internal void OnRemoved()
    {
        _lane = null;
        _registration = null;
    }

    internal void CancelRegistration()
    {
        IDisposable? registration = _registration;
        OnRemoved();
        registration?.Dispose();
    }

    /// <summary>
    /// Executes the scheduled item's action.
    /// </summary>
    internal abstract void Invoke();

    /// <summary>
    /// Cancels the item by removing it from the queue.
    /// </summary>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Disposes the scheduled item.
    /// </summary>
    /// <param name="disposing">True if called from Dispose(), false if from finalizer.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        _disposed = true;
        if (disposing)
        {
            _lane?.RemoveItem(this);
        }
    }

    private string DebuggerDisplay => string.Create(CultureInfo.InvariantCulture, $"Due={DueTime:HH:mm:ss.fff} Seq={SequenceNumber}");

    /// <summary>
    /// Comparer for ordering <see cref="ScheduledItem"/> instances by due time, then by sequence number.
    /// </summary>
    private sealed class ScheduledItemComparer : IComparer<ScheduledItem>
    {
        /// <summary>
        /// Gets the singleton instance of the comparer.
        /// </summary>
        public static ScheduledItemComparer Instance { get; } = new();

        private ScheduledItemComparer() { }

        /// <inheritdoc />
        public int Compare(ScheduledItem? x, ScheduledItem? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            var dueTimeComparison = x.DueTime.CompareTo(y.DueTime);
            return dueTimeComparison != 0 ? dueTimeComparison : x.SequenceNumber.CompareTo(y.SequenceNumber);
        }
    }
}
