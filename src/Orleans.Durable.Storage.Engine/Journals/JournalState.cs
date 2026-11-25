using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Journals;

/// <summary>
/// A journal's state, the fold of its history (journals.md §2.1, §4.2): its <see cref="CurrentVersion"/>, whether it
/// exists, a tombstone's deletion time, and its content's length. A key with no history has no state; its version is
/// <see cref="JournalVersion.Origin"/>.
/// </summary>
/// <remarks>
/// Journals folds a key's state from its history read (journals.md §8.3) and applies further entries to a state it
/// owns, as a primary's warm state applies its own decided batches; a caller sees only the state's values.
/// </remarks>
public sealed class JournalState
{
    /// <summary>
    /// The share of a journal's base, <c>α</c>, which bytes appended after it may reach before Journals asks for a
    /// <c>Replace</c> (journals.md §8.7).
    /// </summary>
    internal const long ReplaceRequestAlpha = 1;

    /// <summary>
    /// The minimum bytes appended after a journal's base before Journals asks for a <c>Replace</c> by size alone
    /// (64 KiB; journals.md §8.7), so small journals do not snapshot every few hundred bytes.
    /// </summary>
    internal const long ReplaceRequestMinBytes = 64 * 1024;

    /// <summary>
    /// The bytes a state holds, which a primary's warm-state budget counts (journals.md §8.8): the object's 16-byte
    /// header, its version (a 33-byte inline base and an 8-byte count, 48 bytes aligned), its tombstone's 16, its
    /// content's length and base length (16), and its existence (8 aligned).
    /// </summary>
    internal const long EstimatedBytes = 104;

    private long? _deletionTicks;

    private JournalState(JournalVersion version, bool exists, long? deletionTicks, long contentLength, long baseLength)
    {
        Debug.Assert(deletionTicks is null || !exists && contentLength == 0 && baseLength == 0, "A deleted journal neither exists nor holds content.");
        Debug.Assert(baseLength >= 0 && contentLength >= baseLength, "Content length is at least its base length.");
        CurrentVersion = version;
        Exists = exists;
        _deletionTicks = deletionTicks;
        ContentLength = contentLength;
        BaseLength = baseLength;
    }

    /// <summary>Gets the journal's version, which every accepted entry advances.</summary>
    public JournalVersion CurrentVersion { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the journal exists: its latest opener is a <c>Replace</c>, an empty one included,
    /// or an <c>Append</c> follows it.
    /// </summary>
    public bool Exists { get; private set; }

    /// <summary>
    /// Gets the deletion time of the journal's tombstone when it is deleted: its latest opener is a <c>Delete</c> and no
    /// <c>Append</c> follows it; otherwise <see langword="null"/>.
    /// </summary>
    public DateTimeOffset? DeletedAt => _deletionTicks is { } ticks ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;

    /// <summary>Gets the length of the journal's content.</summary>
    public long ContentLength { get; private set; }

    /// <summary>
    /// Gets the length of the journal's base: the content its latest <c>Replace</c> or archive prefix began, or 0 when
    /// the journal has no base (journals.md §8.7).
    /// </summary>
    public long BaseLength { get; private set; }

    /// <summary>Gets the bytes appended after the journal's base (journals.md §8.7).</summary>
    internal long BytesSinceBase => ContentLength - BaseLength;

    /// <summary>
    /// Gets a value indicating whether the state's bytes appended since its base pass the snapshot-request bound
    /// (<c>BytesSinceBase &gt; max(ReplaceRequestMinBytes, α × BaseLength)</c>, with <c>α = 1</c> and
    /// <c>ReplaceRequestMinBytes = 64 KiB</c>; journals.md §8.7).
    /// </summary>
    internal bool IsReplaceRequested => BytesSinceBase > Math.Max(ReplaceRequestMinBytes, ReplaceRequestAlpha * BaseLength);

    /// <summary>Folds a key's history read, its prefix and its live rows (journals.md §8.3).</summary>
    /// <returns>The state, or <see langword="null"/> when the key has no history: <c>Absent</c> and no live row.</returns>
    /// <exception cref="JournalStoreCorruptException">A <c>Delete</c>'s body or the prefix's tombstone is not a valid tombstone.</exception>
    internal static JournalState? Fold(ArchiveRead read) => Fold(read.Prefix, read.Rows);

    /// <summary>
    /// Folds a key's prefix and live rows, which one history read gives (journals.md §8.3). The state starts from the
    /// prefix's: it exists when a <c>Replace</c> opened the prefix's base or the prefix holds content, it is deleted when
    /// the prefix holds a tombstone, and its content is the prefix's. Each live row the prefix does not hold then applies
    /// by its kind and body. The version is the newest live row's, held or not, or, when no listed ledger holds a row of
    /// the key, the rebase to <c>(prefix.VersionBase, 0)</c> (JS-I10).
    /// </summary>
    /// <param name="prefix">The store's prefix of the key, or <see langword="null"/> when the store answered <c>Absent</c>.</param>
    /// <param name="rows">Every live row of the key, in strictly increasing tag order, each marked held or not.</param>
    /// <returns>The state, or <see langword="null"/> when the key has no history: <c>Absent</c> and no live row.</returns>
    /// <exception cref="JournalStoreCorruptException">A <c>Delete</c>'s body or the prefix's tombstone is not a valid tombstone.</exception>
    internal static JournalState? Fold(ArchivePrefix? prefix, ReadOnlySpan<ArchiveRow> rows) => Fold(prefix, rows, JournalLoadMutant.None, staleBase: null);

    /// <summary>
    /// Folds a key's prefix and live rows as <see cref="Fold(ArchivePrefix?, ReadOnlySpan{ArchiveRow})"/> does, or as
    /// <paramref name="mutant"/> breaks it, rebasing to <paramref name="staleBase"/> for <see cref="JournalLoadMutant.RebaseOnStaleToken"/>.
    /// </summary>
    internal static JournalState? Fold(ArchivePrefix? prefix, ReadOnlySpan<ArchiveRow> rows, JournalLoadMutant mutant, RowToken? staleBase)
    {
        if (prefix is null && rows.IsEmpty)
        {
            return null;
        }

        Debug.Assert(prefix is not null || !ContainsHeld(rows), "Only a prefix holds rows: an Absent store holds none.");
        var rebase = rows.IsEmpty || mutant == JournalLoadMutant.RebaseWithLiveRows && prefix is not null;
        var versionBase = mutant == JournalLoadMutant.RebaseOnStaleToken && staleBase is { } stale ? stale : prefix?.VersionBase ?? default;
        var version = rebase ? new JournalVersion(versionBase, 0) : JournalVersion.FromRowVersion(rows[^1].Version);
        var exists = prefix is not null && (prefix.HasBase || prefix.Content.Length > 0);
        var deletionTicks = prefix?.Tombstone is { } tombstone ? (long?)ReadTombstone(tombstone.Span, at: null) : null;
        var prefixLength = prefix?.Content.Length ?? 0;
        var state = new JournalState(version, exists, deletionTicks, prefixLength, prefixLength);
        for (var index = 0; index < rows.Length; index++)
        {
            Debug.Assert(index == 0 || Follows(rows[index - 1].Version, rows[index].Version), "JS-I10 at load: each live row of a key takes the version after its predecessor's.");
            if (!rows[index].IsHeld || mutant == JournalLoadMutant.ReplayWithoutHolds)
            {
                state.ApplyRow(rows[index]);
            }
        }

        return state;
    }

    /// <summary>
    /// Returns whether <paramref name="entry"/> applies to <paramref name="state"/> by its version (journals.md §2.1,
    /// JS-I1): an <c>Append</c>, <c>Replace</c>, or <c>Delete</c> at the state's version, and an <c>Expired</c> at the
    /// version of a deleted state. An <c>Expired</c>'s retention is admission's to judge.
    /// </summary>
    internal static bool AppliesTo(JournalState? state, in JournalEntry entry) =>
        entry.ExpectedVersion == (state?.CurrentVersion ?? JournalVersion.Origin)
        && (entry.Kind != JournalEntryKind.Expired || state?.DeletedAt is not null);

    /// <summary>
    /// Applies <paramref name="entry"/>, decided at <paramref name="version"/>, to <paramref name="state"/>, which its one
    /// owner holds and which it changes and returns, or creates.
    /// </summary>
    /// <returns>The state after the entry.</returns>
    /// <exception cref="JournalStoreCorruptException">
    /// The entry does not apply to <paramref name="state"/>, or <paramref name="version"/> does not follow its version: a
    /// state whose count is the <c>u64</c> maximum is one no row can advance.
    /// </exception>
    internal static JournalState Apply(JournalState? state, in JournalEntry entry, JournalVersion version)
    {
        var current = state?.CurrentVersion ?? JournalVersion.Origin;
        if (!AppliesTo(state, entry) || !Follows(current.ToRowVersion(), version.ToRowVersion()))
        {
            throw new JournalStoreCorruptException(string.Create(
                CultureInfo.InvariantCulture,
                $"The {entry.Kind} at expected version {entry.ExpectedVersion}, decided at version {version}, does not apply to a journal at {Describe(state)}."));
        }

        state ??= new JournalState(current, exists: false, deletionTicks: null, contentLength: 0, baseLength: 0);
        state.CurrentVersion = version;
        switch (entry.Kind)
        {
            case JournalEntryKind.Append:
                state.Append(entry.PayloadLength);
                break;
            case JournalEntryKind.Replace:
                state.Replace(entry.PayloadLength);
                break;
            case JournalEntryKind.Delete:
                state.Delete(entry.DeletionTicks);
                break;
            case JournalEntryKind.Expired:
                state.Expire();
                break;
            default:
                throw new UnreachableException("An entry's kind is defined.");
        }

        return state;
    }

    /// <inheritdoc/>
    public override string ToString() => Describe(this);

    private static string Describe(JournalState? state) => state is null
        ? "version Origin with no history"
        : string.Create(
            CultureInfo.InvariantCulture,
            $"version {state.CurrentVersion}, {(state.Exists ? "existing" : state.DeletedAt is { } deleted ? $"deleted at {deleted:O}" : "absent")}, {state.ContentLength} bytes of content");

    /// <summary>Returns whether <paramref name="after"/> is the version after <paramref name="before"/>: the same base, and the next count.</summary>
    private static bool Follows(RowVersion before, RowVersion after) =>
        after.Base == before.Base && after.Count != 0 && after.Count - 1 == before.Count;

    private static bool ContainsHeld(ReadOnlySpan<ArchiveRow> rows)
    {
        foreach (var row in rows)
        {
            if (row.IsHeld)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reads a tombstone, the archive prefix's or the body of the <c>Delete</c> row at <paramref name="at"/>: 8 bytes of
    /// UTC ticks in the deletion time's range (journals.md §6, §7).
    /// </summary>
    private static long ReadTombstone(ReadOnlySpan<byte> tombstone, Tag? at)
    {
        if (tombstone.Length != JournalEntry.TombstoneLength)
        {
            throw NotATombstone(at, string.Create(CultureInfo.InvariantCulture, $"{tombstone.Length} bytes"));
        }

        var ticks = BinaryPrimitives.ReadInt64LittleEndian(tombstone);
        if (ticks is < 0 or > JournalEntry.MaxTicks)
        {
            throw NotATombstone(at, string.Create(CultureInfo.InvariantCulture, $"deletion time {ticks}"));
        }

        return ticks;
    }

    /// <summary>Reads the tombstone of a <c>Delete</c> row whose body is not empty.</summary>
    private static long ReadTombstone(in ArchiveRow row)
    {
        var body = row.Body.AsReadOnlySequence();
        if (body.Length != JournalEntry.TombstoneLength)
        {
            throw NotATombstone(row.Tag, string.Create(CultureInfo.InvariantCulture, $"{body.Length} bytes"));
        }

        Span<byte> tombstone = stackalloc byte[JournalEntry.TombstoneLength];
        body.CopyTo(tombstone);
        return ReadTombstone(tombstone, row.Tag);
    }

    private static JournalStoreCorruptException NotATombstone(Tag? at, string what)
    {
        var where = at is { } tag
            ? string.Create(CultureInfo.InvariantCulture, $"The Delete row at ({tag.Ledger}, {tag.Slot}, {tag.Index}) has a body")
            : "The archive prefix's tombstone";
        return new JournalStoreCorruptException(string.Create(
            CultureInfo.InvariantCulture,
            $"{where} of {what}: a tombstone is 8 bytes, a deletion time of 0 to {JournalEntry.MaxTicks} ticks."));
    }

    /// <summary>Applies a live row the prefix does not hold by its kind and body (journals.md §4.2).</summary>
    private void ApplyRow(in ArchiveRow row)
    {
        switch (row.Kind)
        {
            case RowKind.Append:
                Append(row.BodyLength);
                break;
            case RowKind.Replace:
                Replace(row.BodyLength);
                break;
            case RowKind.Delete when row.BodyLength == 0:
                Expire();
                break;
            case RowKind.Delete:
                Delete(ReadTombstone(row));
                break;
            case RowKind.Message:
                throw new UnreachableException("RowKind.Message is excluded from journal folds (LG-I15).");
            default:
                throw new UnreachableException("A history read's rows are of defined kinds.");
        }
    }

    private void Append(long bodyLength)
    {
        Exists = true;
        _deletionTicks = null;
        ContentLength += bodyLength;
    }

    /// <summary>Creates a shallow copy of this state, used only by test mutants that keep warm state across claims or apply on admit.</summary>
    internal JournalState Clone() => new(CurrentVersion, Exists, _deletionTicks, ContentLength, BaseLength);

    private void Replace(long bodyLength)
    {
        Exists = true;
        _deletionTicks = null;
        ContentLength = bodyLength;
        BaseLength = bodyLength;
    }

    /// <summary>Ends the content and leaves a tombstone of <paramref name="deletionTicks"/>.</summary>
    private void Delete(long deletionTicks)
    {
        Exists = false;
        _deletionTicks = deletionTicks;
        ContentLength = 0;
        BaseLength = 0;
    }

    /// <summary>Ends the content and any tombstone, leaving the journal absent.</summary>
    private void Expire()
    {
        Exists = false;
        _deletionTicks = null;
        ContentLength = 0;
        BaseLength = 0;
    }
}
