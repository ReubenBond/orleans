using System.Buffers;

namespace Orleans.Durable.Storage.Engine.Archive.Logs;

/// <summary>
/// An archive-log store (archive-logs.md §3): per-key logs of generations, each opened once by an opener with its base
/// and extended by append runs of packed units, every write conditioned on the position it starts from. It is stateless:
/// each call stands alone. Providers: <c>MemoryArchiveLogStore</c>, Azure's model in process; <c>ChunkArchiveLogStore</c>, the
/// chunk layout over any object store; and <c>AzureArchiveLogStore</c> in <c>Orleans.Durable.Storage.AzureStorage</c>.
/// </summary>
/// <remarks>
/// <para>
/// Content crosses as <see cref="ReadOnlySequence{T}"/>s, one per unit or base, which the store reads only during the
/// call; its caller keeps the buffers behind them for retries (conventions.md §1). Each write carries the store's
/// transport checksum, which the store computes from the content it is given.
/// </para>
/// <para>
/// A call which throws leaves its outcome unknown: each unit's condition lets it land at most once, at its position, and a
/// retry of a landed unit fails its condition. <see cref="ArchiveLogWriteOutcome.ConditionFailed"/> asserts nothing about
/// why: its caller reads the log's properties and classifies them (archive-logs.md §8.3). Every answer reflects durable
/// state (conventions.md §13).
/// </para>
/// <para>
/// <b>The caller's demand: one write per token.</b> Every write a caller issues from one log's position is identical in
/// kind, opener, content, and packing. A segment fixes each key's write, its commands and the token it starts from
/// (archive-logs.md §2.3); the packing is a pure function of the layout the store's record fixes (HI-I3); and each
/// defining row stores its key's token chain (JS-I10), so racing drains, stragglers, and retries all issue that one write.
/// The store's conditions fence each write by its position and generation, not by its content, so two different writes
/// from one position would both seem to land; the store checks what it cheaply can, an opener's base against the stored
/// one, and throws <see cref="ArchiveLogCorruptException"/> on a difference.
/// </para>
/// </remarks>
public interface IArchiveLogStore
{
    /// <summary>Gets the store's layout: its packing, its limits, and its name rule, which the store's record holds.</summary>
    ArchiveLogLayout Layout { get; }

    /// <summary>
    /// Returns whether the store can form the log name <paramref name="name"/> under its layout's
    /// <see cref="ArchiveLogLayout.NameRule"/>, as one name that no other aliases; a pure function of the name and the rule.
    /// </summary>
    bool AdmitsName(string name);

    /// <summary>
    /// Reads the log <paramref name="name"/> as of one linearizable state: <see cref="ArchiveLogRead.Absent"/> when it is
    /// absent; <see cref="ArchiveLogRead.Stale"/> when it changed during the read or an object it names is gone; otherwise
    /// <see cref="ArchiveLogRead.Read"/>, whose content its caller owns and disposes.
    /// </summary>
    /// <exception cref="ArchiveLogCorruptException">The log's objects break their format, disagree with each other, or name another key.</exception>
    /// <exception cref="NotSupportedException">The log's content would pass <see cref="ArchiveLogRead.Read.MaxContentLength"/> bytes.</exception>
    ValueTask<ArchiveLogRead> ReadAsync(ArchiveLogName name, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the log's position and its current generation's opener, as of one linearizable state; null when the log is
    /// absent.
    /// </summary>
    /// <exception cref="ArchiveLogCorruptException">The log's objects break their format or name another key.</exception>
    ValueTask<ArchiveLogProperties?> GetPropertiesAsync(ArchiveLogName name, CancellationToken cancellationToken);

    /// <summary>
    /// Appends <paramref name="run"/>, packed units, unit by unit, each only while the log is at generation
    /// <c>from.Generation</c> with the length at which the unit starts. A run from <see cref="ArchiveLogPosition.Origin"/>
    /// first creates the log at generation 1, only when it is absent, and then appends as a run on generation 1 from length
    /// 0; a run from any other position never creates the log.
    /// </summary>
    /// <param name="name">The log.</param>
    /// <param name="from">The write's starting position, <see cref="ArchiveLogPosition.Origin"/> for a key's first write.</param>
    /// <param name="run">The units, each non-empty and at most <see cref="ArchiveLogLayout.MaxAppendBytes"/>, read only during the call.</param>
    /// <param name="cancellationToken">Stops the caller's wait; the outcome is then unknown.</param>
    /// <returns>
    /// <see cref="ArchiveLogWriteOutcome.Applied"/> once every unit is durable; <see cref="ArchiveLogWriteOutcome.ConditionFailed"/>
    /// when a unit's condition fails, having landed the units before it and nothing after.
    /// </returns>
    /// <exception cref="ArchiveLogRefusedException">The store refused a unit no retry can land.</exception>
    /// <exception cref="ArchiveLogCorruptException">The store's answer contradicts the run's packing.</exception>
    ValueTask<ArchiveLogWriteOutcome> AppendAsync(ArchiveLogName name, ArchiveLogPosition from, IReadOnlyList<ReadOnlySequence<byte>> run, CancellationToken cancellationToken);

    /// <summary>
    /// Creates generation <c>from.Generation + 1</c> with base <paramref name="content"/> and the opener's metadata, only
    /// once: its base first, only when absent, and otherwise checked against the stored one; then its empty write-ahead
    /// object, only while the log's current generation is <c>from.Generation</c> (absent, when that is 0).
    /// </summary>
    /// <param name="name">The log.</param>
    /// <param name="from">The write's starting position, <see cref="ArchiveLogPosition.Origin"/> for a key's first write.</param>
    /// <param name="opener">The opener's kind and tombstone.</param>
    /// <param name="content">The generation's base, read only during the call.</param>
    /// <param name="cancellationToken">Stops the caller's wait; the outcome is then unknown.</param>
    /// <returns>
    /// <see cref="ArchiveLogWriteOutcome.Applied"/> once both are durable; <see cref="ArchiveLogWriteOutcome.ConditionFailed"/>
    /// when the write-ahead object's condition fails. On the chunk layout the base is the generation's only object: an
    /// existing base answers <see cref="ArchiveLogWriteOutcome.ConditionFailed"/> after its check, and
    /// <see cref="ArchiveLogWriteOutcome.Applied"/> means the opener's base is the highest.
    /// </returns>
    /// <exception cref="ArchiveLogCorruptException">The stored base differs from <paramref name="content"/>: another write from the same position.</exception>
    /// <exception cref="ArchiveLogRefusedException">The store refused the base or the generation, which no retry can make.</exception>
    ValueTask<ArchiveLogWriteOutcome> OpenAsync(ArchiveLogName name, ArchiveLogPosition from, ArchiveLogOpener opener, ReadOnlySequence<byte> content, CancellationToken cancellationToken);

    /// <summary>
    /// Sweeper only. Lists one entry per stored generation base, with the key hash it records, in the ordinal order of
    /// the bases' names: <c>{log}/{g:D20}</c> on Azure and in memory, <c>{log}/{g:D20}/base-{h:x16}</c> on the chunk
    /// layout (archive-logs.md §7.4): every one which existed throughout the
    /// listing, and possibly ones created or deleted during it, which are hints. Objects whose names do not parse strictly
    /// are not listed; a base whose recorded key hash does not parse is listed with none. A log's bases need not be
    /// adjacent, since a log's name may extend another's with <c>/</c>, but a log's bases come before any name its own
    /// <c>{log}/</c> does not begin, so a sweep streams the listing in memory bounded by the names' nesting. The listing
    /// comes in pages of the store's size, each with a continuation, so a sweeper may list one slice per sweep
    /// (archive-logs.md §8.5): a continuation names a position in the ordinal order of the bases' names, after the page's
    /// last base and at or before the next one's; it stays valid indefinitely, whatever bases are created or deleted
    /// meanwhile, and a listing from it returns the bases whose names lie after that position. Only the last page's
    /// continuation is <see langword="null"/>, and a listing of no bases is one empty page.
    /// </summary>
    /// <param name="continuation">A page's continuation, after whose position to list, or <see langword="null"/> from the first base.</param>
    /// <param name="cancellationToken">Stops the listing.</param>
    IAsyncEnumerable<ArchiveLogGenerationPage> ListGenerationsAsync(string? continuation, CancellationToken cancellationToken);

    /// <summary>
    /// Sweeper only. Deletes the base of <paramref name="generation"/> of the log <paramref name="log"/>, and on the
    /// chunk layout its chunks; they are absent afterwards, and an absent base succeeds. The caller has observed the log's current generation above
    /// <paramref name="generation"/> (LOG-I2).
    /// </summary>
    ValueTask DeleteGenerationAsync(string log, ulong generation, CancellationToken cancellationToken);
}
