using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Archive.Logs.Control;

/// <summary>
/// A drain's answer (archive-logs.md §2.3, conventions.md §15): <see cref="Drained"/>, the ledger's segments complete
/// below a slot; <see cref="OpenSegment"/>, those complete below the start of a segment open at the drain's bound;
/// <see cref="Complete"/>, every segment of the closed ledger complete; <see cref="Waiting"/>, an older overlapping ledger
/// is listed; or <see cref="Dropped"/>, the ledger has left the map.
/// </summary>
public readonly union ArchiveLogDrainOutcome(
    ArchiveLogDrainOutcome.Drained,
    ArchiveLogDrainOutcome.OpenSegment,
    ArchiveLogDrainOutcome.Complete,
    ArchiveLogDrainOutcome.Waiting,
    ArchiveLogDrainOutcome.Dropped) : IEquatable<ArchiveLogDrainOutcome>
{
    /// <inheritdoc/>
    public bool Equals(ArchiveLogDrainOutcome other) => Equals(Value, other.Value);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ArchiveLogDrainOutcome other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;

    /// <inheritdoc/>
    public override string ToString() => Value?.ToString() ?? "default";

    /// <summary>
    /// Every segment of the ledger which starts below <paramref name="Below"/> is complete: closed, and every key's write
    /// held (LOG-I3). <paramref name="Below"/> is the start of the first segment not yet complete, which starts at or past
    /// the drain's bound, or whose drain met backpressure.
    /// </summary>
    /// <param name="Below">The start of the first segment not yet complete.</param>
    public sealed record Drained(Slot Below);

    /// <summary>
    /// Every segment of the ledger which starts below <paramref name="From"/> is complete, and the segment which starts at
    /// <paramref name="From"/> is open at the drain's bound: no decided batch below the bound starts a later segment, and
    /// the bound is not the closed ledger's <c>T</c>. Its keys were not written; a later drain takes it once a later
    /// segment starts below a tail it learns.
    /// </summary>
    /// <param name="From">The open segment's start, the first segment not yet complete.</param>
    public sealed record OpenSegment(Slot From);

    /// <summary>
    /// The ledger is closed and every segment below its <c>T</c> is complete: the caller submits its drop
    /// (archive-logs.md §2.4).
    /// </summary>
    public sealed record Complete
    {
        private Complete()
        {
        }

        /// <summary>Gets the one value.</summary>
        public static Complete Instance { get; } = new();
    }

    /// <summary>The ledger map lists <paramref name="Older"/>, a ledger with a lower ID whose range overlaps the drained one; the drain wrote nothing.</summary>
    /// <param name="Older">The older ledger.</param>
    public sealed record Waiting(LedgerId Older);

    /// <summary>
    /// The ledger has left the map: a ledger map value shows it dropped, below its <c>NextLedgerId</c> and not listed, so
    /// a drain completed it, or this worker's drop of it applied while the process's view still lists it.
    /// </summary>
    public sealed record Dropped
    {
        private Dropped()
        {
        }

        /// <summary>Gets the one value.</summary>
        public static Dropped Instance { get; } = new();
    }
}
