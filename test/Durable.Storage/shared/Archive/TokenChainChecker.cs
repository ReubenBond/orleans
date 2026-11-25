using System.Collections.Immutable;
using System.Globalization;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// The chain checker (HI-I4, JS-I10; verification.md §3.2): replays the archive store's <c>Plan</c> over a key's writes,
/// across segments and ledgers, through splits and merges, and checks every stored token against it, as archive.md §8.1
/// step 4 states. Each write is held by the prefix in none, part, or all of its commands. A write held in none starts from
/// the prefix's token, the earliest such, or from <c>Plan</c> of the write before it, and its defining row's stored
/// version follows the key's previous row's, or a rebase, which the caller allows by naming the dropped ledgers: the first
/// count of the version base <c>Plan</c> replays, the store's, once the previous row's ledger has dropped; a write held in
/// part lies after every write held whole, at <c>Plan</c> of its held part, the prefix's token; and no write is held after
/// one not held whole. It reads only the rows it is given
/// and the store's pure members, and holds no model of its own.
/// </summary>
internal static class TokenChainChecker
{
    /// <summary>
    /// Checks a key's live rows, in tag order, each with whether the prefix holds it, against the chain from
    /// <paramref name="anchor"/>, the prefix's token, or <c>Origin</c> for <c>Absent</c> or for rows from the key's first.
    /// </summary>
    /// <param name="store">The store, whose <c>Plan</c> the chain replays.</param>
    /// <param name="anchor">The prefix's token, or <c>Origin</c>.</param>
    /// <param name="rows">The key's rows, in tag order.</param>
    /// <param name="dropped">
    /// The ledgers dropped so far, which allow a rebase after a row of theirs to the version base the chain replays, which a
    /// store whose version base is its token gives (journals.md §8.3), or none, which allows no rebase.
    /// </param>
    /// <param name="forgotten">
    /// Whether the LSM forgot the key entirely after an accepted <c>Expired</c>, so a write after an <c>Expired</c> may
    /// start a new lifetime from <c>Origin</c> (JS-I10).
    /// </param>
    /// <returns>The first violation, in words, or <see langword="null"/> when the chain holds.</returns>
    public static string? Check(IArchiveStore store, RowToken anchor, IReadOnlyList<ChainRow> rows, IReadOnlySet<LedgerId>? dropped = null, bool forgotten = false)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(rows);
        RowToken? expected = null;
        ChainRow? previous = null;
        var sawNotWhollyHeld = false;
        for (var start = 0; start < rows.Count;)
        {
            var end = start + 1;
            while (end < rows.Count && rows[end].Segment == rows[start].Segment)
            {
                end++;
            }

            var segment = rows.Skip(start).Take(end - start).ToArray();
            var before = previous;
            previous = segment[^1];
            start = end;
            if (segment.Any(row => row.Token != segment[0].Token))
            {
                return Invariant($"HI-I4: the key's rows in segment {Show(segment[0].Segment)} store two tokens");
            }

            var write = WriteOf(segment);
            if (HeldOf(segment, write) is not { } held)
            {
                return Invariant($"HI-I4: the rows the prefix holds of segment {Show(write.Segment)} are not a prefix of its write's commands");
            }

            if (held == 0)
            {
                var from = expected ?? anchor;
                if (forgotten && before is { } expired && IsExpired(expired) && segment[0].Version == new RowVersion(default, 1) && write.Token == default)
                {
                    // The LSM forgot the key after its Expired: a new lifetime from Origin.
                    (expected, sawNotWhollyHeld) = (store.Plan(write), true);
                    continue;
                }

                if (write.Token != from)
                {
                    return Invariant($"HI-I4: the write of segment {Show(write.Segment)} stores token {write.Token}, not {from}, which Plan replayed from the anchor {anchor} gives");
                }

                if (before is { } last
                    && !IsRebase(segment[0].Version, last, from, dropped)
                    && (segment[0].Version.Count == 0 || new RowVersion(segment[0].Version.Base, segment[0].Version.Count - 1) != last.Version))
                {
                    return Invariant($"HI-I4: the defining row {segment[0].Tag} stores the version {segment[0].Version}, which does not follow its key's previous row's {last.Version}");
                }

                (expected, sawNotWhollyHeld) = (store.Plan(write), true);
                continue;
            }

            if (sawNotWhollyHeld)
            {
                return Invariant($"HI-I4: the prefix holds the write of segment {Show(write.Segment)} after an earlier write it does not hold whole");
            }

            if (held == write.Commands.Length)
            {
                expected = null;
                continue;
            }

            var part = store.Plan(new ArchiveWrite(write.Segment, write.Token, write.Commands[..held], write.Superseded));
            if (part != anchor)
            {
                return Invariant($"HI-I4: the prefix holds the write of segment {Show(write.Segment)} through command {held}, at {part}, but its token is {anchor}");
            }

            (expected, sawNotWhollyHeld) = (store.Plan(write), true);
        }

        return null;
    }

    /// <summary>
    /// Returns whether a defining row's version follows a rebase (journals.md §8.3, JS-I10): once the ledger of its key's
    /// previous row has dropped, the first count of the version base the chain replays, <paramref name="from"/>, which
    /// differs from the previous row's base.
    /// </summary>
    private static bool IsRebase(RowVersion version, ChainRow previous, RowToken from, IReadOnlySet<LedgerId>? dropped) =>
        dropped is not null && dropped.Contains(previous.Tag.Ledger) && version.Count == 1 && version.Base == from && version.Base != previous.Version.Base;

    /// <summary>Returns whether a row is an <c>Expired</c>'s: a <c>Delete</c> with no tombstone.</summary>
    private static bool IsExpired(ChainRow row) => row.Kind == RowKind.Delete && row.BodyLength == 0;

    /// <summary>Returns the key's write of one segment's rows, in tag order (archive.md §2.1).</summary>
    public static ArchiveWrite WriteOf(IReadOnlyList<ChainRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var opener = OpenerOf(rows);
        var commands = rows.Skip(opener).Select(static row => row.Kind switch
        {
            RowKind.Append => ArchiveCommand.Append(row.BodyLength),
            RowKind.Replace => ArchiveCommand.Replace(row.BodyLength),
            RowKind.Delete => ArchiveCommand.Delete(row.Tombstone),
            RowKind.Message => throw new ArgumentOutOfRangeException(nameof(rows), "RowKind.Message is excluded from archive writes (LG-I15)."),
            _ => throw new ArgumentOutOfRangeException(nameof(rows), "A row's kind is defined."),
        });
        return new ArchiveWrite(rows[0].Segment, rows[0].Token, [.. commands], opener);
    }

    /// <summary>The index of a segment's opener, the last <c>Replace</c> or <c>Delete</c>, or 0 for an append run.</summary>
    private static int OpenerOf(IReadOnlyList<ChainRow> rows)
    {
        var last = 0;
        for (var index = 0; index < rows.Count; index++)
        {
            if (rows[index].Kind is RowKind.Replace or RowKind.Delete)
            {
                last = index;
            }
        }

        return last;
    }

    /// <summary>
    /// Returns how many of the write's commands the prefix holds, or null when its held rows are not a prefix of them:
    /// a row before the opener is held exactly when the opener is.
    /// </summary>
    private static int? HeldOf(ChainRow[] rows, ArchiveWrite write)
    {
        var opener = rows.Length - write.Commands.Length;
        var held = rows.Skip(opener).TakeWhile(static row => row.Held).Count();
        return rows.Skip(opener + held).Any(static row => row.Held) || rows.Take(opener).Any(row => row.Held != held > 0) ? null : held;
    }

    private static string Show(LedgerSegmentId segment) => Invariant($"({segment.Ledger}, {segment.Start})");

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// A key's row as the chain checker reads it: its tag, segment, kind, body length, a <c>Delete</c>'s body, its stored
/// token, its version, and whether the prefix holds it.
/// </summary>
internal readonly record struct ChainRow(Tag Tag, LedgerSegmentId Segment, RowKind Kind, int BodyLength, ReadOnlyMemory<byte> Tombstone, RowToken Token, RowVersion Version, bool Held)
{
    /// <summary>Returns the chain checker's view of a scanned row.</summary>
    public static ChainRow Of(in ArchiveRow row) =>
        new(row.Tag, row.Segment, row.Kind, row.BodyLength, row.Kind == RowKind.Delete && !row.IsHeld ? row.Body.ToArray() : ReadOnlyMemory<byte>.Empty, row.Token, row.Version, row.IsHeld);

    /// <summary>Returns the chain checker's view of a read's rows.</summary>
    public static ImmutableArray<ChainRow> Of(ReadOnlySpan<ArchiveRow> rows)
    {
        var builder = ImmutableArray.CreateBuilder<ChainRow>(rows.Length);
        foreach (var row in rows)
        {
            builder.Add(Of(row));
        }

        return builder.MoveToImmutable();
    }
}
