using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.ObjectStore;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm;

/// <summary>
/// The LSM's archive store (archive-lsm.md §2.5, §2.8, §8.3, §8.6): a key's prefix, assembled from its rows in the
/// committed sections whose partitions contain it, absence from the sections' metadata, and the LSM's pure members over
/// its empty token.
/// </summary>
/// <remarks>
/// <para>
/// A prefix holds the key's rows of every ledger the caller's map value no longer lists
/// (<see cref="LedgerMapValue.NoLongerLists"/>), which the store holds (HI-I2, LSM-I3), and its coverage is that map
/// value: <see cref="Holds"/> holds exactly the writes of those ledgers, whose rows a reader never scans, and none of a
/// listed ledger's, nor of one past the map's <c>NextLedgerId</c>, whose rows a reader scans from slot 0 once its map
/// lists them (<see cref="LedgerMapCoverage"/>). Its version base is the tag of the prefix's last row, encoded in 20
/// bytes, and its token is empty: the LSM plans nothing.
/// </para>
/// <para>
/// The store keeps one manifest value, its view, read through the process's <see cref="ManifestView"/>. A read uses it
/// while it shows complete every ledger the caller's ledger map value no longer lists; otherwise the store takes the
/// manifest view's newer current value, joins the manifest read in flight, or reads the manifest after the call began,
/// and every manifest read after the map value covers it, since a ledger leaves the map only once complete (LSM-I3) and
/// completeness is permanent (LSM-I1). So every answer, <see cref="ArchiveStoreRead.Absent"/> included, comes from a
/// manifest value which covers the caller's map value (LSM-I4). A map value which lists open a ledger the view shows
/// complete predates the ledger's close and drain, and the read answers <see cref="ArchiveStoreRead.Stale"/>.
/// </para>
/// <para>
/// A process reads each run object's tail at most once while it stays cached: concurrent first reads share one fetch, the
/// verified tail serves every later read of its sections, which it is checked against (archive §7), and a tail found
/// corrupt stays corrupt. <see cref="PreloadAsync"/> names the key ranges whose section metadata the process keeps
/// loaded: it loads the view's sections in them at once, and before a newer value becomes the view the store loads those
/// of its sections in them which the view did not list, the reads which wait for the view waiting for those loads too; a
/// load which fails lets the view advance, and its section loads on a read's first need of it (archive-lsm.md §8.3,
/// §8.7). An object the view names which is absent answers <see cref="ArchiveStoreRead.Stale"/> once a manifest read
/// after the miss no longer lists its section; one which still lists it is corrupt.
/// </para>
/// </remarks>
public sealed class LsmArchiveStore : IArchiveStore
{
    /// <summary>The layout the store's record names.</summary>
    public const string Layout = "lsm";

    private readonly IObjectStore _store;
    private readonly ManifestView _manifest;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _adoptionInterval;
    private readonly TailCache _tails;
    private readonly BlockCache? _blocks;
    private readonly Lock _sync = new();
    private ManifestValue? _view;
    private (ulong Epoch, Task Task)? _advance;
    private long _lastAdoption;

    // The newest map epoch the view was found to cover: a map value at or below it drops only ledgers it covers.
    private ulong _coveredMapEpoch;
    private int _coverageChecks;

    /// <summary>Gets the number of times a read checked the view's coverage of a map value; for tests.</summary>
    internal int CoverageChecks => Volatile.Read(ref _coverageChecks);

    /// <summary>
    /// Gets or sets a value indicating whether a read skips any listed section whose object is gone rather than answering
    /// <see cref="ArchiveStoreRead.Stale"/>, as the model's <c>ignore-missing</c> mutant does (LSM-I4, LSM-I5).
    /// </summary>
    internal bool IgnoreMissingMutant { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a read omits the check that the map's open ledger is not already complete
    /// in the view, as the model's <c>no-predates-check</c> mutant does (LSM-I4, LSM-I5).
    /// </summary>
    internal bool SkipPredatesCheckMutant { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a read folds every drained row in the view and reports coverage by the
    /// view's watermarks rather than the map's dropped ledgers, as the model's <c>hold-by-watermark</c> mutant does
    /// (LSM-I4, LSM-I5).
    /// </summary>
    internal bool HoldByWatermarkMutant { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether an <see cref="ArchiveStoreRead.Absent"/> answer also emits a bound for a
    /// complete listed ledger, whose compacted expiry may have removed a row of the key; tests set this mutant to verify
    /// they catch it.
    /// </summary>
    internal bool BoundCompleteLedgerMutant { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether <see cref="ReadAsync"/> skips the <see cref="Covers"/> check and returns the
    /// store's current manifest view even when the ledger map dropped a ledger that view does not show complete
    /// (<c>uncovered-drop</c>, archive-lsm.md §8.3, §10): tests set this to verify that pairing a dropped ledger's map value
    /// with a lagging manifest fails the oracle.
    /// </summary>
    internal bool UncoveredDropMutant { get; set; }

    /// <summary>Gets the bytes of the tails the process's cache holds; for tests.</summary>
    internal long CachedTailBytes => _tails.CachedBytes;

    /// <summary>Gets the process's section metadata cache, which its compaction worker shares; for tests.</summary>
    internal TailCache Tails => _tails;

    /// <summary>Gets the process's volatile-disk block cache, or <see langword="null"/> when none is configured; for tests.</summary>
    internal BlockCache? Blocks => _blocks;

    /// <summary>Gets the epoch of the store's view, the manifest value its last answer came from; for tests.</summary>
    internal ulong? ViewEpoch
    {
        get
        {
            lock (_sync)
            {
                return _view?.Epoch;
            }
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="LsmArchiveStore"/> class.
    /// </summary>
    /// <param name="store">The object store holding the LSM.</param>
    /// <param name="manifest">The process's manifest view, through which the store reads the manifest.</param>
    /// <param name="tails">The process's cache of verified tails, which its compaction worker shares.</param>
    /// <param name="options">The interval between adoptions of the view's current value.</param>
    /// <param name="timeProvider">Spaces the adoptions.</param>
    /// <param name="blocks">The process's volatile-disk block cache, or <see langword="null"/> when none is configured.</param>
    /// <exception cref="ArgumentOutOfRangeException">The options are invalid.</exception>
    public LsmArchiveStore(IObjectStore store, ManifestView manifest, TailCache tails, ArchiveOptions options, TimeProvider timeProvider, BlockCache? blocks = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(tails);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        options = options with { };
        options.Validate();
        _store = store;
        _manifest = manifest;
        _tails = tails;
        _blocks = blocks;
        _timeProvider = timeProvider;
        _adoptionInterval = options.ViewAdoptionInterval;
        _lastAdoption = timeProvider.GetTimestamp();
    }

    /// <inheritdoc/>
    public ArchiveStoreKind Kind => ArchiveStoreKind.Lsm;

    /// <summary>
    /// Makes <paramref name="ranges"/> the key ranges whose section metadata the process keeps loaded, replacing the
    /// previous set, and loads at once that of every section the store's view lists whose partition overlaps one of them;
    /// a store with no view yet takes its manifest view's current value, when there is one, as its view, loading it so,
    /// and otherwise its first view loads them. Each later view loads only its new sections in the ranges. An entry
    /// outside them leaves the cache once no read uses it between two of the cache's passes (archive-lsm.md §2.5, §8.7).
    /// </summary>
    /// <param name="ranges">The ranges, those of the open ledgers the process's node is primary of.</param>
    /// <returns>
    /// A task which completes once the loads have, whatever each ended with: a load which fails leaves its section to load
    /// on a read's first need of it, and one which finds the tail corrupt leaves the reads which need it to answer
    /// <see cref="ArchiveCorruptException"/>.
    /// </returns>
    public Task PreloadAsync(IEnumerable<KeyRange> ranges)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        _tails.SetPreloaded([.. ranges]);
        ManifestValue? view;
        lock (_sync)
        {
            view = _view;
        }

        if (view is null)
        {
            return _manifest.Current is { } current ? AdvanceAsync(current.Value) : Task.CompletedTask;
        }

        _tails.Retain(view);
        return LoadAsync(view.Sections.Where(section => _tails.IsPreloaded(section.Partition)));
    }

    /// <inheritdoc/>
    /// <remarks>Unbounded: the caller's own limit alone bounds a body.</remarks>
    public int? MaxAppendBytes => null;

    /// <inheritdoc/>
    /// <remarks>
    /// <see cref="ArchiveStoreRead.Absent"/> when the metadata of every section containing the key excludes it, which
    /// reads no block, or when the sections hold no row of the key in a ledger the map value no longer lists. Otherwise
    /// <see cref="ArchiveStoreRead.Read"/>: the prefix of those rows (archive-lsm.md §8.6), with the map value as its
    /// coverage. <see cref="ArchiveStoreRead.Stale"/> when an object the view names is gone, or the map value lists open
    /// a ledger the view shows complete.
    /// </remarks>
    /// <exception cref="ArchiveCorruptException">An object breaks its format or contradicts the manifest, the manifest breaks its
    /// format or records a closed ledger's watermark past its <c>T</c>, or a manifest read after the map shows a ledger the
    /// map dropped incomplete.</exception>
    public ValueTask<ArchiveStoreRead> ReadAsync(ReadOnlyMemory<byte> key, LedgerMapValue map, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (TryGetCoveredView(map, out var view))
        {
            return ReadWithViewAsync(view, key, map, cancellationToken);
        }

        return ReadAfterViewAsync(key, map, cancellationToken);
    }

    private async ValueTask<ArchiveStoreRead> ReadAfterViewAsync(
        ReadOnlyMemory<byte> key,
        LedgerMapValue map,
        CancellationToken cancellationToken)
    {
        var view = await ViewCoveringSlowAsync(map, cancellationToken).ConfigureAwait(false);
        return await ReadWithViewAsync(view, key, map, cancellationToken).ConfigureAwait(false);
    }

    private ValueTask<ArchiveStoreRead> ReadWithViewAsync(
        ManifestValue view,
        ReadOnlyMemory<byte> key,
        LedgerMapValue map,
        CancellationToken cancellationToken)
    {
        var lookup = map.Find(key.Span);
        if (!SkipPredatesCheckMutant && PredatesView(lookup, view))
        {
            return ValueTask.FromResult<ArchiveStoreRead>(ArchiveStoreRead.Stale.Instance);
        }

        var sections = view.SectionsContaining(key.Span);
        if (sections.IsEmpty)
        {
            return ValueTask.FromResult<ArchiveStoreRead>(AbsentOf(lookup, view));
        }

        // Fast path: resolve all tails synchronously from TailCache without allocating a Task[] or candidate List.
        var allCached = true;
        var candidateCount = 0;
        SectionRef firstSection = default!;
        RunSectionMetadata? firstMetadata = null;
        RunBlockSpan firstBlocks = default;
        List<(SectionRef Section, RunSectionMetadata Metadata, RunBlockSpan Blocks)>? extraCandidates = null;
        var keySpan = key.Span;

        for (var index = 0; index < sections.Length; index++)
        {
            var section = sections[index];
            if (!_tails.TryGet(section, out var tail))
            {
                allCached = false;
                break;
            }

            var metadata = RunObjectReader.SectionOf(tail, section);
            var blocks = metadata.Candidates(keySpan);
            if (blocks.Count > 0)
            {
                if (candidateCount == 0)
                {
                    firstSection = section;
                    firstMetadata = metadata;
                    firstBlocks = blocks;
                }
                else
                {
                    if (extraCandidates is null)
                    {
                        extraCandidates = [];
                        _ = extraCandidates.EnsureCapacity(sections.Length);
                        extraCandidates.Add((firstSection, firstMetadata!, firstBlocks));
                    }

                    extraCandidates.Add((section, metadata, blocks));
                }

                candidateCount++;
            }
        }

        if (allCached)
        {
            return candidateCount switch
            {
                0 => ValueTask.FromResult<ArchiveStoreRead>(AbsentOf(lookup, view)),
                1 => ReadSingleCandidateAsync(key, lookup, firstSection, firstMetadata!, firstBlocks, map, view, cancellationToken),
                _ => ReadRowsAsync(key, lookup, extraCandidates!, map, view, cancellationToken),
            };
        }

        return ReadWithUncachedTailsAsync(key, lookup, sections, map, view, cancellationToken);
    }

    private async ValueTask<ArchiveStoreRead> ReadWithUncachedTailsAsync(
        ReadOnlyMemory<byte> key,
        LedgerLookup? lookup,
        ImmutableArray<SectionRef> sections,
        LedgerMapValue map,
        ManifestValue view,
        CancellationToken cancellationToken)
    {
        var tails = new Task<RunTail?>[sections.Length];
        for (var index = 0; index < sections.Length; index++)
        {
            tails[index] = _tails.GetAsync(sections[index], cancellationToken);
        }

        await WhenSettled(tails).ConfigureAwait(false);
        var candidateCount = 0;
        SectionRef firstSection = default!;
        RunSectionMetadata? firstMetadata = null;
        RunBlockSpan firstBlocks = default;
        List<(SectionRef Section, RunSectionMetadata Metadata, RunBlockSpan Blocks)>? extraCandidates = null;
        for (var index = 0; index < sections.Length; index++)
        {
            if (await tails[index].ConfigureAwait(false) is not { } tail)
            {
                if (IgnoreMissingMutant)
                {
                    continue;
                }

                return await StaleAsync(sections[index], cancellationToken).ConfigureAwait(false);
            }

            var section = sections[index];
            var metadata = RunObjectReader.SectionOf(tail, section);
            var blocks = metadata.Candidates(key.Span);
            if (blocks.Count > 0)
            {
                if (candidateCount == 0)
                {
                    firstSection = section;
                    firstMetadata = metadata;
                    firstBlocks = blocks;
                }
                else
                {
                    if (extraCandidates is null)
                    {
                        extraCandidates = [];
                        _ = extraCandidates.EnsureCapacity(sections.Length);
                        extraCandidates.Add((firstSection, firstMetadata!, firstBlocks));
                    }

                    extraCandidates.Add((section, metadata, blocks));
                }

                candidateCount++;
            }
        }

        return candidateCount switch
        {
            0 => AbsentOf(lookup, view),
            1 => await ReadSingleCandidateAsync(key, lookup, firstSection, firstMetadata!, firstBlocks, map, view, cancellationToken).ConfigureAwait(false),
            _ => await ReadRowsAsync(key, lookup, extraCandidates!, map, view, cancellationToken).ConfigureAwait(false),
        };
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The record is the root object <c>store</c> (archive-lsm.md §2.8), with the layout <c>lsm</c> and no <c>Plan</c>
    /// parameters; a create which finds it exists reads the winner's.
    /// </remarks>
    public ValueTask<ArchiveStoreRecord> StoreRecordAsync(IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken) =>
        StoreRecordAsync(_store, parameters, cancellationToken);

    /// <summary>
    /// Answers the record of the LSM over <paramref name="objects"/>, creating it when it is absent, as
    /// <see cref="StoreRecordAsync(IReadOnlyDictionary{string, string}, CancellationToken)"/> does: the store's record needs no
    /// manifest or tail cache, so a caller which only creates or adopts it builds no store.
    /// </summary>
    /// <param name="objects">The LSM's object store.</param>
    /// <param name="parameters">The caller parameters a created record holds.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    /// <returns>The store's record.</returns>
    public static async ValueTask<ArchiveStoreRecord> StoreRecordAsync(IObjectStore objects, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(objects);
        ArchiveStoreRecord.CheckParameters(parameters);
        if (await ReadRecordAsync(objects, cancellationToken).ConfigureAwait(false) is not { } stored)
        {
            var created = ArchiveStoreRecord.Create(ArchiveStoreId.NewRandom(), Layout, ImmutableDictionary<string, string>.Empty, parameters);
            using var content = ArcBuffer.Wrap(created.Encode());
            switch (await objects.CreateAsync(ObjectName.Store, content, null, cancellationToken).ConfigureAwait(false))
            {
                case ObjectCreateOutcome.Created:
                    return created;
                case ObjectCreateOutcome.Exists:
                    stored = await ReadRecordAsync(objects, cancellationToken).ConfigureAwait(false)
                        ?? throw new ArchiveCorruptException($"{ObjectName.Store} exists but reads as absent.");
                    break;
                case null:
                default:
                    throw new UnreachableException();
            }
        }

        stored.CheckStoreFieldsMatch(Layout, ImmutableDictionary<string, string>.Empty);
        return stored;
    }

    /// <inheritdoc/>
    /// <exception cref="ArchiveCorruptException"><paramref name="token"/> is not empty, the LSM's only token.</exception>
    public ArchivePlan Begin(RowToken token) => LedgerMapCoverage.Begin(token, "LSM");

    /// <inheritdoc/>
    /// <remarks>
    /// The prefix holds every write of a ledger its read's map value no longer lists, and none of another's
    /// (archive-lsm.md §2.8).
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="prefix"/> is not one of the LSM's.</exception>
    public bool Holds(ArchivePrefix prefix, ArchiveWrite write, int count)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        if (HoldByWatermarkMutant && prefix.Coverage is ManifestValue view)
        {
            ArgumentNullException.ThrowIfNull(write);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(count, write.Commands.Length);
            return count == 0 || view.IsComplete(write.Segment.Ledger) || write.Segment.Start < view.RecordedWatermark(write.Segment.Ledger);
        }

        return LedgerMapCoverage.Holds(prefix, write, count);
    }

    /// <inheritdoc/>
    /// <remarks>Never: the LSM keeps no per-key limit.</remarks>
    public bool IsReplaceRequested(RowToken token)
    {
        LedgerMapCoverage.CheckToken(token, "LSM");
        return false;
    }

    /// <inheritdoc/>
    public bool AdmitsKey(ReadOnlySpan<byte> key) => true;

    /// <summary>
    /// Returns whether <paramref name="view"/> shows complete every ledger <paramref name="map"/> no longer lists: every
    /// ledger below the map's <c>NextLedgerId</c> which it does not list.
    /// </summary>
    internal static bool Covers(ManifestValue view, LedgerMapValue map)
    {
        for (var id = view.DrainedBelow.Value; id < map.NextLedgerId.Value; id++)
        {
            var ledger = new LedgerId(id);
            if (map.NoLongerLists(ledger) && !view.IsComplete(ledger))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Sorts <paramref name="rows"/> by tag.</summary>
    /// <exception cref="ArchiveCorruptException">Two sections hold a row under one tag.</exception>
    private static void Sort(List<TaggedRow> rows)
    {
        rows.Sort(static (left, right) => left.Tag.CompareTo(right.Tag));
        for (var index = 1; index < rows.Count; index++)
        {
            if (rows[index].Tag == rows[index - 1].Tag)
            {
                throw new ArchiveCorruptException($"Two sections hold a row of one key under tag ({rows[index].Tag.Ledger}, {rows[index].Tag.Slot}, {rows[index].Tag.Index}).");
            }
        }
    }

    /// <summary>
    /// Builds an <see cref="ArchiveStoreRead.Absent"/> answer with per-ledger empty bounds for <paramref name="lookup"/>
    /// from <paramref name="view"/> when the view holds zero rows of the key (archive.md §8.1). Compaction supersedes or
    /// drops rows only of complete ledgers (archive-lsm.md §8.2, §8.4), so for every incomplete listed ledger its recorded
    /// watermark is a proven empty bound (and a segment start, LSM-I2).
    /// </summary>
    private ArchiveStoreRead.Absent AbsentOf(LedgerLookup? lookup, ManifestValue view)
    {
        if (lookup is not { } found)
        {
            return ArchiveStoreRead.Absent.Instance;
        }

        var maxCount = found.Closed.Length + 1;
        Span<LedgerAbsentBound> scratch = maxCount <= 8 ? stackalloc LedgerAbsentBound[maxCount] : new LedgerAbsentBound[maxCount];
        var count = 0;
        foreach (var ledger in found.Closed)
        {
            AddBound(ledger, view, scratch, ref count);
        }

        AddBound(found.Open, view, scratch, ref count);
        return count == 0 ? ArchiveStoreRead.Absent.Instance : ArchiveStoreRead.Absent.WithBounds(scratch[..count]);

        void AddBound(LedgerMetadata ledger, ManifestValue view, Span<LedgerAbsentBound> destination, ref int count)
        {
            if (!BoundCompleteLedgerMutant && view.IsComplete(ledger.Id))
            {
                return;
            }

            if (view.Watermark(ledger) is { } watermark && watermark > Slot.Zero)
            {
                destination[count++] = new LedgerAbsentBound(ledger.Id, watermark);
            }
        }
    }

    /// <summary>
    /// Returns whether <paramref name="view"/> shows complete the open ledger <paramref name="lookup"/> names, so the map
    /// predates the ledger's close.
    /// </summary>
    private static bool PredatesView(LedgerLookup? lookup, ManifestValue view) =>
        lookup is { } found && view.Watermark(found.Open) is null;

    /// <summary>Completes once every task has, whatever its outcome, so no failure goes unobserved.</summary>
    private static async Task WhenSettled(IEnumerable<Task> tasks)
    {
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Each task's own outcome is read in order by the caller.
        }
    }

    /// <summary>
    /// Reads the rows of <paramref name="key"/> from a single candidate section directly without boxing into a task array,
    /// <see cref="WhenSettled"/>, or copying into an intermediate row list.
    /// </summary>
    private async ValueTask<ArchiveStoreRead> ReadSingleCandidateAsync(
        ReadOnlyMemory<byte> key,
        LedgerLookup? lookup,
        SectionRef section,
        RunSectionMetadata metadata,
        RunBlockSpan blocks,
        LedgerMapValue map,
        ManifestValue view,
        CancellationToken cancellationToken)
    {
        var read = await RunObjectReader.ReadRowsAsync(_store, section.Object, metadata, blocks, key, _blocks, admitToCache: true, cancellationToken).ConfigureAwait(false);
        if (read is null)
        {
            return IgnoreMissingMutant
                ? AbsentOf(lookup, view)
                : await StaleAsync(section, cancellationToken).ConfigureAwait(false);
        }

        using (read)
        {
            var rows = read.Rows;
            if (rows.IsEmpty)
            {
                return AbsentOf(lookup, view);
            }

            if (HoldByWatermarkMutant)
            {
                return new ArchiveStoreRead.Read(ArchivePrefix.Fold(rows, default, LedgerMapCoverage.VersionBaseOf(rows[^1].Tag), view));
            }

            return LedgerMapCoverage.Read(this, rows, map);
        }
    }

    /// <summary>
    /// Reads the rows of <paramref name="key"/> from every candidate section at once, and answers the prefix of those of
    /// the ledgers <paramref name="map"/> no longer lists, or <see cref="ArchiveStoreRead.Absent"/> when there are none;
    /// or <see cref="ArchiveStoreRead.Stale"/> when the first, in the view's order, whose object is gone.
    /// </summary>
    private async ValueTask<ArchiveStoreRead> ReadRowsAsync(
        ReadOnlyMemory<byte> key,
        LedgerLookup? lookup,
        List<(SectionRef Section, RunSectionMetadata Metadata, RunBlockSpan Blocks)> candidates,
        LedgerMapValue map,
        ManifestValue view,
        CancellationToken cancellationToken)
    {
        var reads = new Task<RunRows?>[candidates.Count];
        for (var index = 0; index < candidates.Count; index++)
        {
            reads[index] = RunObjectReader.ReadRowsAsync(_store, candidates[index].Section.Object, candidates[index].Metadata, candidates[index].Blocks, key, _blocks, admitToCache: true, cancellationToken).AsTask();
        }

        await WhenSettled(reads).ConfigureAwait(false);
        var rows = new List<TaggedRow>();
        try
        {
            SectionRef? gone = null;
            Exception? failure = null;
            for (var index = 0; index < reads.Length; index++)
            {
                if (!reads[index].IsCompletedSuccessfully)
                {
                    failure ??= reads[index].Exception?.InnerException ?? new OperationCanceledException(cancellationToken);
                }
                else if (reads[index].Result is { } read)
                {
                    read.MoveTo(rows);
                }
                else
                {
                    gone ??= candidates[index].Section;
                }
            }

            if (failure is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
            }

            if (!IgnoreMissingMutant && gone is not null)
            {
                return await StaleAsync(gone, cancellationToken).ConfigureAwait(false);
            }

            Sort(rows);
            if (HoldByWatermarkMutant)
            {
                return rows.Count == 0
                    ? AbsentOf(lookup, view)
                    : new ArchiveStoreRead.Read(ArchivePrefix.Fold(CollectionsMarshal.AsSpan(rows), default, LedgerMapCoverage.VersionBaseOf(rows[^1].Tag), view));
            }

            if (rows.Count == 0)
            {
                return AbsentOf(lookup, view);
            }

            return LedgerMapCoverage.Read(this, CollectionsMarshal.AsSpan(rows), map);
        }
        finally
        {
            foreach (var row in rows)
            {
                row.Row.Dispose();
            }
        }
    }

    /// <summary>
    /// Returns the store's view when it covers <paramref name="map"/>; otherwise a newer value which covers it: the manifest
    /// view's current one, the manifest read in flight, or a manifest read begun after the call, which covers every map
    /// value read before it. Now and then, at most once per adoption interval, the view adopts a newer current value even
    /// when it covers the map, so it trails compactions and sweeps by at most that long.
    /// </summary>
    /// <exception cref="ArchiveCorruptException">The manifest read shows a ledger the map dropped incomplete.</exception>
    private bool TryGetCoveredView(LedgerMapValue map, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ManifestValue? view)
    {
        if (_timeProvider.GetElapsedTime(Interlocked.Read(ref _lastAdoption)) < _adoptionInterval)
        {
            lock (_sync)
            {
                if (_view is { } current && map.Epoch <= _coveredMapEpoch)
                {
                    view = current;
                    return true;
                }
            }
        }

        view = null;
        return false;
    }

    private ValueTask<ManifestValue> ViewCoveringAsync(LedgerMapValue map, CancellationToken cancellationToken) =>
        TryGetCoveredView(map, out var view)
            ? ValueTask.FromResult(view)
            : ViewCoveringSlowAsync(map, cancellationToken);

    private async ValueTask<ManifestValue> ViewCoveringSlowAsync(LedgerMapValue map, CancellationToken cancellationToken)
    {
        await AdoptCurrentWhenDueAsync(cancellationToken).ConfigureAwait(false);
        if (await CoveringViewOrNullAsync(map, null, cancellationToken).ConfigureAwait(false) is { } view)
        {
            return view;
        }

        if (_manifest.Current is { } current && await CoveringViewOrNullAsync(map, current.Value, cancellationToken).ConfigureAwait(false) is { } adopted)
        {
            return adopted;
        }

        var joined = (await _manifest.JoinAsync(cancellationToken).ConfigureAwait(false)).Value;
        if (await CoveringViewOrNullAsync(map, joined, cancellationToken).ConfigureAwait(false) is { } joinedView)
        {
            return joinedView;
        }

        var read = (await _manifest.ReadAsync(cancellationToken).ConfigureAwait(false)).Value;
        return await CoveringViewOrNullAsync(map, read, cancellationToken).ConfigureAwait(false)
            ?? throw new ArchiveCorruptException(string.Create(CultureInfo.InvariantCulture, $"The manifest at epoch {read.Epoch}, read after the ledger map at epoch {map.Epoch}, shows incomplete a ledger the map dropped."));
    }

    private Task AdoptCurrentWhenDueAsync(CancellationToken cancellationToken)
    {
        if (_timeProvider.GetElapsedTime(Interlocked.Read(ref _lastAdoption)) < _adoptionInterval)
        {
            return Task.CompletedTask;
        }

        _ = Interlocked.Exchange(ref _lastAdoption, _timeProvider.GetTimestamp());
        return _manifest.Current is { } current ? AdvanceAsync(current.Value).WaitAsync(cancellationToken) : Task.CompletedTask;
    }

    /// <summary>
    /// Advances the view to <paramref name="candidate"/> when that is newer, and returns the view when it covers
    /// <paramref name="map"/>, otherwise <see langword="null"/>. Coverage only grows with the manifest's epoch (LSM-I1), so
    /// the newer value is the one to check, once; a view found to cover a map epoch covers every older map value, which
    /// drops a subset of the ledgers it drops.
    /// </summary>
    private async ValueTask<ManifestValue?> CoveringViewOrNullAsync(LedgerMapValue map, ManifestValue? candidate, CancellationToken cancellationToken)
    {
        if (candidate is not null)
        {
            await AdvanceAsync(candidate).WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        ManifestValue? view;
        lock (_sync)
        {
            view = _view;
            if (view is null)
            {
                return null;
            }

            if (map.Epoch <= _coveredMapEpoch)
            {
                return view;
            }
        }

        _ = Interlocked.Increment(ref _coverageChecks);
        if (UncoveredDropMutant)
        {
            return view;
        }

        return Covers(view, map) ? RecordCovers(view, map) : null;
    }
    /// <summary>Records that <paramref name="view"/>, while it is the view, covers <paramref name="map"/>.</summary>
    private ManifestValue RecordCovers(ManifestValue view, LedgerMapValue map)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_view, view))
            {
                _coveredMapEpoch = Math.Max(_coveredMapEpoch, map.Epoch);
            }
        }

        return view;
    }

    /// <summary>
    /// Makes <paramref name="value"/> the view when it is newer, once the metadata of its sections in the preloaded ranges
    /// which the view did not list has loaded, or its loads failed, and runs the cache's pass over it. An advance to a
    /// value at least as new already in flight is joined rather than started again.
    /// </summary>
    private async Task AdvanceAsync(ManifestValue value)
    {
        ManifestValue? previous;
        Task? joined = null;
        TaskCompletionSource? advanced = null;
        lock (_sync)
        {
            if (_view is not null && value.Epoch <= _view.Epoch)
            {
                return;
            }

            if (_advance is { } inFlight && inFlight.Epoch >= value.Epoch)
            {
                joined = inFlight.Task;
            }
            else
            {
                advanced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _advance = (value.Epoch, advanced.Task);
            }

            previous = _view;
        }

        if (joined is not null)
        {
            await joined.ConfigureAwait(false);
            return;
        }

        try
        {
            var loads = _tails.Mutant == MetadataCacheMutant.LoadsNoNewSections ? [] : NewPreloadedSections(previous, value);
            if (_tails.Mutant != MetadataCacheMutant.InstallsBeforeLoads)
            {
                await LoadAsync(loads).ConfigureAwait(false);
            }

            Install(value);
            if (_tails.Mutant == MetadataCacheMutant.InstallsBeforeLoads)
            {
                _ = LoadAsync(loads);
            }
        }
        finally
        {
            lock (_sync)
            {
                if (_advance is { } current && ReferenceEquals(current.Task, advanced!.Task))
                {
                    _advance = null;
                }
            }

            advanced!.SetResult();
        }
    }

    /// <summary>Makes <paramref name="value"/> the view when it is newer, and runs the cache's pass over it.</summary>
    private void Install(ManifestValue value)
    {
        lock (_sync)
        {
            if (_view is not null && value.Epoch <= _view.Epoch)
            {
                return;
            }

            _view = value;
            _coveredMapEpoch = 0;
            _tails.Retain(value);
        }
    }

    /// <summary>
    /// Returns the sections <paramref name="value"/> lists whose objects <paramref name="previous"/> did not, all of them
    /// when there is none, whose partitions overlap a preloaded range.
    /// </summary>
    private List<SectionRef> NewPreloadedSections(ManifestValue? previous, ManifestValue value)
    {
        if (_tails.Preloaded.IsEmpty)
        {
            return [];
        }

        var known = previous?.Sections.Select(static section => section.Object).ToHashSet();
        return [.. value.Sections.Where(section => known?.Contains(section.Object) != true && _tails.IsPreloaded(section.Partition))];
    }

    /// <summary>Loads the metadata of <paramref name="sections"/> into the cache, completing once every load has, however it ended.</summary>
    private Task LoadAsync(IEnumerable<SectionRef> sections) =>
        WhenSettled([.. sections.Select(section => _tails.GetAsync(section, CancellationToken.None, read: false))]);

    /// <summary>
    /// Answers a read which found <paramref name="section"/>'s object absent: stale once a manifest read after the miss no
    /// longer lists the section, which then becomes the view. It joins a read in flight first, and reads again only when
    /// that still lists the section.
    /// </summary>
    /// <exception cref="ArchiveCorruptException">The manifest read after the miss still lists the section.</exception>
    private async ValueTask<ArchiveStoreRead> StaleAsync(SectionRef section, CancellationToken cancellationToken)
    {
        var read = (await _manifest.JoinAsync(cancellationToken).ConfigureAwait(false)).Value;
        if (read.Contains(section))
        {
            read = (await _manifest.ReadAsync(cancellationToken).ConfigureAwait(false)).Value;
            if (read.Contains(section))
            {
                throw new ArchiveCorruptException(string.Create(CultureInfo.InvariantCulture, $"{section.Object} is absent, though the manifest at epoch {read.Epoch}, read after the miss, lists its section at offset {section.Offset}."));
            }
        }

        await AdvanceAsync(read).WaitAsync(cancellationToken).ConfigureAwait(false);
        return ArchiveStoreRead.Stale.Instance;
    }

    /// <summary>Reads the store's record, or <see langword="null"/> when it is absent.</summary>
    /// <exception cref="ArchiveCorruptException">The record breaks its format.</exception>
    private static async ValueTask<ArchiveStoreRecord?> ReadRecordAsync(IObjectStore objects, CancellationToken cancellationToken)
    {
        if (await objects.ReadVersionedAsync(ObjectName.Store, cancellationToken).ConfigureAwait(false) is not { } found)
        {
            return null;
        }

        using var content = found.Content;
        return ArchiveStoreRecord.Decode(content.ToArray()) switch
        {
            ArchiveStoreRecord record => record,
            InvalidDataException rejection => throw new ArchiveCorruptException($"{ObjectName.Store} breaks the format of a store's record.", rejection),
            null => throw new UnreachableException(),
        };
    }
}
