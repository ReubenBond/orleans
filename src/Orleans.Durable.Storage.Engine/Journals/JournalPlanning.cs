using System.Diagnostics;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Journals;

/// <summary>
/// A key's planning state on its primary (journals.md §8.1, JS-I10): its recorded segment and its token there, once, and
/// two incremental <c>Plan</c> folds of its write there. The written fold takes the rows batches hold, and plans each
/// row's token; the admitted fold takes every admitted entry as though it joins the recorded segment, which step 4's
/// candidates judge. With no recorded segment, as after a load whose newest live segment the archive holds part of, or
/// a rebase, the token is the one the key's next write starts from and both folds are empty writes from it. Its one
/// owner, the key's admission, changes it in place under its lock; it is constant in size per key.
/// </summary>
internal sealed class JournalPlanning
{
    /// <summary>
    /// The bytes a key's planning state holds, which a primary's warm-state budget counts (journals.md §8.8): the
    /// object's 16-byte header, its 24-byte segment, its 33-byte inline token, 40 aligned, and two references, and its
    /// two folds, each within <see cref="ArchivePlan.MaxStateBytes"/>.
    /// </summary>
    internal const long EstimatedBytes = 96 + 2 * ArchivePlan.MaxStateBytes;

    private JournalPlanning(LedgerSegmentId? segment, RowToken token, ArchivePlan written, ArchivePlan admitted)
    {
        Segment = segment;
        Token = token;
        Written = written;
        Admitted = admitted;
    }

    /// <summary>Gets the recorded segment, or <see langword="null"/> before the key's next row records one.</summary>
    public LedgerSegmentId? Segment { get; private set; }

    /// <summary>Gets the key's token in the recorded segment, which its defining row there stores; with none, the next write's.</summary>
    public RowToken Token { get; private set; }

    /// <summary>Gets the fold of the key's write in the recorded segment through the rows batches hold.</summary>
    public ArchivePlan Written { get; private set; }

    /// <summary>Gets the fold of the key's write in the recorded segment through every admitted entry.</summary>
    public ArchivePlan Admitted { get; private set; }

    /// <summary>
    /// Returns the planning state a key's history read gives (journals.md §8.3): the newest live segment's identity, its
    /// stored token, and both folds of the key's write there, when no row of it is held; otherwise, as after a rebase or
    /// a load whose newest segment a drain copied, no recorded segment, and empty folds from the read's
    /// <see cref="ArchiveRead.NextToken"/>.
    /// </summary>
    public static JournalPlanning Load(IArchiveStore store, ArchiveRead read) => Load(store, read.Rows, read.NextToken);

    /// <summary>Returns the planning state of a history read as <see cref="Load(IArchiveStore, ArchiveRead)"/> does, or as <paramref name="mutant"/> breaks it.</summary>
    internal static JournalPlanning Load(IArchiveStore store, ArchiveRead read, JournalLoadMutant mutant) =>
        mutant == JournalLoadMutant.NoLazyRoll ? Unrecorded(store, read.NextToken) : Load(store, read);

    /// <summary>Returns the planning state of a history read's rows and next token (see <see cref="Load(IArchiveStore, ArchiveRead)"/>).</summary>
    public static JournalPlanning Load(IArchiveStore store, ReadOnlySpan<ArchiveRow> rows, RowToken nextToken)
    {
        var start = rows.Length;
        while (start > 0 && rows[start - 1].Segment == rows[^1].Segment)
        {
            start--;
        }

        var newest = rows[start..];
        if (newest.IsEmpty || ContainsHeld(newest))
        {
            return Unrecorded(store, nextToken);
        }

        var token = newest[0].Token;
        var planning = new JournalPlanning(newest[0].Segment, token, store.Begin(token), store.Begin(token));
        foreach (var row in newest)
        {
            if (row.SupersededBefore > 0)
            {
                planning.Written.Supersede(row.SupersededBefore);
                planning.Admitted.Supersede(row.SupersededBefore);
            }

            var command = row.ToCommand();
            planning.AddWritten(store, command);
            planning.AddAdmitted(store, command);
        }

        Debug.Assert(planning.Written.End() == nextToken, "The newest write's fold reaches the token the read gives after it (HI-I3).");
        return planning;
    }

    /// <summary>Returns a planning state with no recorded segment, whose next write starts from <paramref name="token"/>.</summary>
    public static JournalPlanning Unrecorded(IArchiveStore store, RowToken token) => new(null, token, store.Begin(token), store.Begin(token));

    /// <summary>
    /// Records <paramref name="segment"/>, another than the recorded one, with the key's token there,
    /// <paramref name="token"/> (journals.md §8.1 step 5), and begins both folds, empty, from it.
    /// </summary>
    public void RollTo(IArchiveStore store, LedgerSegmentId segment, RowToken token)
    {
        Debug.Assert(Segment != segment, "A roll records another segment.");
        Token = token;
        Segment = segment;
        Written = store.Begin(token);
        Admitted = store.Begin(token);
    }

    /// <summary>Adds a row's command a batch holds to the written fold; an opener begins the write again from the segment's token.</summary>
    public void AddWritten(IArchiveStore store, ArchiveCommand command) => Written = Add(store, Written, command);

    /// <summary>Adds an admitted entry's command to the admitted fold; an opener begins the write again from the segment's token.</summary>
    public void AddAdmitted(IArchiveStore store, ArchiveCommand command) => Admitted = Add(store, Admitted, command);

    /// <summary>
    /// Returns whether the store's limits admit <paramref name="append"/> whichever segment its batch joins (journals.md
    /// §8.1 step 4): the admitted write grown by it, and an empty write from that write's <c>End()</c> grown by it, which
    /// is the same write while the admitted write is empty.
    /// </summary>
    public bool AdmitsEitherWay(IArchiveStore store, ArchiveCommand append) =>
        Admitted.Admits(append) && (Admitted.Count == 0 || store.Begin(Admitted.End()).Admits(append));

    /// <summary>Returns whether the two folds agree: the same commands taken, the same token reached.</summary>
    public bool FoldsAgree() => Written.Count == Admitted.Count && Written.End() == Admitted.End();

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

    private ArchivePlan Add(IArchiveStore store, ArchivePlan fold, ArchiveCommand command)
    {
        if (command.IsOpener && fold.Count > 0)
        {
            // An opener supersedes the commands before it in the segment: the write is the opener and what follows, after
            // the rows it supersedes, which the store's plan may count (archive.md §3).
            var superseded = fold.Superseded + fold.Count;
            fold = store.Begin(Token);
            fold.Supersede(superseded);
        }

        fold.Add(command);
        return fold;
    }
}
