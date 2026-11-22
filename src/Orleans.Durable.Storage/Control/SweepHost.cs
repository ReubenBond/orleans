using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Archive.Logs.Control;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Archive.Lsm.Control;
using Orleans.Durable.Storage.Engine.Cluster.Control;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.Ledgers.Objects;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Runtime;

namespace Orleans.Durable.Storage.Control;

/// <summary>
/// The silo's sweep host (orleans.md §5.6, archive-lsm.md §8.8, archive-logs.md §2.5): a long-lived service, which outlives
/// the controller grain's activations, holds the archive store's sweeps, <see cref="LsmSweeps"/> or
/// <see cref="ArchiveLogSweeps"/>, and runs them when the controller grain's passes on this silo ask, at least
/// <c>SweepInterval</c> apart.
/// </summary>
/// <remarks>
/// A request while one is pending, or during the wait before a sweep, is the same request; one during a sweep asks for the
/// next. The loop runs in no caller's execution context (conventions.md §12). Sweeps which wait for the silo's archive gate
/// sweep nothing until it passes; a store the gate refused is never swept, and the host logs it as critical and sweeps no
/// more; a gate cancelled as the silo stops ends the sweeps quietly. The loop alone logs the refusal, at the sweep which
/// meets it, and then ends, so the host's end follows its report (#389); a refusal no request met wakes the loop.
/// </remarks>
internal sealed partial class SweepHost(
    ControlOptions options,
    ArchiveSweeps sweeps,
    TimeProvider timeProvider,
    ILoggerFactory loggers,
    ObjectLedgerSweeps? objectLedgers = null) : ILifecycleParticipant<ISiloLifecycle>, IDisposable
{
    private readonly ILogger _logger = loggers.CreateLogger<SweepHost>();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly SemaphoreSlim _objectWake = new(0, 1);
    private readonly Lock _objectSync = new();
    private readonly CancellationTokenSource _stopping = new();
    private Action? _cancelObjectSweep;
    private Task _loop = Task.CompletedTask;
    private Task _objectLoop = Task.CompletedTask;
    private int _pending;
    private int _objectPending;
    private LedgerMapValue? _objectMap;
    private long _requests;
    private int _sweeps;
    private int _objectSweeps;

    /// <summary>Gets how many sweeps the host has completed; for tests.</summary>
    internal int Sweeps => Volatile.Read(ref _sweeps);

    /// <summary>Gets how many object-ledger sweeps the host has completed; for tests.</summary>
    internal int ObjectSweeps => Volatile.Read(ref _objectSweeps);

    /// <summary>Gets a value indicating whether the host's loops have ended; for tests.</summary>
    internal bool Ended => _loop.IsCompleted && _objectLoop.IsCompleted;

    /// <summary>Gets how many sweeps were asked for: a pass of the controller grain on this silo asks once, once its gate passed.</summary>
    internal long Requests => Interlocked.Read(ref _requests);

    /// <summary>Asks the host to sweep.</summary>
    public void Request()
    {
        _ = Interlocked.Increment(ref _requests);
        Wake();
    }

    /// <summary>Asks the host to sweep the object-ledger store against <paramref name="map"/> (orleans.md §5.14).</summary>
    public void RequestObjectLedgers(LedgerMapValue map)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (objectLedgers is null)
        {
            return;
        }

        Volatile.Write(ref _objectMap, map);
        WakeObjectLedgers();
    }

    /// <summary>
    /// Cancels any in-flight object-ledger sweep and clears the requested map so a deactivated controller grain stops
    /// sweeping on this silo until another activation asks (orleans.md §5.14).
    /// </summary>
    public void CancelObjectLedgers()
    {
        if (objectLedgers is null)
        {
            return;
        }

        Action? current;
        lock (_objectSync)
        {
            Volatile.Write(ref _objectMap, null);
            current = _cancelObjectSweep;
            _cancelObjectSweep = null;
        }

        if (current is not null)
        {
            try
            {
                current();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>Wakes the loop for a sweep; only the wake which sets the flag releases it, so the semaphore never fills.</summary>
    private void Wake()
    {
        if (Interlocked.Exchange(ref _pending, 1) == 0)
        {
            _ = _wake.Release();
        }
    }

    /// <summary>Wakes the object-ledger sweep loop; only the wake which sets the flag releases it.</summary>
    private void WakeObjectLedgers()
    {
        if (Interlocked.Exchange(ref _objectPending, 1) == 0)
        {
            _ = _objectWake.Release();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Action? current;
        lock (_objectSync)
        {
            current = _cancelObjectSweep;
            _cancelObjectSweep = null;
        }

        if (current is not null)
        {
            try
            {
                current();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        _wake.Dispose();
        _objectWake.Dispose();
        _stopping.Dispose();
    }

    /// <inheritdoc/>
    public void Participate(ISiloLifecycle observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        _ = observer.Subscribe(nameof(SweepHost), ServiceLifecycleStage.Active, _ => Start(), _ => StopAsync());
    }

    /// <summary>Starts the host's loop, and its watch of the gate, which wakes the loop at a refusal whether or not a sweep is asked.</summary>
    internal Task Start()
    {
        using (ExecutionContext.SuppressFlow())
        {
            _loop = Task.Run(() => RunAsync(_stopping.Token), CancellationToken.None);
            _ = WatchGateAsync();
            if (objectLedgers is not null)
            {
                _objectLoop = Task.Run(() => RunObjectLedgersAsync(_stopping.Token), CancellationToken.None);
                _ = WatchObjectGateAsync();
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Wakes the loop once the gate is refused: the controller grain asks for no sweep while its silo's gate has not passed
    /// (orleans.md §5.6), so a refused silo's host may never be asked, yet its loop's sweep meets the refusal and reports it.
    /// The wake is no request: <see cref="Requests"/> counts the controller grain's alone.
    /// </summary>
    private async Task WatchGateAsync()
    {
        try
        {
            await sweeps.Gate.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The silo stops: nothing to report.
        }
        catch (Exception refusal) when (refusal is not OutOfMemoryException)
        {
            try
            {
                Wake();
            }
            catch (ObjectDisposedException)
            {
                // The host was disposed before the gate's refusal: its loop has ended, and nothing is left to report it.
            }
        }
    }

    private async Task WatchObjectGateAsync()
    {
        if (objectLedgers is null)
        {
            return;
        }

        try
        {
            await objectLedgers.Gate.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception refusal) when (refusal is not OutOfMemoryException)
        {
            try
            {
                WakeObjectLedgers();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>Stops the host's loop and waits for it to end.</summary>
    internal async Task StopAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(_loop, _objectLoop).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset? lastStart = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _wake.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (lastStart is { } last && last + options.SweepInterval - timeProvider.GetUtcNow() is var wait && wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, timeProvider, cancellationToken).ConfigureAwait(false);
                }

                // Requests until now are this sweep's.
                Volatile.Write(ref _pending, 0);
                var started = timeProvider.GetUtcNow();
                switch ((await sweeps.SweepAsync(cancellationToken).ConfigureAwait(false)).Value)
                {
                    case ArchiveSweepOutcome.Swept:
                        lastStart = started;
                        _ = Interlocked.Increment(ref _sweeps);
                        break;
                    case ArchiveSweepOutcome.Waiting:
                        break;
                    case ArchiveSweepOutcome.Refused refused:
                        // The gate's refusal never changes for the silo's life, so no later request sweeps the store; the loop
                        // reports it here alone, once, before it ends.
                        LogRefused(_logger, refused.Reason);
                        return;
                    case ArchiveSweepOutcome.Stopping:
                        // The gate was cancelled: the silo stops, and its sweeps with it, with nothing to report.
                        return;
                    case null:
                    default:
                        throw new UnreachableException("conventions.md §15: a sweep answers a union which holds a case.");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (ArchiveCorruptException corrupt)
            {
                // Corruption of a verified archive object lies outside the fault model (system-model §4); it is reported.
                LogSweepCorrupt(_logger, corrupt);
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                // A failed sweep deletes nothing it should not; the next request sweeps again (archive-lsm.md §2.7, archive-logs.md §2.5).
                LogSweepFailed(_logger, failure);
            }
        }
    }

    private async Task RunObjectLedgersAsync(CancellationToken cancellationToken)
    {
        if (objectLedgers is null)
        {
            return;
        }

        DateTimeOffset? lastStart = null;
        DateTimeOffset? lastFailure = null;
        ulong? lastEpoch = null;
        var failureBackoff = TimeSpan.Zero;
        var firstBackoff = TimeSpan.FromTicks(Math.Min(TimeSpan.FromSeconds(1).Ticks, Math.Max(1L, options.SweepInterval.Ticks)));
        var maxBackoff = options.SweepInterval > firstBackoff ? options.SweepInterval : firstBackoff;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _objectWake.WaitAsync(cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref _objectPending, 0);

                var map = Volatile.Read(ref _objectMap);
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (failureBackoff > TimeSpan.Zero && lastFailure is { } failedAt)
                    {
                        var retryWait = failedAt + failureBackoff - timeProvider.GetUtcNow();
                        if (retryWait > TimeSpan.Zero)
                        {
                            await Task.Delay(retryWait, timeProvider, cancellationToken).ConfigureAwait(false);
                        }

                        map = Volatile.Read(ref _objectMap);
                        break;
                    }

                    if (map is not null && map.Epoch == lastEpoch && lastStart is { } last)
                    {
                        var wait = last + options.SweepInterval - timeProvider.GetUtcNow();
                        if (wait > TimeSpan.Zero)
                        {
                            // Wait for either the sweep interval or a fresh wake (e.g. a new map epoch or gate refusal).
                            using var sleepCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            var wakeTask = _objectWake.WaitAsync(sleepCts.Token);
                            var delayTask = Task.Delay(wait, timeProvider, sleepCts.Token);
                            var completed = await Task.WhenAny(wakeTask, delayTask).ConfigureAwait(false);
                            await sleepCts.CancelAsync().ConfigureAwait(false);
                            await Task.WhenAll(wakeTask, delayTask).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                            cancellationToken.ThrowIfCancellationRequested();

                            // Reset _objectPending whenever wakeTask consumed the semaphore permit (even if delayTask
                            // won WhenAny), so a concurrent WakeObjectLedgers never leaves _objectPending stuck at 1.
                            if (wakeTask.IsCompletedSuccessfully)
                            {
                                Volatile.Write(ref _objectPending, 0);
                            }

                            map = Volatile.Read(ref _objectMap);
                            var waitGateEnded = objectLedgers.Gate.IsFaulted || objectLedgers.Gate.IsCanceled;
                            if (completed == wakeTask && map is not null && map.Epoch == lastEpoch && !waitGateEnded)
                            {
                                continue;
                            }
                        }
                    }

                    break;
                }

                // A refusal or cancellation wakes the loop even with no map; otherwise nothing to sweep if cleared.
                var gateEnded = objectLedgers.Gate.IsFaulted || objectLedgers.Gate.IsCanceled;
                if (map is null && !gateEnded)
                {
                    continue;
                }

                using var sweepCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                lock (_objectSync)
                {
                    // Re-read inside lock so CancelObjectLedgers cannot race with installing _objectSweep.
                    map = Volatile.Read(ref _objectMap);
                    gateEnded = objectLedgers.Gate.IsFaulted || objectLedgers.Gate.IsCanceled;
                    if (map is null && !gateEnded)
                    {
                        continue;
                    }

                    _cancelObjectSweep = sweepCts.Cancel;
                }

                ArchiveSweepOutcome outcome;
                var started = timeProvider.GetUtcNow();
                try
                {
                    outcome = await objectLedgers.SweepAsync(map, sweepCts.Token).ConfigureAwait(false);
                }
                finally
                {
                    lock (_objectSync)
                    {
                        _cancelObjectSweep = null;
                    }
                }

                switch (outcome.Value)
                {
                    case ArchiveSweepOutcome.Swept:
                        lastStart = started;
                        lastFailure = null;
                        failureBackoff = TimeSpan.Zero;
                        if (map is not null)
                        {
                            lastEpoch = map.Epoch;
                        }

                        _ = Interlocked.Increment(ref _objectSweeps);
                        break;
                    case ArchiveSweepOutcome.Waiting:
                        break;
                    case ArchiveSweepOutcome.Refused refused:
                        LogObjectLedgersRefused(_logger, refused.Reason);
                        return;
                    case ArchiveSweepOutcome.Stopping:
                        return;
                    case null:
                    default:
                        throw new UnreachableException("conventions.md §15: a sweep answers a union which holds a case.");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                // Cancelled by CancelObjectLedgers on controller grain deactivation; keep the host loop alive for a later activation.
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                lastFailure = timeProvider.GetUtcNow();
                failureBackoff = failureBackoff == TimeSpan.Zero
                    ? firstBackoff
                    : TimeSpan.FromTicks(Math.Min(failureBackoff.Ticks * 2, maxBackoff.Ticks));
                LogObjectSweepFailed(_logger, failure);
                if (Volatile.Read(ref _objectMap) is not null)
                {
                    WakeObjectLedgers();
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A sweep of the archive store failed; the next request sweeps again.")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A sweep of the object-ledger store failed; the next request sweeps again.")]
    private static partial void LogObjectSweepFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Critical, Message = "A sweep of the archive store found an object corrupt; corruption of a verified archive object lies outside the fault model (system-model §4).")]
    private static partial void LogSweepCorrupt(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 6245, Level = LogLevel.Critical, Message = "The silo's archive gate refused its archive store, so its sweep host never sweeps it.")]
    private static partial void LogRefused(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 6249, Level = LogLevel.Critical, Message = "The silo's object-ledger gate refused its object-ledger store, so its sweep host never sweeps it.")]
    private static partial void LogObjectLedgersRefused(ILogger logger, Exception exception);
}

/// <summary>
/// A request's sweep (orleans.md §5.6, conventions.md §15): <see cref="Swept"/>; <see cref="Waiting"/>, before the silo's
/// archive gate passed, with nothing swept; <see cref="Refused"/>, by the gate, so the store is never swept; or
/// <see cref="Stopping"/>, the gate cancelled as the silo stops.
/// </summary>
internal readonly union ArchiveSweepOutcome(ArchiveSweepOutcome.Swept, ArchiveSweepOutcome.Waiting, ArchiveSweepOutcome.Refused, ArchiveSweepOutcome.Stopping)
{
    /// <summary>The store was swept.</summary>
    public sealed record Swept
    {
        private Swept()
        {
        }

        /// <summary>Gets the one value.</summary>
        public static Swept Instance { get; } = new();
    }

    /// <summary>The silo's archive gate has not passed, so nothing was swept.</summary>
    public sealed record Waiting
    {
        private Waiting()
        {
        }

        /// <summary>Gets the one value.</summary>
        public static Waiting Instance { get; } = new();
    }

    /// <summary>The silo's archive gate refused the store.</summary>
    /// <param name="Reason">The gate's refusal.</param>
    public sealed record Refused(Exception Reason);

    /// <summary>The silo's archive gate was cancelled: the silo stops, so nothing is swept again.</summary>
    public sealed record Stopping
    {
        private Stopping()
        {
        }

        /// <summary>Gets the one value.</summary>
        public static Stopping Instance { get; } = new();
    }
}

/// <summary>One archive store's sweeps, which the silo's sweep host runs (orleans.md §5.6).</summary>
internal abstract class ArchiveSweeps
{
    /// <summary>
    /// Gets the silo's archive gate as the sweeps take it: a task which completes once the gate passed, faults with its
    /// refusal, and is cancelled as the silo stops.
    /// </summary>
    public abstract Task Gate { get; }

    /// <summary>Sweeps the store once, or answers why not.</summary>
    /// <param name="cancellationToken">Stops the sweep; what it deleted stays deleted.</param>
    public abstract Task<ArchiveSweepOutcome> SweepAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The LSM's sweeps (archive-lsm.md §2.7, §8.8): only once the silo's archive gate passed, which checked the store's record
/// against the registry's identity (OR-I20), its <see cref="Sweeper"/>, whose marks live in memory, so its first sweep
/// deletes nothing, and after each sweep the object store's maintenance.
/// </summary>
internal sealed partial class LsmSweeps : ArchiveSweeps
{
    private readonly Task _gate;
    private readonly Sweeper _sweeper;
    private readonly ObjectStoreMaintenance? _maintenance;
    private readonly ILogger _logger;

    /// <summary>Initializes a new instance of the <see cref="LsmSweeps"/> class.</summary>
    /// <param name="gate">
    /// The silo's archive gate: completes once it passed, faults only with its refusal, and is cancelled only as the silo
    /// stops.
    /// </param>
    /// <param name="objects">The LSM's object store.</param>
    /// <param name="maintenance">The object store's maintenance, run after each sweep, when it needs one.</param>
    /// <param name="timeProvider">The sweeper's clock.</param>
    /// <param name="loggers">The sweeper's and the sweeps' loggers.</param>
    public LsmSweeps(Task gate, IObjectStore objects, ObjectStoreMaintenance? maintenance, TimeProvider timeProvider, ILoggerFactory loggers)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(loggers);
        _gate = gate;
        _sweeper = new Sweeper(objects, new ManifestView(objects), timeProvider, loggers.CreateLogger<Sweeper>());
        _maintenance = maintenance;
        _logger = loggers.CreateLogger<LsmSweeps>();
    }

    /// <inheritdoc/>
    public override Task Gate => _gate;

    /// <inheritdoc/>
    public override async Task<ArchiveSweepOutcome> SweepAsync(CancellationToken cancellationToken)
    {
        if (!_gate.IsCompleted)
        {
            return ArchiveSweepOutcome.Waiting.Instance;
        }

        if (_gate.IsCanceled)
        {
            return ArchiveSweepOutcome.Stopping.Instance;
        }

        if (_gate.Exception?.InnerException is { } refusal)
        {
            return new ArchiveSweepOutcome.Refused(refusal);
        }

        var result = await _sweeper.SweepAsync(cancellationToken).ConfigureAwait(false);
        LogSwept(_logger, result.Marked, result.Deleted);
        _maintenance?.Run();
        return ArchiveSweepOutcome.Swept.Instance;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Swept the object store: marked {Marked} objects, deleted {Deleted}.")]
    private static partial void LogSwept(ILogger logger, int marked, int deleted);
}

/// <summary>
/// Archive logs' sweeps (archive-logs.md §2.5, §8.5): only once the silo's archive gate passed, which checks the store's
/// kind, prefix, and record, and the caller parameters, the sweeper's evidence (orleans.md §5.13); before its first sweep
/// they open a view of the ledger map, which the sweeper reads before it deletes a dropped ledger's markers, tried again at
/// each request until it succeeds.
/// </summary>
internal sealed partial class ArchiveLogSweeps : ArchiveSweeps
{
    private readonly Task<ArchiveGateEvidence> _gate;
    private readonly IMetaStore _metaStore;
    private readonly Guid _cluster;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _logger;
    private ArchiveLogSweeper? _sweeper;

    /// <summary>Initializes a new instance of the <see cref="ArchiveLogSweeps"/> class.</summary>
    /// <param name="gate">
    /// The silo's archive gate on archive logs: completes with archive logs' evidence once it passed; faults only with its
    /// refusal; and is cancelled only as the silo stops.
    /// </param>
    /// <param name="metaStore">The MetaStore holding the cluster's ledger map.</param>
    /// <param name="cluster">The cluster.</param>
    /// <param name="loggers">The sweeper's and the sweeps' loggers.</param>
    public ArchiveLogSweeps(Task<ArchiveGateEvidence> gate, IMetaStore metaStore, Guid cluster, ILoggerFactory loggers)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(metaStore);
        ArgumentNullException.ThrowIfNull(loggers);
        _gate = gate;
        _metaStore = metaStore;
        _cluster = cluster;
        _loggers = loggers;
        _logger = loggers.CreateLogger<ArchiveLogSweeps>();
    }

    /// <inheritdoc/>
    public override Task Gate => _gate;

    /// <summary>Gets the pages of the bases' listing each sweep lists at most; the sweeper's default, fewer in tests.</summary>
    internal int PagesPerSweep { get; init; } = ArchiveLogSweeper.DefaultPagesPerSweep;

    /// <inheritdoc/>
    /// <remarks>The sweeps keep one sweeper once built, so each sweep's slice resumes where the last one's ended.</remarks>
    public override async Task<ArchiveSweepOutcome> SweepAsync(CancellationToken cancellationToken)
    {
        if (_sweeper is null)
        {
            if (!_gate.IsCompleted)
            {
                return ArchiveSweepOutcome.Waiting.Instance;
            }

            if (_gate.IsCanceled)
            {
                return ArchiveSweepOutcome.Stopping.Instance;
            }

            if (_gate.Exception?.InnerException is { } refusal)
            {
                return new ArchiveSweepOutcome.Refused(refusal);
            }

            var register = await _metaStore.GetRegisterAsync(_cluster, LedgerMapRegister.Name, cancellationToken).ConfigureAwait(false);
            var map = await LedgerMapView.OpenAsync(register, cancellationToken).ConfigureAwait(false);
            var evidence = await _gate.ConfigureAwait(false) is ArchiveGateEvidence.Logs logs
                ? logs.Gate
                : throw new InvalidOperationException("Archive logs' gate passes with archive logs' evidence.");
            _sweeper = new ArchiveLogSweeper(evidence, map, _loggers.CreateLogger<ArchiveLogSweeper>()) { PagesPerSweep = PagesPerSweep };
        }

        var result = await _sweeper.SweepAsync(cancellationToken).ConfigureAwait(false);
        LogSwept(_logger, result.BasesDeleted, result.BasesListed, result.MarkersDeleted, result.MarkersListed, result.Unreadable, result.Skipped, result.BasesEnded);
        return ArchiveSweepOutcome.Swept.Instance;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Swept the archive logs: deleted {BasesDeleted} of {BasesListed} generation bases and {MarkersDeleted} of {MarkersListed} drain markers; left {Unreadable} unreadable logs' and {Skipped} unparsed bases; the slice ended the pass over the bases: {BasesEnded}.")]
    private static partial void LogSwept(ILogger logger, int basesDeleted, int basesListed, int markersDeleted, int markersListed, int unreadable, int skipped, bool basesEnded);
}

/// <summary>
/// The object-ledger store's sweeps (orleans.md §5.14, object-ledgers.md §2.4): runs <see cref="ObjectLedgerSweeper"/>
/// once the silo's object-ledger gate passed and a map view is available; a refused gate never sweeps.
/// </summary>
internal sealed partial class ObjectLedgerSweeps
{
    private readonly Task<ObjectLedgerStore> _gate;
    private readonly ILogger _logger;
    private ObjectLedgerSweeper? _sweeper;

    /// <summary>Initializes a new instance of the <see cref="ObjectLedgerSweeps"/> class.</summary>
    public ObjectLedgerSweeps(Task<ObjectLedgerStore> gate, ILoggerFactory loggers)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(loggers);
        _gate = gate;
        _logger = loggers.CreateLogger<ObjectLedgerSweeps>();
    }

    /// <summary>Gets the silo's object-ledger gate.</summary>
    public Task Gate => _gate;

    /// <summary>Sweeps the object-ledger store against <paramref name="map"/> once, or answers why not.</summary>
    public async Task<ArchiveSweepOutcome> SweepAsync(LedgerMapValue? map, CancellationToken cancellationToken)
    {
        if (!_gate.IsCompleted)
        {
            return ArchiveSweepOutcome.Waiting.Instance;
        }

        if (_gate.IsCanceled)
        {
            return ArchiveSweepOutcome.Stopping.Instance;
        }

        if (_gate.Exception?.InnerException is { } refusal)
        {
            return new ArchiveSweepOutcome.Refused(refusal);
        }

        if (map is null)
        {
            return ArchiveSweepOutcome.Waiting.Instance;
        }

        var store = await _gate.ConfigureAwait(false);
        _sweeper ??= new ObjectLedgerSweeper(store);
        var deleted = await _sweeper.SweepAsync(map, cancellationToken).ConfigureAwait(false);
        LogSwept(_logger, deleted, map.Epoch);
        return ArchiveSweepOutcome.Swept.Instance;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Swept the object-ledger store at map epoch {Epoch}: deleted {Deleted} objects.")]
    private static partial void LogSwept(ILogger logger, int deleted, ulong epoch);
}
