using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Orleans.Durable.Storage.Engine.Archive.Control;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.ObjectStore;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm.Control;

/// <summary>
/// Compacts one partition's sections (archive §2.4, §8.2, §8.8): it merges the inputs' rows of each key in tag order,
/// drops the rows the drop rule allows, writes the rest as compaction outputs, and returns the change
/// <c>Compaction(inputs, outputs)</c>, which its caller submits to the manifest committer.
/// </summary>
/// <remarks>
/// <para>
/// The geometry: a partition which holds more than <c>k</c> sections compacts. When they total at least <c>F × s</c>
/// bytes it pushes them into a section for each child one grid step deeper which receives rows; otherwise it merges in
/// place only its sections smaller than <c>s</c>, at least two, into one section of the same partition. A compaction
/// takes its inputs in position order up to <c>2 × F × s</c> bytes, at least one: the sections it leaves count as outside
/// it, and a later compaction takes them. Each output section is an object of its own, or several when it would pass the
/// bound on a run object's length.
/// </para>
/// <para>
/// The drop rule (LSM-I5): a key's rows before its last <c>Replace</c> or <c>Delete</c> among the inputs of a ledger the
/// manifest value shows complete are dropped, and that row is kept, a tombstoned <c>Delete</c> among them, so the key's
/// content, tombstone, and version base survive; a <c>Replace</c> or <c>Delete</c> of a ledger not yet complete, an open
/// ledger's drain's, supersedes nothing, since a reader of the open ledger may stop below it. An
/// empty <c>Delete</c> is dropped too when no older row of its key lies outside the compaction: no section the manifest
/// value lists outside the inputs whose partition contains the key holds it below the row's tag, by its tag span or its
/// Bloom filter and block index, and every live ledger with a lower ID whose range contains the key is complete in that
/// value. Rows only move from the live ledgers into sections and new rows of a key take higher tags, so the decision
/// stays true until the commit. A ledger whose range the map does not give, because the map no longer or does not yet
/// list it, must be complete, so the rule holds whichever of the map and the manifest was read first.
/// </para>
/// <para>
/// The worker compacts one output partition at a time, in key order: for a push, each descendant one grid step deeper;
/// for a merge in place, its one partition. For each, it reads the blocks of each input which may hold the partition's
/// keys, located by the block index's first keys, merges them, releasing each row once it is written, and uploads the
/// partition's section before it moves on, so it holds one output partition's inputs and output at a time. It checks each
/// input's row count and tag span over all its reads once every output partition is written. Its compactions hold at
/// most about <see cref="ArchiveOptions.CompactionBudgetBytes"/> at once: each output partition acquires an estimate of
/// the memory its reads hold, or the whole budget when that passes it, before it reads them, and releases it once its
/// section is uploaded. An exception leaves any object it uploaded as garbage, which the sweeper reclaims once a commit
/// removes one of the inputs; after corruption no commit does, so its caller compacts the partition no more until it is
/// repaired.
/// </para>
/// <para>
/// A section which breaks its format or contradicts the manifest on a read is read again, once, whole, past the process's
/// caches, before the worker reports it corrupt: a fault of one read never makes corruption (§8.2). Corruption which
/// persists, or two inputs holding a row of one key under one tag, which no read fault makes, counts in
/// <c>orleans.durable.storage.archive.lsm.compaction.corrupt</c>.
/// </para>
/// <para>
/// An input whose object is gone was removed by a newer manifest value and then swept (LSM-I6), or deleted while listed.
/// The worker reads the manifest afresh through the process's view: a value which still lists that input's section shows
/// a listed object deleted, corruption which counts as above (§2.4); otherwise it plans the partition's compaction again
/// from the fresh value, at most <see cref="Attempts"/> times in all.
/// </para>
/// </remarks>
public sealed class CompactionWorker
{
    private readonly IObjectStore _store;
    private readonly TailCache _tails;
    private readonly ManifestView _manifest;
    private readonly ArchiveOptions _options;
    private readonly BlockCache? _blocks;
    private readonly long _maxObjectLength;
    private readonly ByteBudget _budget;

    /// <summary>
    /// Gets or sets a value indicating whether an expiry drops without checking whether lower live ledgers are complete,
    /// as the model's <c>drop-ignoring-live</c> mutant does (LSM-I5).
    /// </summary>
    internal bool DropIgnoringLiveMutant { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether an expiry drops without checking outside sections, as the model's
    /// <c>drop-ignoring-sections</c> mutant does (LSM-I5).
    /// </summary>
    internal bool DropIgnoringSectionsMutant { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a <c>Replace</c> or <c>Delete</c> of an incomplete ledger supersedes older
    /// rows, as the model's <c>supersede-by-open</c> mutant does (LSM-I4, LSM-I5).
    /// </summary>
    internal bool SupersedeByOpenMutant { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="CompactionWorker"/> class.
    /// </summary>
    /// <param name="store">The object store holding the inputs, to which the outputs are uploaded.</param>
    /// <param name="tails">The process's cache of verified tails, which its archive store shares.</param>
    /// <param name="manifest">The process's manifest view, through which the worker reads the manifest afresh when an
    /// input's object is gone.</param>
    /// <param name="options">The sections' settings, the geometry (the grid step, <c>k</c>, and <c>s</c>), and the budget
    /// of input bytes.</param>
    /// <param name="blocks">The process's volatile-disk block cache, whose inputs bypass admission and through which
    /// compaction outputs are written, or <see langword="null"/> when none is configured.</param>
    /// <exception cref="ArgumentOutOfRangeException">The options are invalid.</exception>
    public CompactionWorker(IObjectStore store, TailCache tails, ManifestView manifest, ArchiveOptions options, BlockCache? blocks = null)
        : this(store, tails, manifest, options, RunObjectFormat.MaxObjectLength, blocks)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CompactionWorker"/> class which closes an output object before its
    /// projected length passes <paramref name="maxObjectLength"/>, so tests reach several objects with small rows.
    /// </summary>
    internal CompactionWorker(IObjectStore store, TailCache tails, ManifestView manifest, ArchiveOptions options, long maxObjectLength, BlockCache? blocks = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(tails);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(options);

        // The worker holds the values it validated, whatever its caller changes later (archive-lsm.md §8.8).
        options = options with { };
        options.Validate();
        ArgumentOutOfRangeException.ThrowIfLessThan(maxObjectLength, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxObjectLength, RunObjectFormat.MaxObjectLength);
        _store = store;
        _tails = tails;
        _manifest = manifest;
        _options = options;
        _blocks = blocks;
        _maxObjectLength = maxObjectLength;
        _budget = new ByteBudget(options.CompactionBudgetBytes);
    }

    /// <summary>
    /// The managed bytes a row read for a compaction holds beside its encoding: its <see cref="TaggedRow"/>, its key's
    /// array header, and its payload's lease, about 180 bytes; a lease charges each row these and its key's length.
    /// </summary>
    internal const int RowOverheadBytes = 180;

    /// <summary>
    /// The plans a compaction makes at most: its first, and one from each manifest read afresh after an input's object was
    /// gone, which a concurrent compaction removed and a sweep then deleted; past them it fails as an unknown outcome.
    /// </summary>
    internal const int Attempts = 3;

    /// <summary>Gets the bytes the budget holds at most at once; for tests.</summary>
    internal long BudgetCapacity => _budget.Capacity;

    /// <summary>Gets the bytes no compaction holds; for tests.</summary>
    internal long AvailableBudgetBytes => _budget.Available;

    /// <summary>
    /// Returns the partitions of <paramref name="manifest"/> which hold a compaction (archive §8.8), each once, in the order
    /// of their first section's position: those whose compactions the composition assigns.
    /// </summary>
    public static ImmutableArray<KeyRange> OverfullPartitions(ManifestValue manifest, ArchiveOptions options)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(options);
        return
        [
            .. manifest.SectionsByPartition()
                .Where(partition => Plan(partition.Key, partition.Value, options) is not null)
                .OrderBy(static partition => partition.Value[0], SectionRef.PositionOrder)
                .Select(static partition => partition.Key),
        ];
    }

    /// <summary>
    /// Compacts <paramref name="partition"/>'s sections in <paramref name="manifest"/> under the geometry (archive §8.8) and
    /// the drop rule (§8.2), uploading the outputs.
    /// </summary>
    /// <param name="partition">The partition to compact.</param>
    /// <param name="manifest">A manifest value read, or one whose replace returned <c>Applied</c>, which the change derives
    /// from (archive §3): its sections are the inputs, and those outside the inputs and its completions decide which
    /// empty <c>Delete</c> rows drop.</param>
    /// <param name="map">A ledger map value read from the register, or one whose replace returned <c>Applied</c>, which
    /// gives the ranges of the live ledgers.</param>
    /// <param name="cancellationToken">Stops the compaction; an object it uploaded is garbage.</param>
    /// <returns>
    /// <see cref="CompactionOutcome.Compacted"/> with the change <c>Compaction(inputs, outputs)</c>, its objects uploaded,
    /// which the caller submits to the manifest committer; or <see cref="CompactionOutcome.NothingToCompact"/> when
    /// <paramref name="manifest"/>, or a manifest read afresh after an input's object was gone, holds no compaction of the
    /// partition. An object uploaded before an input was found gone is garbage.
    /// </returns>
    /// <exception cref="ArchiveCorruptException">An input or an outside section's object breaks its format or contradicts
    /// the manifest, on a read and again on a read past the caches; two inputs hold a row of one key under one tag; or an
    /// input's object is gone while a manifest read afresh lists its section (archive §2.4). The objects uploaded before
    /// it are garbage which the sweeper reclaims only once the inputs leave the manifest, so the caller compacts the
    /// partition no more (archive §8.8).</exception>
    /// <exception cref="IOException">A section broke its format on one read and verified on the read past the caches, a
    /// fault of the read; or every one of <see cref="Attempts"/> plans met an input gone which the next fresh read no longer
    /// listed: the caller compacts again later.</exception>
    public async Task<CompactionOutcome> CompactAsync(KeyRange partition, ManifestValue manifest, LedgerMapValue map, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(map);
        for (var attempt = 1; ; attempt++)
        {
            if (Plan(partition, manifest.SectionsOf(partition), _options) is not { } plan)
            {
                return CompactionOutcome.NothingToCompact.Instance;
            }

            var suspect = new Suspect();
            SectionRef gone;
            try
            {
                var compaction = await CompactAsync(plan, manifest, map, suspect, cancellationToken).ConfigureAwait(false);
                if (compaction.Gone is not { } missing)
                {
                    return compaction.Compacted!;
                }

                gone = missing;
            }
            catch (ArchiveCorruptException corruption)
            {
                gone = await ConfirmAsync(suspect.Section, corruption, cancellationToken).ConfigureAwait(false);
            }

            var fresh = (await _manifest.ReadAsync(cancellationToken).ConfigureAwait(false)).Value;
            if (fresh.Contains(gone))
            {
                ArchiveMetrics.RecordCorruptCompaction();
                throw new ArchiveCorruptException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The object {gone.Object} of the section at offset {gone.Offset} in partition {gone.Partition} is gone while the manifest at epoch {fresh.Epoch} lists the section: a listed object deleted."));
            }

            if (attempt == Attempts)
            {
                throw new IOException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Each of {Attempts} compactions of partition {partition} met an input whose object was gone, which the manifest read afresh no longer listed."));
            }

            manifest = fresh;
        }
    }

    private async Task<Attempt> CompactAsync(CompactionPlan plan, ManifestValue manifest, LedgerMapValue map, Suspect suspect, CancellationToken cancellationToken)
    {
        var tails = await FetchTailsAsync(_tails, plan.Inputs, suspect, cancellationToken).ConfigureAwait(false);
        if (Array.IndexOf(tails, null) is var missing and >= 0)
        {
            return new Attempt(null, plan.Inputs[missing]);
        }

        var metadata = new RunSectionMetadata[plan.Inputs.Length];
        for (var index = 0; index < metadata.Length; index++)
        {
            metadata[index] = SectionOf(tails[index]!, plan.Inputs[index], suspect);
        }

        var totals = new RunObjectFormat.SectionTotals[plan.Inputs.Length];
        using var outputs = new OutputObjects(_store, _options.SectionSettings, plan.Inputs, _maxObjectLength, _blocks);
        var older = new OlderRows(_tails, manifest, map, plan.Inputs, suspect, DropIgnoringLiveMutant, DropIgnoringSectionsMutant, SupersedeByOpenMutant, cancellationToken);
        foreach (var read in OutputReads(plan, metadata))
        {
            using var lease = await _budget.AcquireAsync(Math.Min(read.ChargedBytes, _budget.Capacity), cancellationToken).ConfigureAwait(false);
            var (inputs, gone) = await ReadRangesAsync(plan.Inputs, metadata, read.Partition, suspect, cancellationToken).ConfigureAwait(false);
            if (inputs is null)
            {
                return new Attempt(null, gone);
            }

            await CompactPartitionAsync(inputs, totals, read.Partition, older, outputs, cancellationToken).ConfigureAwait(false);
        }

        // A push reads no input whole, so each input's totals are checked over all its reads; a merge in place's whole
        // reads checked them already, and one check serves both. An input none of whose blocks any output partition
        // reads, as one whose index placed its blocks outside the partition would be, fails here too.
        CheckTotals(plan.Inputs, metadata, totals, suspect);
        var change = new ManifestChange.Compaction(plan.Inputs, outputs.Sections);

        // A sweep deletes the inputs an output's trailer records once the output is listed (archive §7, LSM-I5), so each
        // output records exactly the change's inputs.
        Debug.Assert(outputs.RecordExactly(change.Inputs), "Each output's trailer records exactly the change's inputs.");
        return new Attempt(new CompactionOutcome.Compacted(change), null);
    }

    /// <summary>
    /// Confirms the corruption a compaction met (archive §8.2): a section's, <paramref name="suspect"/>, by reading it
    /// again, once, whole, past the caches; two inputs holding a row of one key under one tag, which no read fault makes,
    /// as found. Confirmed corruption counts in <c>orleans.durable.storage.archive.lsm.compaction.corrupt</c>.
    /// </summary>
    /// <returns>The suspect, when its object is gone since.</returns>
    /// <exception cref="ArchiveCorruptException">The corruption stands.</exception>
    /// <exception cref="IOException">The suspect verified: the first read's fault did not persist.</exception>
    private async Task<SectionRef> ConfirmAsync(SectionRef? suspect, ArchiveCorruptException corruption, CancellationToken cancellationToken)
    {
        if (suspect is null)
        {
            ArchiveMetrics.RecordCorruptCompaction();
            ExceptionDispatchInfo.Throw(corruption);
        }

        try
        {
            if (await RunObjectReader.ReadTailAsync(_store, suspect, cancellationToken).ConfigureAwait(false) is not { } tail)
            {
                return suspect;
            }

            using var rows = await RunObjectReader.ReadSectionAsync(_store, suspect.Object, RunObjectReader.SectionOf(tail, suspect), cancellationToken).ConfigureAwait(false);
            if (rows is null)
            {
                return suspect;
            }
        }
        catch (ArchiveCorruptException)
        {
            ArchiveMetrics.RecordCorruptCompaction();
            throw;
        }

        throw new IOException(
            string.Create(CultureInfo.InvariantCulture, $"The section of {suspect.Object} at offset {suspect.Offset} broke its format on a read and verified on a read past the caches."),
            corruption);
    }

    /// <summary>
    /// Returns the compaction the geometry chooses for <paramref name="partition"/>, whose sections are
    /// <paramref name="sections"/> (archive §8.8): a push of its sections, or a merge in place of those smaller than
    /// <c>s</c>, each capped at <c>2 × F × s</c> bytes; or <see langword="null"/> when it holds at most <c>k</c> sections,
    /// or, at the deepest grid depth, where it never pushes, fewer than two smaller than <c>s</c>.
    /// </summary>
    internal static CompactionPlan? Plan(KeyRange partition, ImmutableArray<SectionRef> sections, ArchiveOptions options)
    {
        if (sections.Length <= options.MaxSectionsPerPartition)
        {
            return null;
        }

        var sectionBytes = options.SectionBytes;
        var pushBytes = (1L << options.GridBits) * sectionBytes;
        var childDepth = partition.Depth + options.GridBits;
        if (sections.Sum(static section => section.Length) >= pushBytes && childDepth <= KeyRange.MaxDepth)
        {
            return new CompactionPlan(partition, Capped(sections, 2 * pushBytes), childDepth);
        }

        // Fewer than F sections hold s or more when they total less than F × s, and more than k ≥ F are listed, so at
        // least two are smaller; only a partition at the deepest grid depth, which never pushes, may hold fewer. Any two
        // of them total less than 2 × s, within the cap.
        ImmutableArray<SectionRef> small = [.. sections.Where(section => section.Length < sectionBytes)];
        return small.Length >= 2 ? new CompactionPlan(partition, Capped(small, 2 * pushBytes), partition.Depth) : null;
    }

    /// <summary>
    /// Returns, in key order, the output partitions of <paramref name="plan"/> whose keys some input's blocks may hold,
    /// each with the bytes its lease charges: an estimate of the memory its reads hold, the first also charging the
    /// inputs' tails, which the compaction holds throughout (<see cref="Charge"/>).
    /// </summary>
    internal static ImmutableArray<OutputRead> OutputReads(CompactionPlan plan, IReadOnlyList<RunSectionMetadata> metadata)
    {
        Debug.Assert(metadata.All(section => section.Entry.Partition == plan.Partition), "Every input lies in the compacted partition, which its output partitions split without gaps or overlaps.");
        var reads = ImmutableArray.CreateBuilder<OutputRead>();
        var tailBytes = plan.Inputs.DistinctBy(static input => input.Object).Sum(static input => input.Tail.Length);
        foreach (var partition in plan.OutputPartitions())
        {
            var charged = 0L;
            foreach (var section in metadata)
            {
                charged += Charge(section, section.Covering(partition));
            }

            if (charged > 0)
            {
                reads.Add(new OutputRead(partition, charged + (reads.Count == 0 ? tailBytes : 0)));
            }
        }

        return reads.DrainToImmutable();
    }

    /// <summary>
    /// Returns the memory a read of <paramref name="blocks"/> of <paramref name="section"/> holds, estimated: the blocks'
    /// bytes, and for each of their share of the section's rows, <see cref="RowOverheadBytes"/> and the average length of
    /// the block index's first keys. Rows move from the input's pages to the output's as they are written, so the output
    /// adds little to it.
    /// </summary>
    internal static long Charge(RunSectionMetadata section, RunBlockSpan blocks)
    {
        if (blocks.Count == 0)
        {
            return 0;
        }

        var entry = section.Entry;
        var index = section.Index.Blocks;
        var averageKey = index.Sum(static block => (long)block.FirstKey.Length) / index.Length;
        var rows = (long)Math.Ceiling((double)entry.RowCount * blocks.Length / entry.Length);
        return blocks.Length + rows * (RowOverheadBytes + averageKey);
    }

    /// <summary>
    /// Fetches the verified tail of each of <paramref name="sections"/>' objects, all at once, through the process's tail
    /// cache, and throws the first fault in their order.
    /// </summary>
    /// <returns>Each section's tail, in order, or <see langword="null"/> where its object is gone.</returns>
    private static async Task<RunTail?[]> FetchTailsAsync(TailCache cache, IReadOnlyList<SectionRef> sections, Suspect suspect, CancellationToken cancellationToken)
    {
        var fetches = new Task<RunTail?>[sections.Count];
        for (var index = 0; index < fetches.Length; index++)
        {
            fetches[index] = cache.GetAsync(sections[index], cancellationToken);
        }

        await ((Task)Task.WhenAll(fetches)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        var tails = new RunTail?[fetches.Length];
        for (var index = 0; index < fetches.Length; index++)
        {
            try
            {
                tails[index] = await fetches[index].ConfigureAwait(false);
            }
            catch (ArchiveCorruptException) when (suspect.Blame(sections[index]))
            {
                throw new UnreachableException();
            }
        }

        return tails;
    }

    /// <summary>Returns <paramref name="section"/>'s metadata from its object's tail, blaming it if it breaks its format.</summary>
    private static RunSectionMetadata SectionOf(RunTail tail, SectionRef section, Suspect suspect)
    {
        try
        {
            return RunObjectReader.SectionOf(tail, section);
        }
        catch (ArchiveCorruptException) when (suspect.Blame(section))
        {
            throw new UnreachableException();
        }
    }

    /// <summary>
    /// Merges <paramref name="inputs"/>, one output partition's rows of each input, which it takes, into
    /// <paramref name="totals"/>; writes the rows the drop rule keeps into <paramref name="partition"/>'s section; and
    /// uploads it.
    /// </summary>
    private static async Task CompactPartitionAsync(
        ArraySegment<TaggedRow>[] inputs,
        RunObjectFormat.SectionTotals[] totals,
        KeyRange partition,
        OlderRows older,
        OutputObjects outputs,
        CancellationToken cancellationToken)
    {
        using var merge = new Merge(inputs);
        for (var input = 0; input < inputs.Length; input++)
        {
            totals[input].Add(inputs[input]);
        }

        outputs.BeginPartition(partition);
        var key = new List<TaggedRow>();
        try
        {
            while (merge.NextKey(key))
            {
                for (var index = await FirstKeptIndexAsync(key, older).ConfigureAwait(false); index < key.Count; index++)
                {
                    var row = key[index];
                    if (!outputs.Fits(row))
                    {
                        await outputs.UploadAsync(cancellationToken).ConfigureAwait(false);
                    }

                    outputs.Add(row);
                }

                Release(key);
            }
        }
        finally
        {
            Release(key);
        }

        await outputs.UploadAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Checks each input's totals over all its reads against its trailer entry (archive §7).
    /// </summary>
    /// <exception cref="ArchiveCorruptException">An input holds other than its entry's row count or tag span.</exception>
    private static void CheckTotals(ImmutableArray<SectionRef> inputs, RunSectionMetadata[] metadata, RunObjectFormat.SectionTotals[] totals, Suspect suspect)
    {
        for (var input = 0; input < inputs.Length; input++)
        {
            if (totals[input].Check(metadata[input].Entry) is { } rejection)
            {
                _ = suspect.Blame(inputs[input]);
                throw new ArchiveCorruptException(string.Create(CultureInfo.InvariantCulture, $"The section of {inputs[input].Object} at offset {inputs[input].Offset} breaks the run object's format: {rejection.Message}"), rejection);
            }
        }
    }

    /// <summary>
    /// Returns <paramref name="sections"/>, in position order, less each which would take their total past
    /// <paramref name="capBytes"/>, the first always kept.
    /// </summary>
    private static ImmutableArray<SectionRef> Capped(ImmutableArray<SectionRef> sections, long capBytes)
    {
        var taken = ImmutableArray.CreateBuilder<SectionRef>(sections.Length);
        var total = 0L;
        foreach (var section in sections)
        {
            if (taken.Count == 0 || total + section.Length <= capBytes)
            {
                taken.Add(section);
                total += section.Length;
            }
        }

        return taken.DrainToImmutable();
    }

    /// <summary>
    /// Returns the index of the first of <paramref name="rows"/>, one key's rows in tag order, which the drop rule keeps:
    /// its last <c>Replace</c> or <c>Delete</c> of a ledger the manifest value shows complete, or the row after it when that
    /// is an empty <c>Delete</c> with no older row of its key outside the compaction; 0 when it has neither (LSM-I5). A
    /// <c>Replace</c> or <c>Delete</c> of a ledger not yet complete, which an open ledger's drain wrote, supersedes nothing:
    /// a reader whose map lists that ledger open scans it only below its own tail, which may lie below the row, and
    /// meets the rows it would supersede only in the store.
    /// </summary>
    private static async ValueTask<int> FirstKeptIndexAsync(List<TaggedRow> rows, OlderRows older)
    {
        var last = rows.FindLastIndex(row => row.Row.Kind is RowKind.Replace or RowKind.Delete && older.CanSupersede(row.Tag.Ledger));
        if (last < 0)
        {
            return 0;
        }

        var (tag, row) = rows[last];
        return IsExpiry(row) && await older.NoneOutsideAsync(row.Key, tag).ConfigureAwait(false) ? last + 1 : last;
    }

    /// <summary>Returns whether <paramref name="row"/> is an expiry: an empty <c>Delete</c>, which holds no tombstone.</summary>
    private static bool IsExpiry(Row row) => row.Kind == RowKind.Delete && row.Body.Length == 0;

    private static void Release(ReadOnlySpan<TaggedRow> rows)
    {
        foreach (var row in rows)
        {
            row.Row.Dispose();
        }
    }

    private static void Release(List<TaggedRow> rows)
    {
        Release(CollectionsMarshal.AsSpan(rows));
        rows.Clear();
    }

    /// <summary>
    /// Reads, from every input at once, the rows whose keys lie in <paramref name="partition"/>.
    /// </summary>
    /// <returns>Each input's rows, which the caller owns, or <see langword="null"/> and the input whose object is gone.</returns>
    private async Task<(ArraySegment<TaggedRow>[]? Rows, SectionRef? Gone)> ReadRangesAsync(ImmutableArray<SectionRef> inputs, RunSectionMetadata[] metadata, KeyRange partition, Suspect suspect, CancellationToken cancellationToken)
    {
        var reads = new Task<ArraySegment<TaggedRow>?>[inputs.Length];
        for (var index = 0; index < inputs.Length; index++)
        {
            reads[index] = ReadRangeAsync(inputs[index], metadata[index], partition, suspect, cancellationToken);
        }

        await ((Task)Task.WhenAll(reads)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (reads.All(static read => read.IsCompletedSuccessfully && read.Result is not null))
        {
            return ([.. reads.Select(static read => read.Result!.Value)], null);
        }

        foreach (var read in reads.Where(static read => read.IsCompletedSuccessfully))
        {
            Release(read.Result ?? default);
        }

        if (reads.FirstOrDefault(static read => !read.IsCompletedSuccessfully) is { } failed)
        {
            _ = await failed.ConfigureAwait(false);
        }

        return (null, inputs[Array.FindIndex(reads, static read => read.Result is null)]);
    }

    /// <summary>One plan's compaction: its outcome, or the input whose object it found gone.</summary>
    private readonly record struct Attempt(CompactionOutcome.Compacted? Compacted, SectionRef? Gone);

    private async Task<ArraySegment<TaggedRow>?> ReadRangeAsync(SectionRef section, RunSectionMetadata metadata, KeyRange partition, Suspect suspect, CancellationToken cancellationToken)
    {
        try
        {
            // Compaction inputs may hit cached blocks but never admit misses or bump S3-FIFO popularity (archive-lsm.md §8.7).
            using var rows = await RunObjectReader.ReadRangeAsync(_store, section.Object, metadata, partition, _blocks, admitToCache: false, cancellationToken).ConfigureAwait(false);
            return rows?.TakeRows();
        }
        catch (ArchiveCorruptException) when (suspect.Blame(section))
        {
            throw new UnreachableException();
        }
    }

    /// <summary>
    /// The section in which a compaction first found corruption, which an exception filter records as the exception passes,
    /// so the exception propagates unchanged; none when the corruption lies between inputs.
    /// </summary>
    private sealed class Suspect
    {
        private SectionRef? _section;

        public SectionRef? Section => Volatile.Read(ref _section);

        /// <summary>Records <paramref name="section"/> unless a section is recorded, and returns false, so the filter lets the exception pass.</summary>
        public bool Blame(SectionRef section)
        {
            _ = Interlocked.CompareExchange(ref _section, section, null);
            return false;
        }
    }

    /// <summary>
    /// Decides whether an older row of a key may lie outside the compaction: in a section the manifest value lists outside
    /// the inputs, or undrained in a live ledger.
    /// </summary>
    private sealed class OlderRows(
        TailCache tails,
        ManifestValue manifest,
        LedgerMapValue map,
        ImmutableArray<SectionRef> inputs,
        Suspect suspect,
        bool dropIgnoringLive,
        bool dropIgnoringSections,
        bool supersedeByOpen,
        CancellationToken cancellationToken)
    {
        private readonly HashSet<SectionRef> _inputs = [.. inputs];
        private List<(LedgerId Id, KeyRange? Range)>? _incomplete;

        /// <summary>
        /// Returns whether a <c>Replace</c> or <c>Delete</c> of <paramref name="ledger"/> may supersede older rows: when
        /// the manifest value shows <paramref name="ledger"/> complete, which stays so in every later value (LSM-I1), the
        /// committing one among them.
        /// </summary>
        public bool CanSupersede(LedgerId ledger) => supersedeByOpen || manifest.IsComplete(ledger);

        /// <summary>
        /// Returns whether no row of <paramref name="key"/> below <paramref name="tag"/> lies outside the compaction. It
        /// fetches the tails of every outside section which may hold one at once, then decides in the view's order.
        /// </summary>
        /// <exception cref="ArchiveCorruptException">An outside section's object is corrupt.</exception>
        public async ValueTask<bool> NoneOutsideAsync(ReadOnlyMemory<byte> key, Tag tag)
        {
            if (!dropIgnoringLive && !LowerLedgersComplete(key.Span, tag.Ledger))
            {
                return false;
            }

            if (dropIgnoringSections)
            {
                return true;
            }

            SectionRef[] outside = [.. manifest.SectionsContaining(key.Span).Where(section => !_inputs.Contains(section) && section.MinTag < tag)];
            var fetched = await FetchTailsAsync(tails, outside, suspect, cancellationToken).ConfigureAwait(false);
            for (var index = 0; index < outside.Length; index++)
            {
                // An object gone since the value was read leaves the question open, which keeps the row.
                if (fetched[index] is not { } tail
                    || SectionOf(tail, outside[index], suspect).Candidates(key.Span).Count > 0)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Returns whether every ledger below <paramref name="ledger"/> whose range may contain <paramref name="key"/> is
        /// complete in the manifest value: one the map lists with another range cannot hold the key, and one whose range
        /// the map does not give must be complete.
        /// </summary>
        private bool LowerLedgersComplete(ReadOnlySpan<byte> key, LedgerId ledger)
        {
            if (ledger >= map.NextLedgerId)
            {
                return false;
            }

            _incomplete ??= Incomplete();
            foreach (var (id, range) in _incomplete)
            {
                if (id >= ledger)
                {
                    break;
                }

                if (range is not { } known || known.Contains(key))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Returns, in ID order, each ledger below the map's next ID which the manifest value does not show complete, with
        /// its range when the map lists it.
        /// </summary>
        private List<(LedgerId Id, KeyRange? Range)> Incomplete()
        {
            var incomplete = new List<(LedgerId, KeyRange?)>();
            for (var id = manifest.DrainedBelow.Value; id < map.NextLedgerId.Value; id++)
            {
                var ledger = new LedgerId(id);
                if (!manifest.IsComplete(ledger))
                {
                    incomplete.Add((ledger, map.Get(ledger)?.Range));
                }
            }

            return incomplete;
        }
    }

    /// <summary>
    /// The inputs' rows merged in order of key, then tag, handed out one key at a time; the rows not yet handed out stay
    /// owned here until <see cref="Dispose"/>. Each row leaves its input's array as it is handed out, so the arrays keep
    /// no released row's key or lease reachable.
    /// </summary>
    private sealed class Merge : IDisposable
    {
        private readonly ArraySegment<TaggedRow>[] _inputs;
        private readonly int[] _next;
        private readonly PriorityQueue<int, TaggedRow> _heads = new(RunObjectFormat.RowOrder);

        public Merge(ArraySegment<TaggedRow>[] inputs)
        {
            _inputs = inputs;
            _next = new int[inputs.Length];
            for (var input = 0; input < inputs.Length; input++)
            {
                if (inputs[input].Count > 0)
                {
                    _heads.Enqueue(input, inputs[input][0]);
                }
            }
        }

        /// <summary>
        /// Moves the next key's rows, in tag order, into <paramref name="rows"/>, which then owns them.
        /// </summary>
        /// <returns>Whether a key remained.</returns>
        /// <exception cref="ArchiveCorruptException">Two inputs hold a row of the key under one tag.</exception>
        public bool NextKey(List<TaggedRow> rows)
        {
            Debug.Assert(rows.Count == 0, "The previous key's rows were released.");
            while (_heads.TryPeek(out var input, out var head) && (rows.Count == 0 || head.Row.Key.Span.SequenceEqual(rows[0].Row.Key.Span)))
            {
                _ = _heads.Dequeue();
                rows.Add(head);
                var rest = _inputs[input];
                rest[_next[input]++] = default;
                if (_next[input] < rest.Count)
                {
                    _heads.Enqueue(input, rest[_next[input]]);
                }

                if (rows.Count > 1 && rows[^2].Tag == head.Tag)
                {
                    throw new ArchiveCorruptException(string.Create(CultureInfo.InvariantCulture, $"Two sections hold a row of one key under tag ({head.Tag.Ledger}, {head.Tag.Slot}, {head.Tag.Index})."));
                }
            }

            return rows.Count > 0;
        }

        public void Dispose()
        {
            for (var input = 0; input < _inputs.Length; input++)
            {
                var rest = _inputs[input].AsSpan(_next[input]);
                Release(rest);
                rest.Clear();
                _next[input] = _inputs[input].Count;
            }
        }
    }
    /// <summary>
    /// The compaction's output objects: each output partition's rows written in order into a section of their own, across
    /// as many objects as keep each within the length bound, each uploaded under a fresh name as it closes.
    /// </summary>
    private sealed class OutputObjects : IDisposable
    {
        private readonly IObjectStore _store;
        private readonly RunSectionSettings _settings;
        private readonly long _maxObjectLength;
        private readonly BlockCache? _blocks;
        private readonly RunObjectCondition.Compaction _condition;
        private readonly long _trailerLength;
        private readonly ImmutableArray<SectionRef>.Builder _sections = ImmutableArray.CreateBuilder<SectionRef>();
        private readonly List<RunObjectCondition> _recorded = [];
        private KeyRange _partition;
        private OpenObject? _open;

        public OutputObjects(IObjectStore store, RunSectionSettings settings, ImmutableArray<SectionRef> inputs, long maxObjectLength, BlockCache? blocks)
        {
            ImmutableArray<SectionPosition> positions = [.. inputs.Select(static input => new SectionPosition(input.Object, input.Offset))];
            _store = store;
            _settings = settings;
            _maxObjectLength = maxObjectLength;
            _blocks = blocks;
            _condition = new RunObjectCondition.Compaction(positions);
            _trailerLength = RunObjectFormat.CompactionTrailerFixedLength(positions);
        }

        /// <summary>Gets the sections of the objects uploaded.</summary>
        public ImmutableArray<SectionRef> Sections => _sections.ToImmutable();

        /// <summary>
        /// Returns whether every uploaded object's trailer records, as its compaction's inputs, exactly the positions of
        /// <paramref name="inputs"/>.
        /// </summary>
        public bool RecordExactly(ImmutableArray<SectionRef> inputs)
        {
            HashSet<SectionPosition> positions = [.. inputs.Select(static input => new SectionPosition(input.Object, input.Offset))];
            return _recorded.TrueForAll(condition => condition.Value is RunObjectCondition.Compaction { Inputs: var recorded } && positions.SetEquals(recorded));
        }

        /// <summary>
        /// Starts the rows of <paramref name="partition"/>, which every object opened until the next call holds the section
        /// of; the previous partition's objects are uploaded.
        /// </summary>
        public void BeginPartition(KeyRange partition)
        {
            Debug.Assert(_open is null, "The previous output partition's objects are uploaded.");
            _partition = partition;
        }

        /// <summary>
        /// Returns whether <paramref name="row"/> fits the open object: no object is open, or the row keeps its projected
        /// length within the bound.
        /// </summary>
        public bool Fits(TaggedRow row) =>
            _open is not { } open || open.ProjectedLength + RunObjectFormat.MaxRowFootprint(row.Row, _settings.BloomBitsPerKey) <= _maxObjectLength;

        /// <summary>
        /// Writes <paramref name="row"/> into the open object's section of the output partition, opening an object when none
        /// is open.
        /// </summary>
        public void Add(TaggedRow row)
        {
            Debug.Assert(Fits(row), "A row which does not fit the open object waits for its upload.");
            if (_open is not { } open)
            {
                open = _open = new OpenObject(_condition, _settings, _partition, _trailerLength + RunObjectFormat.SectionFixedLength(_partition));
                open.Writer.BeginSection(_partition);
            }

            open.Writer.Add(row);
            open.ProjectedLength += RunObjectFormat.MaxRowFootprint(row.Row, _settings.BloomBitsPerKey);
        }

        /// <summary>Uploads the open object, if any, under a fresh name, and records its section.</summary>
        public async Task UploadAsync(CancellationToken cancellationToken)
        {
            if (_open is not { } open)
            {
                return;
            }

            EncodedRunObject encoded;
            using (open)
            {
                _open = null;
                open.Writer.EndSection();
                encoded = open.Writer.Complete();
            }

            using (encoded)
            {
                var name = RunObjectNames.Compaction(open.Partition, RunObjectNames.NewId());
                var created = await ObjectContentChanges.StopOnChangeAsync(() => _store.CreateAsync(name, encoded.Content, encoded.Crc32C, cancellationToken)).ConfigureAwait(false);

                // A fresh random name which exists holds only this compaction's own landed attempt (archive §3).
                Debug.Assert(created.Value is ObjectCreateOutcome.Created or ObjectCreateOutcome.Exists, "A create answers Created or Exists.");
                if (_blocks is not null)
                {
                    await _blocks.AdmitEncodedObjectAsync(name, encoded, cancellationToken).ConfigureAwait(false);
                }

                _sections.AddRange(encoded.SectionRefs(name));
                _recorded.Add(encoded.Trailer.Condition);
            }
        }

        public void Dispose() => _open?.Dispose();

        /// <summary>
        /// An object being written: its writer, its one section's partition, which names it, and its projected length.
        /// </summary>
        private sealed class OpenObject(RunObjectCondition.Compaction condition, RunSectionSettings settings, KeyRange partition, long projectedLength) : IDisposable
        {
            public RunObjectWriter Writer { get; } = new(condition, settings);

            public KeyRange Partition { get; } = partition;

            public long ProjectedLength { get; set; } = projectedLength;

            public void Dispose() => Writer.Dispose();
        }
    }
}

/// <summary>
/// A compaction the geometry chooses (archive §8.8): its partition, its inputs in position order, and the depth of its
/// outputs' partitions, the partition's own for a merge in place, one grid step deeper for a push.
/// </summary>
/// <param name="Partition">The compacted partition.</param>
/// <param name="Inputs">The input sections: the partition's for a push, its smaller ones for a merge in place, up to the cap.</param>
/// <param name="OutputDepth">The depth of the outputs' partitions.</param>
internal sealed record CompactionPlan(KeyRange Partition, ImmutableArray<SectionRef> Inputs, int OutputDepth)
{
    /// <summary>Gets a value indicating whether the compaction pushes its rows into the partition's children.</summary>
    public bool IsPush => OutputDepth > Partition.Depth;

    /// <summary>
    /// Returns the output partitions, in key order: for a push, the <c>2^(OutputDepth − Depth)</c> descendants of the
    /// partition at <see cref="OutputDepth"/>; for a merge in place, the partition itself.
    /// </summary>
    public ImmutableArray<KeyRange> OutputPartitions()
    {
        if (!IsPush)
        {
            return [Partition];
        }

        // The descendant numbered i, in key order, follows the partition's prefix with i's bits, most significant first.
        var bits = OutputDepth - Partition.Depth;
        var partitions = ImmutableArray.CreateBuilder<KeyRange>(1 << bits);
        var prefix = new byte[(OutputDepth + 7) / 8];
        for (var descendant = 0; descendant < 1 << bits; descendant++)
        {
            Array.Clear(prefix);
            Partition.Prefix.CopyTo(prefix);
            for (var bit = 0; bit < bits; bit++)
            {
                if ((descendant >> (bits - 1 - bit) & 1) != 0)
                {
                    var position = Partition.Depth + bit;
                    prefix[position / 8] |= (byte)(0x80 >> (position % 8));
                }
            }

            partitions.Add(new KeyRange(prefix, OutputDepth));
        }

        return partitions.MoveToImmutable();
    }
}

/// <summary>
/// One output partition of a compaction (archive §8.2) and the bytes its lease charges the compaction budget: an estimate
/// of the memory its reads hold, and, for the first, the inputs' tails.
/// </summary>
/// <param name="Partition">The output partition.</param>
/// <param name="ChargedBytes">The bytes its lease charges.</param>
internal readonly record struct OutputRead(KeyRange Partition, long ChargedBytes);
