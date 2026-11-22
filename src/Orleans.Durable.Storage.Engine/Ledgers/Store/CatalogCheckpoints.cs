using System.Diagnostics;
using System.Globalization;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Wal;

namespace Orleans.Durable.Storage.Engine.Ledgers.Store;

/// <summary>
/// Owns a LedgerStore's catalog checkpoints in <c>wal/</c>: which are valid, the latest create or drop record they must
/// come to reflect, the cut of each capture, writes, the deletion of superseded ones, and the WAL retention bound they
/// allow (LS-I3, LS-I4; ledgerstore.md §7.2, §8.4, §8.8).
/// </summary>
/// <remarks>
/// <para>
/// A catalog checkpoint is the named file <c>{file:D20}-{offset:D20}.clsc</c>, named by its cut and created whole. This
/// class holds cuts and builds names only at I/O. A crash or a failed write, which stops the process
/// (conventions.md §14), may leave a published name whose write never completed, so a deletion lists the directory and
/// deletes every catalog checkpoint but the two newest valid ones, whatever its validity, and the WAL is released only
/// once a deletion has left exactly those two (LS-I4).
/// </para>
/// <para>
/// Its owner takes turns writing, deleting, and releasing: the open, then maintenance, then disposal. Its owner calls
/// <see cref="Published"/> and <see cref="Capture"/> under the lock which orders its catalog's publications, so a capture's
/// cut and catalog agree.
/// </para>
/// </remarks>
internal sealed class CatalogCheckpoints
{
    internal const string Extension = ".clsc";

    /// <summary>
    /// The number of valid catalog checkpoints the store keeps, and must hold before it releases WAL (LS-I4).
    /// </summary>
    internal const int Kept = 2;

    private readonly WalDirectory _directory;
    private readonly Lock _sync = new();

    // The cuts of the catalog checkpoints known valid, ascending: those the open read valid and those this open wrote.
    private readonly List<WalPosition> _valid;

    // The lowest WAL file the store retains; every file below it was released.
    private ulong _firstRetainedFile;

    // The position of the latest create or drop record published, or null before the first.
    private WalPosition? _latestPublished;

    // Whether the last deletion left exactly the kept catalog checkpoints and no write has begun since.
    private bool _onlyKeptPresent;

    internal CatalogCheckpoints(WalDirectory directory, IEnumerable<WalPosition> valid, ulong firstRetainedFile)
    {
        _directory = directory;
        _valid = [.. valid.Order()];
        _firstRetainedFile = firstRetainedFile;
    }

    /// <summary>
    /// Gets the cut of the newest valid catalog checkpoint, or <see langword="null"/> before the first.
    /// </summary>
    public WalPosition? Cut
    {
        get
        {
            lock (_sync)
            {
                return _valid.Count == 0 ? null : _valid[^1];
            }
        }
    }

    /// <summary>
    /// Gets the cuts of the catalog checkpoints known valid, ascending.
    /// </summary>
    public IReadOnlyList<WalPosition> Valid
    {
        get
        {
            lock (_sync)
            {
                return [.. _valid];
            }
        }
    }


    /// <summary>
    /// Gets a value indicating whether a published create or drop record awaits a catalog checkpoint which reflects it.
    /// </summary>
    public bool Pending
    {
        get
        {
            lock (_sync)
            {
                return _latestPublished is { } latest && (_valid.Count == 0 || latest >= _valid[^1]);
            }
        }
    }

    /// <summary>
    /// Returns the name of the catalog checkpoint whose cut is <paramref name="cut"/>.
    /// </summary>
    public static string FileName(WalPosition cut) =>
        string.Create(CultureInfo.InvariantCulture, $"{cut.File:D20}-{cut.Offset:D20}{Extension}");

    /// <summary>
    /// Parses a catalog checkpoint's name into its cut; a name in any other form is not a catalog checkpoint's.
    /// </summary>
    public static bool TryParseFileName(string name, out WalPosition cut)
    {
        cut = default;
        if (name.Length != 20 + 1 + 20 + Extension.Length
            || name[20] != '-'
            || !name.EndsWith(Extension, StringComparison.Ordinal)
            || !ulong.TryParse(name.AsSpan(0, 20), NumberStyles.None, CultureInfo.InvariantCulture, out var file)
            || !ulong.TryParse(name.AsSpan(21, 20), NumberStyles.None, CultureInfo.InvariantCulture, out var offset))
        {
            return false;
        }

        cut = new WalPosition(file, offset);
        return string.Equals(name, FileName(cut), StringComparison.Ordinal);
    }

    /// <summary>
    /// Lists the cuts of the catalog checkpoints in <paramref name="directory"/>, ascending; other names with the extension
    /// are not checkpoints.
    /// </summary>
    public static async ValueTask<List<WalPosition>> ListAsync(WalDirectory directory, CancellationToken cancellationToken)
    {
        var cuts = new List<WalPosition>();
        foreach (var name in await directory.ListNamedFilesAsync(Extension, cancellationToken).ConfigureAwait(false))
        {
            if (TryParseFileName(name, out var cut))
            {
                cuts.Add(cut);
            }
        }

        cuts.Sort();
        return cuts;
    }

    /// <summary>
    /// Reads the catalog checkpoints of <paramref name="cuts"/> newest first until <see cref="Kept"/> are valid (§7.2,
    /// §8.7 step 5).
    /// </summary>
    /// <returns>The valid ones read, oldest first.</returns>
    /// <exception cref="IOException">
    /// A catalog checkpoint could not be read, which stops the process (conventions.md §14); the open fails and deletes
    /// nothing.
    /// </exception>
    public static async ValueTask<List<CatalogCheckpoint>> ReadNewestValidAsync(
        WalDirectory directory, IReadOnlyList<WalPosition> cuts, CancellationToken cancellationToken)
    {
        var valid = new List<CatalogCheckpoint>(Kept);
        for (var index = cuts.Count - 1; index >= 0 && valid.Count < Kept; index--)
        {
            // LS-I3: a catalog checkpoint which fails validation is never loaded; maintenance deletes it once two newer
            // valid ones exist.
            var decoding = await ReadAsync(directory, cuts[index], cancellationToken).ConfigureAwait(false);
            Debug.Assert(decoding.Value is not null, "conventions.md §15: a decoder answers a union which holds a case.");
            var checkpoint = decoding switch
            {
                CatalogCheckpoint loaded => loaded,
                InvalidDataException => null,
                null => throw new UnreachableException(),
            };
            if (checkpoint is not null)
            {
                valid.Insert(0, checkpoint);
            }
        }

        return valid;
    }

    /// <summary>
    /// Reads and decodes the catalog checkpoint of <paramref name="cut"/>: the checkpoint, or the rejection naming the
    /// rule it breaks as a catalog checkpoint of its name's cut (ledgerstore.md §7.2).
    /// </summary>
    public static async ValueTask<CatalogDecoding> ReadAsync(WalDirectory directory, WalPosition cut, CancellationToken cancellationToken)
    {
        var file = await directory.OpenNamedFileAsync(FileName(cut), cancellationToken).ConfigureAwait(false);
        await using (file.ConfigureAwait(false))
        {
            if (file.Length > int.MaxValue)
            {
                return ReplicaCatalog.Rejection("length-mismatch");
            }

            using var contents = await file.ReadAsync(0, (int)file.Length, cancellationToken).ConfigureAwait(false);
            return ReplicaCatalog.Decode(contents.ToArray(), cut);
        }
    }

    /// <summary>
    /// Records a published create or drop record, which a later capture's cut lies above.
    /// </summary>
    public void Published(WalPosition position)
    {
        lock (_sync)
        {
            _latestPublished = position;
        }
    }

    /// <summary>
    /// Returns the cut of a capture taken while the durable frontier is <paramref name="frontier"/>: the frontier, or just
    /// past the latest published create or drop record when that lies at or above it (§8.8).
    /// </summary>
    /// <remarks>
    /// The caller holds the lock which orders publications and reads its catalog under it. The WAL publishes a record before
    /// its frontier passes it, and the catalog gate writes each create or drop record only after the previous one is
    /// published, so every create or drop record below the cut is published and reflected in that catalog, and every later
    /// one lies at or above the cut. A record can begin at the frontier: one which shares its first page with a durable
    /// record while its later pages are in flight (WAL-I7). Neither the frontier nor the latest record decreases, so cuts
    /// never decrease.
    /// </remarks>
    /// <param name="frontier">The WAL's durable frontier.</param>
    /// <param name="final">Whether the WAL has drained, so the frontier lies past every record.</param>
    public WalPosition Capture(WalPosition frontier, bool final)
    {
        lock (_sync)
        {
            return !final && _latestPublished is { } latest && latest >= frontier
                ? new WalPosition(latest.File, checked(latest.Offset + 1))
                : frontier;
        }
    }

    /// <summary>
    /// Writes <paramref name="catalog"/> as the catalog checkpoint of <paramref name="cut"/>, completing once it is durable
    /// and in force; one already in force at or past the cut is kept.
    /// </summary>
    /// <remarks>
    /// A failed write stops the process (conventions.md §14) and leaves any name it published to the next deletion, which
    /// lists it; no release relies on it. A cancelled one publishes no name.
    /// </remarks>
    /// <returns>Whether it wrote the checkpoint, rather than keeping one in force.</returns>
    public async ValueTask<bool> WriteAsync(ReplicaCatalog catalog, WalPosition cut, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_valid.Count != 0 && cut <= _valid[^1])
            {
                return false;
            }

            _onlyKeptPresent = false;
        }

        using (var writer = ArcBufferWriter.CreateAligned())
        {
            var length = catalog.Encode(cut, writer);
            using var contents = writer.ConsumeSlice(length);
            var file = await _directory.CreateNamedFileAsync(FileName(cut), contents, cancellationToken).ConfigureAwait(false);
            await file.DisposeAsync().ConfigureAwait(false);
        }

        lock (_sync)
        {
            _valid.Add(cut);
        }

        return true;
    }

    /// <summary>
    /// Lists the directory and deletes, durably, every catalog checkpoint but the <see cref="Kept"/> newest valid ones,
    /// whatever its validity, so the WAL past their cuts may be released (LS-I4). With fewer valid ones it deletes nothing.
    /// </summary>
    /// <returns>Whether exactly the kept ones remain.</returns>
    public async ValueTask<bool> DeleteSupersededAsync(CancellationToken cancellationToken)
    {
        WalPosition[] kept;
        lock (_sync)
        {
            if (_valid.Count < Kept)
            {
                return false;
            }

            kept = [.. _valid[^Kept..]];
        }

        var listed = await ListAsync(_directory, cancellationToken).ConfigureAwait(false);
        foreach (var cut in listed.Where(cut => Array.IndexOf(kept, cut) < 0))
        {
            await _directory.DeleteNamedFileAsync(FileName(cut), cancellationToken).ConfigureAwait(false);
        }

        lock (_sync)
        {
            _ = _valid.RemoveAll(cut => Array.IndexOf(kept, cut) < 0);
            _onlyKeptPresent = true;
        }

        return true;
    }

    /// <summary>
    /// Returns the retention bound (LS-I4): while exactly the <see cref="Kept"/> newest valid catalog checkpoints are
    /// present, the lowest of <paramref name="frontierFile"/>, the file of the WAL's durable frontier, each live
    /// replica's first file, and the file holding the older one's cut; otherwise the lowest retained file, so nothing
    /// more is released.
    /// </summary>
    public ulong RetentionBound(ulong frontierFile, ReplicaCatalog catalog)
    {
        lock (_sync)
        {
            if (_valid.Count != Kept || !_onlyKeptPresent)
            {
                return _firstRetainedFile;
            }

            var bound = Math.Min(frontierFile, _valid[0].File);
            foreach (var replica in catalog.Replicas)
            {
                bound = Math.Min(bound, replica.FirstFile);
            }

            return Math.Max(bound, _firstRetainedFile);
        }
    }

    /// <summary>
    /// Records that every WAL file below <paramref name="bound"/> was released.
    /// </summary>
    public void Released(ulong bound)
    {
        lock (_sync)
        {
            _firstRetainedFile = Math.Max(_firstRetainedFile, bound);
        }
    }
}
