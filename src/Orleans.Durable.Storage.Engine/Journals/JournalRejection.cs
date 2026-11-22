using System.Collections.Immutable;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Journals;

/// <summary>
/// A rejection as admission decided it, which the white-box rejection oracle checks (journals.md §10): the entry, and
/// what it was decided from: its key's applied state, at the admission's applied position, and the key's in-flight
/// entries, in admission order.
/// </summary>
/// <param name="Key">The entry's row key.</param>
/// <param name="RecordCrc">The CRC-32C of the entry's record, which identifies the write.</param>
/// <param name="Kind">The entry's kind.</param>
/// <param name="Expected">The entry's expected version.</param>
/// <param name="Applied">The applied state's version, which the rejection reports.</param>
/// <param name="AppliedDeletedAt">The applied state's tombstone, when it is deleted.</param>
/// <param name="AppliedBelow">The slot below which the admission had applied every batch of its own.</param>
/// <param name="InFlight">The key's entries admitted and not yet landed.</param>
/// <param name="DecidedAt">The admission's clock when it decided.</param>
/// <param name="TombstoneRetention">The retention an <c>Expired</c> is judged by.</param>
/// <param name="RecordedHere">Whether the key has a recorded segment in the admission's ledger (journals.md §8.3).</param>
/// <param name="LoadStartedAt">Where in the admission's sequence the key's latest load started.</param>
/// <param name="AcceptedAt">Where in the admission's sequence the entry was accepted, or 0 when it was rejected as it arrived.</param>
/// <param name="Cold">
/// Whether the entry is an <c>Expired</c> whose key held no loaded warm state, rejected without a load (journals.md §8.6):
/// it reports its own expected version, decided from no applied state.
/// </param>
internal sealed record JournalRejection(
    JournalRowKey Key,
    uint RecordCrc,
    JournalEntryKind Kind,
    JournalVersion Expected,
    JournalVersion Applied,
    DateTimeOffset? AppliedDeletedAt,
    Slot AppliedBelow,
    ImmutableArray<JournalRejection.InFlightEntry> InFlight,
    DateTimeOffset DecidedAt,
    TimeSpan TombstoneRetention,
    bool RecordedHere,
    long LoadStartedAt,
    long AcceptedAt,
    bool Cold)
{
    /// <summary>An in-flight entry of the rejected entry's key.</summary>
    /// <param name="Kind">Its kind.</param>
    /// <param name="Expected">Its expected version.</param>
    internal readonly record struct InFlightEntry(JournalEntryKind Kind, JournalVersion Expected);
}
