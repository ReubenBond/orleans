using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Threading;

namespace Orleans.Durable.Storage.Engine.Journals;

/// <summary>
/// A process's Journals (journals.md §2, §8, §9): journal writes and reads on the primary of each key's open ledger, over
/// the process's <see cref="LedgerWriterRegistry"/> and its map view, the history reads of an <see cref="ArchiveReader"/>
/// over the registry's reader, and the archive store's pure functions. It names no store: the composition gives it one.
/// </summary>
/// <remarks>
/// <para>
/// Each writer the registry serves gets one <see cref="LedgerAdmission"/> at its first use, which admits that ledger's
/// entries for its claim and holds their warm state. The store checks the registry under its own lock: the registry
/// answers a ledger's one writer and then nothing forever (<see cref="LedgerWriterRegistry.TryGet"/>), so an admission is
/// retired exactly once the registry no longer serves its writer, and no two admissions share a writer. It notices that at
/// each use and after each reconciliation of the registry with a newer map value or a newly unserved ledger, and retires
/// the rest at its own disposal, which waits for every retired admission's proposals. The composition disposes the store
/// before its registry, so the writers decide those proposals from the view rather than fail them <c>Ambiguous</c>.
/// </para>
/// <para>
/// A key's warm state loads from <see cref="ArchiveReader.ReadAsync(ReadOnlyMemory{byte}, CancellationToken)"/>, backed by the node's
/// <see cref="JournalStateDatabase"/> key indexes and <see cref="ArchiveReader.ReadStoreAsync"/> when provided
/// (journals.md §8.3), and is discarded when its primary claim ends or the process restarts (JS-I6). A load or a read
/// answers only where its read was of the admission's open ledger while the registry still serves its writer, so the
/// read was linearizable here (JS-I3, JS-I6); otherwise it answers <c>Moved</c>. An owner read then waits for its
/// admission to apply every batch of its own below the read's tail, so a write at the version the read returns is judged
/// against an applied state at least as new as the read. A <c>Moved</c> which would name this process's own node routes
/// again once the registry has reconciled with the map, and otherwise names no owner, so the caller reads the map again
/// (journals.md §8.5).
/// </para>
/// </remarks>
public sealed class JournalStore : IAsyncDisposable
{
    private const int MaxColdExpiryConcurrency = 2;

    private readonly LedgerWriterRegistry _writers;
    private readonly LedgerMapView _map;
    private readonly ArchiveReader _archiveReader;
    private readonly IArchiveStore _store;
    private readonly JournalStoreOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly JournalStateDatabase? _stateDatabase;
    private readonly Lock _lock = new();
    private readonly Dictionary<LedgerId, Served> _served = [];
    private readonly Dictionary<LedgerId, int> _releasing = [];
    private Served[] _servedSnapshot = [];
    private readonly List<Task> _retiring = [];
    private readonly ConcurrentDictionary<JournalRowKey, RowToken> _staleBases = new();
    private readonly ConcurrentDictionary<JournalRowKey, (LedgerId Ledger, JournalState? Applied, RowToken NextToken)> _retainedWarmSeeds = new();
    private readonly CancellationTokenSource _disposing = new();
    private readonly CallBudget _historyReads;
    private readonly Task _following;

    // Written only under the lock; read without it only where a stale false is checked again under it.
    private bool _disposed;
    private bool _applyOnAdmitMutant;
    private bool _ignoreRetentionMutant;
    private int _admissionsCreated;
    private int _expiryScans;

    /// <summary>
    /// Initializes a new instance of the <see cref="JournalStore"/> class, which copies its options, runs the startup checks
    /// of journals.md §8.4 on the copy, and then follows the registry's map view. Construction performs no I/O, and is the
    /// composition's start check.
    /// </summary>
    /// <param name="writers">The process's writer registry, whose reader and map view the store reads through.</param>
    /// <param name="store">The cluster's archive store, whose record holds <see cref="StoreParameters"/>.</param>
    /// <param name="options">The Journals policy, which the store copies.</param>
    /// <param name="timeProvider">Stamps each <c>Delete</c> and judges each <c>Expired</c>'s retention.</param>
    /// <exception cref="JournalStoreLimitException">
    /// A startup check fails: the largest batch admission builds passes the replica's payload limit,
    /// <c>MaxBodyBytes</c> passes the store's <c>MaxAppendBytes</c>, or <c>MaxQueuedBytes</c> or the writers'
    /// <c>MaxProposalBytes</c> holds less than the largest batch.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">A setting of <paramref name="options"/> is out of its range.</exception>
    public JournalStore(LedgerWriterRegistry writers, IArchiveStore store, JournalStoreOptions options, TimeProvider timeProvider)
        : this(writers, store, options, timeProvider, stateDatabase: null, beforeFollowing: null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="JournalStore"/> class backed by <paramref name="stateDatabase"/>'s
    /// per-ledger key indexes for live ledger reads (journals.md §8.3). Construction performs no I/O, and is the
    /// composition's start check.
    /// </summary>
    /// <param name="writers">The process's writer registry, whose reader and map view the store reads through.</param>
    /// <param name="store">The cluster's archive store, whose record holds <see cref="StoreParameters"/>.</param>
    /// <param name="options">The Journals policy, which the store copies.</param>
    /// <param name="timeProvider">Stamps each <c>Delete</c> and judges each <c>Expired</c>'s retention.</param>
    /// <param name="stateDatabase">The node's key-index database, or <see langword="null"/> to scan live ledgers.</param>
    public JournalStore(LedgerWriterRegistry writers, IArchiveStore store, JournalStoreOptions options, TimeProvider timeProvider, JournalStateDatabase? stateDatabase)
        : this(writers, store, options, timeProvider, stateDatabase, beforeFollowing: null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="JournalStore"/> class whose follower awaits
    /// <paramref name="beforeFollowing"/> once it starts, before it first waits for a change, which a test holds to start
    /// the follower late.
    /// </summary>
    /// <param name="writers">The process's writer registry, whose reader and map view the store reads through.</param>
    /// <param name="store">The cluster's archive store, whose record holds <see cref="StoreParameters"/>.</param>
    /// <param name="options">The Journals policy, which the store copies.</param>
    /// <param name="timeProvider">Stamps each <c>Delete</c> and judges each <c>Expired</c>'s retention.</param>
    /// <param name="beforeFollowing">A step the follower awaits as it starts, or none.</param>
    internal JournalStore(LedgerWriterRegistry writers, IArchiveStore store, JournalStoreOptions options, TimeProvider timeProvider, Func<Task>? beforeFollowing)
        : this(writers, store, options, timeProvider, stateDatabase: null, beforeFollowing)
    {
    }

    private JournalStore(LedgerWriterRegistry writers, IArchiveStore store, JournalStoreOptions options, TimeProvider timeProvider, JournalStateDatabase? stateDatabase, Func<Task>? beforeFollowing)
    {
        ArgumentNullException.ThrowIfNull(writers);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _options = options.Clone();
        CheckStartup(_options, store, writers.MaxBatchLength, writers.Options.MaxProposalBytes);
        _writers = writers;
        _map = writers.Reader.Map;
        _stateDatabase = stateDatabase;
        _archiveReader = new ArchiveReader(writers.Reader, store, timeProvider, stateDatabase?.KeyIndex);
        _store = store;
        _timeProvider = timeProvider;
        _historyReads = CallBudget.ForCalls(_options.MaxConcurrentHistoryReads, timeProvider, static waited => new UnreachableException("A history read waits for its slot with no deadline."));
        StoreParameters = StoreParametersOf(_options);

        // The follower's baseline is the view and the unserved ledgers as the store is constructed, before any admission
        // exists, so every later change which unserves an admission's writer wakes the follower, however late it starts.
        var epoch = _map.Current.Epoch;
        var unserved = _writers.Unserved;

        // The view serves every key, so it is followed in no caller's execution context (conventions.md §12).
        using (ExecutionContext.SuppressFlow())
        {
            _following = Task.Run(() => FollowAsync(epoch, unserved, beforeFollowing), CancellationToken.None);
        }
    }

    /// <summary>
    /// Gets the caller parameters the composition records in the archive store's record (archive.md §2.4), and compares
    /// with the record's, <c>ArchiveStoreRecord.CheckParametersMatch</c>, across the cluster's processes: <c>max-body-bytes</c>, the
    /// <c>MaxBodyBytes</c> this store enforces, so every process bounds bodies alike (journals.md §8.4).
    /// </summary>
    public IReadOnlyDictionary<string, string> StoreParameters { get; }

    /// <summary>
    /// Returns the caller parameters a store over <paramref name="options"/> records (<see cref="StoreParameters"/>), which a
    /// composition records before it builds a store, as a new cluster's first registration does (orleans.md §5.3).
    /// </summary>
    /// <param name="options">The Journals policy.</param>
    /// <exception cref="ArgumentOutOfRangeException">A setting is out of its range: a store never records one.</exception>
    public static IReadOnlyDictionary<string, string> StoreParametersOf(JournalStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["max-body-bytes"] = options.MaxBodyBytes.ToString(CultureInfo.InvariantCulture),
        }.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>Gets or sets a step the follower awaits before each sweep of the served writers, which tests hold.</summary>
    internal Func<Task>? BeforeSweep { get; set; }

    /// <summary>
    /// Gets or sets a step each proposal's outcome awaits once its writer has decided it, before admission applies it,
    /// which a test holds to keep a decided batch unapplied; none by default.
    /// </summary>
    internal Func<Task>? BeforeApply { get; set; }

    /// <summary>Gets or sets the <c>ReplaceRequired</c> mutant each admission the store creates decides as, which tests set (journals.md §10).</summary>
    internal LedgerAdmission.ReplaceRequiredMutant ReplaceRequiredMutation { get; set; }

    /// <summary>Gets or sets the reload mutant each admission the store creates rejects as, which tests set (journals.md §10).</summary>
    internal LedgerAdmission.ReloadMutant ReloadMutation { get; set; }

    /// <summary>Gets or sets the chain mutant each admission the store creates plans as, which tests set to show the chain checks catch it.</summary>
    internal LedgerAdmission.ChainMutant ChainMutation { get; set; }

    /// <summary>Gets or sets the rejection mutant each admission the store creates decides as, which tests set to show the oracles catch it.</summary>
    internal LedgerAdmission.RejectionMutant RejectionMutation { get; set; }

    /// <summary>Gets or sets the mutant the store's loads and reads fold as, which tests set to show the version checker or the cold-primary property catches it.</summary>
    internal JournalLoadMutant LoadMutation { get; set; }

    /// <summary>Gets or sets an observer of each rejection an admission the store creates decides, with its ledger, for the white-box rejection oracle.</summary>
    internal Action<LedgerId, JournalRejection>? OnRejected { get; set; }

    /// <summary>Gets or sets an observer of each <c>ReplaceRequired</c> an admission the store creates decides, with its ledger, for the white-box oracle.</summary>
    internal Action<LedgerId, JournalReplaceRequired>? OnReplaceRequired { get; set; }

    /// <summary>Gets or sets the mutant the store's reads alone fold as, which tests set to show the read-bound oracle catches it; none folds as <see cref="LoadMutation"/>.</summary>
    internal JournalLoadMutant ReadMutation { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether each admission applies entries at admission rather than
    /// after <c>Durable</c> (<c>apply-on-admit</c>, journals.md §10, JS-I2/JS-I3).
    /// </summary>
    internal bool ApplyOnAdmitMutant
    {
        get => Volatile.Read(ref _applyOnAdmitMutant);
        set
        {
            lock (_lock)
            {
                Volatile.Write(ref _applyOnAdmitMutant, value);
                foreach (var served in _served.Values)
                {
                    served.Admission.ApplyOnAdmitMutant = value;
                }
            }
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether expiry scans and admissions skip the tombstone retention check (JS-I7 mutant).
    /// </summary>
    internal bool IgnoreRetentionMutant
    {
        get => Volatile.Read(ref _ignoreRetentionMutant);
        set
        {
            lock (_lock)
            {
                Volatile.Write(ref _ignoreRetentionMutant, value);
                foreach (var served in _served.Values)
                {
                    served.Admission.IgnoreRetentionMutant = value;
                }
            }
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether owner reads answer warm state's newer version instead of the folded read
    /// cut's version (<c>answers-warm-state-newer-version</c>, journals.md §8.2, §10, JS-I3).
    /// </summary>
    internal bool AnswersWarmStateNewerVersionMutant { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether warm state is retained across primary claims (JS-I6 mutant): tests set
    /// this to verify that reusing stale warm state after a range leaves and returns fails the oracle.
    /// </summary>
    internal bool KeepWarmStateAcrossClaimsMutant { get; set; }

    /// <summary>Gets the number of entries waiting in the store's admissions, which the progress check reads once the run is quiet.</summary>
    internal int WaitingEntries
    {
        get
        {
            lock (_lock)
            {
                return _served.Values.Sum(static served => served.Admission.Waiting);
            }
        }
    }

    /// <summary>
    /// Gets or sets an observer of each admission the store creates, with its ledger and writer, which runs under the store's
    /// lock before the admission proposes anything, as a simulation's trace of the writer needs.
    /// </summary>
    internal Action<LedgerId, LedgerWriter>? OnAdmission { get; set; }

    /// <summary>Gets the history reader the store's loads and reads take, whose read-order mutant tests set.</summary>
    internal ArchiveReader ArchiveReader => _archiveReader;

    /// <summary>Gets the number of admissions the store has created, which tests read.</summary>
    internal int AdmissionsCreated => Volatile.Read(ref _admissionsCreated);

    /// <summary>Gets the number of owner reads waiting for their admissions to apply, which tests read.</summary>
    internal int ApplyWaiters
    {
        get
        {
            lock (_lock)
            {
                return _served.Values.Sum(static served => served.Admission.ApplyWaiters);
            }
        }
    }

    /// <summary>Gets the ledgers whose writers the store holds an admission of, which tests read.</summary>
    internal IReadOnlyCollection<LedgerId> ServedLedgers
    {
        get
        {
            lock (_lock)
            {
                return [.. _served.Keys];
            }
        }
    }

    /// <summary>
    /// Answers the slot below which the store's admission of <paramref name="ledger"/> has applied every batch its writer
    /// decided to warm state, the ledger's applied slot (orleans.md §5.9); <see langword="null"/> while the store holds no
    /// admission of it, before its first write.
    /// </summary>
    /// <param name="ledger">An open ledger whose primary is the store's node.</param>
    /// <returns>The applied slot, or <see langword="null"/>.</returns>
    public Slot? AppliedBelow(LedgerId ledger)
    {
        // The admission's own lock is taken after the store's is released, so no journal call waits behind it.
        LedgerAdmission? admission;
        lock (_lock)
        {
            admission = _served.TryGetValue(ledger, out var served) ? served.Admission : null;
        }

        return admission?.AppliedBelow;
    }

    /// <summary>
    /// Encodes an <c>Append</c> of <paramref name="payload"/>, non-empty, to the journal <paramref name="key"/>, once, as the
    /// writing process does (journals.md §2.2): the caller writes the record through <see cref="WriteAsync"/> or forwards
    /// it, sends the same record again after <c>Moved</c> or <c>Fenced</c>, and disposes it.
    /// </summary>
    /// <param name="key">The journal's row key, which a caller encodes once for its journal.</param>
    /// <param name="expectedVersion">The version of the state the entry applies to.</param>
    /// <param name="payload">The payload, of which the record retains its own lease; the caller keeps its own.</param>
    /// <returns>The encoded record, which the caller owns.</returns>
    /// <exception cref="ArgumentException">
    /// The payload is empty or longer than <c>MaxBodyBytes</c>, or the archive store does not admit the key
    /// (<c>AdmitsKey</c>).
    /// </exception>
    public JournalEntryRecord EncodeAppend(JournalRowKey key, JournalVersion expectedVersion, ArcBuffer payload)
    {
        Check(key, payload);
        return JournalEntryRecord.Append(key, expectedVersion, payload);
    }

    /// <summary>
    /// Encodes a <c>Replace</c> with <paramref name="snapshot"/>, possibly empty, of the journal <paramref name="key"/>, once,
    /// as <see cref="EncodeAppend"/> does.
    /// </summary>
    /// <param name="key">The journal's row key.</param>
    /// <param name="expectedVersion">The version of the state the entry applies to.</param>
    /// <param name="snapshot">The snapshot, <c>default</c> when empty, of which the record retains its own lease.</param>
    /// <returns>The encoded record, which the caller owns.</returns>
    /// <exception cref="ArgumentException">The snapshot is longer than <c>MaxBodyBytes</c>, or the archive store does not admit the key.</exception>
    public JournalEntryRecord EncodeReplace(JournalRowKey key, JournalVersion expectedVersion, ArcBuffer snapshot)
    {
        Check(key, snapshot);
        return JournalEntryRecord.Replace(key, expectedVersion, snapshot);
    }

    /// <summary>
    /// Encodes a <c>Delete</c> of the journal <paramref name="key"/>, stamped once from the store's clock, as
    /// <see cref="EncodeAppend"/> does, so a resend carries the same deletion time.
    /// </summary>
    /// <param name="key">The journal's row key.</param>
    /// <param name="expectedVersion">The version of the state the entry applies to.</param>
    /// <returns>The encoded record, which the caller owns.</returns>
    /// <exception cref="ArgumentException">The archive store does not admit the key.</exception>
    public JournalEntryRecord EncodeDelete(JournalRowKey key, JournalVersion expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(key);
        LedgerAdmission.CheckKey(key, _store, nameof(key));
        return JournalEntryRecord.Delete(key, expectedVersion, _timeProvider.GetUtcNow());
    }

    /// <summary>
    /// Writes an encoded entry: one this process encoded, or one forwarded to it, which the composition verified where its
    /// bytes entered the process (<see cref="JournalEntryRecord.Decode(JournalRowKey, ArcBuffer, out JournalEntryRecord)"/>,
    /// JS-I5). The store retains its own lease on the record while it needs it; the caller keeps and disposes its own.
    /// </summary>
    /// <param name="record">An <c>Append</c>, <c>Replace</c>, or <c>Delete</c>.</param>
    /// <param name="cancellationToken">Stops the caller waiting, which asserts nothing (journals.md §2.2).</param>
    /// <returns>The write's outcome; <c>Moved</c> when this process does not serve the key's open ledger's writer.</returns>
    /// <exception cref="ArgumentException">
    /// The record is the default or an <c>Expired</c>, which only the expiry scan writes (JS-I7); the archive store does not
    /// admit its key (<c>AdmitsKey</c>); or its payload is longer than <c>MaxBodyBytes</c>.
    /// </exception>
    /// <exception cref="ObjectDisposedException">
    /// The store was disposed. The store throws it only for a write which had no effect: before any admission takes the
    /// entry, or, for an entry an admission took and holds waiting, before it is proposed (journals.md §2.2).
    /// </exception>
    /// <exception cref="JournalStoreUnavailableException">
    /// The load of the key's state could not reach its ledger's quorum, so the write was never proposed and had no effect
    /// (journals.md §2.2, §6).
    /// </exception>
    /// <exception cref="JournalStoreCorruptException">The key's history, which its state loads from, contradicts itself.</exception>
    public Task<JournalWriteOutcome> WriteAsync(JournalEntryRecord record, CancellationToken cancellationToken = default)
    {
        LedgerAdmission.CheckRecord(record, _store, _options.MaxBodyBytes);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        if (Route(record.Key) is { } served)
        {
            var outcome = served.Admission.AdmitForStore(record, cancellationToken, out var mayMove);
            if (!mayMove)
            {
                return outcome;
            }

            if (outcome.IsCompletedSuccessfully)
            {
                var result = outcome.Result;
                if (result.Value is not JournalMoved moved || moved.Owner != _writers.Node)
                {
                    return outcome;
                }

                return RetryAfterMovedAsync(record.Retain(), cancellationToken);
            }

            return AwaitOrRetryAsync(outcome, record.Retain(), cancellationToken);
        }

        var immediateMoved = MovedOf(record.Key);
        if (immediateMoved.Owner != _writers.Node)
        {
            return Task.FromResult(new JournalWriteOutcome(immediateMoved));
        }

        return RetryAfterMovedAsync(record.Retain(), cancellationToken);
    }

    /// <summary>
    /// Reads the journal <paramref name="key"/> (journals.md §2.3, §8.2): its version, existence, length, and content, the
    /// fold of one history read through one cut, which reflects every entry acknowledged before the call and only decided
    /// entries (JS-I3), and the replace request the read gives.
    /// </summary>
    /// <param name="key">The journal's row key.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The read, which the caller disposes, or <c>Moved</c>.</returns>
    /// <exception cref="JournalStoreCorruptException">The history contradicts itself.</exception>
    /// <exception cref="JournalStoreUnavailableException">The history read could not reach its ledger's quorum (journals.md §2.3, §6).</exception>
    /// <exception cref="ObjectDisposedException">The store was disposed; a read has no effect (journals.md §2.2).</exception>
    public async Task<JournalReadOutcome> ReadAsync(JournalRowKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        var outcome = await ReadOnceAsync(key, cancellationToken).ConfigureAwait(false);
        if (outcome.Value is not JournalMoved moved || moved.Owner != _writers.Node)
        {
            return outcome;
        }

        await CatchUpAsync(cancellationToken).ConfigureAwait(false);
        outcome = await ReadOnceAsync(key, cancellationToken).ConfigureAwait(false);
        return outcome.Value is JournalMoved again && again.Owner == _writers.Node ? new JournalMoved(Owner: null) : outcome;
    }

    /// <summary>
    /// Stops following the map view, and disposes every admission, which fences it and waits for its proposals: every write
    /// the store accepted has its answer once this returns. The composition disposes the store before its registry.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var served in _served.Values.ToArray())
            {
                Retire(served);
            }
        }

        try
        {
            await _disposing.CancelAsync().ConfigureAwait(false);
            await _following.ConfigureAwait(false);
            Task[] retiring;
            lock (_lock)
            {
                retiring = [.. _retiring];
            }

            // A retired admission's fault, which only a defect raises, surfaces here.
            await Task.WhenAll(retiring).ConfigureAwait(false);
        }
        finally
        {
            _disposing.Dispose();
        }
    }

    /// <summary>
    /// Runs one expiry scan (journals.md §8.6): proposes an <c>Expired</c> for each deleted key whose tombstone retention
    /// has passed on the store's clock — first among the warm state of active primary claims, and then among cold keys
    /// discovered from the caught-up key indexes of <see cref="JournalStateDatabase"/> when configured — in rounds of at
    /// most <c>MaxBatchEntries</c>, each awaited before the next. Admission decides each by the key's state (JS-I1, JS-I7),
    /// so a candidate is a hint: a journal written or deleted again since, or with an entry in flight, keeps its state.
    /// </summary>
    /// <param name="cancellationToken">Stops the scan, which asserts nothing about the <c>Expired</c>s it proposed.</param>
    /// <returns>What the scan proposed, by each <c>Expired</c>'s outcome.</returns>
    /// <exception cref="ObjectDisposedException">The store was disposed, before or during the scan.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<JournalExpiryScanResult> ExpireTombstonesAsync(CancellationToken cancellationToken = default)
    {
        _ = Interlocked.Increment(ref _expiryScans);
        if (BeforeExpiryScan is { } hold)
        {
            await hold().ConfigureAwait(false);
        }

        var warmCandidates = ExpiryCandidates();
        var result = await JournalExpiryScan.RunAsync(this, warmCandidates, _options.MaxBatchEntries, cancellationToken).ConfigureAwait(false);
        if (_stateDatabase is null)
        {
            return result;
        }

        var cutoffTicks = IgnoreRetentionMutant
            ? long.MaxValue
            : _timeProvider.GetUtcNow().UtcTicks - _options.TombstoneRetention.Ticks;

        var map = _map.Current;
        var openLedgers = new List<(LedgerMetadata Ledger, Slot Below)>();
        foreach (var ledger in map.Ledgers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ledger.Status.Value is not LedgerStatus.Open || ledger.Primary.Node != _writers.Node)
            {
                continue;
            }

            if (_writers.TryGet(ledger.Id) is not { } writer)
            {
                continue;
            }

            var appliedBelow = AppliedBelow(ledger.Id) ?? Slot.Zero;
            var indexedWatermark = _stateDatabase.Watermark(ledger.Id);
            var readerTail = _writers.Reader.KnownContiguousTail(ledger);
            var openBelow = new Slot(Math.Max(
                Math.Max(appliedBelow.Value, indexedWatermark.Value),
                Math.Max(writer.KnownContiguousTail.Value, readerTail.Value)));
            openLedgers.Add((ledger, openBelow));
        }

        if (openLedgers.Count == 0)
        {
            return result;
        }

        var coldKeys = new List<JournalRowKey>();
        await _stateDatabase.CollectExpiredTombstonesAsync(
            openLedgers,
            map,
            cutoffTicks,
            IsWarmKey,
            coldKeys,
            cancellationToken).ConfigureAwait(false);

        if (coldKeys.Count == 0)
        {
            return result;
        }

        var coldConcurrency = Math.Min(_options.MaxBatchEntries, MaxColdExpiryConcurrency);
        var batchResult = await JournalExpiryScan.RunColdAsync(this, coldKeys, coldConcurrency, cancellationToken).ConfigureAwait(false);
        return new JournalExpiryScanResult(
            result.Expired + batchResult.Expired,
            result.Rejected + batchResult.Rejected,
            result.Moved + batchResult.Moved,
            result.Fenced + batchResult.Fenced,
            result.Ambiguous + batchResult.Ambiguous,
            result.Failed + batchResult.Failed);
    }

    /// <summary>
    /// Cooperatively drains in-flight and staged journal writes for <paramref name="ledger"/> within
    /// <paramref name="deadline"/> and then stops its <see cref="LedgerWriter"/> from admitting new batches
    /// (orleans.md §5.6, OR-I24).
    /// </summary>
    /// <remarks>
    /// While draining, already-accepted writes flush to durability while new writes are rejected so active load cannot
    /// starve the release. If another node concurrently seals the ledger, draining aborts immediately with
    /// <see cref="LedgerPrepareReleaseOutcome.Sealed"/>.
    /// </remarks>
    public async Task<LedgerPrepareReleaseOutcome> PrepareReleaseAsync(
        LedgerId ledger,
        KeyRange range,
        TimeSpan deadline,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(deadline, TimeSpan.Zero);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        cancellationToken.ThrowIfCancellationRequested();

        var started = _timeProvider.GetTimestamp();
        if (_map.Current.Get(ledger) is not { IsOpen: true } listed || listed.Range != range)
        {
            return LedgerPrepareReleaseOutcome.Sealed.Instance;
        }

        Served? served;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _ = _served.TryGetValue(ledger, out served);
            _releasing[ledger] = _releasing.GetValueOrDefault(ledger) + 1;
        }

        LedgerWriter? writer = null;
        var admissionDrained = false;
        try
        {
            writer = served?.Writer ?? _writers.TryGet(ledger);
            if (writer is null)
            {
                return _writers.TryGetHeld(ledger) is { Sequencer.StopReason: SequencerStopReason.Released } held
                    ? new LedgerPrepareReleaseOutcome.Released(held.KnownContiguousTail)
                    : LedgerPrepareReleaseOutcome.Sealed.Instance;
            }

            if (writer.Sequencer.StopReason == SequencerStopReason.Released)
            {
                return new LedgerPrepareReleaseOutcome.Released(writer.KnownContiguousTail);
            }

            if (served is not null)
            {
                var admissionDrain = await served.Admission.PrepareReleaseAsync(deadline, writer.Fenced, cancellationToken).ConfigureAwait(false);
                if (admissionDrain == LedgerAdmission.LedgerAdmissionDrainResult.Sealed)
                {
                    return LedgerPrepareReleaseOutcome.Sealed.Instance;
                }

                if (admissionDrain == LedgerAdmission.LedgerAdmissionDrainResult.TimedOut)
                {
                    return LedgerPrepareReleaseOutcome.TimedOut.Instance;
                }

                admissionDrained = true;
            }

            var remaining = deadline - _timeProvider.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
            {
                if (!writer.Sequencer.IsStopped)
                {
                    return LedgerPrepareReleaseOutcome.TimedOut.Instance;
                }

                return writer.Sequencer.StopReason == SequencerStopReason.Released
                    ? new LedgerPrepareReleaseOutcome.Released(writer.KnownContiguousTail)
                    : LedgerPrepareReleaseOutcome.Sealed.Instance;
            }

            return await writer.PrepareReleaseAsync(remaining, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            var lastReleasing = false;
            lock (_lock)
            {
                if (_releasing.TryGetValue(ledger, out var count))
                {
                    if (count <= 1)
                    {
                        _ = _releasing.Remove(ledger);
                        lastReleasing = true;
                    }
                    else
                    {
                        _releasing[ledger] = count - 1;
                    }
                }

                if (writer is not null && writer.Sequencer.IsStopped
                    && _served.TryGetValue(ledger, out var current)
                    && ReferenceEquals(current.Writer, writer))
                {
                    Retire(current);
                }
            }

            if (admissionDrained && lastReleasing && served is not null && writer is { Sequencer.IsStopped: false })
            {
                served.Admission.CancelDrain();
            }
        }
    }

    /// <summary>Gets the number of expiry scans the store has started, which tests read.</summary>
    internal int ExpiryScans => Volatile.Read(ref _expiryScans);

    /// <summary>Gets or sets a step each expiry scan awaits as it starts, before it reads its candidates, which tests hold.</summary>
    internal Func<Task>? BeforeExpiryScan { get; set; }

    /// <summary>
    /// Gets or sets an observer of each <c>Expired</c> an expiry scan proposes, told its key and expected version before the
    /// proposal, which returns the observer of its outcome, told none when the proposal failed; for the simulation's history.
    /// </summary>
    internal Func<JournalRowKey, JournalVersion, Action<JournalWriteOutcome?>>? OnExpiryProposed { get; set; }

    /// <summary>
    /// Admits an <c>Expired</c> of the deleted state at <paramref name="expectedVersion"/> through the admission which serves
    /// <paramref name="key"/>, for the expiry scan (<see cref="JournalExpiryScan"/>, journals.md §8.6, JS-I7), its only
    /// caller in the product; tests call it as a scan would.
    /// </summary>
    internal Task<JournalWriteOutcome> ExpireAsync(JournalRowKey key, JournalVersion expectedVersion, CancellationToken cancellationToken = default) =>
        Route(key) is { } served ? served.Admission.ExpireAsync(key, expectedVersion, cancellationToken) : Task.FromResult(new JournalWriteOutcome(MovedOf(key)));

    /// <summary>
    /// Loads a cold tombstone candidate found in the key indexes (journals.md §8.6) and submits its <c>Expired</c>
    /// through the admission serving <paramref name="key"/>, for the expiry scan (<see cref="JournalExpiryScan"/>, JS-I7).
    /// </summary>
    internal Task<JournalWriteOutcome?> ExpireColdAsync(JournalRowKey key, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        if (Route(key) is not { } served)
        {
            return Task.FromResult<JournalWriteOutcome?>(new JournalWriteOutcome(MovedOf(key)));
        }

        var observer = OnExpiryProposed is { } onProposed
            ? (Func<JournalVersion, Action<JournalWriteOutcome?>?>)(version => onProposed(key, version))
            : null;
        return served.Admission.ExpireColdAsync(key, observer, cancellationToken);
    }

    /// <summary>
    /// Returns the expiry scan's candidates (<see cref="LedgerAdmission.AddExpiryCandidates"/>) from every admission the
    /// store holds, each read under the admission's own lock after the store's is released, so no journal call waits on a
    /// scan behind more than one admission's keys.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The store was disposed.</exception>
    private List<(JournalRowKey Key, JournalVersion Version)> ExpiryCandidates()
    {
        LedgerAdmission[] admissions;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            admissions = [.. _served.Values.Select(static served => served.Admission)];
        }

        var candidates = new List<(JournalRowKey Key, JournalVersion Version)>();
        foreach (var admission in admissions)
        {
            admission.AddExpiryCandidates(candidates);
        }

        return candidates;
    }

    private bool IsWarmKey(JournalRowKey key)
    {
        if (_map.FindOpen(key.Span) is not { } open)
        {
            return false;
        }

        LedgerAdmission? admission;
        lock (_lock)
        {
            admission = _served.TryGetValue(open.Id, out var served) ? served.Admission : null;
        }

        return admission?.IsWarm(key) == true;
    }

    /// <summary>
    /// Runs the startup checks of journals.md §8.4 on <paramref name="options"/>, after their ranges: the largest batch
    /// admission builds (<see cref="LedgerAdmission.LargestBatch"/>) is within the replica's payload limit, which also holds
    /// admission's own guard, a batch of one longest row; <c>MaxBodyBytes</c> is at most the store's
    /// <c>MaxAppendBytes</c>; and <c>MaxQueuedBytes</c> and the writers' <c>MaxProposalBytes</c> each hold the largest
    /// batch.
    /// </summary>
    /// <exception cref="JournalStoreLimitException">A check fails, naming its bound.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A setting is out of its range.</exception>
    internal static void CheckStartup(JournalStoreOptions options, IArchiveStore store, int maxBatchLength, long maxProposalBytes)
    {
        LedgerAdmission.CheckRanges(options);
        var largest = LedgerAdmission.LargestBatch(options);
        if (largest > maxBatchLength)
        {
            throw new JournalStoreLimitException(
                JournalStoreLimit.PayloadLimit,
                string.Create(CultureInfo.InvariantCulture, $"The largest batch admission builds, {largest} bytes, passes the replica's payload limit of {maxBatchLength} bytes."),
                nameof(options));
        }

        if (store.MaxAppendBytes is { } maxAppendBytes && options.MaxBodyBytes > maxAppendBytes)
        {
            throw new JournalStoreLimitException(
                JournalStoreLimit.MaxAppendBytes,
                string.Create(CultureInfo.InvariantCulture, $"MaxBodyBytes, {options.MaxBodyBytes} bytes, passes the archive store's MaxAppendBytes, {maxAppendBytes} bytes."),
                nameof(options));
        }

        if (options.MaxQueuedBytes < largest)
        {
            throw new JournalStoreLimitException(
                JournalStoreLimit.MaxQueuedBytes,
                string.Create(CultureInfo.InvariantCulture, $"MaxQueuedBytes, {options.MaxQueuedBytes} bytes, holds less than the largest batch, {largest} bytes."),
                nameof(options));
        }

        if (maxProposalBytes < largest)
        {
            throw new JournalStoreLimitException(
                JournalStoreLimit.MaxProposalBytes,
                string.Create(CultureInfo.InvariantCulture, $"The writers' MaxProposalBytes, {maxProposalBytes} bytes, holds less than the largest batch, {largest} bytes."),
                nameof(options));
        }
    }

    /// <summary>Starts an admission's disposal on the thread pool, in no caller's execution context (conventions.md §12).</summary>
    private static Task DisposeDetached(Served served)
    {
        var suppress = !ExecutionContext.IsFlowSuppressed();
        var flow = suppress ? ExecutionContext.SuppressFlow() : default;
        try
        {
            return Task.Run(
                async () => await served.Admission.DisposeAsync().ConfigureAwait(false),
                CancellationToken.None);
        }
        finally
        {
            if (suppress)
            {
                flow.Undo();
            }
        }
    }

    /// <summary>Checks a key and a payload as the writing process does, before it encodes their record (journals.md §2.2).</summary>
    private void Check(JournalRowKey key, ArcBuffer payload)
    {
        ArgumentNullException.ThrowIfNull(key);
        LedgerAdmission.CheckKey(key, _store, nameof(key));
        LedgerAdmission.CheckPayloadLength(payload.Length, _options.MaxBodyBytes, nameof(payload));
    }

    /// <summary>
    /// Awaits an admission write that could still complete with a same-node <see cref="JournalMoved"/>, and retries once
    /// after the registry has reconciled when the first answer names this process's own node (journals.md §8.5).
    /// </summary>
    private async Task<JournalWriteOutcome> AwaitOrRetryAsync(
        Task<JournalWriteOutcome> first,
        JournalEntryRecord record,
        CancellationToken cancellationToken)
    {
        using (record)
        {
            var outcome = await first.ConfigureAwait(false);
            if (outcome.Value is not JournalMoved moved || moved.Owner != _writers.Node)
            {
                return outcome;
            }

            // A Moved has no effect (JS-I4), so the same record may be sent again.
            await CatchUpAsync(cancellationToken).ConfigureAwait(false);
            outcome = await WriteOnceAsync(record, cancellationToken).ConfigureAwait(false);
            return outcome.Value is JournalMoved again && again.Owner == _writers.Node ? new JournalWriteOutcome(new JournalMoved(Owner: null)) : outcome;
        }
    }

    /// <summary>
    /// Retries a write once after the registry has reconciled with the map when the first attempt answered a same-node
    /// <see cref="JournalMoved"/> (journals.md §8.5).
    /// </summary>
    private async Task<JournalWriteOutcome> RetryAfterMovedAsync(JournalEntryRecord record, CancellationToken cancellationToken)
    {
        using (record)
        {
            await CatchUpAsync(cancellationToken).ConfigureAwait(false);
            var outcome = await WriteOnceAsync(record, cancellationToken).ConfigureAwait(false);
            return outcome.Value is JournalMoved again && again.Owner == _writers.Node ? new JournalWriteOutcome(new JournalMoved(Owner: null)) : outcome;
        }
    }

    private Task<JournalWriteOutcome> WriteOnceAsync(JournalEntryRecord record, CancellationToken cancellationToken) =>
        Route(record.Key) is { } served ? served.Admission.AdmitAsync(record, cancellationToken) : Task.FromResult(new JournalWriteOutcome(MovedOf(record.Key)));

    /// <summary>Waits until the registry has reconciled with the map view as it is now, so it serves what that view lists.</summary>
    private Task CatchUpAsync(CancellationToken cancellationToken) => _writers.ReconciledAsync(_map.Current.Epoch, cancellationToken);

    /// <summary>
    /// Reads <paramref name="key"/> on its primary once (journals.md §8.2): the fold of one history read, once the read is
    /// its admission's (<see cref="IsServedBy"/>, JS-I3) and that admission has applied every batch of its own below the
    /// read's tail; or <c>Moved</c>.
    /// </summary>
    private async ValueTask<JournalReadOutcome> ReadOnceAsync(JournalRowKey key, CancellationToken cancellationToken)
    {
        if (Route(key) is not { } served)
        {
            return MovedOf(key);
        }

        var read = await ReadHistoryAsync(key, includeBodies: true, cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsServedBy(read, served))
            {
                read.Dispose();
                return MovedOf(key);
            }

            var applied = await served.Admission.WaitAppliedAsync(read.Through[served.Ledger], cancellationToken).ConfigureAwait(false);
            if (!applied)
            {
                read.Dispose();
                return MovedOf(key);
            }

            var versionOverride = AnswersWarmStateNewerVersionMutant ? served.Admission.TryGetWarmVersion(key) : null;
            return new JournalReadResult(read, JournalState.Fold(read.Prefix, read.Rows, ReadMutation == JournalLoadMutant.None ? LoadMutation : ReadMutation, StaleBase(key, read)), _store, versionOverride);
        }
        catch
        {
            read.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Runs a history read of <paramref name="key"/>, the archive counted only through <see cref="ArchiveReader"/> (JS-I9),
    /// reporting the archive's corruption as the journal's, and a quorum of the key's ledger out of reach as the store's
    /// unavailability (journals.md §6), once one of the store's
    /// <c>MaxConcurrentHistoryReads</c> slots is free; it holds the slot only while it reads, so no read waits for a slot
    /// behind a read which waits for anything but the archive and the replicas (journals.md §8.8).
    /// </summary>
    private async ValueTask<ArchiveRead> ReadHistoryAsync(JournalRowKey key, bool includeBodies, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _disposed) || _disposing.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(JournalStore), "The store was disposed while a history read waited for a slot.");
        }

        using var fastSlot = _historyReads.TryEnter();
        using var slowSlot = fastSlot is null ? await EnterHistoryReadAsync(cancellationToken).ConfigureAwait(false) : null;
        try
        {
            return await _archiveReader.ReadAsync(key.Memory, includeBodies, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArchiveCorruptException or InvalidDataException)
        {
            throw new JournalStoreCorruptException(string.Create(CultureInfo.InvariantCulture, $"The history of the journal '{key}' contradicts itself: {exception.Message}"), exception);
        }
        catch (AggregateException quorum) when (LedgerReplicaFailures.IsTransient(quorum, cancellationToken))
        {
            throw new JournalStoreUnavailableException(string.Create(CultureInfo.InvariantCulture, $"The history read of the journal '{key}' could not reach its ledger's quorum: {quorum.Message}"), quorum);
        }
    }

    /// <summary>
    /// Waits, in arrival order, for one of the store's history-read slots when no slot is immediately available. The
    /// store's disposal withdraws the wait, which then throws <see cref="ObjectDisposedException"/>, the store's answer for
    /// a call without effect (journals.md §2.2).
    /// </summary>
    private async ValueTask<CallBudget.Grant> EnterHistoryReadAsync(CancellationToken cancellationToken)
    {
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposing.Token);
        try
        {
            return await _historyReads.EnterAsync(deadline: null, waiting.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(JournalStore), "The store was disposed while a history read waited for a slot.");
        }
    }

    /// <summary>Gets the store's history-read slots, a budget of calls alone, which tests read.</summary>
    internal CallBudget HistoryReads => _historyReads;

    /// <summary>
    /// Returns whether <paramref name="read"/> is <paramref name="served"/>'s to answer (JS-I3, JS-I6): the read was of the
    /// admission's open ledger, and the registry still serves the admission's writer.
    /// </summary>
    /// <remarks>
    /// A read of another open ledger is never its admission's: the view only moves forward and a closed ledger never opens
    /// again, so the admission's ledger is closed by then and its writer unserved. A read of the admission's ledger is
    /// linearizable: the admission's writer was created before the read began, and its ledger's <c>Ledger</c> reports
    /// itself served from then on (<c>LedgerTail.ServedHere</c>), while a seal check which found it sealed restarts the read.
    /// </remarks>
    private bool IsServedBy(ArchiveRead read, Served served)
    {
        if (read.OpenLedger != served.Ledger)
        {
            Debug.Assert(!IsCurrent(served), "A read whose map lists another open ledger over the key finds the admission's ledger closed.");
            return false;
        }

        Debug.Assert(read.Linearizable, "A read of a ledger whose writer this process created before the read is linearizable (HI-I1).");
        return IsCurrent(served);
    }

    /// <summary>Returns whether the registry still serves <paramref name="served"/>'s writer.</summary>
    private bool IsCurrent(Served served) => _writers.TryGet(served.Ledger) == served.Writer;

    /// <summary>
    /// Returns the key's state and planning state as a restarted primary would load them from one history read now, which
    /// the cold-primary property compares with the warm primary's (journals.md §10).
    /// </summary>
    internal async Task<JournalWarm> LoadColdAsync(JournalRowKey key, CancellationToken cancellationToken)
    {
        using var read = await ReadHistoryAsync(key, includeBodies: false, cancellationToken).ConfigureAwait(false);
        var load = JournalLoad.Of(read, _store, LoadMutation, StaleBase(key, read));
        return JournalWarm.Of(load.State, load.Planning);
    }

    /// <summary>Returns the warm state of the key, from the admission which serves it, while nothing of it waits or is in flight.</summary>
    internal JournalWarm? Warm(JournalRowKey key) => Route(key)?.Admission.Warm(key);

    /// <summary>Returns the version base <see cref="JournalLoadMutant.RebaseOnStaleToken"/> rebases to: the first a load of the key saw.</summary>
    private RowToken? StaleBase(JournalRowKey key, ArchiveRead read) =>
        LoadMutation == JournalLoadMutant.RebaseOnStaleToken && read.Prefix is { } prefix ? _staleBases.GetOrAdd(key, prefix.VersionBase) : null;

    /// <summary>
    /// Loads a key for <paramref name="served"/>'s admission (journals.md §8.3, JS-I9): its state and planning state from one
    /// history read, or <c>Moved</c> when the read is not the admission's to answer (<see cref="IsServedBy"/>).
    /// </summary>
    private async ValueTask<JournalLoadOutcome> LoadAsync(Served served, JournalRowKey key, CancellationToken cancellationToken)
    {
        if (KeepWarmStateAcrossClaimsMutant
            && _retainedWarmSeeds.TryGetValue(key, out var retained)
            && retained.Ledger != served.Ledger)
        {
            var stale = new JournalLoad(retained.Applied?.Clone(), JournalPlanning.Load(_store, [], retained.NextToken));
            return new JournalLoadOutcome(new JournalLoadOutcome.Loaded(stale));
        }

        using var read = await ReadHistoryAsync(key, includeBodies: false, cancellationToken).ConfigureAwait(false);
        return IsServedBy(read, served)
            ? new JournalLoadOutcome(new JournalLoadOutcome.Loaded(JournalLoad.Of(read, _store, LoadMutation, StaleBase(key, read))))
            : new JournalLoadOutcome(MovedOf(key));
    }

    /// <summary>Proposes an admission's batch through its writer, whose decided outcome awaits <see cref="BeforeApply"/>, if a test set it.</summary>
    private ValueTask<ProposalOutcome> ProposeAsync(LedgerWriter writer, LedgerBatch batch, CancellationToken cancellationToken)
    {
        var outcome = writer.ProposeAsync(batch, cancellationToken);
        return BeforeApply is { } hold ? HeldAsync(outcome, hold) : outcome;

        static async ValueTask<ProposalOutcome> HeldAsync(ValueTask<ProposalOutcome> outcome, Func<Task> hold)
        {
            var decided = await outcome.ConfigureAwait(false);
            await hold().ConfigureAwait(false);
            return decided;
        }
    }

    /// <summary>Returns the key's <c>Moved</c>, naming the primary of its open ledger in the map view, if it lists one.</summary>
    private JournalMoved MovedOf(JournalRowKey key) => new(_map.FindOpen(key.Span)?.Primary.Node);

    /// <summary>
    /// Returns the admission of the writer the registry serves for the open ledger the map lists over <paramref name="key"/>,
    /// creating it at the writer's first use, or <see langword="null"/> when the process serves none. Under the store's lock
    /// it reads the registry, retires an admission whose writer the registry no longer serves, and creates at most one per
    /// writer (<see cref="LedgerWriterRegistry.TryGet"/>).
    /// </summary>
    /// <exception cref="ObjectDisposedException">The store was disposed.</exception>
    private Served? Route(JournalRowKey key)
    {
        if (_map.FindOpen(key.Span) is not { } open)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
            return null;
        }

        return EnsureServed(open.Id);
    }

    private Served? EnsureServed(LedgerId ledger)
    {
        if (!Volatile.Read(ref _disposed))
        {
            var snapshot = Volatile.Read(ref _servedSnapshot);
            for (var index = 0; index < snapshot.Length; index++)
            {
                var candidate = snapshot[index];
                if (candidate.Ledger == ledger)
                {
                    if (ReferenceEquals(candidate.Writer, _writers.TryGet(ledger)))
                    {
                        return candidate;
                    }

                    break;
                }
            }
        }

        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var writer = _writers.TryGet(ledger);
            if (_served.TryGetValue(ledger, out var served))
            {
                if (served.Writer == writer)
                {
                    return served;
                }

                Debug.Assert(writer is null, "The registry answers one writer per ledger, then nothing (LP-I1).");
                Retire(served);
            }

            if (writer is null || _releasing.ContainsKey(ledger))
            {
                return null;
            }

            // Creating an admission does no I/O: it starts its completions and returns.
            served = new Served(this, ledger, writer);
            OnAdmission?.Invoke(ledger, writer);
            _served.Add(ledger, served);
            Volatile.Write(ref _servedSnapshot, [.. _served.Values]);
            _admissionsCreated++;
            return served;
        }
    }

    /// <summary>
    /// Retires an admission whose writer the registry no longer serves, or at the store's disposal: removes it, starts its
    /// disposal, and keeps the disposal for the store's, in one section under the lock. A retired admission's fault, which
    /// only a defect raises, surfaces at the store's disposal.
    /// </summary>
    private void Retire(Served served)
    {
        Debug.Assert(_lock.IsHeldByCurrentThread, "Retirement is registered under the lock which removes the admission.");
        if (KeepWarmStateAcrossClaimsMutant)
        {
            served.Admission.CopyWarmForMutant(served.Ledger, _retainedWarmSeeds);
        }

        _ = _served.Remove(served.Ledger);
        Volatile.Write(ref _servedSnapshot, _served.Count == 0 ? [] : [.. _served.Values]);
        _ = _retiring.RemoveAll(static task => task.IsCompletedSuccessfully);
        _retiring.Add(DisposeDetached(served));
    }

    /// <summary>
    /// Retires every admission whose writer the registry no longer serves, after each reconciliation of the registry with a
    /// newer map value or a newly unserved ledger than <paramref name="epoch"/> and <paramref name="unserved"/>, the
    /// baseline the constructor took, so it sweeps what the registry has decided.
    /// </summary>
    private async Task FollowAsync(ulong epoch, ImmutableArray<LedgerId> unserved, Func<Task>? beforeFollowing)
    {
        try
        {
            if (beforeFollowing is not null)
            {
                await beforeFollowing().WaitAsync(_disposing.Token).ConfigureAwait(false);
            }

            while (true)
            {
                await WaitForChangeAsync(epoch, unserved).ConfigureAwait(false);
                epoch = _map.Current.Epoch;
                await _writers.ReconciledAsync(epoch, _disposing.Token).ConfigureAwait(false);
                unserved = _writers.Unserved;
                if (BeforeSweep is { } beforeSweep)
                {
                    await beforeSweep().WaitAsync(_disposing.Token).ConfigureAwait(false);
                }

                lock (_lock)
                {
                    foreach (var served in _served.Values.ToArray())
                    {
                        if (!IsCurrent(served))
                        {
                            Retire(served);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_disposing.IsCancellationRequested)
        {
            // The store's disposal ends the following.
        }
    }

    /// <summary>
    /// Waits until the map view passes <paramref name="epoch"/> or the registry lists a ledger unserved which
    /// <paramref name="unserved"/> does not, as when a writer's lifetime stops while its ledger is listed open.
    /// </summary>
    private async Task WaitForChangeAsync(ulong epoch, ImmutableArray<LedgerId> unserved)
    {
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(_disposing.Token);
        var changed = _map.WaitForChangeAsync(epoch, waiting.Token);
        var stopped = _writers.WaitForUnservedAsync(unserved.ToHashSet(), waiting.Token);
        _ = await Task.WhenAny(changed, stopped).ConfigureAwait(false);
        await waiting.CancelAsync().ConfigureAwait(false);
        foreach (var wait in (Task[])[changed, stopped])
        {
            try
            {
                await wait.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (waiting.IsCancellationRequested)
            {
                // The other wait ended first, or the store is being disposed.
            }
        }

        _disposing.Token.ThrowIfCancellationRequested();
    }

    /// <summary>A served writer and its admission, which loads its keys through the store.</summary>
    private sealed class Served
    {
        public Served(JournalStore store, LedgerId ledger, LedgerWriter writer)
        {
            Ledger = ledger;
            Writer = writer;
            Admission = LedgerAdmission.For(
                writer,
                store._options,
                store._store,
                store._timeProvider,
                (key, disposal) => store.LoadAsync(this, key, disposal),
                (batch, cancellationToken) => store.ProposeAsync(writer, batch, cancellationToken));
            Admission.ReloadMutation = store.ReloadMutation;
            Admission.ReplaceRequiredMutation = store.ReplaceRequiredMutation;
            Admission.ChainMutation = store.ChainMutation;
            Admission.RejectionMutation = store.RejectionMutation;
            Admission.ApplyOnAdmitMutant = store.ApplyOnAdmitMutant;
            Admission.IgnoreRetentionMutant = store.IgnoreRetentionMutant;
            if (store.OnRejected is { } rejected)
            {
                Admission.OnRejected = rejection => rejected(ledger, rejection);
            }

            if (store.OnReplaceRequired is { } replaceRequired)
            {
                Admission.OnReplaceRequired = decision => replaceRequired(ledger, decision);
            }
        }

        public LedgerId Ledger { get; }

        public LedgerWriter Writer { get; }

        public LedgerAdmission Admission { get; }
    }
}
