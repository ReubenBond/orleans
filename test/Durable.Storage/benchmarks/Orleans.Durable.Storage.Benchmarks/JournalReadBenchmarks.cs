using System.Globalization;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Archive.Lsm.Control;
using Orleans.Durable.Storage.Engine.Archive.Memory;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Journals;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.Ledgers.Memory;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Engine.Wal.FileSystem;
using Orleans.Durable.Storage.FileSystem.Archive;
using Orleans.Durable.Storage.RocksDb;

namespace Orleans.Durable.Storage.Benchmarks;

/// <summary>
/// Measures primary live-ledger journal reads and state folding through <see cref="JournalStore.ReadAsync"/>
/// (which reads the journal's rows from the live ledger and folds them into the current state rather than serving
/// from <c>JournalWarmState</c>).
/// </summary>
[MemoryDiagnoser]
public class JournalWarmReadBenchmarks : IAsyncDisposable
{
    private static readonly NodeId Node = JournalWriteBenchmarks.MakeNodeId(1);

    private InMemoryLedgerReplicaTransport? _transport;
    private InMemoryArchiveStore? _archive;
    private LedgerMapCommitter? _committer;
    private LedgerReader? _reader;
    private LedgerWriterRegistry? _registry;
    private JournalStore? _journals;
    private JournalRowKey _key = null!;
    private JournalVersion _expectedVersion;

    [Params(1024, 65536)]
    public int JournalBytes { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        try
        {
            _transport = new InMemoryLedgerReplicaTransport();
            var register = new InMemoryConditionalRegister();
            var map = await LedgerMapView.OpenAsync(register, CancellationToken.None).ConfigureAwait(false);
            _committer = new LedgerMapCommitter(register, int.MaxValue);
            _reader = new LedgerReader(map, _transport, TimeProvider.System, Node);
            var writerOptions = new LedgerWriterOptions(1L << 30, int.MaxValue, TimeSpan.FromHours(1), 32L << 20);
            _registry = new LedgerWriterRegistry(_reader, 1 << 25, writerOptions, TimeProvider.System, NullLoggerFactory.Instance);
            var executor = new TransitionExecutor(_registry, NullLogger<TransitionExecutor>.Instance);
            var bootstrap = await executor.ExecuteAsync(new Transition.Bootstrap(new TransitionChoice(Node, [Node], 64, 0, map.Current.Epoch)), _committer.CommitAsync, CancellationToken.None).ConfigureAwait(false);
            if (bootstrap.Value is not LedgerMapCommitOutcome.Applied)
            {
                throw new InvalidOperationException($"Bootstrap failed: {bootstrap.Value}");
            }

            _ = await map.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            await _registry.ReconciledAsync(map.Current.Epoch, CancellationToken.None).ConfigureAwait(false);
            _archive = new InMemoryArchiveStore();
            _journals = new JournalStore(_registry, _archive, new JournalStoreOptions { WarmStateBytes = 64 << 20 }, TimeProvider.System);

            _key = JournalRowKey.Encode("warm-journal");
            using var payload = JournalWriteBenchmarks.MakeBuffer(JournalBytes);
            using var record = _journals.EncodeReplace(_key, JournalVersion.Origin, payload);
            var outcome = await _journals.WriteAsync(record).ConfigureAwait(false);
            if (outcome.Value is not JournalWriteOutcome.Accepted accepted)
            {
                throw new InvalidOperationException($"Warm setup write failed: {outcome.Value}");
            }

            _expectedVersion = accepted.Version;
        }
        catch
        {
            await CleanupAsync().ConfigureAwait(false);
            throw;
        }
    }

    [Benchmark]
    public async ValueTask<long> WarmRead()
    {
        var read = await _journals!.ReadAsync(_key).ConfigureAwait(false);
        if (read.Value is not JournalReadResult found)
        {
            throw new InvalidOperationException($"Unexpected warm read outcome: {read.Value}");
        }

        using (found)
        {
            if (found.ContentLength != JournalBytes || found.CurrentVersion != _expectedVersion)
            {
                throw new InvalidOperationException($"WarmRead mismatch: len={found.ContentLength} (expected {JournalBytes}), version={found.CurrentVersion} (expected {_expectedVersion}).");
            }

            return found.ContentLength;
        }
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        if (_journals is not null)
        {
            await _journals.DisposeAsync().ConfigureAwait(false);
            _journals = null;
        }

        _archive?.Dispose();
        _archive = null;

        if (_registry is not null)
        {
            await _registry.DisposeAsync().ConfigureAwait(false);
            _registry = null;
        }

        if (_reader is not null)
        {
            await _reader.DisposeAsync().ConfigureAwait(false);
            _reader = null;
        }

        if (_committer is not null)
        {
            await _committer.DisposeAsync().ConfigureAwait(false);
            _committer = null;
        }

        _transport?.Dispose();
        _transport = null;
    }

    public async ValueTask DisposeAsync()
    {
        await CleanupAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Measures absent-journal first-touch reads through <see cref="JournalStore"/> over a caught-up RocksDB
/// <see cref="JournalStateDatabase"/> and a preloaded <see cref="LsmArchiveStore"/> across 100 and 10,000 drained
/// journals, verifying zero object-store requests (journals.md §8.2, §8.3; archive-lsm.md §8.3).
/// </summary>
[MemoryDiagnoser]
public class JournalAbsentReadBenchmarks : IAsyncDisposable
{
    private const int AbsentKeyPoolSize = 256;
    private static readonly NodeId Node = JournalWriteBenchmarks.MakeNodeId(1);

    private string? _rootDirectory;
    private CountingObjectStore? _countingObjects;
    private InMemoryLedgerReplicaTransport? _transport;
    private ManifestCommitter? _manifestCommitter;
    private LedgerMapCommitter? _mapCommitter;
    private LedgerReader? _reader;
    private LedgerApplyLoop? _applyLoop;
    private RocksDbJournalIndex? _index;
    private JournalStateDatabase? _stateDb;
    private LedgerWriterRegistry? _registry;
    private JournalStore? _journals;
    private JournalRowKey[] _absentKeys = [];
    private int _nextAbsent;
    private long _objectCallsAfterSetup;

    [Params(100, 10_000)]
    public int DrainedJournals { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        try
        {
            _rootDirectory = Path.Combine(Path.GetTempPath(), $"clog-journal-absent-bench-{Guid.NewGuid():N}");
            var objectsDir = Path.Combine(_rootDirectory, "objects");
            var stateDir = Path.Combine(_rootDirectory, "state");
            _ = Directory.CreateDirectory(objectsDir);
            _ = Directory.CreateDirectory(stateDir);

            var fileObjects = new FileSystemObjectStore(objectsDir, TimeProvider.System);
            _countingObjects = new CountingObjectStore(fileObjects);
            var manifest = new ManifestView(_countingObjects);
            _manifestCommitter = new ManifestCommitter(_countingObjects, manifest, TimeProvider.System, TimeSpan.Zero);
            var archiveOptions = new ArchiveOptions { GridBits = 1, ViewAdoptionInterval = TimeSpan.FromHours(1) };
            var lsmStore = new LsmArchiveStore(_countingObjects, manifest, new TailCache(_countingObjects, archiveOptions), archiveOptions, TimeProvider.System);

            _transport = new InMemoryLedgerReplicaTransport();
            var register = new InMemoryConditionalRegister();
            var map = await LedgerMapView.OpenAsync(register, CancellationToken.None).ConfigureAwait(false);
            _mapCommitter = new LedgerMapCommitter(register, int.MaxValue);
            _reader = new LedgerReader(map, _transport, TimeProvider.System, Node);
            var writerOptions = new LedgerWriterOptions(128L << 20, int.MaxValue, TimeSpan.FromHours(1), 32L << 20);
            _registry = new LedgerWriterRegistry(_reader, 1 << 25, writerOptions, TimeProvider.System, NullLoggerFactory.Instance);
            var executor = new TransitionExecutor(_registry, NullLogger<TransitionExecutor>.Instance);
            var bootstrap = await executor.ExecuteAsync(new Transition.Bootstrap(new TransitionChoice(Node, [Node], 64, 0, map.Current.Epoch)), _mapCommitter.CommitAsync, CancellationToken.None).ConfigureAwait(false);
            if (bootstrap.Value is not LedgerMapCommitOutcome.Applied)
            {
                throw new InvalidOperationException($"Bootstrap failed: {bootstrap.Value}");
            }

            _ = await map.ReadAsync(CancellationToken.None).ConfigureAwait(false);

            _index = new RocksDbJournalIndex(stateDir);
            _stateDb = await JournalStateDatabase.OpenAsync(_index, _reader, timeProvider: TimeProvider.System).ConfigureAwait(false);
            _applyLoop = new LedgerApplyLoop(_reader, _stateDb.Applier, TimeProvider.System);
            _stateDb.Attach(_applyLoop);

            await _registry.ReconciledAsync(map.Current.Epoch, CancellationToken.None).ConfigureAwait(false);
            _journals = new JournalStore(_registry, lsmStore, new JournalStoreOptions { WarmStateBytes = 0 }, TimeProvider.System, _stateDb);

            // Populate DrainedJournals distinct keys in batches, then drain them into an LSM section.
            using var payload = JournalWriteBenchmarks.MakeBuffer(128);
            var inFlight = new List<Task<JournalWriteOutcome>>(64);
            for (var i = 0; i < DrainedJournals; i++)
            {
                var key = JournalRowKey.Encode(string.Create(CultureInfo.InvariantCulture, $"present-{i:D6}"));
                var record = _journals.EncodeReplace(key, JournalVersion.Origin, payload);
                inFlight.Add(WriteAndDisposeAsync(_journals, record));
                if (inFlight.Count == 64)
                {
                    await DrainBatchAsync(inFlight).ConfigureAwait(false);
                }
            }

            if (inFlight.Count > 0)
            {
                await DrainBatchAsync(inFlight).ConfigureAwait(false);
            }

            var open = map.Current.Ledgers.Single(static l => l.Status.Value is LedgerStatus.Open);
            var endMarker = await _registry.TryGet(open.Id)!.EndSegmentAsync().ConfigureAwait(false);
            if (!endMarker.TryGetValue(out ProposalOutcome.Durable marker))
            {
                throw new InvalidOperationException($"EndSegment failed: {endMarker}");
            }

            var drainWorker = new DrainWorker(_countingObjects, manifest, _reader, archiveOptions, TimeProvider.System);
            while (true)
            {
                var drained = await drainWorker.DrainAsync(map.Current.Get(open.Id)!.Value, marker.Slot + 1, _manifestCommitter.CommitAsync).ConfigureAwait(false);
                if (drained.Value is not DrainOutcome.Drained done)
                {
                    throw new InvalidOperationException($"Drain failed: {drained.Value}");
                }

                if (done.Change.Below == marker.Slot)
                {
                    break;
                }
            }

            // Preload LSM section metadata and catch up the RocksDB key index to the ledger tail.
            await lsmStore.PreloadAsync([open.Range]).ConfigureAwait(false);
            var tailOutcome = await _reader.ReadTailAsync(map.Current.Get(open.Id)!.Value).ConfigureAwait(false);
            if (tailOutcome.Value is not LedgerTail tail)
            {
                throw new InvalidOperationException($"ReadTail failed: {tailOutcome.Value}");
            }

            _applyLoop.Wake(open.Id);
            var deadline = TimeProvider.System.GetTimestamp() + TimeProvider.System.TimestampFrequency * 30;
            while (_stateDb.Watermark(open.Id) < tail.ContiguousTail)
            {
                if (TimeProvider.System.GetTimestamp() > deadline)
                {
                    throw new TimeoutException($"StateDb watermark {_stateDb.Watermark(open.Id)} did not reach {tail.ContiguousTail}.");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(5), TimeProvider.System).ConfigureAwait(false);
            }

            _absentKeys = new JournalRowKey[AbsentKeyPoolSize];
            for (var i = 0; i < AbsentKeyPoolSize; i++)
            {
                _absentKeys[i] = JournalRowKey.Encode(string.Create(CultureInfo.InvariantCulture, $"absent-key-{i:D6}"));
            }

            // Verify once before timing that an absent read makes zero object-store calls.
            _objectCallsAfterSetup = _countingObjects.TotalCalls;
            var check = await AbsentRead().ConfigureAwait(false);
            if (check != 0)
            {
                throw new InvalidOperationException("Expected absent journal.");
            }

            if (_countingObjects.TotalCalls != _objectCallsAfterSetup)
            {
                throw new InvalidOperationException(
                    $"Expected 0 object-store calls on absent read with preloaded LSM and caught-up key index, observed {_countingObjects.TotalCalls - _objectCallsAfterSetup}.");
            }
        }
        catch
        {
            await CleanupAsync().ConfigureAwait(false);
            throw;
        }
    }

    [Benchmark]
    public async ValueTask<long> AbsentRead()
    {
        var key = _absentKeys[_nextAbsent++ & (AbsentKeyPoolSize - 1)];
        var read = await _journals!.ReadAsync(key).ConfigureAwait(false);
        if (read.Value is not JournalReadResult found)
        {
            throw new InvalidOperationException($"Unexpected absent read outcome: {read.Value}");
        }

        using (found)
        {
            if (found.ContentLength != 0 || found.CurrentVersion != JournalVersion.Origin)
            {
                throw new InvalidOperationException($"AbsentRead mismatch: len={found.ContentLength}, version={found.CurrentVersion}.");
            }

            return found.ContentLength;
        }
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        var objectCallsDuringBenchmark = _countingObjects is not null ? _countingObjects.TotalCalls - _objectCallsAfterSetup : 0;

        if (_journals is not null)
        {
            await _journals.DisposeAsync().ConfigureAwait(false);
            _journals = null;
        }

        if (_registry is not null)
        {
            await _registry.DisposeAsync().ConfigureAwait(false);
            _registry = null;
        }

        if (_applyLoop is not null)
        {
            await _applyLoop.DisposeAsync().ConfigureAwait(false);
            _applyLoop = null;
        }

        if (_stateDb is not null)
        {
            await _stateDb.DisposeAsync().ConfigureAwait(false);
            _stateDb = null;
        }

        if (_index is not null)
        {
            await _index.DisposeAsync().ConfigureAwait(false);
            _index = null;
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

        if (_manifestCommitter is not null)
        {
            await _manifestCommitter.DisposeAsync().ConfigureAwait(false);
            _manifestCommitter = null;
        }

        _transport?.Dispose();
        _transport = null;
        _countingObjects = null;

        if (_rootDirectory is not null && Directory.Exists(_rootDirectory))
        {
            Directory.Delete(_rootDirectory, recursive: true);
            _rootDirectory = null;
        }

        if (objectCallsDuringBenchmark != 0)
        {
            throw new InvalidOperationException(
                $"AbsentRead made {objectCallsDuringBenchmark} object-store calls across benchmark (expected 0).");
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

    private static async Task DrainBatchAsync(List<Task<JournalWriteOutcome>> batch)
    {
        var results = await Task.WhenAll(batch).ConfigureAwait(false);
        batch.Clear();
        foreach (var result in results)
        {
            if (result.Value is not JournalWriteOutcome.Accepted)
            {
                throw new InvalidOperationException($"Setup write failed: {result.Value}");
            }
        }
    }
}

/// <summary>
/// Measures cold first-touch reads on a drained journal in <see cref="LsmArchiveStore"/> over a local
/// <see cref="FileSystemObjectStore"/>, comparing uncached reads (trailer + block from object store), preloaded
/// section metadata cache (<see cref="TailCache"/>), and section metadata + local volatile-disk <see cref="BlockCache"/>.
/// </summary>
[MemoryDiagnoser]
public class JournalColdReadBenchmarks : IAsyncDisposable
{
    public enum ColdReadCacheMode
    {
        Uncached,
        SectionMetadataCache,
        MetadataAndBlockCache,
    }

    private const int SeededJournals = 1_000;
    private static readonly NodeId Node = JournalWriteBenchmarks.MakeNodeId(1);

    private string? _rootDirectory;
    private CountingObjectStore? _countingObjects;
    private InMemoryLedgerReplicaTransport? _transport;
    private ManifestView? _manifest;
    private ManifestCommitter? _manifestCommitter;
    private LedgerMapCommitter? _mapCommitter;
    private LedgerReader? _reader;
    private LedgerWriterRegistry? _registry;
    private BlockCache? _blockCache;
    private JournalStore? _journals;
    private ArchiveOptions _archiveOptions = new();
    private JournalRowKey _targetKey = null!;
    private JournalVersion _expectedVersion;
    private long _readsExecuted;
    private long _objectCallsBeforeBench;

    [Params(ColdReadCacheMode.Uncached, ColdReadCacheMode.SectionMetadataCache, ColdReadCacheMode.MetadataAndBlockCache)]
    public ColdReadCacheMode Mode { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        try
        {
            _rootDirectory = Path.Combine(Path.GetTempPath(), $"clog-journal-cold-bench-{Guid.NewGuid():N}");
            var objectsDir = Path.Combine(_rootDirectory, "objects");
            var cacheDir = Path.Combine(_rootDirectory, "block-cache");
            _ = Directory.CreateDirectory(objectsDir);
            _ = Directory.CreateDirectory(cacheDir);

            var fileObjects = new FileSystemObjectStore(objectsDir, TimeProvider.System);
            _countingObjects = new CountingObjectStore(fileObjects);
            _manifest = new ManifestView(_countingObjects);
            _manifestCommitter = new ManifestCommitter(_countingObjects, _manifest, TimeProvider.System, TimeSpan.Zero);
            _archiveOptions = new ArchiveOptions
            {
                GridBits = 1,
                BlockCacheBytes = 64L << 20,
                BlockCacheRegionBytes = 4L << 20,
                TailCacheBytes = Mode == ColdReadCacheMode.Uncached ? 0 : 256L << 20,
                ViewAdoptionInterval = TimeSpan.FromHours(1),
            };

            _transport = new InMemoryLedgerReplicaTransport();
            var register = new InMemoryConditionalRegister();
            var map = await LedgerMapView.OpenAsync(register, CancellationToken.None).ConfigureAwait(false);
            _mapCommitter = new LedgerMapCommitter(register, int.MaxValue);
            _reader = new LedgerReader(map, _transport, TimeProvider.System, Node);
            var writerOptions = new LedgerWriterOptions(128L << 20, int.MaxValue, TimeSpan.FromHours(1), 32L << 20);
            _registry = new LedgerWriterRegistry(_reader, 1 << 25, writerOptions, TimeProvider.System, NullLoggerFactory.Instance);
            var executor = new TransitionExecutor(_registry, NullLogger<TransitionExecutor>.Instance);
            var bootstrap = await executor.ExecuteAsync(new Transition.Bootstrap(new TransitionChoice(Node, [Node], 64, 0, map.Current.Epoch)), _mapCommitter.CommitAsync, CancellationToken.None).ConfigureAwait(false);
            if (bootstrap.Value is not LedgerMapCommitOutcome.Applied)
            {
                throw new InvalidOperationException($"Bootstrap failed: {bootstrap.Value}");
            }

            _ = await map.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            await _registry.ReconciledAsync(map.Current.Epoch, CancellationToken.None).ConfigureAwait(false);

            if (Mode == ColdReadCacheMode.MetadataAndBlockCache)
            {
                _blockCache = await BlockCache.OpenAsync(cacheDir, _archiveOptions, WalFileSystem.Os).ConfigureAwait(false);
            }

            // Write 1,000 journals (4 KiB each) into ledger 0, then replace, drain completely, and drop ledger 0
            // so every cold read hits the LSM archive.
            var tempLsm = new LsmArchiveStore(_countingObjects, _manifest, new TailCache(_countingObjects, _archiveOptions), _archiveOptions, TimeProvider.System);
            await using (var writerStore = new JournalStore(_registry, tempLsm, new JournalStoreOptions { WarmStateBytes = 0 }, TimeProvider.System))
            {
                using var payload = JournalWriteBenchmarks.MakeBuffer(4096);
                List<Task<JournalWriteOutcome>> inFlight = [];
                _ = inFlight.EnsureCapacity(64);
                for (var i = 0; i < SeededJournals; i++)
                {
                    var key = JournalRowKey.Encode(string.Create(CultureInfo.InvariantCulture, $"cold-journal-{i:D5}"));
                    var record = writerStore.EncodeReplace(key, JournalVersion.Origin, payload);
                    inFlight.Add(WriteAndDisposeAsync(writerStore, record));
                    if (inFlight.Count == 64)
                    {
                        await DrainBatchAsync(inFlight).ConfigureAwait(false);
                    }
                }

                if (inFlight.Count > 0)
                {
                    await DrainBatchAsync(inFlight).ConfigureAwait(false);
                }
            }

            var oldLedger = map.Current.Ledgers.Single(static l => l.Status.Value is LedgerStatus.Open).Id;
            var replace = await executor.ExecuteAsync(new Transition.Replace(oldLedger, new TransitionChoice(Node, [Node], 64, 0, map.Current.Epoch)), _mapCommitter.CommitAsync, CancellationToken.None).ConfigureAwait(false);
            if (replace.Value is not LedgerMapCommitOutcome.Applied)
            {
                throw new InvalidOperationException($"Replace failed: {replace.Value}");
            }

            _ = await map.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            await _registry.ReconciledAsync(map.Current.Epoch, CancellationToken.None).ConfigureAwait(false);

            var closed = map.Current.Get(oldLedger)!.Value;
            var closedEnd = closed.Status.Value is LedgerStatus.Closed status
                ? status.T
                : throw new InvalidOperationException("Expected closed ledger.");

            var drainWorker = new DrainWorker(_countingObjects, _manifest, _reader, _archiveOptions, TimeProvider.System, _blockCache);
            while (true)
            {
                var drained = await drainWorker.DrainAsync(closed, closedEnd, _manifestCommitter.CommitAsync).ConfigureAwait(false);
                if (drained.Value is not DrainOutcome.Drained done)
                {
                    throw new InvalidOperationException($"Drain failed: {drained.Value}");
                }

                if (done.Change.Complete)
                {
                    break;
                }
            }

            var dropper = new LedgerDropper(map, _manifest, NullLogger<LedgerDropper>.Instance);
            _ = await dropper.DropCompleteAsync(_mapCommitter.CommitAsync).ConfigureAwait(false);
            await _registry.ReconciledAsync(map.Current.Epoch, CancellationToken.None).ConfigureAwait(false);

            _targetKey = JournalRowKey.Encode("cold-journal-00500");

            // Reuse one JournalStore in all modes; in Uncached mode, TailCacheBytes == 0 and no preloaded ranges
            // ensures every ColdRead fetches both tail and block from the object store without per-call JournalStore setup.
            var lsm = new LsmArchiveStore(_countingObjects, _manifest, new TailCache(_countingObjects, _archiveOptions), _archiveOptions, TimeProvider.System, _blockCache);
            if (Mode != ColdReadCacheMode.Uncached)
            {
                await lsm.PreloadAsync([KeyRange.Root]).ConfigureAwait(false);
            }

            _journals = new JournalStore(_registry, lsm, new JournalStoreOptions { WarmStateBytes = 0 }, TimeProvider.System);

            if (Mode == ColdReadCacheMode.MetadataAndBlockCache)
            {
                // Prime the block cache once so the target block is hot on local disk.
                var primed = await _journals.ReadAsync(_targetKey).ConfigureAwait(false);
                if (primed.Value is not JournalReadResult found)
                {
                    throw new InvalidOperationException($"Warming block cache failed: {primed.Value}");
                }

                found.Dispose();
            }

            _readsExecuted = 0;
            _expectedVersion = JournalVersion.Origin;
            _objectCallsBeforeBench = _countingObjects.TotalCalls;
        }
        catch
        {
            await CleanupAsync().ConfigureAwait(false);
            throw;
        }
    }

    [Benchmark]
    public async ValueTask<long> ColdRead()
    {
        _readsExecuted++;
        var outcome = await _journals!.ReadAsync(_targetKey).ConfigureAwait(false);
        if (outcome.Value is not JournalReadResult hit)
        {
            throw new InvalidOperationException($"Unexpected cold read: {outcome.Value}");
        }

        using (hit)
        {
            if (!hit.Exists || hit.ContentLength != 4096 || hit.CurrentVersion == JournalVersion.Origin)
            {
                throw new InvalidOperationException(
                    $"ColdRead mismatch: exists={hit.Exists}, len={hit.ContentLength} (expected 4096), version={hit.CurrentVersion}.");
            }

            if (_expectedVersion == JournalVersion.Origin)
            {
                _expectedVersion = hit.CurrentVersion;
            }
            else if (hit.CurrentVersion != _expectedVersion)
            {
                throw new InvalidOperationException(
                    $"ColdRead version drifted: {hit.CurrentVersion} (expected {_expectedVersion}).");
            }

            return hit.ContentLength;
        }
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        var calls = _countingObjects is not null ? _countingObjects.TotalCalls - _objectCallsBeforeBench : 0;
        var reads = _readsExecuted;
        if (reads > 0)
        {
            var callsPerRead = calls / (double)reads;
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"// ColdRead[{Mode}]: reads={reads}, objectStoreCalls={calls}, callsPerRead={callsPerRead:F2}"));
        }

        if (_journals is not null)
        {
            await _journals.DisposeAsync().ConfigureAwait(false);
            _journals = null;
        }

        if (_registry is not null)
        {
            await _registry.DisposeAsync().ConfigureAwait(false);
            _registry = null;
        }

        if (_reader is not null)
        {
            await _reader.DisposeAsync().ConfigureAwait(false);
            _reader = null;
        }

        _blockCache?.Dispose();
        _blockCache = null;

        if (_mapCommitter is not null)
        {
            await _mapCommitter.DisposeAsync().ConfigureAwait(false);
            _mapCommitter = null;
        }

        if (_manifestCommitter is not null)
        {
            await _manifestCommitter.DisposeAsync().ConfigureAwait(false);
            _manifestCommitter = null;
        }

        _transport?.Dispose();
        _transport = null;
        _countingObjects = null;

        if (_rootDirectory is not null && Directory.Exists(_rootDirectory))
        {
            Directory.Delete(_rootDirectory, recursive: true);
            _rootDirectory = null;
        }

        if (reads > 0)
        {
            var expectedCalls = Mode switch
            {
                ColdReadCacheMode.Uncached => reads * 2,
                ColdReadCacheMode.SectionMetadataCache => reads,
                ColdReadCacheMode.MetadataAndBlockCache => 0L,
                _ => throw new InvalidOperationException($"Unknown mode {Mode}."),
            };

            if (calls != expectedCalls)
            {
                throw new InvalidOperationException(
                    $"ColdRead[{Mode}] made {calls} object-store calls across {reads} reads (expected {expectedCalls}).");
            }
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

    private static async Task DrainBatchAsync(List<Task<JournalWriteOutcome>> batch)
    {
        var results = await Task.WhenAll(batch).ConfigureAwait(false);
        batch.Clear();
        foreach (var result in results)
        {
            if (result.Value is not JournalWriteOutcome.Accepted)
            {
                throw new InvalidOperationException($"Setup write failed: {result.Value}");
            }
        }
    }
}

/// <summary>
/// Wraps an <see cref="IObjectStore"/> and counts calls by category so benchmarks verify exact object-store request counts.
/// </summary>
internal sealed class CountingObjectStore(IObjectStore inner) : IObjectStore
{
    private long _creates;
    private long _replaces;
    private long _reads;
    private long _readVersioned;
    private long _deletes;
    private long _lists;

    public long Creates => Volatile.Read(ref _creates);

    public long Replaces => Volatile.Read(ref _replaces);

    public long Reads => Volatile.Read(ref _reads);

    public long ReadVersioned => Volatile.Read(ref _readVersioned);

    public long TotalCalls => Creates + Replaces + Reads + ReadVersioned + Volatile.Read(ref _deletes) + Volatile.Read(ref _lists);

    public ValueTask<ObjectCreateOutcome> CreateAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default)
    {
        _ = Interlocked.Increment(ref _creates);
        return inner.CreateAsync(name, content, crc32c, cancellationToken);
    }

    public ValueTask<ObjectReplaceOutcome> ReplaceAsync(
        ObjectName name,
        ObjectVersion? expectedVersion,
        ArcBuffer content,
        uint? crc32c,
        CancellationToken cancellationToken = default)
    {
        _ = Interlocked.Increment(ref _replaces);
        return inner.ReplaceAsync(name, expectedVersion, content, crc32c, cancellationToken);
    }

    public ValueTask<ArcBuffer?> ReadAsync(ObjectName name, long offset, int length, CancellationToken cancellationToken = default)
    {
        _ = Interlocked.Increment(ref _reads);
        return inner.ReadAsync(name, offset, length, cancellationToken);
    }

    public ValueTask<VersionedObject?> ReadVersionedAsync(ObjectName name, CancellationToken cancellationToken = default)
    {
        _ = Interlocked.Increment(ref _readVersioned);
        return inner.ReadVersionedAsync(name, cancellationToken);
    }

    public ValueTask<ObjectProperties?> GetPropertiesAsync(ObjectName name, CancellationToken cancellationToken = default)
    {
        _ = Interlocked.Increment(ref _reads);
        return inner.GetPropertiesAsync(name, cancellationToken);
    }

    public ValueTask DeleteAsync(ObjectName name, CancellationToken cancellationToken = default)
    {
        _ = Interlocked.Increment(ref _deletes);
        return inner.DeleteAsync(name, cancellationToken);
    }

    public IAsyncEnumerable<ListedObject> ListAsync(string prefix, string? startAfter = null, CancellationToken cancellationToken = default)
    {
        _ = Interlocked.Increment(ref _lists);
        return inner.ListAsync(prefix, startAfter, cancellationToken);
    }
}
