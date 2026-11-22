using Orleans.Durable.Storage.Engine.Journals;

namespace Orleans.Durable.Storage;

/// <summary>
/// The answer of a journal write on the node (<see cref="LocalJournals.WriteAsync"/>, orleans.md §5.5, conventions.md §15):
/// the Journals' <see cref="JournalWriteOutcome"/> cases, with the node's own <see cref="Throttled"/>, which it answers
/// before the Journals while the key's ledger is throttled, and <see cref="Unavailable"/>, which it answers for the
/// Journals' <see cref="JournalStoreUnavailableException"/>. Both the router's local path and a forwarded write reach the
/// Journals through it, so a throttled primary answers alike on either path (OR-I10).
/// </summary>
internal readonly union LocalJournalWriteOutcome(
    JournalWriteOutcome.Accepted,
    JournalWriteOutcome.Rejected,
    JournalWriteOutcome.ReplaceRequired,
    JournalMoved,
    JournalWriteOutcome.Fenced,
    JournalWriteOutcome.Ambiguous,
    LocalJournalWriteOutcome.Throttled,
    LocalJournalWriteOutcome.Unavailable) : IEquatable<LocalJournalWriteOutcome>
{
    /// <inheritdoc/>
    public bool Equals(LocalJournalWriteOutcome other) => Equals(Value, other.Value);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is LocalJournalWriteOutcome other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;

    /// <inheritdoc/>
    public override string ToString() => Value?.ToString() ?? string.Empty;

    /// <summary>
    /// The primary's node answered the write before its Journals while the key's ledger is throttled (orleans.md §5.9,
    /// OR-I10): the write had no effect, and the router resends it to the same primary with backoff.
    /// </summary>
    public sealed record Throttled
    {
        private Throttled()
        {
        }

        /// <summary>Gets the one answer.</summary>
        public static Throttled Instance { get; } = new();
    }

    /// <summary>
    /// The Journals failed the write with <see cref="JournalStoreUnavailableException"/>: the key's state could not load
    /// while its ledger's quorum was out of reach, so the entry was never proposed and the write had no effect (journals.md
    /// §6, OR-I7). The router re-reads the map and resends it with backoff.
    /// </summary>
    public sealed record Unavailable
    {
        private Unavailable()
        {
        }

        /// <summary>Gets the one answer.</summary>
        public static Unavailable Instance { get; } = new();
    }
}
