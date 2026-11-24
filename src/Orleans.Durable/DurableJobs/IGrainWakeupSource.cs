using System;

namespace Orleans.Durable.Jobs;

/// <summary>
/// A grain-scoped component whose pending work requires periodic or scheduled wakeups coordinated with journal writes
/// (orleans.md §5.15).
/// </summary>
public interface IGrainWakeupSource
{
    /// <summary>Gets the durable job name that wakes this source.</summary>
    string JobName { get; }

    /// <summary>
    /// Returns the next UTC time at which this source has pending work to execute, or <see langword="null"/> when no
    /// wakeup is required.
    /// </summary>
    /// <param name="now">The current UTC time on the Durable Jobs clock.</param>
    DateTimeOffset? GetNextDueTime(DateTimeOffset now);
}
