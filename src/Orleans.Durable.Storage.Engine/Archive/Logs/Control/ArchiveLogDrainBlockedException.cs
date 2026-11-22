using System.Globalization;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Archive.Logs.Control;

/// <summary>Why a ledger's drain is blocked (archive-logs.md §6), which the operator procedure takes in turn.</summary>
public enum ArchiveLogDrainBlockedCause
{
    /// <summary>No cause: an exception made without one, which no drain throws.</summary>
    Unspecified,

    /// <summary>
    /// The key's log is corrupt, its predecessor is missing although every older ledger dropped, or its write's stored
    /// token breaks its key's chain (HI-I4).
    /// </summary>
    Corrupt,

    /// <summary>
    /// A decided batch of the segment is lost, which makes the rest of the segment unresolvable, so every key whose write
    /// the segment's remainder would decide blocks.
    /// </summary>
    Damaged,

    /// <summary>The store refuses the key's write for a reason no retry removes.</summary>
    Refused,

    /// <summary>
    /// The ledger's highest drain marker breaks its format, names an end not past its start or past <c>T</c>, names a slot
    /// which starts no segment, or vanished while the ledger is still listed; the remedy is to delete it, after which the
    /// drain resumes from the marker below it.
    /// </summary>
    CorruptMarker,
}

/// <summary>
/// A drain stopped where no retry makes it succeed (archive-logs.md §6): it holds its ledger's drain, its drop, and its
/// descendants' drains until an operator acts. It names the ledger, the segment, and the cause, and, for a blocked key,
/// the key and its log; a <see cref="ArchiveLogDrainBlockedCause.Damaged"/> segment names no key, since every key its lost
/// batch and the batches after it would decide blocks, and the drain cannot resolve which those are, and a
/// <see cref="ArchiveLogDrainBlockedCause.CorruptMarker"/> names the marker's segment. Every attempt of a blocked drain
/// throws it again, so the composition reports the cause at each.
/// </summary>
public sealed class ArchiveLogDrainBlockedException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ArchiveLogDrainBlockedException"/> class.</summary>
    public ArchiveLogDrainBlockedException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ArchiveLogDrainBlockedException"/> class.</summary>
    public ArchiveLogDrainBlockedException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ArchiveLogDrainBlockedException"/> class.</summary>
    public ArchiveLogDrainBlockedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    private ArchiveLogDrainBlockedException(ReadOnlyMemory<byte>? key, ArchiveLogName? log, LedgerSegmentId segment, ArchiveLogDrainBlockedCause cause, Exception innerException)
        : base(Describe(key, log, segment, cause, innerException), innerException)
    {
        Key = key;
        Log = log;
        Segment = segment;
        Cause = cause;
    }

    /// <summary>Gets the blocked key, or <see langword="null"/> when the drain blocks at no one key.</summary>
    public ReadOnlyMemory<byte>? Key { get; }

    /// <summary>Gets the blocked key's log, or <see langword="null"/> when the drain blocks at no one key.</summary>
    public ArchiveLogName? Log { get; }

    /// <summary>Gets the segment at which the drain stops: its ledger and start.</summary>
    public LedgerSegmentId Segment { get; }

    /// <summary>Gets why no retry makes the drain succeed.</summary>
    public ArchiveLogDrainBlockedCause Cause { get; }

    /// <summary>Returns the exception of a key whose write no retry makes succeed.</summary>
    /// <param name="key">The key, which the exception copies.</param>
    /// <param name="log">The key's log.</param>
    /// <param name="segment">The segment.</param>
    /// <param name="cause">Why: <see cref="ArchiveLogDrainBlockedCause.Corrupt"/> or <see cref="ArchiveLogDrainBlockedCause.Refused"/>.</param>
    /// <param name="innerException">The failure which showed it.</param>
    public static ArchiveLogDrainBlockedException ForKey(ReadOnlySpan<byte> key, ArchiveLogName log, LedgerSegmentId segment, ArchiveLogDrainBlockedCause cause, Exception innerException)
    {
        ArgumentNullException.ThrowIfNull(log);
        return cause is ArchiveLogDrainBlockedCause.Corrupt or ArchiveLogDrainBlockedCause.Refused
            ? new(key.ToArray(), log, segment, cause, innerException)
            : throw new ArgumentOutOfRangeException(nameof(cause), cause, "A key blocks for corruption or a refusal.");
    }

    /// <summary>Returns the exception of a segment whose batch is lost or breaks its format, which names no key.</summary>
    public static ArchiveLogDrainBlockedException ForDamagedSegment(LedgerSegmentId segment, Exception innerException) =>
        new(null, null, segment, ArchiveLogDrainBlockedCause.Damaged, innerException);

    /// <summary>Returns the exception of a drain marker which is corrupt, naming the segment it marks.</summary>
    public static ArchiveLogDrainBlockedException ForCorruptMarker(LedgerSegmentId segment, Exception innerException) =>
        new(null, null, segment, ArchiveLogDrainBlockedCause.CorruptMarker, innerException);

    private static string Describe(ReadOnlyMemory<byte>? key, ArchiveLogName? log, LedgerSegmentId segment, ArchiveLogDrainBlockedCause cause, Exception innerException) =>
        key is not { } blocked
            ? string.Create(CultureInfo.InvariantCulture, $"The drain of ledger {segment.Ledger} is blocked at its segment from slot {segment.Start} ({cause}): {innerException.Message}")
            : string.Create(CultureInfo.InvariantCulture, $"The drain of ledger {segment.Ledger} is blocked at its segment from slot {segment.Start} by key {Convert.ToHexStringLower(blocked.Span)}, log '{log}' ({cause}): {innerException.Message}");
}
