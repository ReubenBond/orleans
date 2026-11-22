using System.Globalization;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Archive.Control;

/// <summary>
/// A closed segment whose rows the drain cannot hold within its process's drain memory, the store's
/// <c>DrainMemoryBytes</c>, even alone (archive-lsm.md §8.1, archive-logs.md §8.1): the segment drains whole or not at
/// all, so its ledger cannot drain under the budget, and an operator raises it or the segment's writer wrote rows past
/// what drains were sized for. It names the ledger and the segment, a defined error the composition alerts on rather
/// than an <see cref="OutOfMemoryException"/>.
/// </summary>
public sealed class DrainMemoryExceededException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="DrainMemoryExceededException"/> class.</summary>
    public DrainMemoryExceededException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="DrainMemoryExceededException"/> class.</summary>
    public DrainMemoryExceededException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="DrainMemoryExceededException"/> class.</summary>
    public DrainMemoryExceededException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="DrainMemoryExceededException"/> class for a segment.</summary>
    /// <param name="segment">The segment, its ledger and start.</param>
    /// <param name="charged">The bytes the drain charged for the segment's rows when it passed the budget.</param>
    /// <param name="budget">The process's drain memory.</param>
    public DrainMemoryExceededException(LedgerSegmentId segment, long charged, long budget)
        : base(string.Create(
            CultureInfo.InvariantCulture,
            $"The segment of ledger {segment.Ledger} which starts at slot {segment.Start} needs more than the drain memory of {budget} bytes: its rows charged {charged} bytes before the scan reached its end."))
    {
        Segment = segment;
        Charged = charged;
        Budget = budget;
    }

    /// <summary>Gets the segment, its ledger and start.</summary>
    public LedgerSegmentId Segment { get; }

    /// <summary>Gets the bytes the drain charged for the segment's rows when it passed the budget.</summary>
    public long Charged { get; }

    /// <summary>Gets the process's drain memory.</summary>
    public long Budget { get; }
}
