using System.Globalization;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Archive.Lsm.Control;
using Orleans.Durable.Storage.Engine.Archive.Memory;
using Orleans.Durable.Storage.Engine.Journals;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.Ledgers.Memory;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;
using Orleans.Durable.Storage.FileSystem.Archive;

namespace Orleans.Durable.Storage.Benchmarks;

/// <summary>
/// Measures <see cref="DrainWorker"/> throughput draining a closed 4 MiB ledger segment into <see cref="LsmArchiveStore"/>
/// across 100 vs 10,000 distinct journals, verifying that object-store PUTs per segment remain 1 (independent of journal
/// count; test/Durable.Storage/docs/spec/README.md §1, archive-lsm.md §8.2).
/// </summary>
[MemoryDiagnoser]
public class LsmDrainBenchmarks : IAsyncDisposable
{
    private const int TotalRowsPerSegment = 10_000;
    private const int RowBodyBytes = 420;
    private const int PrecreatedDirBatchSize = 64;
    private static readonly NodeId Node = JournalWriteBenchmarks.MakeNodeId(1);

    private string? _rootDirectory;
    private string? _iterationDirectory;
    private readonly Queue<string> _iterationDirs = new();
    private InMemoryLedgerReplicaTransport? _transport;
    private LedgerMapCommitter? _mapCommitter;
    private LedgerReader? _reader;
    private LedgerMetadata _closedLedger;
    private Slot _closedEnd;
    private long _drainsExecuted;
    private long _totalPuts;

    [Params(100, 10_000)]
    public int DistinctJournals { get; set; }

    [Params(false, true)]
    public bool FileSystemStore { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        try
        {
            if (FileSystemStore)
            {
                _rootDirectory = Path.Combine(Path.GetTempPath(), $"clog-lsm-drain-bench-{Guid.NewGuid():N}");
                _ = Directory.CreateDirectory(_rootDirectory);
            }

            _transport = new InMemoryLedgerReplicaTransport();
            var register = new InMemoryConditionalRegister();
            var map = await LedgerMapView.OpenAsync(register, CancellationToken.None).ConfigureAwait(false);
            _mapCommitter = new LedgerMapCommitter(register, int.MaxValue);
            _reader = new LedgerReader(map, _transport, TimeProvider.System, Node);
            var writerOptions = new LedgerWriterOptions(64L << 20, int.MaxValue, TimeSpan.FromHours(1), 32L << 20);
            await using var registry = new LedgerWriterRegistry(_reader, 1 << 25, writerOptions, TimeProvider.System, NullLoggerFactory.Instance);
            var executor = new TransitionExecutor(registry, NullLogger<TransitionExecutor>.Instance);
            var bootstrap = await executor.ExecuteAsync(new Transition.Bootstrap(new TransitionChoice(Node, [Node], 64, 0, map.Current.Epoch)), _mapCommitter.CommitAsync, CancellationToken.None).ConfigureAwait(false);
            if (bootstrap.Value is not LedgerMapCommitOutcome.Applied)
            {
                throw new InvalidOperationException($"Bootstrap failed: {bootstrap.Value}");
            }

            _ = await map.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            await registry.ReconciledAsync(map.Current.Epoch, CancellationToken.None).ConfigureAwait(false);
            using var scratchArchive = new InMemoryArchiveStore();
            await using var journals = new JournalStore(registry, scratchArchive, new JournalStoreOptions { WarmStateBytes = 0 }, TimeProvider.System);

            // Hold row count and payload bytes constant (10,000 × 420 B > 4 MiB) across 100 and 10,000 distinct journals.
            using var payload = JournalWriteBenchmarks.MakeBuffer(RowBodyBytes);
            var versions = new JournalVersion[DistinctJournals];
            Array.Fill(versions, JournalVersion.Origin);

            var inFlight = new List<(int KeyIndex, Task<JournalWriteOutcome> Task)>(64);
            for (var r = 0; r < TotalRowsPerSegment; r++)
            {
                var keyIdx = r % DistinctJournals;
                if (inFlight.Any(x => x.KeyIndex == keyIdx) || inFlight.Count == 64)
                {
                    await CompleteBatchAsync(inFlight, versions).ConfigureAwait(false);
                }

                var key = JournalRowKey.Encode(string.Create(CultureInfo.InvariantCulture, $"drain-key-{keyIdx:D6}"));
                var record = versions[keyIdx] == JournalVersion.Origin
                    ? journals.EncodeReplace(key, JournalVersion.Origin, payload)
                    : journals.EncodeAppend(key, versions[keyIdx], payload);
                inFlight.Add((keyIdx, WriteAndDisposeAsync(journals, record)));
            }

            if (inFlight.Count > 0)
            {
                await CompleteBatchAsync(inFlight, versions).ConfigureAwait(false);
            }

            var oldId = map.Current.Ledgers.Single(static l => l.Status.Value is LedgerStatus.Open).Id;
            var replace = await executor.ExecuteAsync(new Transition.Replace(oldId, new TransitionChoice(Node, [Node], 64, 0, map.Current.Epoch)), _mapCommitter.CommitAsync, CancellationToken.None).ConfigureAwait(false);
            if (replace.Value is not LedgerMapCommitOutcome.Applied)
            {
                throw new InvalidOperationException($"Replace failed: {replace.Value}");
            }

            _ = await map.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            _closedLedger = map.Current.Get(oldId)!.Value;
            _closedEnd = _closedLedger.Status.Value is LedgerStatus.Closed status
                ? status.T
                : throw new InvalidOperationException("Expected closed ledger.");

            _drainsExecuted = 0;
            _totalPuts = 0;
        }
        catch
        {
            await CleanupAsync().ConfigureAwait(false);
            throw;
        }
    }

    [IterationSetup]
    public void SetupIteration()
    {
        if (!FileSystemStore)
        {
            return;
        }

        _iterationDirs.Clear();
        _iterationDirectory = Path.Combine(_rootDirectory!, Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(_iterationDirectory);
        for (var i = 0; i < PrecreatedDirBatchSize; i++)
        {
            var sub = Path.Combine(_iterationDirectory, i.ToString(CultureInfo.InvariantCulture));
            _ = Directory.CreateDirectory(sub);
            _iterationDirs.Enqueue(sub);
        }
    }

    [IterationCleanup]
    public void CleanupIteration()
    {
        _iterationDirs.Clear();
        if (_iterationDirectory is not null && Directory.Exists(_iterationDirectory))
        {
            Directory.Delete(_iterationDirectory, recursive: true);
            _iterationDirectory = null;
        }
    }

    [Benchmark]
    public async Task<long> DrainSegment()
    {
        IObjectStore backing;
        if (FileSystemStore)
        {
            string iterDir;
            if (_iterationDirs.Count > 0)
            {
                iterDir = _iterationDirs.Dequeue();
            }
            else
            {
                iterDir = Path.Combine(_iterationDirectory ?? _rootDirectory!, Guid.NewGuid().ToString("N"));
                _ = Directory.CreateDirectory(iterDir);
            }

            backing = new FileSystemObjectStore(iterDir, TimeProvider.System);
        }
        else
        {
            backing = new InMemoryObjectStore();
        }

        var counting = new CountingObjectStore(backing);
        var manifest = new ManifestView(counting);
        await using var committer = new ManifestCommitter(counting, manifest, TimeProvider.System, TimeSpan.Zero);
        var worker = new DrainWorker(counting, manifest, _reader!, new ArchiveOptions { GridBits = 1 }, TimeProvider.System);

        long totalBytes = 0;
        while (true)
        {
            var outcome = await worker.DrainAsync(_closedLedger, _closedEnd, committer.CommitAsync).ConfigureAwait(false);
            if (outcome.Value is not DrainOutcome.Drained done)
            {
                throw new InvalidOperationException($"Drain failed: {outcome.Value}");
            }

            foreach (var section in done.Change.Sections)
            {
                totalBytes += section.Length;
            }

            if (done.Change.Complete)
            {
                break;
            }
        }

        if (totalBytes < 4L << 20)
        {
            throw new InvalidOperationException($"Expected drained section >= 4 MiB, observed {totalBytes} bytes.");
        }

        if (counting.Creates != 1)
        {
            throw new InvalidOperationException(
                $"Expected 1 object-store PUT for a single 4 MiB segment ({DistinctJournals} journals), observed {counting.Creates}.");
        }

        _drainsExecuted++;
        _totalPuts += counting.Creates;
        return totalBytes;
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        CleanupIteration();
        if (_drainsExecuted > 0)
        {
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"// DrainSegment[journals={DistinctJournals}, fs={FileSystemStore}]: runs={_drainsExecuted}, avgObjectPuts={_totalPuts / (double)_drainsExecuted:F2}"));
        }

        if (_reader is not null)
        {
            await _reader.DisposeAsync().ConfigureAwait(false);
            _reader = null;
        }

        if (_mapCommitter is not null)
        {
            await _mapCommitter.DisposeAsync().ConfigureAwait(false);
            _mapCommitter = null;
        }

        _transport?.Dispose();
        _transport = null;

        if (_rootDirectory is not null && Directory.Exists(_rootDirectory))
        {
            Directory.Delete(_rootDirectory, recursive: true);
            _rootDirectory = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CleanupAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private static async Task<JournalWriteOutcome> WriteAndDisposeAsync(JournalStore store, JournalEntryRecord record)
    {
        using (record)
        {
            return await store.WriteAsync(record).ConfigureAwait(false);
        }
    }

    private static async Task CompleteBatchAsync(List<(int KeyIndex, Task<JournalWriteOutcome> Task)> inFlight, JournalVersion[] versions)
    {
        var results = await Task.WhenAll(inFlight.Select(static x => x.Task)).ConfigureAwait(false);
        for (var i = 0; i < inFlight.Count; i++)
        {
            if (results[i].Value is not JournalWriteOutcome.Accepted accepted)
            {
                throw new InvalidOperationException($"Setup write failed: {results[i].Value}");
            }

            versions[inFlight[i].KeyIndex] = accepted.Version;
        }

        inFlight.Clear();
    }
}

/// <summary>
/// Measures <see cref="CompactionWorker"/> throughput merging 8 overlapping ~1 MiB LSM sections (~8.5 MiB total input)
/// in <see cref="KeyRange.Root"/> for both <c>MergeInPlace</c> and <c>Push</c> into child partitions.
/// </summary>
[MemoryDiagnoser]
public class LsmCompactionBenchmarks : IAsyncDisposable
{
    private const int SectionCount = 8;
    private const int RowsPerSection = 1_024;
    private const int RowBodyBytes = 1_024;
    private static readonly NodeId Node = JournalWriteBenchmarks.MakeNodeId(1);

    private InMemoryObjectStore _sourceObjects = null!;
    private ManifestValue _manifest = null!;
    private LedgerMapValue _map = null!;
    private ArchiveOptions _options = null!;

    [Params("MergeInPlace", "Push")]
    public string Mode { get; set; } = "MergeInPlace";

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _sourceObjects = new InMemoryObjectStore();
        var manifestView = new ManifestView(_sourceObjects);
        await using var manifestCommitter = new ManifestCommitter(_sourceObjects, manifestView, TimeProvider.System, TimeSpan.Zero);

        using var transport = new InMemoryLedgerReplicaTransport();
        var register = new InMemoryConditionalRegister();
        var mapView = await LedgerMapView.OpenAsync(register, CancellationToken.None).ConfigureAwait(false);
        await using var mapCommitter = new LedgerMapCommitter(register, int.MaxValue);
        await using var reader = new LedgerReader(mapView, transport, TimeProvider.System, Node);
        var writerOptions = new LedgerWriterOptions(64L << 20, int.MaxValue, TimeSpan.FromHours(1), 32L << 20);
        await using var registry = new LedgerWriterRegistry(reader, 1 << 25, writerOptions, TimeProvider.System, NullLoggerFactory.Instance);
        var executor = new TransitionExecutor(registry, NullLogger<TransitionExecutor>.Instance);
        var bootstrap = await executor.ExecuteAsync(new Transition.Bootstrap(new TransitionChoice(Node, [Node], 64, 0, mapView.Current.Epoch)), mapCommitter.CommitAsync, CancellationToken.None).ConfigureAwait(false);
        if (bootstrap.Value is not LedgerMapCommitOutcome.Applied)
        {
            throw new InvalidOperationException($"Bootstrap failed: {bootstrap.Value}");
        }

        _ = await mapView.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        await registry.ReconciledAsync(mapView.Current.Epoch, CancellationToken.None).ConfigureAwait(false);

        var drainOptions = new ArchiveOptions { GridBits = 2 };
        var drainWorker = new DrainWorker(_sourceObjects, manifestView, reader, drainOptions, TimeProvider.System);
        using var payload = JournalWriteBenchmarks.MakeBuffer(RowBodyBytes);

        // Create 8 overlapping closed ledgers (~1 MiB each) with 50% key overlap across sections.
        var versions = new Dictionary<int, JournalVersion>();
        for (var s = 0; s < SectionCount; s++)
        {
            using var scratchArchive = new InMemoryArchiveStore();
            await using var journals = new JournalStore(registry, scratchArchive, new JournalStoreOptions { WarmStateBytes = 0 }, TimeProvider.System);
            var keyBase = s * (RowsPerSection / 2);
            var inFlight = new List<(int KeyIdx, Task<JournalWriteOutcome> Task)>(64);
            for (var i = 0; i < RowsPerSection; i++)
            {
                var keyIdx = keyBase + i;
                var key = JournalRowKey.Encode(string.Create(CultureInfo.InvariantCulture, $"compact-key-{keyIdx:D6}"));
                var expected = versions.GetValueOrDefault(keyIdx, JournalVersion.Origin);
                var record = journals.EncodeReplace(key, expected, payload);
                inFlight.Add((keyIdx, WriteAndDisposeAsync(journals, record)));
                if (inFlight.Count == 64)
                {
                    await CompleteBatchAsync(inFlight, versions).ConfigureAwait(false);
                }
            }

            if (inFlight.Count > 0)
            {
                await CompleteBatchAsync(inFlight, versions).ConfigureAwait(false);
            }

            var oldId = mapView.Current.Ledgers.Single(static l => l.Status.Value is LedgerStatus.Open).Id;
            var replace = await executor.ExecuteAsync(new Transition.Replace(oldId, new TransitionChoice(Node, [Node], 64, 0, mapView.Current.Epoch)), mapCommitter.CommitAsync, CancellationToken.None).ConfigureAwait(false);
            if (replace.Value is not LedgerMapCommitOutcome.Applied)
            {
                throw new InvalidOperationException($"Replace failed: {replace.Value}");
            }

            _ = await mapView.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            await registry.ReconciledAsync(mapView.Current.Epoch, CancellationToken.None).ConfigureAwait(false);
            var closed = mapView.Current.Get(oldId)!.Value;
            var closedEnd = closed.Status.Value is LedgerStatus.Closed status
                ? status.T
                : throw new InvalidOperationException("Expected closed ledger.");

            while (true)
            {
                var outcome = await drainWorker.DrainAsync(closed, closedEnd, manifestCommitter.CommitAsync).ConfigureAwait(false);
                if (outcome.Value is not DrainOutcome.Drained done)
                {
                    throw new InvalidOperationException($"Drain failed: {outcome.Value}");
                }

                if (done.Change.Complete)
                {
                    break;
                }
            }
        }

        _manifest = manifestView.Current!.Value;
        _map = mapView.Current;

        // Configure ArchiveOptions so MergeInPlace merges all 8 sections in Root (below F * s) while Push pushes ALL 8
        // sections into the 4 depth-2 child partitions (F * s <= total < 2 * F * s).
        _options = Mode == "MergeInPlace"
            ? new ArchiveOptions
            {
                GridBits = 2,
                MaxSectionsPerPartition = 4,
                SectionBytes = 4 << 20, // F * s = 4 * 4 MiB = 16 MiB > ~8.5 MiB -> MergeInPlace
            }
            : new ArchiveOptions
            {
                GridBits = 2,
                MaxSectionsPerPartition = 4,
                SectionBytes = 1536 * 1024, // F * s = 6 MiB <= ~8.5 MiB < 2 * F * s = 12 MiB -> Push all 8 sections
            };
    }

    [Benchmark]
    public async Task<long> CompactPartition()
    {
        var worker = new CompactionWorker(_sourceObjects, new TailCache(_sourceObjects, _options), new ManifestView(_sourceObjects), _options);
        var outcome = await worker.CompactAsync(KeyRange.Root, _manifest, _map).ConfigureAwait(false);
        if (outcome.Value is not CompactionOutcome.Compacted compacted)
        {
            throw new InvalidOperationException($"Expected compaction in mode {Mode}, got {outcome.Value}.");
        }

        if (compacted.Change.Inputs.Length != SectionCount)
        {
            throw new InvalidOperationException(
                $"Expected all {SectionCount} input sections to compact in mode {Mode}, observed {compacted.Change.Inputs.Length}.");
        }

        var outputs = compacted.Change.Outputs;
        if (Mode == "MergeInPlace")
        {
            if (outputs.Length != 1 || outputs[0].Partition != KeyRange.Root)
            {
                throw new InvalidOperationException($"MergeInPlace expected 1 Root output section, observed {outputs.Length}.");
            }
        }
        else
        {
            var distinctChildren = outputs.Select(static s => s.Partition).Distinct().ToArray();
            if (distinctChildren.Length != 4 || distinctChildren.Any(static p => p.Depth != 2))
            {
                throw new InvalidOperationException(
                    $"Push expected 4 distinct depth-2 child partitions, observed {distinctChildren.Length} ({string.Join(", ", distinctChildren)}).");
            }
        }

        long outputBytes = 0;
        foreach (var section in outputs)
        {
            outputBytes += section.Length;
            await _sourceObjects.DeleteAsync(section.Object).ConfigureAwait(false);
        }

        if (outputBytes <= 0)
        {
            throw new InvalidOperationException($"Expected positive compacted output bytes in mode {Mode}, observed {outputBytes}.");
        }

        return outputBytes;
    }

    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    private static async Task<JournalWriteOutcome> WriteAndDisposeAsync(JournalStore store, JournalEntryRecord record)
    {
        using (record)
        {
            return await store.WriteAsync(record).ConfigureAwait(false);
        }
    }

    private static async Task CompleteBatchAsync(List<(int KeyIdx, Task<JournalWriteOutcome> Task)> inFlight, Dictionary<int, JournalVersion> versions)
    {
        var results = await Task.WhenAll(inFlight.Select(static x => x.Task)).ConfigureAwait(false);
        for (var i = 0; i < inFlight.Count; i++)
        {
            if (results[i].Value is not JournalWriteOutcome.Accepted accepted)
            {
                throw new InvalidOperationException($"Setup write failed: {results[i].Value}");
            }

            versions[inFlight[i].KeyIdx] = accepted.Version;
        }

        inFlight.Clear();
    }
}
