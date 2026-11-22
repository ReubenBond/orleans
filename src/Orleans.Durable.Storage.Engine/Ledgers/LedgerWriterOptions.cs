using System.Globalization;

namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// When a <see cref="LedgerWriter"/> ends its segments, and the bytes its batches created and not yet decided may hold
/// (ledgers.md §4, §8.2, §8.7). Readers depend on none of them, so they may differ between processes and change between
/// a ledger's writers.
/// </summary>
public sealed record LedgerWriterOptions
{
    /// <summary>The default <see cref="MaxSegmentBytes"/>: 64 MiB (ledgers.md, design rationale).</summary>
    public const long DefaultMaxSegmentBytes = 64L << 20;

    /// <summary>The default <see cref="MaxSegmentKeys"/>: 2<sup>18</sup>.</summary>
    public const int DefaultMaxSegmentKeys = 1 << 18;

    /// <summary>The default <see cref="MaxProposalBytes"/>: 64 MiB per ledger.</summary>
    public const long DefaultMaxProposalBytes = 64L << 20;

    /// <summary>
    /// The longest <see cref="MaxSegmentAge"/>: 30 days, within the longest due time a <see cref="TimeProvider"/>'s timer
    /// takes, about 49.7 days.
    /// </summary>
    public static readonly TimeSpan LongestSegmentAge = TimeSpan.FromDays(30);

    /// <summary>
    /// Initializes a new instance of the <see cref="LedgerWriterOptions"/> class.
    /// </summary>
    /// <param name="maxSegmentBytes">The bytes of batches past which a segment ends.</param>
    /// <param name="maxSegmentKeys">The keys a segment defines before it ends.</param>
    /// <param name="maxSegmentAge">The longest a segment stays open, from its first batch with rows.</param>
    /// <param name="maxProposalBytes">The bytes of the batches a writer created and has not yet decided.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A value is not positive, or <paramref name="maxSegmentAge"/> is above <see cref="LongestSegmentAge"/>.
    /// </exception>
    public LedgerWriterOptions(long maxSegmentBytes, int maxSegmentKeys, TimeSpan maxSegmentAge, long maxProposalBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSegmentBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSegmentKeys);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxSegmentAge, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxSegmentAge, LongestSegmentAge);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxProposalBytes);
        MaxSegmentBytes = maxSegmentBytes;
        MaxSegmentKeys = maxSegmentKeys;
        MaxSegmentAge = maxSegmentAge;
        MaxProposalBytes = maxProposalBytes;
    }

    /// <summary>Gets the defaults: 64 MiB segments of at most 2<sup>18</sup> keys open at most an hour, and 64 MiB of proposals.</summary>
    public static LedgerWriterOptions Default { get; } = new(DefaultMaxSegmentBytes, DefaultMaxSegmentKeys, TimeSpan.FromHours(1), DefaultMaxProposalBytes);

    /// <summary>
    /// Gets the bytes of batches past which a segment ends: at a creation, once the segment's batches total at least this
    /// many, the batch starts a new segment, so a segment passes it by at most its last batch.
    /// </summary>
    public long MaxSegmentBytes { get; }

    /// <summary>
    /// Gets the keys a segment defines before it ends, which bounds the writer's dictionary and the key tables of the
    /// views which read its segments.
    /// </summary>
    public int MaxSegmentKeys { get; }

    /// <summary>
    /// Gets the longest a segment stays open, from its first batch with rows; each writer ends its segments at an age drawn
    /// once between nine tenths of it and it (ledgers.md §8.2).
    /// </summary>
    public TimeSpan MaxSegmentAge { get; }

    /// <summary>
    /// Gets the bytes of the batches a writer created and has not yet decided, at least the largest batch its caller
    /// builds; a creation which would pass it waits.
    /// </summary>
    public long MaxProposalBytes { get; }

    /// <inheritdoc/>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"LedgerWriterOptions {{ MaxSegmentBytes = {MaxSegmentBytes}, MaxSegmentKeys = {MaxSegmentKeys}, MaxSegmentAge = {MaxSegmentAge}, MaxProposalBytes = {MaxProposalBytes} }}");
}
