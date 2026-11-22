using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.ObjectStore;

namespace Orleans.Durable.Storage.Engine.Archive.Logs.Control;

/// <summary>What one sweep of archive logs found and deleted (archive-logs.md §2.5, §8.5).</summary>
/// <param name="BasesListed">The generation bases the sweep's slice of the listing named.</param>
/// <param name="LogsRead">The logs listing two or more bases, whose properties the sweep read.</param>
/// <param name="BasesDeleted">The bases this sweep deleted: each below its log's current generation, as a read after the listing showed.</param>
/// <param name="MarkersListed">The drain markers the listing named.</param>
/// <param name="MarkersDeleted">The markers this sweep deleted: each of a ledger a map read showed dropped.</param>
/// <param name="Unreadable">The logs whose properties read found them corrupt, or whose bases name two keys, which the sweep left in place and logged.</param>
/// <param name="Skipped">The bases whose recorded key hash does not parse, which the sweep left in place and counted.</param>
/// <param name="BasesEnded">Whether the sweep's slice reached the end of the bases' listing, so the next sweep lists from its start; otherwise the next resumes after the slice.</param>
public readonly record struct ArchiveLogSweepResult(int BasesListed, int LogsRead, int BasesDeleted, int MarkersListed, int MarkersDeleted, int Unreadable, int Skipped, bool BasesEnded);

/// <summary>
/// Deletes archive logs' garbage (archive-logs.md §2.5, §8.5): superseded generations' bases, and dropped ledgers' drain
/// markers. It is the only component which deletes anything in the archive-log store or under <c>durable-storage/</c> (LOG-I6), and it
/// never deletes a log's current generation, a log's write-ahead object, or the store's record.
/// </summary>
/// <remarks>
/// <para>
/// <b>Superseded bases.</b> It streams the listing of the store's generation bases, in the order of their names, holding
/// open only the logs whose bases may still follow: a log's bases all fall in the block of names which begin with its own
/// <c>{log}/</c>, and a log whose name extends another's with <c>/</c> falls inside that block, so the open logs form a
/// stack as deep as the names nest, and a log closes at the first name outside its block. Every generation an opener
/// created has its own base, so a closed log which lists one base holds only its current or an in-progress generation;
/// for each listing two or more, it reads the log's properties after the listing and deletes the listed bases below the
/// current generation the read shows, never the current one, and none of a log the read finds absent (LOG-I2). A
/// straggler opener which re-creates a deleted base below the current generation leaves an orphan, which a later sweep
/// deletes the same way. A base whose recorded key hash does not parse it counts and keeps. A sweep lists at most
/// <see cref="PagesPerSweep"/> pages, a slice, and the next sweep resumes from the store's continuation with the logs the
/// slice left open, so a log whose bases straddle two slices is decided whole, once the listing passes its block; a sweep
/// which fails leaves the next to list its slice again from its start. A sweeper runs one sweep at a time.
/// </para>
/// <para>
/// <b>Markers.</b> Beside the bases, it reads the ledger map, then streams the listing of <c>durable-storage/d/</c>, and deletes each
/// marker of a ledger the map shows dropped: below its <c>NextLedgerId</c> and not listed. A dropped ledger never returns
/// to the map, so its markers are never read again; a marker of a ledger the read predates the drop of, or a straggler's
/// created after the listing, a later sweep deletes.
/// </para>
/// <para>
/// It runs at most <see cref="DeleteConcurrency"/> deletions, and as many properties reads, at once. One sweeper per
/// cluster on a cadence suffices, and more are safe: each deletion's condition holds for any number of sweepers, drains,
/// and stragglers.
/// </para>
/// </remarks>
public sealed partial class ArchiveLogSweeper
{
    /// <summary>The pages of the bases' listing a sweep lists at most by default: ten million bases at Azure's 5,000 a page.</summary>
    public const int DefaultPagesPerSweep = 2_000;

    private readonly IArchiveLogStore _logs;
    private readonly IObjectStore _objects;
    private readonly LedgerMapView _map;
    private readonly ILogger _logger;

    // Where the next slice of the bases' listing starts, null at the listing's start, and the logs the last slice left
    // open; and whether a sweep runs, since a slice resumes where the sweep before it ended.
    private string? _resumeAfter;
    private Stack<OpenLog> _open = [];
    private int _sweeping;

    /// <summary>Initializes a new instance of the <see cref="ArchiveLogSweeper"/> class.</summary>
    /// <param name="gate">
    /// The silo's archive gate, which binds the archive-log store and the object store of durable storage's objects under
    /// <c>durable-storage/</c>: a sweep deletes only once the gate passed (archive-logs.md §7.5, orleans.md §5.13).
    /// </param>
    /// <param name="map">The ledger map view, which the sweep reads before listing the markers.</param>
    /// <param name="logger">Logs a log the sweep cannot read.</param>
    public ArchiveLogSweeper(ArchiveLogGate gate, LedgerMapView map, ILogger<ArchiveLogSweeper> logger)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(logger);
        _logs = gate.Logs;
        _objects = gate.Objects;
        _map = map;
        _logger = logger;
    }

    /// <summary>Gets the deletions a sweep runs at once, at least 1; 16 by default.</summary>
    public int DeleteConcurrency
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            field = value;
        }
    } = 16;

    /// <summary>Gets the most logs a sweep held open at once, the names' nesting; for tests.</summary>
    internal int MostOpenLogs { get; private set; }

    /// <summary>
    /// Gets the pages of the bases' listing a sweep lists at most, at least 1; 2,000 by default, ten million bases at
    /// Azure's 5,000 a page, about one sweep interval of listing, so a sweep's cost stays bounded however many logs the
    /// store holds (archive-logs.md §8.5).
    /// </summary>
    public int PagesPerSweep
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            field = value;
        }
    } = DefaultPagesPerSweep;

    /// <summary>
    /// Gets a value indicating whether the sweeper is the <c>delete-without-read</c> mutant, which deletes every listed base
    /// but the highest it lists without reading the log's properties; for tests.
    /// </summary>
    internal bool DeletesWithoutReadMutant { get; init; }

    /// <summary>Sweeps once: one slice of the superseded bases and, beside them, the dropped ledgers' markers.</summary>
    /// <param name="cancellationToken">
    /// Stops the sweep; what it deleted stays deleted, and the next sweep lists the slice again from its start.
    /// </param>
    /// <returns>What the sweep found and deleted.</returns>
    /// <exception cref="InvalidOperationException">Another sweep of this sweeper runs: a slice resumes where the sweep before it ended.</exception>
    public async Task<ArchiveLogSweepResult> SweepAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _sweeping, 1) != 0)
        {
            throw new InvalidOperationException("An archive-log sweeper sweeps once at a time: each slice resumes where the one before it ended.");
        }

        try
        {
            var bases = SweepBasesAsync(cancellationToken);
            var markers = SweepMarkersAsync(cancellationToken);
            await Task.WhenAll(bases, markers).ConfigureAwait(false);
            var (basesListed, logsRead, basesDeleted, unreadable, skipped, ended) = await bases.ConfigureAwait(false);
            var (markersListed, markersDeleted) = await markers.ConfigureAwait(false);
            return new ArchiveLogSweepResult(basesListed, logsRead, basesDeleted, markersListed, markersDeleted, unreadable, skipped, ended);
        }
        finally
        {
            Volatile.Write(ref _sweeping, 0);
        }
    }

    /// <summary>Gets the continuation the next sweep's slice resumes after, or <see langword="null"/> at the listing's start; for tests.</summary>
    internal string? ResumeAfter => _resumeAfter;

    [LoggerMessage(Level = LogLevel.Error, Message = "The archive-log sweeper left the bases of the log '{Log}' in place: {Reason}")]
    private static partial void LogUnreadable(ILogger logger, string log, string reason);

    /// <summary>
    /// Lists one slice of the bases, at most <see cref="PagesPerSweep"/> pages, from where the last slice ended, deciding
    /// each log once the listing passes its block; answers its counts and whether the slice reached the listing's end. A
    /// slice which ends early leaves its open logs and its continuation to the next sweep; a slice which fails leaves the
    /// next sweep to list it again from its start, its open logs forgotten, which decides those logs on fewer of their
    /// bases and so deletes fewer, never one LOG-I2 forbids, since each deletion still follows a read after the listing.
    /// </summary>
    private async Task<(int Listed, int Read, int Deleted, int Unreadable, int Skipped, bool Ended)> SweepBasesAsync(CancellationToken cancellationToken)
    {
        var counts = new BaseCounts();
        var startedAfter = _resumeAfter;
        var open = _open;
        string? resumeAfter = null;
        var listed = false;
        try
        {
            using var deletions = new Bounded(DeleteConcurrency);
            using var reads = new Bounded(DeleteConcurrency);
            try
            {
                var pages = 0;
                await foreach (var page in _logs.ListGenerationsAsync(startedAfter, cancellationToken).ConfigureAwait(false))
                {
                    foreach (var generation in page.Generations)
                    {
                        await WalkAsync(generation, open, counts, reads, deletions, cancellationToken).ConfigureAwait(false);
                    }

                    if (page.Continuation is { } next && ++pages >= PagesPerSweep)
                    {
                        resumeAfter = next;
                        break;
                    }
                }

                while (resumeAfter is null && open.TryPop(out var last))
                {
                    await CloseLogAsync(last, counts, reads, deletions, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                // The reads add deletions, so they settle first; the deletions in flight settle even when a read failed, so
                // no deletion outlives the sweep which owns its slot.
                try
                {
                    await reads.DrainAsync().ConfigureAwait(false);
                }
                finally
                {
                    await deletions.DrainAsync().ConfigureAwait(false);
                }
            }

            listed = true;
        }
        finally
        {
            (_resumeAfter, _open) = listed ? (resumeAfter, resumeAfter is null ? [] : open) : (startedAfter, new Stack<OpenLog>());
        }

        return (counts.Listed, counts.Read, counts.Deleted, counts.Unreadable, counts.Skipped, resumeAfter is null);
    }

    /// <summary>
    /// Walks one listed base: counts it, and skips it when its key hash does not parse; closes each open log whose block of
    /// names the listing has left, all of whose bases it has then listed; and adds the base to its log, opening the log
    /// when it is not the innermost open one.
    /// </summary>
    private async Task WalkAsync(ArchiveLogGeneration generation, Stack<OpenLog> open, BaseCounts counts, Bounded reads, Bounded deletions, CancellationToken cancellationToken)
    {
        counts.Listed++;
        if (generation.KeyHash is null)
        {
            counts.Skipped++;
            return;
        }

        while (open.TryPeek(out var top) && !top.Holds(generation.Log))
        {
            await CloseLogAsync(open.Pop(), counts, reads, deletions, cancellationToken).ConfigureAwait(false);
        }

        if (open.TryPeek(out var current) && current.Log == generation.Log)
        {
            current.Bases.Add(generation);
        }
        else
        {
            open.Push(new OpenLog(generation.Log, [generation]));
            MostOpenLogs = Math.Max(MostOpenLogs, open.Count);
        }
    }

    /// <summary>Decides a log whose bases the listing has all named, when it named two or more, within the reads' bound.</summary>
    private Task CloseLogAsync(OpenLog log, BaseCounts counts, Bounded reads, Bounded deletions, CancellationToken cancellationToken) =>
        log.Bases.Count > 1 ? reads.AddAsync(() => CloseAsync(log, counts, deletions, cancellationToken)) : Task.CompletedTask;
    /// <summary>
    /// Decides a log whose bases the listing has all named, two or more, since a log of one base has none to delete: reads
    /// its properties, and deletes the bases below its current generation. Up to <see cref="DeleteConcurrency"/> logs are
    /// decided at once.
    /// </summary>
    private async Task CloseAsync(OpenLog log, BaseCounts counts, Bounded deletions, CancellationToken cancellationToken)
    {
        var bases = log.Bases;
        Debug.Assert(bases.Count > 1, "Only a log of two bases or more is decided.");

        if (bases.Any(generation => generation.KeyHash != bases[0].KeyHash))
        {
            counts.AddUnreadable();
            LogUnreadable(_logger, log.Log, "its bases record two key hashes.");
            return;
        }

        ulong current;
        if (DeletesWithoutReadMutant)
        {
            current = bases.Max(static generation => generation.Generation);
        }
        else
        {
            counts.AddRead();
            ArchiveLogProperties? properties;
            try
            {
                properties = await _logs.GetPropertiesAsync(bases[0].Name!, cancellationToken).ConfigureAwait(false);
            }
            catch (ArchiveLogCorruptException exception)
            {
                counts.AddUnreadable();
                LogUnreadable(_logger, log.Log, exception.Message);
                return;
            }

            if (properties is null)
            {
                // A log is absent only before its key's first write; its bases' generations are in progress.
                return;
            }

            current = properties.Position.Generation;
        }

        foreach (var generation in bases)
        {
            if (generation.Generation < current)
            {
                // LOG-I2: the read after the listing shows the current generation above this base's.
                counts.AddDeleted();
                await deletions.AddAsync(() => _logs.DeleteGenerationAsync(log.Log, generation.Generation, cancellationToken).AsTask()).ConfigureAwait(false);
            }
        }
    }

    private async Task<(int Listed, int Deleted)> SweepMarkersAsync(CancellationToken cancellationToken)
    {
        // A ledger the map shows dropped never returns, so any marker of it, listed then or later, is garbage (§8.5).
        var map = await _map.ReadAsync(cancellationToken).ConfigureAwait(false);
        var (listed, deleted) = (0, 0);
        using var deletions = new Bounded(DeleteConcurrency);
        try
        {
            await foreach (var marker in _objects.ListAsync(ArchiveLogNames.DrainMarkerPrefix, cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                if (ArchiveLogNames.ParseDrainMarker(marker.Name.Value) is not { } parsed)
                {
                    continue;
                }

                listed++;
                if (parsed.Ledger < map.NextLedgerId && map.Get(parsed.Ledger) is null)
                {
                    deleted++;
                    await deletions.AddAsync(() => _objects.DeleteAsync(marker.Name, cancellationToken).AsTask()).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            await deletions.DrainAsync().ConfigureAwait(false);
        }

        return (listed, deleted);
    }

    /// <summary>A log whose bases the listing may still name: its name and the bases named so far.</summary>
    private sealed record OpenLog(string Log, List<ArchiveLogGeneration> Bases)
    {
        /// <summary>Returns whether the log's block of names, its own and those which extend it with <c>/</c>, holds <paramref name="log"/>.</summary>
        public bool Holds(string log) =>
            log.Length >= Log.Length && log.StartsWith(Log, StringComparison.Ordinal) && (log.Length == Log.Length || log[Log.Length] == '/');
    }

    /// <summary>A sweep's counts of the bases, which the concurrent decisions of logs add to.</summary>
    private sealed class BaseCounts
    {
        private int _read;
        private int _deleted;
        private int _unreadable;

        public int Listed { get; set; }

        public int Read => Volatile.Read(ref _read);

        public int Deleted => Volatile.Read(ref _deleted);

        public int Unreadable => Volatile.Read(ref _unreadable);

        public int Skipped { get; set; }

        public void AddRead() => Interlocked.Increment(ref _read);

        public void AddDeleted() => Interlocked.Increment(ref _deleted);

        public void AddUnreadable() => Interlocked.Increment(ref _unreadable);
    }

    /// <summary>
    /// Work in flight, at most a bound at once: adding some waits while the bound is reached; safe from any thread. It keeps
    /// only what is in flight, a count, the wait for it to reach zero, and the first failure, so a sweep over many logs holds
    /// no task of the work which ended.
    /// </summary>
    private sealed class Bounded(int concurrency) : IDisposable
    {
        private readonly SemaphoreSlim _slots = new(concurrency);
        private readonly Lock _sync = new();
        private int _running;
        private TaskCompletionSource? _drained;
        private Exception? _failure;

        public async Task AddAsync(Func<Task> work)
        {
            await _slots.WaitAsync().ConfigureAwait(false);
            lock (_sync)
            {
                _running++;
            }

            _ = RunAsync(work);
        }

        /// <summary>Waits for every piece of work added, the work added meanwhile included, and throws the first failure.</summary>
        public Task DrainAsync()
        {
            lock (_sync)
            {
                if (_running > 0)
                {
                    return (_drained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
                }

                return _failure is { } failure ? Task.FromException(failure) : Task.CompletedTask;
            }
        }

        public void Dispose() => _slots.Dispose();

        private async Task RunAsync(Func<Task> work)
        {
            try
            {
                await work().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                lock (_sync)
                {
                    _failure ??= exception;
                }
            }
            finally
            {
                _ = _slots.Release();
                TaskCompletionSource? drained = null;
                Exception? failure;
                lock (_sync)
                {
                    if (--_running == 0)
                    {
                        (drained, _drained) = (_drained, null);
                    }

                    failure = _failure;
                }

                if (failure is null)
                {
                    _ = drained?.TrySetResult();
                }
                else
                {
                    _ = drained?.TrySetException(failure);
                }
            }
        }
    }
}
