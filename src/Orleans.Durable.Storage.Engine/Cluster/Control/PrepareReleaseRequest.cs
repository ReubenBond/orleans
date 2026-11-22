using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Cluster.Control;

/// <summary>
/// A request from the cluster controller asking an outgoing primary to cooperatively drain in-flight writes and stop
/// admitting new batches for <see cref="Ledger"/> over <see cref="Range"/> within <see cref="Deadline"/> before a
/// planned replacement handoff (orleans.md §5.6, §5.7, OR-I24).
/// </summary>
internal sealed record PrepareReleaseRequest
{
    /// <summary>Initializes a new instance of the <see cref="PrepareReleaseRequest"/> class.</summary>
    /// <param name="ledger">The open ledger the primary is asked to release.</param>
    /// <param name="range">The key range of <paramref name="ledger"/>.</param>
    /// <param name="deadline">Maximum time the primary may spend draining before answering.</param>
    /// <exception cref="ArgumentException"><paramref name="ledger"/> is the default.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="deadline"/> is not positive.</exception>
    public PrepareReleaseRequest(LedgerId ledger, KeyRange range, TimeSpan deadline)
    {
        if (ledger == default)
        {
            throw new ArgumentException("A prepare-release request names an open ledger.", nameof(ledger));
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(deadline, TimeSpan.Zero);
        Ledger = ledger;
        Range = range;
        Deadline = deadline;
    }

    /// <summary>Gets the open ledger to drain and stop admitting.</summary>
    public LedgerId Ledger { get; }

    /// <summary>Gets the key range of <see cref="Ledger"/>.</summary>
    public KeyRange Range { get; }

    /// <summary>Gets the maximum drain duration.</summary>
    public TimeSpan Deadline { get; }
}
