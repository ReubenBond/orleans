using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Orleans.Durable.Storage.Engine.Archive.Control;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.ObjectStore;

namespace Orleans.Durable.Storage.Engine.Archive.Lsm.Control;

/// <summary>
/// Copies a ledger's decided rows into the LSM (archive §2.3, §8.1), one whole closed segment at a time: it scans the
/// segment which starts at the ledger's watermark, uploads its rows sorted by key and tag in sections of fresh drain
/// outputs, and commits the change through the manifest committer, so every watermark is a segment start or <c>T</c>
/// (LSM-I2).
/// </summary>
/// <remarks>
/// <para>
/// A drain of <c>S</c> starts from the later of this worker's last applied drain of <c>S</c> and the watermark its
/// process's manifest view holds, which needs no read; on its first drain of <c>S</c> from the view, reading the manifest
/// when the view holds none. Every watermark is a segment start, and every drain takes the whole segment at its start, so
/// two drains from one watermark reach the same end: the later commit finds its goal and answers <c>Applied</c>, its upload
/// garbage the sweeper reclaims. No drain is superseded, which a Debug assertion checks (LSM-I2). A commit whose outcome
/// is unknown leaves the change pending, and the next drain of <c>S</c> submits it again before uploading anything.
/// </para>
/// <para>
/// A segment closes by evidence every process sees alike (ledgers.md §2.2): the next decided batch with the start flag,
/// which the scan reads below a contiguous tail, or the closed ledger's <c>T</c>. The scan resumes after backpressure from
/// the slot it reached, with its segment view, under bounded backoff. A drain holds the segment's rows, each key's bytes
/// once, and one object at a time, within the process's drain memory, <see cref="ArchiveOptions.DrainMemoryBytes"/>,
/// which it charges as it scans; its objects end near <see cref="ArchiveOptions.SectionBytes"/>, at the grid partition
/// containing the ledger's range (archive §8.8), and one change commits them all. Every section holds only the ledger's
/// decided rows of the segment, and the drain's sections together hold exactly them (LSM-I2), which the writer checks
/// each object reads back as.
/// </para>
/// </remarks>
public sealed class DrainWorker
{
    // The bytes an array of a key takes beyond the key's, which a drain charges once a key.
    private const long KeyArrayBytes = 24;

    // A row's entry in the drain's array, which a drain charges for each row.
    private static readonly long RowEntryBytes = Unsafe.SizeOf<DrainedRow>();

    private readonly IObjectStore _store;
    private readonly ManifestView _manifest;
    private readonly LedgerReader _reader;
    private readonly BlockCache? _blocks;
    private readonly TimeProvider _time;
    private readonly RunSectionSettings _settings;
    private readonly int _gridBits;
    private readonly long _objectBytes;
    private readonly ByteBudget _budget;
    private readonly Lock _sync = new();
    private readonly Dictionary<LedgerId, ResumePoint> _resume = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="DrainWorker"/> class.
    /// </summary>
    /// <param name="store">The object store the drain outputs are uploaded to.</param>
    /// <param name="manifest">The process's manifest view, from which a drain of a ledger takes its watermark.</param>
    /// <param name="reader">Reads the ledgers' decided batches.</param>
    /// <param name="options">The sections' settings and size, the grid step, and the drain memory.</param>
    /// <param name="timeProvider">The clock of the scans' backoff after backpressure.</param>
    /// <param name="blocks">The process's volatile-disk block cache, through which drain outputs are written, or <see langword="null"/> when none is configured.</param>
    /// <exception cref="ArgumentOutOfRangeException">The options are invalid.</exception>
    public DrainWorker(IObjectStore store, ManifestView manifest, LedgerReader reader, ArchiveOptions options, TimeProvider timeProvider, BlockCache? blocks = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        options = options with { };
        options.Validate();
        _store = store;
        _manifest = manifest;
        _reader = reader;
        _blocks = blocks;
        _time = timeProvider;
        _settings = options.SectionSettings;
        _gridBits = options.GridBits;
        _objectBytes = options.SectionBytes;
        _budget = new ByteBudget(options.DrainMemoryBytes);
    }

    /// <summary>
    /// Gets a value indicating whether the worker is the <c>lsm-mid-segment-watermark</c> mutant, which drains to its bound
    /// inside a segment, as the rejected drain to the contiguous tail did, and starts from a watermark wherever it lies.
    /// </summary>
    internal bool DrainsInsideSegmentsMutant { get; init; }

    /// <summary>Gets how many times a scan resumes after backpressure before the drain answers it; at least 1.</summary>
    internal int BackpressureRetries { get; init; } = 8;

    /// <summary>Gets the first wait before a scan resumes after backpressure, which doubles, jittered, up to a second.</summary>
    internal TimeSpan BackpressureDelay { get; init; } = TimeSpan.FromMilliseconds(20);

    /// <summary>Gets the drain memory no drain holds; for tests.</summary>
    internal long AvailableMemoryBytes => _budget.Available;

    /// <summary>Gets how many segments this worker drained alone, holding the whole drain memory, which the composition reports.</summary>
    public long SegmentsDrainedAlone => Scan.DrainedAlone;

    private DrainScan Scan => field ??= new DrainScan(_reader, _budget, _time, BackpressureRetries, BackpressureDelay);

    /// <summary>
    /// Drains the closed segment of <paramref name="ledger"/> which starts at its watermark, if it closes below
    /// <paramref name="below"/>, and commits the change through <paramref name="commit"/>.
    /// </summary>
    /// <param name="ledger">The ledger, as a ledger map value the caller read lists it.</param>
    /// <param name="below">
    /// The slot below which the drain may read: a contiguous tail of the ledger which <paramref name="ledger"/>'s reader
    /// learned after the map was read, or, for a closed ledger, at most its recovered tail <c>T</c>.
    /// </param>
    /// <param name="commit">Submits a change once to the manifest committer, as its <c>CommitAsync</c> does.</param>
    /// <param name="cancellationToken">Stops the drain; an object it uploaded is garbage, which the sweeper reclaims.</param>
    /// <returns>
    /// <see cref="DrainOutcome.Drained"/> with the change <c>Drain(S, a, b, sections, b = T)</c>, the segment's rows
    /// uploaded, and the committer's answer, where <c>a</c> is the segment's start and <c>b</c> its end, the slot of the
    /// next decided batch which starts a segment, or the closed ledger's <c>T</c>, after which the caller drains again
    /// while <c>b</c> lies below its bound; <see cref="DrainOutcome.OpenSegment"/> when the segment does not close below
    /// <paramref name="below"/>; <see cref="DrainOutcome.Covered"/> when a manifest value shows the ledger complete or its
    /// watermark at or past <paramref name="below"/>; or the scan's <see cref="ReplicaWriteOutcome.Dropped"/>, the ledger
    /// has left the map, or <see cref="ReplicaWriteOutcome.Backpressure"/> before the segment's end once the scan's
    /// retries ran out, which asserts nothing. The last four upload and submit nothing.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="below"/> lies past a closed ledger's <c>T</c>, or, for an
    /// open ledger, past the contiguous tail the reader knows.</exception>
    /// <exception cref="ArchiveCorruptException">The manifest breaks its format, records a watermark past the closed
    /// ledger's <c>T</c>, or records one at a batch which does not start a segment (LSM-I2).</exception>
    /// <exception cref="DrainMemoryExceededException">The segment's rows pass the drain memory alone.</exception>
    /// <remarks>
    /// An exception from <paramref name="commit"/> leaves the change's outcome unknown, and the next drain of the ledger
    /// submits it again. Any other exception leaves the drain undone; an object it uploaded is garbage.
    /// </remarks>
    public async Task<DrainOutcome> DrainAsync(
        LedgerMetadata ledger,
        Slot below,
        Func<ManifestChange, CancellationToken, Task<ManifestCommitOutcome>> commit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commit);
        Slot? t = ledger.Status.Value is LedgerStatus.Closed closed ? closed.T : null;
        if (t is { } end)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(below, end);
        }

        while (true)
        {
            var resume = Resume(ledger.Id);
            if (resume.Value is ResumePoint.Pending pending)
            {
                return await CommitAsync(pending.Change, commit, cancellationToken).ConfigureAwait(false);
            }

            if (await StartAsync(ledger.Id, resume, cancellationToken).ConfigureAwait(false) is not { } start)
            {
                return DrainOutcome.Covered.Instance;
            }

            var a = start.Slot;
            if (a > t)
            {
                throw new ArchiveCorruptException(string.Create(CultureInfo.InvariantCulture, $"{start.Source} ledger {ledger.Id}'s watermark at {a}, past its T {t}."));
            }

            if (a > below)
            {
                return DrainOutcome.Covered.Instance;
            }

            if (a == below)
            {
                // A drain of no slots only completes its ledger, uploading nothing.
                return below == t
                    ? await CommitAsync(new ManifestChange.Drain(ledger.Id, a, below, [], complete: true), commit, cancellationToken).ConfigureAwait(false)
                    : DrainOutcome.Covered.Instance;
            }

            var scanned = await ScanAsync(ledger, start, below, below == t, cancellationToken).ConfigureAwait(false);
            switch (scanned.Value)
            {
                case Segment segment:
                    using (segment)
                    {
                        if (DrainedSince(ledger.Id, segment.End))
                        {
                            // The view learned of another drain of the segment while this one scanned it: start again from there.
                            continue;
                        }

                        var sections = await UploadAsync(ledger, a, segment, cancellationToken).ConfigureAwait(false);
                        return await CommitAsync(new ManifestChange.Drain(ledger.Id, a, segment.End, sections, complete: segment.End == t), commit, cancellationToken).ConfigureAwait(false);
                    }

                case DrainOutcome.OpenSegment open:
                    return open;
                case ReplicaWriteOutcome.Dropped dropped:
                    Forget(ledger.Id);
                    return dropped;
                case ReplicaWriteOutcome.Backpressure backpressure:
                    return backpressure;
                case null:
                default:
                    throw new UnreachableException("conventions.md §15: a scan answers a union which holds a case.");
            }
        }
    }

    /// <summary>
    /// Returns the deepest grid partition containing <paramref name="range"/>: its ancestor at the greatest multiple of
    /// <paramref name="gridBits"/> at or above its depth.
    /// </summary>
    internal static KeyRange GridPartition(KeyRange range, int gridBits)
    {
        var depth = range.Depth - range.Depth % gridBits;
        while (range.Depth > depth)
        {
            range = range.Parent;
        }

        return range;
    }

    /// <summary>
    /// Scans the segment of <paramref name="ledger"/> which starts at <paramref name="start"/>, below
    /// <paramref name="below"/>, through the stores' shared <see cref="DrainScan"/>, within the drain memory.
    /// </summary>
    /// <returns>
    /// The segment, its rows and its end, which the caller disposes; or <see cref="DrainOutcome.OpenSegment"/>,
    /// <see cref="ReplicaWriteOutcome.Dropped"/>, or <see cref="ReplicaWriteOutcome.Backpressure"/>.
    /// </returns>
    /// <exception cref="DrainMemoryExceededException">The segment's rows pass the drain memory alone.</exception>
    private async Task<Scanned> ScanAsync(LedgerMetadata ledger, Start start, Slot below, bool boundIsEnd, CancellationToken cancellationToken)
    {
        Segment? segment = new(new LedgerSegmentId(ledger.Id, start.Slot), _settings.BloomBitsPerKey);
        try
        {
            var scanned = await Scan.ScanAsync(
                ledger,
                segment,
                below,
                boundIsEnd,
                () => new ArchiveCorruptException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{start.Source} ledger {ledger.Id}'s watermark at {start.Slot}, whose batch does not start a segment (LSM-I2).")),
                bySegment: !DrainsInsideSegmentsMutant,
                cancellationToken).ConfigureAwait(false);
            switch (scanned.Value)
            {
                case Slot:
                    var rows = segment;
                    segment = null;
                    return rows;
                case DrainScan.Open:
                    return new DrainOutcome.OpenSegment(start.Slot);
                case ReplicaWriteOutcome.Dropped dropped:
                    return dropped;
                case ReplicaWriteOutcome.Backpressure backpressure:
                    return backpressure;
                case null:
                default:
                    throw new UnreachableException("conventions.md §15: a scan answers a union which holds a case.");
            }
        }
        finally
        {
            segment?.Dispose();
        }
    }
    /// <summary>
    /// Sorts the segment's rows by key and tag and uploads them as one section of a fresh drain output each, an output ending
    /// before the row whose projected length would pass the section size, releasing each output's rows once it is written;
    /// returns their sections, none when there is no row.
    /// </summary>
    private async Task<ImmutableArray<SectionRef>> UploadAsync(LedgerMetadata ledger, Slot from, Segment segment, CancellationToken cancellationToken)
    {
        if (segment.Count == 0)
        {
            return [];
        }

        segment.Sort();
        var partition = GridPartition(ledger.Range, _gridBits);
        var fixedLength = RunObjectFormat.DrainOutputFixedLength(partition);
        var sections = ImmutableArray.CreateBuilder<SectionRef>();
        var first = 0;
        var projected = fixedLength;
        for (var index = 0; index < segment.Count; index++)
        {
            ref readonly var row = ref segment.RowAt(index);
            var footprint = RunObjectFormat.MaxRowFootprint(row.KeyLength, row.Body.Length, _settings.BloomBitsPerKey);

            // An object holds at least one row, and ends before the row which would take it past the section size.
            if (index > first && projected + footprint > _objectBytes)
            {
                sections.AddRange(await UploadObjectAsync(ledger, from, partition, segment, first, index, projected, cancellationToken).ConfigureAwait(false));
                (first, projected) = (index, fixedLength);
            }

            projected += footprint;
        }

        sections.AddRange(await UploadObjectAsync(ledger, from, partition, segment, first, segment.Count, projected, cancellationToken).ConfigureAwait(false));
        return sections.ToImmutable();
    }

    /// <summary>
    /// Writes the segment's rows from <paramref name="first"/> below <paramref name="end"/> as one section of a fresh drain
    /// output, releasing them once written, uploads the object, and returns its section.
    /// </summary>
    private async Task<ImmutableArray<SectionRef>> UploadObjectAsync(LedgerMetadata ledger, Slot from, KeyRange partition, Segment segment, int first, int end, long projected, CancellationToken cancellationToken)
    {
        Debug.Assert(projected <= RunObjectFormat.MaxObjectLength, "A drain output fits a run object: its rows come from batches within the payload limit (archive §7).");
        EncodedRunObject encoded;
        using (var writer = new RunObjectWriter(new RunObjectCondition.Drain(ledger.Id, from), _settings, (int)Math.Min(projected, int.MaxValue)))
        {
            writer.BeginSection(partition);
            for (var index = first; index < end; index++)
            {
                ref readonly var row = ref segment.RowAt(index);
                writer.Add(row.Tag, segment.KeyOf(row), row.Kind, row.Body);
            }

            writer.EndSection();
            encoded = writer.Complete();
        }

        using (encoded)
        {
            segment.Release(end);
            var name = RunObjectNames.Drain(ledger.Id, from, RunObjectNames.NewId());
            var created = await ObjectContentChanges.StopOnChangeAsync(() => _store.CreateAsync(name, encoded.Content, encoded.Crc32C, cancellationToken)).ConfigureAwait(false);

            // A fresh random name which exists holds only this drain's own landed attempt (archive §3, conventions §6).
            Debug.Assert(created.Value is ObjectCreateOutcome.Created or ObjectCreateOutcome.Exists, "A create answers Created or Exists.");
            if (_blocks is not null)
            {
                await _blocks.AdmitEncodedObjectAsync(name, encoded, cancellationToken).ConfigureAwait(false);
            }

            return encoded.SectionRefs(name);
        }
    }

    /// <summary>
    /// Submits <paramref name="change"/>, pending until the committer answers, and records the answer: the next drain of
    /// its ledger starts from its end after <c>Applied</c>, and from a value at least as new as the deciding one after
    /// <c>Superseded</c>, which the committer's contract allows and no drain of a whole segment meets.
    /// </summary>
    private async Task<DrainOutcome> CommitAsync(
        ManifestChange.Drain change,
        Func<ManifestChange, CancellationToken, Task<ManifestCommitOutcome>> commit,
        CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            _resume[change.Ledger] = new ResumePoint.Pending(change);
        }

        var outcome = await commit(change, cancellationToken).ConfigureAwait(false);
        Debug.Assert(
            outcome.Value is not ManifestCommitOutcome.Superseded || DrainsInsideSegmentsMutant,
            "LSM-I1, LSM-I2: a drain of a whole segment from its start is never superseded; another drain from that watermark reaches the same end.");
        ResumePoint next = outcome switch
        {
            ManifestCommitOutcome.Applied when change.Complete => ResumePoint.Complete.Instance,
            ManifestCommitOutcome.Applied => new ResumePoint.From(change.Below),
            ManifestCommitOutcome.Superseded superseded => new ResumePoint.AtLeast(superseded.Epoch),
            null => throw new InvalidOperationException("The committer answered no outcome."),
        };

        lock (_sync)
        {
            // A start only moves forward: an applied drain's end replaces an older one, never a newer one.
            _resume[change.Ledger] = _resume.GetValueOrDefault(change.Ledger).Value is ResumePoint.From from && next.Value is ResumePoint.From to && from.Below > to.Below
                ? from
                : next;
        }

        return new DrainOutcome.Drained(change, outcome);
    }

    /// <summary>
    /// Returns whether the view's current value, which needs no read, shows the ledger's rows below <paramref name="end"/>
    /// drained, recording the start it gives.
    /// </summary>
    private bool DrainedSince(LedgerId ledger, Slot end)
    {
        if (_manifest.Current?.Value is not { } current)
        {
            return false;
        }

        if (current.IsComplete(ledger))
        {
            Record(ledger, ResumePoint.Complete.Instance);
            return true;
        }

        var watermark = current.RecordedWatermark(ledger);
        if (watermark < end)
        {
            return false;
        }

        Record(ledger, new ResumePoint.From(watermark));
        return true;
    }

    private ResumePoint Resume(LedgerId ledger)
    {
        lock (_sync)
        {
            return _resume.GetValueOrDefault(ledger);
        }
    }

    private void Record(LedgerId ledger, ResumePoint next)
    {
        lock (_sync)
        {
            _resume[ledger] = next;
        }
    }

    private void Forget(LedgerId ledger)
    {
        lock (_sync)
        {
            _ = _resume.Remove(ledger);
        }
    }

    /// <summary>
    /// Returns the watermark the drain of <paramref name="ledger"/> starts from, with what gave it, or
    /// <see langword="null"/> when a manifest value, or this worker's own applied drain, shows the ledger complete. A
    /// worker keeps a ledger it completed until its view shows it below <c>DrainedBelow</c>, so a view which lags the
    /// worker's own commit never starts it again from an old watermark.
    /// </summary>
    private async Task<Start?> StartAsync(LedgerId ledger, ResumePoint resume, CancellationToken cancellationToken)
    {
        ManifestSnapshot snapshot;
        switch (resume.Value)
        {
            case ResumePoint.From from:
                if (_manifest.Current?.Value is { } current)
                {
                    if (current.IsComplete(ledger))
                    {
                        Record(ledger, ResumePoint.Complete.Instance);
                        return null;
                    }

                    if (current.RecordedWatermark(ledger) > from.Below)
                    {
                        return new Start(current.RecordedWatermark(ledger), "The manifest records");
                    }
                }

                return new Start(from.Below, "This worker's last applied drain left");
            case ResumePoint.Complete:
                if (_manifest.Current?.Value.DrainedBelow > ledger)
                {
                    Forget(ledger);
                }

                return null;
            case ResumePoint.AtLeast atLeast:
                snapshot = await _manifest.RefreshAsync(atLeast.Epoch, cancellationToken).ConfigureAwait(false);
                Forget(ledger);
                break;
            default:
                snapshot = _manifest.Current ?? await _manifest.ReadAsync(cancellationToken).ConfigureAwait(false);
                break;
        }

        return snapshot.Value.IsComplete(ledger) ? null : new Start(snapshot.Value.RecordedWatermark(ledger), "The manifest records");
    }

    /// <summary>Where a drain starts: a watermark, and what gave it, which names a corruption's source.</summary>
    private readonly record struct Start(Slot Slot, string Source);

    /// <summary>
    /// Where the next drain of a ledger starts (conventions.md §15): from an applied drain's end, from a manifest value at
    /// an epoch or later, by submitting a change whose outcome is unknown again, or nowhere, its ledger complete. The
    /// default, no value, takes the manifest view's.
    /// </summary>
    private readonly union ResumePoint(ResumePoint.From, ResumePoint.AtLeast, ResumePoint.Pending, ResumePoint.Complete)
    {
        public sealed record From(Slot Below);

        public sealed record AtLeast(ulong Epoch);

        public sealed record Pending(ManifestChange.Drain Change);

        public sealed record Complete
        {
            private Complete()
            {
            }

            public static Complete Instance { get; } = new();
        }
    }

    /// <summary>What a segment's scan gives: the segment, which its caller disposes, or why it gives none.</summary>
    private readonly union Scanned(Segment, DrainOutcome.OpenSegment, ReplicaWriteOutcome.Dropped, ReplicaWriteOutcome.Backpressure);

    /// <summary>One drained row: its tag, key span in the segment's key arena, kind, owning batch index (-1 for empty body), and alias body.</summary>
    private readonly record struct DrainedRow(Tag Tag, int KeyOffset, int KeyLength, RowKind Kind, int BatchIndex, ArcBuffer Body);

    /// <summary>A key's location in a segment's pooled key arena; <see cref="Length"/> is 0 while unset.</summary>
    private readonly record struct KeySpan(int Offset, int Length);

    /// <summary>Orders drained rows by key, compared bytewise, then by tag, without virtual comparer dispatch.</summary>
    private readonly struct DrainedRowOrder(byte[] keys) : IComparer<DrainedRow>
    {
        public int Compare(DrainedRow left, DrainedRow right) =>
            RunObjectFormat.CompareRows(keys.AsSpan(left.KeyOffset, left.KeyLength), left.Tag, keys.AsSpan(right.KeyOffset, right.KeyLength), right.Tag);
    }

    /// <summary>
    /// A segment's rows, held in pooled arrays over one retained <see cref="ArcBuffer"/> per batch with non-empty bodies
    /// and a pooled key-byte arena, releasing each batch once all of its rows have been written.
    /// </summary>
    private sealed class Segment(LedgerSegmentId id, int bloomBitsPerKey) : DrainRows(id)
    {
        private DrainedRow[] _rows = [];
        private ArcBuffer[] _batches = [];
        private int[] _batchRemaining = [];
        private int _batchCount;
        private KeySpan[] _keySpans = [];
        private int _keySpanCapacityUsed;
        private byte[] _keyArena = [];
        private int _keyBytesUsed;
        private int _released;

        /// <summary>Gets the number of the segment's rows.</summary>
        public int Count { get; private set; }

        public ref readonly DrainedRow RowAt(int index) => ref _rows[index];

        public ReadOnlySpan<byte> KeyOf(in DrainedRow row) => _keyArena.AsSpan(row.KeyOffset, row.KeyLength);

        public void Sort() => MemoryExtensions.Sort(_rows.AsSpan(0, Count), new DrainedRowOrder(_keyArena));

        /// <summary>Releases the rows below <paramref name="end"/> not yet released, disposing each batch once its last row is released.</summary>
        public void Release(int end)
        {
            for (; _released < end; _released++)
            {
                var batchIndex = _rows[_released].BatchIndex;
                if (batchIndex >= 0 && --_batchRemaining[batchIndex] == 0)
                {
                    _batches[batchIndex].Dispose();
                    _batches[batchIndex] = default;
                }
            }
        }

        /// <summary>
        /// Adds the batch's resolved rows, retaining the batch's buffer at most once for all non-empty bodies in the batch
        /// and storing each distinct key's bytes once in a pooled arena, and returns their charge: each row's entry, body,
        /// and projected length in a drain output, and each key's bytes once. A defining row stores the LSM's plan of every
        /// write, the empty token (HI-I4).
        /// </summary>
        protected internal override long Add(LedgerScanItem item)
        {
            long charge = 0;
            ArcBuffer batchBuffer = default;
            if (item.Read.TryGetValue(out LedgerRead.Data data))
            {
                Reserve(data.Count);
                batchBuffer = data.Batch;
            }

            var batchIndex = -1;
            ArcBuffer cursor = default;
            var cursorOffset = 0;
            foreach (var row in item.Rows)
            {
                Reserve(1);
                var key = row.Key;
                if (row.IsDefining)
                {
                    StoredTokens.AssertPlanned(Id, row.Token, default);
                    charge += key.Length + KeyArrayBytes;
                }

                var keySpan = InternKey((int)row.Ordinal, key);

                ArcBuffer body;
                int ownerBatch;
                if (row.BodyLength == 0)
                {
                    body = default;
                    ownerBatch = -1;
                }
                else
                {
                    if (batchIndex < 0)
                    {
                        var retained = batchBuffer.Retain();
                        batchIndex = AppendBatch(retained);
                        cursor = retained;
                        cursorOffset = 0;
                    }

                    // Rows arrive in ascending offset order within the batch, so advancing cursor keeps slicing O(1).
                    var delta = row.BatchBodyOffset - cursorOffset;
                    if (delta > 0)
                    {
                        cursor = cursor.UnsafeSlice(delta, cursor.Length - delta);
                        cursorOffset = row.BatchBodyOffset;
                    }

                    body = cursor.UnsafeSlice(0, row.BodyLength);
                    ownerBatch = batchIndex;
                    _batchRemaining[batchIndex]++;
                }

                _rows[Count++] = new DrainedRow(row.Tag, keySpan.Offset, keySpan.Length, row.Kind, ownerBatch, body);
                charge += RowEntryBytes + row.BodyLength + RunObjectFormat.MaxRowFootprint(key.Length, row.BodyLength, bloomBitsPerKey);
            }

            return charge;
        }

        protected internal override void Clear()
        {
            Release(Count);
            for (var index = 0; index < _batchCount; index++)
            {
                if (_batches[index].Length > 0)
                {
                    _batches[index].Dispose();
                    _batches[index] = default;
                }
            }

            if (_batches.Length > 0)
            {
                ArrayPool<ArcBuffer>.Shared.Return(_batches, clearArray: true);
            }

            if (_batchRemaining.Length > 0)
            {
                ArrayPool<int>.Shared.Return(_batchRemaining, clearArray: false);
            }

            if (_keySpans.Length > 0)
            {
                ArrayPool<KeySpan>.Shared.Return(_keySpans, clearArray: false);
            }

            if (_keyArena.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(_keyArena, clearArray: false);
            }

            if (_rows.Length > 0)
            {
                ArrayPool<DrainedRow>.Shared.Return(_rows, clearArray: true);
            }

            (_rows, _batches, _batchRemaining, _batchCount, _keySpans, _keySpanCapacityUsed, _keyArena, _keyBytesUsed, Count, _released) =
                ([], [], [], 0, [], 0, [], 0, 0, 0);
        }

        private int AppendBatch(ArcBuffer retained)
        {
            if (_batchCount == _batches.Length)
            {
                var nextSize = Math.Max(16, 2 * _batches.Length);
                var nextBatches = ArrayPool<ArcBuffer>.Shared.Rent(nextSize);
                var nextRemaining = ArrayPool<int>.Shared.Rent(nextBatches.Length);
                if (_batchCount > 0)
                {
                    Array.Copy(_batches, nextBatches, _batchCount);
                    Array.Copy(_batchRemaining, nextRemaining, _batchCount);
                    ArrayPool<ArcBuffer>.Shared.Return(_batches, clearArray: true);
                    ArrayPool<int>.Shared.Return(_batchRemaining, clearArray: false);
                }

                _batches = nextBatches;
                _batchRemaining = nextRemaining;
            }

            var index = _batchCount++;
            _batches[index] = retained;
            _batchRemaining[index] = 0;
            return index;
        }

        private KeySpan InternKey(int ordinal, ReadOnlySpan<byte> key)
        {
            if (ordinal >= _keySpanCapacityUsed)
            {
                if (ordinal >= _keySpans.Length)
                {
                    var next = ArrayPool<KeySpan>.Shared.Rent(Math.Max(ordinal + 1, Math.Max(16, 2 * _keySpans.Length)));
                    if (_keySpanCapacityUsed > 0)
                    {
                        Array.Copy(_keySpans, next, _keySpanCapacityUsed);
                    }

                    if (_keySpans.Length > 0)
                    {
                        ArrayPool<KeySpan>.Shared.Return(_keySpans, clearArray: false);
                    }

                    _keySpans = next;
                }

                Array.Clear(_keySpans, _keySpanCapacityUsed, ordinal + 1 - _keySpanCapacityUsed);
                _keySpanCapacityUsed = ordinal + 1;
            }

            ref var existing = ref _keySpans[ordinal];
            if (existing.Length != 0)
            {
                return existing;
            }

            var needed = _keyBytesUsed + key.Length;
            if (needed > _keyArena.Length)
            {
                var next = ArrayPool<byte>.Shared.Rent(Math.Max(needed, Math.Max(256, 2 * _keyArena.Length)));
                if (_keyBytesUsed > 0)
                {
                    Buffer.BlockCopy(_keyArena, 0, next, 0, _keyBytesUsed);
                }

                if (_keyArena.Length > 0)
                {
                    ArrayPool<byte>.Shared.Return(_keyArena, clearArray: false);
                }

                _keyArena = next;
            }

            var offset = _keyBytesUsed;
            key.CopyTo(_keyArena.AsSpan(offset, key.Length));
            _keyBytesUsed = needed;
            existing = new KeySpan(offset, key.Length);
            return existing;
        }

        /// <summary>Makes room for <paramref name="rows"/> more rows, renting a larger array when they pass this one.</summary>
        private void Reserve(int rows)
        {
            if (Count + rows <= _rows.Length)
            {
                return;
            }

            var larger = ArrayPool<DrainedRow>.Shared.Rent(Math.Max(Count + rows, Math.Max(16, 2 * _rows.Length)));
            Array.Copy(_rows, larger, Count);
            if (_rows.Length > 0)
            {
                ArrayPool<DrainedRow>.Shared.Return(_rows, clearArray: true);
            }

            _rows = larger;
        }
    }
}
