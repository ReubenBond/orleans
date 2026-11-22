using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Journals;

/// <summary>
/// A <c>ReplaceRequired</c> as admission decided it, which the white-box oracle checks (journals.md §8.1 step 4, §10):
/// the <c>Append</c>, and what it was decided from: the admission's applied position and the key's in-flight entries.
/// </summary>
/// <param name="Key">The entry's row key.</param>
/// <param name="RecordCrc">The CRC-32C of the entry's record, which identifies the write.</param>
/// <param name="Expected">The entry's expected version.</param>
/// <param name="PayloadLength">The <c>Append</c>'s payload length.</param>
/// <param name="AppliedBelow">The slot below which the admission had applied every batch of its own.</param>
/// <param name="InFlight">The number of the key's entries admitted and not yet landed.</param>
internal sealed record JournalReplaceRequired(JournalRowKey Key, uint RecordCrc, JournalVersion Expected, long PayloadLength, Slot AppliedBelow, int InFlight);
