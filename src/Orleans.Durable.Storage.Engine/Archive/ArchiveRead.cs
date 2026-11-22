using System.Diagnostics;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Archive;

/// <summary>
/// A key's history read (archive.md §2.2, §4): the store's prefix, every live row of the key in tag order, each marked
/// held or not, the decided end of each live ledger read, the open ledger it read, and whether the read is linearizable. Its history is the
/// prefix followed by the rows not held. It owns its prefix and its rows' bodies; its caller disposes it.
/// </summary>
public sealed class ArchiveRead : IDisposable
{
    private readonly ArchiveRow[] _rows;
    private int _disposed;

    internal ArchiveRead(ArchivePrefix? prefix, ArchiveRow[] rows, IReadOnlyDictionary<LedgerId, Slot> through, LedgerId? openLedger, bool linearizable, RowToken nextToken)
    {
        Debug.Assert(!linearizable || openLedger is { } open && through.ContainsKey(open), "A linearizable read checked the seal of the open ledger it read.");
        Prefix = prefix;
        _rows = rows;
        Through = through;
        OpenLedger = openLedger;
        Linearizable = linearizable;
        NextToken = nextToken;
    }

    /// <summary>Gets the store's prefix of the key, which the read owns, or <see langword="null"/> when the store answered <c>Absent</c>.</summary>
    public ArchivePrefix? Prefix { get; }

    /// <summary>
    /// Gets every live row of the key, from slot 0 of each listed ledger, in strictly increasing tag order, which the read
    /// owns: a row the prefix holds with its body's length only, and a row not held with its body.
    /// </summary>
    public ReadOnlySpan<ArchiveRow> Rows => _rows;

    /// <summary>
    /// Gets, for each live ledger of the map value read whose range contains the key, the decided end the read reached in
    /// it: a closed ledger's recorded <c>T</c>, or the tail a seal check begun after the store answered found for the open
    /// one.
    /// </summary>
    public IReadOnlyDictionary<LedgerId, Slot> Through { get; }

    /// <summary>
    /// Gets the open ledger over the key in the map value the read took, whose seal check gave its decided end in
    /// <see cref="Through"/>, and whose writer made the read <see cref="Linearizable"/> when this process served it; or
    /// <see langword="null"/> when the value lists no open ledger over the key, as before bootstrap.
    /// </summary>
    public LedgerId? OpenLedger { get; }

    /// <summary>
    /// Gets a value indicating whether the read includes every row of the key answered durable before it began: true
    /// only when the process serves the writer of the key's open ledger, whose seal check here covered every proposal the
    /// writer answered durable (HI-I1, LG-I8); the read then names that ledger as its <see cref="OpenLedger"/>. Otherwise the read holds a prefix of the key's history, which a caller
    /// answers as a linearizable read only by forwarding it to the primary.
    /// </summary>
    public bool Linearizable { get; }

    /// <summary>
    /// Gets the token the key's next write starts from after every write the read met (archive.md §2.2): <c>Plan</c> of
    /// the key's write in the last segment the scans met, held or not, or, with no live row, the prefix's token, or
    /// <c>Origin</c> when the store answered <c>Absent</c>. A write the prefix holds whole lies at the prefix's token.
    /// </summary>
    public RowToken NextToken { get; }

    /// <summary>Releases the prefix's content and the rows' bodies.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Prefix?.Dispose();
        ArchiveMerge.Release(_rows);
    }
}
