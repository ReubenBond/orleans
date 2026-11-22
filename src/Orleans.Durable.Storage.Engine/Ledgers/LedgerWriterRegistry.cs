using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Polly;

namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// Serves this process's primaries from its map view (ledgers.md §2.4, §8.3): it holds each sequencer lifetime an
/// executor in this process created, adopts it into the ledger's <see cref="LedgerWriter"/> once the view lists the
/// ledger open under its claim, serves that writer while the view does and its lifetime runs, and lists the open ledgers
/// the view names this node's primary of under a claim this process does not hold.
/// </summary>
/// <remarks>
/// <para>
/// A writer leaves the registry only once the view has decided every proposal it admitted: the view lists the ledger
/// closed, or no longer lists it. A lifetime whose change can no longer apply never served and admitted nothing, and
/// leaves at once. The registry then stops the lifetime with <see cref="SequencerStopReason.Requested"/>, disposing the
/// writer, which decides its undecided proposals from the view. A writer whose lifetime stopped while the view lists the
/// ledger open stays held, unserved, so its stopped appends still learn <c>T</c> (LP-I16, ledger-protocol.md §2.3).
/// </para>
/// <para>
/// One task, the follower, reconciles: it wakes when the view changes, when a held lifetime stops, and when an executor
/// holds a lifetime or asks for a reconciliation, and while lifetimes are pending it re-reads the register with bounded,
/// jittered backoff, so a change whose commit outcome was lost is still decided. Each writer leases the process's
/// <see cref="Ledger"/> of its ledger from the <see cref="LedgerReader"/>, which, with its transport, outlives the
/// registry.
/// </para>
/// </remarks>
public sealed partial class LedgerWriterRegistry : IAsyncDisposable
{
    private readonly LedgerMapView _map;
    private readonly LedgerWriterHost _host;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly ResiliencePipeline<bool> _mapReadRetry;
    private readonly Lock _lock = new();
    private readonly Dictionary<SequencerInstanceId, Pending> _pending = [];
    private readonly Dictionary<LedgerId, int> _closing = [];
    private readonly List<Task> _leaving = [];
    private readonly List<(ulong Epoch, TaskCompletionSource Reconciled)> _reconciledWaiters = [];
    private readonly CancellationTokenSource _disposing = new();
    private readonly Task _following;
    private readonly Task? _heartbeating;

    // Replaced whole under the lock and read without it, so TryGet takes no lock.
    private ImmutableDictionary<LedgerId, LedgerWriter> _held = [];
    private ImmutableArray<LedgerId> _unserved = [];
    private long _unservedVersion;
    private TaskCompletionSource _unservedChanged = NewSignal();
    private TaskCompletionSource _wake = NewSignal();
    private Task? _rereading;
    private ulong? _reconciledEpoch;
    private bool _dirty;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="LedgerWriterRegistry"/> class for the process of the node
    /// <paramref name="reader"/> names, over the process's reader and its map view, and begins following the view.
    /// </summary>
    /// <param name="reader">
    /// The process's reader, which holds the process's <see cref="Ledger"/>s and its map view, which outlives the registry
    /// with its transport, and whose <see cref="LedgerReader.LocalNode"/> is the node whose process this is.
    /// </param>
    /// <param name="maxBatchLength">The replica's payload limit, which the composition takes from its replica transport.</param>
    /// <param name="options">The cluster's writer options: when the writers end segments, and their proposal budget.</param>
    /// <param name="timeProvider">Times the writers' repeated writes and the registry's re-reads of the map.</param>
    /// <param name="loggerFactory">Creates the logs of the registry and its writers.</param>
    /// <param name="segmentEnded">
    /// The sink of each segment a writer ends, with the keys it defined (ledgers.md §2.4), called under the writer's lock, so
    /// it must not block or call the writer; <see langword="null"/> for none.
    /// </param>
    public LedgerWriterRegistry(LedgerReader reader, int maxBatchLength, LedgerWriterOptions options, TimeProvider timeProvider, ILoggerFactory loggerFactory, Action<LedgerSegmentKeys>? segmentEnded = null)
        : this(reader, maxBatchLength, options, timeProvider, loggerFactory, random: null, segmentEnded)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="LedgerWriterRegistry"/> class whose writers draw their segment ages
    /// from <paramref name="random"/>, which a test or a simulation seeds.
    /// </summary>
    internal LedgerWriterRegistry(LedgerReader reader, int maxBatchLength, LedgerWriterOptions options, TimeProvider timeProvider, ILoggerFactory loggerFactory, Random? random, Action<LedgerSegmentKeys>? segmentEnded = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (reader.LocalNode is not { } node || node == default)
        {
            throw new ArgumentException("A registry serves a node's process, whose reader names its node.", nameof(reader));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBatchLength);
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxProposalBytes < maxBatchLength)
        {
            // A batch at the payload limit could never be created, and every primary would abandon its writer on it.
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.MaxProposalBytes,
                string.Create(System.Globalization.CultureInfo.InvariantCulture, $"MaxProposalBytes holds at least the payload limit, {maxBatchLength} bytes (ledgers.md §8.7)."));
        }

        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        Node = node;
        Reader = reader;
        _map = reader.Map;
        _timeProvider = timeProvider;
        _logger = loggerFactory.CreateLogger<LedgerWriterRegistry>();
        _host = new LedgerWriterHost(maxBatchLength, loggerFactory.CreateLogger<LedgerWriter>(), options, random, segmentEnded);
        _mapReadRetry = LedgerWriter.CreateMapReadRetry(timeProvider);

        // The view serves every primary, so it is followed in no caller's execution context (conventions.md §12).
        using (ExecutionContext.SuppressFlow())
        {
            _following = Task.Run(FollowAsync, CancellationToken.None);
            if (reader.LeaseOptions.IsEnabled)
            {
                _heartbeating = Task.Run(HeartbeatLoopAsync, CancellationToken.None);
            }
        }
    }

    /// <summary>Gets the node whose process this is.</summary>
    public NodeId Node { get; }

    /// <summary>Gets the clock used for retries, idle lease heartbeats, and transition lease waits.</summary>
    internal TimeProvider TimeProvider => _timeProvider;

    /// <summary>Gets the process's reader, through which the registry leases each writer's <see cref="Ledger"/>.</summary>
    internal LedgerReader Reader { get; }

    /// <summary>Gets the replica's payload limit, which every batch of the registry's writers stays within.</summary>
    internal int MaxBatchLength => _host.MaxBatchLength;

    /// <summary>Gets the cluster's writer options, which every writer of the registry follows.</summary>
    internal LedgerWriterOptions Options => _host.Options;

    /// <summary>Gets the number of lifetimes executors created which the view has not yet decided.</summary>
    internal int PendingLifetimes
    {
        get
        {
            lock (_lock)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>
    /// Gets the open ledgers the map view lists with this node as primary under a claim this process does not hold, and
    /// which no executor in this process is closing: lifetimes which stopped, and claims of an earlier process of the
    /// node. Each needs a <c>Replace</c>.
    /// </summary>
    public ImmutableArray<LedgerId> Unserved
    {
        get
        {
            lock (_lock)
            {
                return _unserved;
            }
        }
    }

    /// <summary>
    /// Returns the primary of <paramref name="ledger"/> while the map view lists it open under a claim this process holds,
    /// and its lifetime runs.
    /// </summary>
    /// <remarks>
    /// Per ledger it answers <see langword="null"/> until the registry adopts the one lifetime the map names for it, then
    /// that writer, and then <see langword="null"/> forever, once the view lists the ledger closed or the lifetime stops:
    /// never a second writer, since the map lists one claim per ledger (LP-I1), the view only moves forward, a closed ledger
    /// never opens again, and a stopped lifetime never runs again. A caller may hold the writer it answered as the
    /// ledger's one writer, and take a later <see langword="null"/> as final.
    /// </remarks>
    /// <param name="ledger">The ledger.</param>
    /// <returns>The ledger's writer, or <see langword="null"/>.</returns>
    public LedgerWriter? TryGet(LedgerId ledger) =>
        Volatile.Read(ref _held).TryGetValue(ledger, out var writer)
        && _map.Current.Get(ledger) is { IsOpen: true } listed
        && listed.Primary == new PrimaryClaim(Node, writer.InstanceId)
        && !writer.Sequencer.IsStopped
            ? writer
            : null;

    /// <summary>
    /// Returns the held writer for <paramref name="ledger"/> while the map view lists it open under this process's
    /// claim, even if its lifetime has stopped (for example after cooperative <c>PrepareRelease</c> or a concurrent seal).
    /// </summary>
    internal LedgerWriter? TryGetHeld(LedgerId ledger) =>
        Volatile.Read(ref _held).TryGetValue(ledger, out var writer)
        && _map.Current.Get(ledger) is { IsOpen: true } listed
        && listed.Primary == new PrimaryClaim(Node, writer.InstanceId)
            ? writer
            : null;

    /// <summary>
    /// Completes with <see cref="Unserved"/> once it lists a ledger <paramref name="known"/> does not.
    /// </summary>
    /// <param name="known">The unserved ledgers the caller already handles.</param>
    /// <param name="cancellationToken">Ends this caller's wait.</param>
    /// <returns>The unserved ledgers, among them at least one not in <paramref name="known"/>.</returns>
    public async Task<ImmutableArray<LedgerId>> WaitForUnservedAsync(IReadOnlySet<LedgerId> known, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(known);
        long checkedVersion = -1;
        while (true)
        {
            Task changed;
            lock (_lock)
            {
                if (_unservedVersion != checkedVersion)
                {
                    checkedVersion = _unservedVersion;
                    foreach (var ledger in _unserved)
                    {
                        if (!known.Contains(ledger))
                        {
                            return _unserved;
                        }
                    }
                }

                changed = _unservedChanged.Task;
            }

            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Stops following the view, stops every held lifetime, and disposes every writer, which fails each proposal its view
    /// has not decided with <see cref="ObjectDisposedException"/>.
    /// </summary>
    /// <returns>A task which completes once every writer is disposed.</returns>
    public async ValueTask DisposeAsync()
    {
        Task[] leaving;
        Task? rereading;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var pending in _pending.Values)
            {
                pending.Lifetime.Stop(SequencerStopReason.Requested);
            }

            _pending.Clear();
            foreach (var (_, writer) in _held)
            {
                _leaving.Add(Leave(writer));
            }

            _held = [];
            leaving = [.. _leaving];
            rereading = _rereading;
            foreach (var (_, reconciled) in _reconciledWaiters)
            {
                _ = reconciled.TrySetResult();
            }

            _reconciledWaiters.Clear();
        }

        try
        {
            _disposing.Cancel();
            await _following.ConfigureAwait(false);
            if (_heartbeating is not null)
            {
                await _heartbeating.ConfigureAwait(false);
            }
        }
        finally
        {
            // ledger-protocol.md §6.3: no writer outlives its registry, whatever the following met.
            await Task.WhenAll(leaving).ConfigureAwait(false);
            if (rereading is not null)
            {
                await rereading.ConfigureAwait(false);
            }

            _disposing.Dispose();
        }
    }

    /// <summary>
    /// Holds <paramref name="lifetime"/>, which an executor in this process created for a ledger its change opens, with
    /// the ledgers the change closes (none for a bootstrap), the ranges it opens, and its <c>ChosenAt</c>; the view then
    /// decides it.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The registry was disposed; the lifetime is stopped.</exception>
    internal void Hold(Sequencer lifetime, ImmutableArray<LedgerId> closes, ImmutableArray<KeyRange> opens, ulong chosenAt)
    {
        TaskCompletionSource wake;
        lock (_lock)
        {
            if (_disposed)
            {
                lifetime.Stop(SequencerStopReason.Requested);
                throw new ObjectDisposedException(nameof(LedgerWriterRegistry));
            }

            // LG-I3: the write which names the lifetime assigns its ledger an ID at or above the register's NextLedgerId,
            // which the view's never passes.
            _pending.Add(lifetime.InstanceId, new Pending(lifetime, closes, opens, chosenAt, _map.Current.NextLedgerId));
            wake = MarkDirty();
        }

        _ = wake.TrySetResult();
    }

    /// <summary>
    /// Stops <paramref name="lifetime"/>, which an executor holds, and lets it go: the change which would have named it was
    /// never submitted, or was refused or rejected, or failed for good, so nothing of it can apply.
    /// </summary>
    internal void Abandon(Sequencer lifetime)
    {
        lock (_lock)
        {
            if (_pending.Remove(lifetime.InstanceId))
            {
                lifetime.Stop(SequencerStopReason.Requested);
            }

            Debug.Assert(
                !_held.Values.Any(writer => writer.InstanceId == lifetime.InstanceId),
                "A lifetime is abandoned only when its change did not apply, so no view listed its claim.");
        }
    }

    /// <summary>
    /// Returns whether <paramref name="claim"/> names this node and a lifetime this process holds or has pending, so a
    /// change which names it was submitted by an executor in this process (ledgers.md §8.1).
    /// </summary>
    internal bool Knows(PrimaryClaim claim)
    {
        if (claim.Node != Node)
        {
            return false;
        }

        lock (_lock)
        {
            if (_pending.ContainsKey(claim.Sequencer))
            {
                return true;
            }

            foreach (var (_, writer) in _held)
            {
                if (writer.InstanceId == claim.Sequencer)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Returns the writer of the ledger this process holds over <paramref name="process"/>, served or not, for an
    /// executor in this process which closes the ledger (ledgers.md §8.1).
    /// </summary>
    internal LedgerWriter? HeldWriter(Ledger process)
    {
        if (!Volatile.Read(ref _held).TryGetValue(process.Id, out var writer))
        {
            return null;
        }

        Debug.Assert(ReferenceEquals(writer.Ledger, process), "A process holds one Ledger of each ledger, which its writer and executor share.");
        return writer;
    }

    /// <summary>
    /// Begins the closing of <paramref name="ledgers"/> by an executor in this process: until the scope ends, they are not
    /// <see cref="Unserved"/>, so no controller replaces a ledger this process is closing. A scope which ends with a
    /// ledger still listed open, as when its executor was interrupted, leaves it unserved.
    /// </summary>
    internal ClosingScope BeginClosing(ImmutableArray<LedgerId> ledgers)
    {
        TaskCompletionSource wake;
        lock (_lock)
        {
            foreach (var ledger in ledgers)
            {
                _closing[ledger] = _closing.GetValueOrDefault(ledger) + 1;
            }

            wake = MarkDirty();
        }

        _ = wake.TrySetResult();
        return new ClosingScope(this, ledgers);
    }

    /// <summary>
    /// Returns a task which completes once the follower has reconciled with a view value at <paramref name="epoch"/> or
    /// later, in a reconciliation which began after the call (ledgers.md §8.1 step 5), or once the registry is disposed.
    /// </summary>
    internal Task ReconciledAsync(ulong epoch, CancellationToken cancellationToken)
    {
        TaskCompletionSource reconciled;
        TaskCompletionSource wake;
        lock (_lock)
        {
            if (_disposed)
            {
                return Task.CompletedTask;
            }

            reconciled = NewSignal();
            _reconciledWaiters.Add((epoch, reconciled));
            wake = MarkDirty();
        }

        _ = wake.TrySetResult();
        return reconciled.Task.WaitAsync(cancellationToken);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static bool IsProcessCritical(Exception exception) => exception is OutOfMemoryException;

    /// <summary>Starts a writer's disposal off the registry's lock, in no caller's execution context (conventions.md §12).</summary>
    private static Task Leave(LedgerWriter writer)
    {
        using (ExecutionContext.SuppressFlow())
        {
            return Task.Run(async () => await writer.DisposeAsync().ConfigureAwait(false), CancellationToken.None);
        }
    }

    /// <summary>Returns the position of the first ledger in <paramref name="ledgers"/>, in ID order, at or above <paramref name="id"/>.</summary>
    private static int LowerBound(ImmutableArray<LedgerMetadata> ledgers, LedgerId id)
    {
        int lower = 0, upper = ledgers.Length;
        while (lower < upper)
        {
            var middle = lower + (upper - lower) / 2;
            (lower, upper) = ledgers[middle].Id.Value < id.Value ? (middle + 1, upper) : (lower, middle);
        }

        return lower;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The registry could not serve ledger {Ledger}, which its map view lists under a claim of this process; the ledger is unserved.")]
    private static partial void LogAdoptionFailed(ILogger logger, LedgerId ledger, Exception exception);

    [LoggerMessage(Level = LogLevel.Critical, Message = "The registry stopped following its map view.")]
    private static partial void LogFollowingFailed(ILogger logger, Exception exception);

    /// <summary>Marks the registry for a reconciliation; the caller holds the lock, and signals the returned wake after it.</summary>
    private TaskCompletionSource MarkDirty()
    {
        _dirty = true;
        var wake = _wake;
        _wake = NewSignal();
        return wake;
    }

    private void WakeForStop()
    {
        TaskCompletionSource wake;
        lock (_lock)
        {
            wake = MarkDirty();
        }

        _ = wake.TrySetResult();
    }

    private void EndClosing(ImmutableArray<LedgerId> ledgers)
    {
        TaskCompletionSource wake;
        lock (_lock)
        {
            foreach (var ledger in ledgers)
            {
                var count = _closing[ledger] - 1;
                if (count == 0)
                {
                    _ = _closing.Remove(ledger);
                }
                else
                {
                    _closing[ledger] = count;
                }
            }

            wake = MarkDirty();
        }

        _ = wake.TrySetResult();
    }

    /// <summary>
    /// Reconciles the held lifetimes and writers with the view's <see cref="LedgerMapView.Current"/> (ledgers.md §8.3),
    /// read under the registry's lock; only the follower calls it, so reconciliations apply the view's values in epoch
    /// order. A pass whose view and marks are unchanged does nothing.
    /// </summary>
    /// <returns>The epoch it reconciled with, and the wake which a later mark signals.</returns>
    private (ulong Epoch, Task Wake) ReconcilePass()
    {
        List<Sequencer>? adopted = null;
        List<(LedgerId Ledger, Exception Failure)>? failures = null;
        ulong epoch;
        Task wake;
        lock (_lock)
        {
            var view = _map.Current;
            epoch = view.Epoch;
            wake = _wake.Task;
            if (_disposed || !_dirty && _reconciledEpoch == epoch)
            {
                return (epoch, wake);
            }

            _dirty = false;
            _ = _leaving.RemoveAll(static leaving => leaving.IsCompleted);
            if (_pending.Count > 0)
            {
                DecidePending(view, ref adopted, ref failures);
            }

            LeaveDecided(view);
            UpdateUnserved(view);
            _reconciledEpoch = epoch;
            for (var index = _reconciledWaiters.Count - 1; index >= 0; index--)
            {
                if (_reconciledWaiters[index].Epoch <= epoch)
                {
                    _ = _reconciledWaiters[index].Reconciled.TrySetResult();
                    _reconciledWaiters.RemoveAt(index);
                }
            }

            if (_pending.Count > 0 && _rereading is null)
            {
                using (ExecutionContext.SuppressFlow())
                {
                    _rereading = Task.Run(RereadAsync, CancellationToken.None);
                }
            }
        }

        foreach (var (ledger, failure) in failures ?? [])
        {
            LogAdoptionFailed(_logger, ledger, failure);
        }

        foreach (var lifetime in adopted ?? [])
        {
            // A stop which leaves the ledger open makes it unserved, which the follower's next pass lists.
            _ = lifetime.Stopped.ContinueWith(
                static (_, state) => ((LedgerWriterRegistry)state!).WakeForStop(),
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        return (epoch, wake);
    }

    /// <summary>
    /// Decides the held lifetimes from <paramref name="view"/> in one pass over the ledgers it lists from the lowest ID a
    /// pending change can assign: adopts each into its ledger's writer once the view lists the ledger open under its
    /// claim, and stops each once the view lists its claim closed, or its change's rule fails, as then it does in every
    /// later map. A ledger whose writer cannot be created is left unserved, and the pass goes on. The caller holds the
    /// lock.
    /// </summary>
    private void DecidePending(LedgerMapValue view, ref List<Sequencer>? adopted, ref List<(LedgerId, Exception)>? failures)
    {
        var from = ulong.MaxValue;
        foreach (var (_, pending) in _pending)
        {
            from = Math.Min(from, pending.From.Value);
        }

        var ledgers = view.Ledgers;
        for (var index = LowerBound(ledgers, new LedgerId(from)); index < ledgers.Length; index++)
        {
            var listed = ledgers[index];
            if (listed.Primary.Node != Node || !_pending.Remove(listed.Primary.Sequencer, out var pending))
            {
                continue;
            }

            Debug.Assert(listed.Id.Value >= pending.From.Value, "LG-I3: a change assigns IDs at or above the NextLedgerId of every earlier map.");
            if (!listed.IsOpen)
            {
                // The view missed the ledger's whole open life: the lifetime never served and admitted nothing.
                pending.Lifetime.Stop(SequencerStopReason.Requested);
                continue;
            }

            try
            {
                using var lease = Reader.LeaseLedger(listed);
                _held = _held.Add(listed.Id, lease.CreateWriter(pending.Lifetime, _map, _host, _timeProvider));
            }
            catch (Exception exception) when (!IsProcessCritical(exception))
            {
                Debug.Assert(!Reader.IsDisposed, "The reader outlives the registry.");
                pending.Lifetime.Stop(SequencerStopReason.Requested);
                (failures ??= []).Add((listed.Id, exception));
                continue;
            }

            (adopted ??= []).Add(pending.Lifetime);
        }

        List<SequencerInstanceId>? decided = null;
        foreach (var (id, pending) in _pending)
        {
            if (!view.InstallRuleHolds(pending.Closes.AsSpan(), pending.Opens.AsSpan(), pending.ChosenAt))
            {
                pending.Lifetime.Stop(SequencerStopReason.Requested);
                (decided ??= []).Add(id);
            }
        }

        foreach (var id in decided ?? [])
        {
            _ = _pending.Remove(id);
        }
    }

    /// <summary>
    /// Lets each held writer go whose ledger the view lists closed or no longer lists: the view has then decided every
    /// proposal it admitted (LP-I16). The caller holds the lock.
    /// </summary>
    private void LeaveDecided(LedgerMapValue view)
    {
        foreach (var (id, writer) in _held)
        {
            if (view.Get(id) is not { IsOpen: true })
            {
                // The disposal starts before the writer leaves, so a writer the registry no longer holds is always leaving.
                _leaving.Add(Leave(writer));
                _held = _held.Remove(id);
            }
        }
    }

    /// <summary>
    /// Lists the open ledgers <paramref name="view"/> names this node's primary of under a claim this process does not
    /// hold with a running lifetime, and which no executor in this process is closing; it replaces the list, and signals
    /// its waiters, only when the list changes. The caller holds the lock.
    /// </summary>
    private void UpdateUnserved(LedgerMapValue view)
    {
        var previous = _unserved;
        var kept = 0;
        ImmutableArray<LedgerId>.Builder? changed = null;
        foreach (var listed in view.Ledgers)
        {
            if (!IsUnserved(listed))
            {
                continue;
            }

            if (changed is null && kept < previous.Length && previous[kept] == listed.Id)
            {
                kept++;
                continue;
            }

            if (changed is null)
            {
                changed = ImmutableArray.CreateBuilder<LedgerId>();
                changed.AddRange(previous.AsSpan(..kept));
            }

            changed.Add(listed.Id);
        }

        if (changed is null && kept == previous.Length)
        {
            return;
        }

        _unserved = changed?.ToImmutable() ?? ImmutableArray.Create(previous.AsSpan(..kept));
        _unservedVersion++;
        _ = _unservedChanged.TrySetResult();
        _unservedChanged = NewSignal();
    }

    private bool IsUnserved(LedgerMetadata listed) =>
        listed.IsOpen
        && listed.Primary.Node == Node
        && !_closing.ContainsKey(listed.Id)
        && !(_held.TryGetValue(listed.Id, out var writer)
            && writer.InstanceId == listed.Primary.Sequencer
            && (!writer.Sequencer.IsStopped || writer.Sequencer.StopReason == SequencerStopReason.Released));

    /// <summary>
    /// Reconciles, then waits for the view to pass the epoch it reconciled with, or for a mark, so no value it reaches
    /// and no stop goes unreconciled, until disposal.
    /// </summary>
    private async Task FollowAsync()
    {
        try
        {
            while (true)
            {
                var (epoch, wake) = ReconcilePass();
                using var waiting = CancellationTokenSource.CreateLinkedTokenSource(_disposing.Token);
                var changed = _map.WaitForChangeAsync(epoch, waiting.Token);
                _ = await Task.WhenAny(changed, wake).ConfigureAwait(false);
                waiting.Cancel();
                try
                {
                    _ = await changed.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (waiting.IsCancellationRequested)
                {
                    // The wake came first, or the registry is being disposed.
                }

                _disposing.Token.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException) when (_disposing.IsCancellationRequested)
        {
            // Disposal ends the following.
        }
        catch (Exception exception) when (!IsProcessCritical(exception))
        {
            LogFollowingFailed(_logger, exception);
            Debug.Fail($"The registry stopped following its map view: {exception}");
            throw;
        }
    }

    /// <summary>
    /// While lifetimes are pending, re-reads the register with bounded, jittered backoff (ledgers.md §8.1), so a change
    /// whose commit outcome its executor never learned is still decided: the read publishes into the view, which the
    /// follower reconciles with.
    /// </summary>
    private async Task RereadAsync()
    {
        try
        {
            await Task.Delay(LedgerWriter.MapReadBaseDelay, _timeProvider, _disposing.Token).ConfigureAwait(false);
            _ = await _mapReadRetry.ExecuteAsync(
                static async (registry, cancellationToken) =>
                {
                    _ = await registry._map.ReadAsync(cancellationToken).ConfigureAwait(false);
                    return registry.StopRereadingWhenDecided();
                },
                this,
                _disposing.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (_disposing.IsCancellationRequested && !IsProcessCritical(exception))
        {
            // Disposal ends the re-reads, including a transient read failure that raced cancellation.
        }
    }

    /// <summary>
    /// Renews primary leases for open, replica-backed ledgers that have been idle for at least one third of the
    /// configured lease duration, sending at most one batched <see cref="ILedgerReplicaTransport.HeartbeatAsync"/> RPC per
    /// peer node (ledger-protocol.md §8.6, ledgers.md §2.4).
    /// </summary>
    internal async Task RunHeartbeatPassAsync(CancellationToken cancellationToken = default)
    {
        var leaseOptions = Reader.LeaseOptions;
        if (!leaseOptions.IsEnabled)
        {
            return;
        }

        var renewalInterval = TimeSpan.FromTicks(Math.Max(1L, leaseOptions.LeaseDuration.Ticks / 3));
        var renewalIntervalTicks = LedgerLeaseOptions.CeilingTimestampTicks(renewalInterval, _timeProvider.TimestampFrequency);
        var held = Volatile.Read(ref _held);
        if (held.IsEmpty)
        {
            return;
        }

        List<(LedgerWriter Writer, LedgerLocation.Nodes Nodes)>? due = null;
        foreach (var (_, writer) in held)
        {
            if (writer.Sequencer.IsStopped
                || writer.Ledger.Location.Value is not LedgerLocation.Nodes nodes
                || !writer.Ledger.NeedsHeartbeat(renewalIntervalTicks))
            {
                continue;
            }

            (due ??= []).Add((writer, nodes));
        }

        if (due is null)
        {
            return;
        }

        due.Sort(static (left, right) => left.Writer.Ledger.Id.Value.CompareTo(right.Writer.Ledger.Id.Value));

        var byNode = new SortedDictionary<NodeId, List<LedgerId>>(Comparer<NodeId>.Create(static (left, right) => left.Value.CompareTo(right.Value)));
        foreach (var (writer, nodes) in due)
        {
            foreach (var replicaNode in nodes.Replicas)
            {
                if (!byNode.TryGetValue(replicaNode, out var batch))
                {
                    batch = [];
                    byNode.Add(replicaNode, batch);
                }

                batch.Add(writer.Ledger.Id);
            }
        }

        // LP-I18: record sendTimestamp before dispatching any heartbeat RPC so primary expiry is bounded by send time.
        var sendTimestamp = _timeProvider.GetTimestamp();
        var peerBatches = new LedgerId[byNode.Count][];
        var peerTasks = new Task<ReplicaHeartbeatReply?>[byNode.Count];
        var index = 0;
        foreach (var (peerNode, ledgerIds) in byNode)
        {
            var batch = ledgerIds.ToArray();
            peerBatches[index] = batch;
            peerTasks[index] = SendPeerHeartbeatAsync(peerNode, batch, cancellationToken);
            index++;
        }

        var replies = await Task.WhenAll(peerTasks).ConfigureAwait(false);
        var outcomesByLedger = new Dictionary<LedgerId, (int Granted, long MinGrantTicks, bool Sealed)>(due.Count);
        for (var peerIndex = 0; peerIndex < replies.Length; peerIndex++)
        {
            if (replies[peerIndex] is not { } reply)
            {
                continue;
            }

            var batch = peerBatches[peerIndex];
            var outcomes = reply.Outcomes.Span;
            if (outcomes.Length != batch.Length)
            {
                continue;
            }

            var grantTicks = reply.LeaseGrant.Ticks;
            for (var itemIndex = 0; itemIndex < batch.Length; itemIndex++)
            {
                var ledgerId = batch[itemIndex];
                var status = outcomes[itemIndex];
                _ = outcomesByLedger.TryGetValue(ledgerId, out var acc);
                if (acc.Granted == 0 && !acc.Sealed && acc.MinGrantTicks == 0)
                {
                    acc = (0, long.MaxValue, false);
                }

                switch (status)
                {
                    case ReplicaHeartbeatStatus.Granted when grantTicks > 0:
                        acc = (acc.Granted + 1, Math.Min(acc.MinGrantTicks, grantTicks), acc.Sealed);
                        break;
                    case ReplicaHeartbeatStatus.Sealed:
                    case ReplicaHeartbeatStatus.Dropped:
                        acc = (acc.Granted, acc.MinGrantTicks, true);
                        break;
                    case ReplicaHeartbeatStatus.Granted:
                    case ReplicaHeartbeatStatus.Absent:
                    default:
                        break;
                }

                outcomesByLedger[ledgerId] = acc;
            }
        }

        foreach (var (writer, nodes) in due)
        {
            if (!outcomesByLedger.TryGetValue(writer.Ledger.Id, out var acc))
            {
                continue;
            }

            if (acc.Sealed)
            {
                // LP-I14, LP-I18: durable seal or drop stops the primary and permanently revokes its local lease.
                writer.Ledger.OnObservedSeal();
            }
            else if (acc.Granted >= nodes.Quorum && acc.MinGrantTicks > 0 && acc.MinGrantTicks < long.MaxValue)
            {
                writer.Ledger.TryExtendLease(sendTimestamp, TimeSpan.FromTicks(acc.MinGrantTicks));
            }
        }
    }

    private async Task<ReplicaHeartbeatReply?> SendPeerHeartbeatAsync(NodeId peerNode, LedgerId[] batch, CancellationToken cancellationToken)
    {
        try
        {
            var answer = await Reader.Transport.HeartbeatAsync(peerNode, batch, cancellationToken).ConfigureAwait(false);
            return answer.TryGetValue(out ReplicaHeartbeatReply reply) ? reply : null;
        }
        catch (Exception exception) when (!IsProcessCritical(exception))
        {
            // Transient peer failure or cancellation skips this peer; remaining peers can still satisfy quorum.
            return null;
        }
    }

    private async Task HeartbeatLoopAsync()
    {
        var leaseOptions = Reader.LeaseOptions;
        var interval = TimeSpan.FromTicks(Math.Max(1L, leaseOptions.LeaseDuration.Ticks / 3));
        try
        {
            while (!_disposing.IsCancellationRequested)
            {
                await Task.Delay(interval, _timeProvider, _disposing.Token).ConfigureAwait(false);
                await RunHeartbeatPassAsync(_disposing.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_disposing.IsCancellationRequested)
        {
            // Disposal ends the heartbeat loop.
        }
        catch (Exception exception) when (!IsProcessCritical(exception))
        {
            LogFollowingFailed(_logger, exception);
            Debug.Fail($"The registry's lease heartbeat loop stopped unexpectedly: {exception}");
            throw;
        }
    }

    /// <summary>Ends the re-reads once no lifetime is pending; the next pass which finds one pending starts them again.</summary>
    private bool StopRereadingWhenDecided()
    {
        lock (_lock)
        {
            if (_pending.Count > 0 && !_disposed)
            {
                return false;
            }

            _rereading = null;
            return true;
        }
    }

    /// <summary>A lifetime an executor created, which the view has not yet decided, and the lowest ID its change can assign.</summary>
    private sealed record Pending(Sequencer Lifetime, ImmutableArray<LedgerId> Closes, ImmutableArray<KeyRange> Opens, ulong ChosenAt, LedgerId From);

    /// <summary>An executor's closing of ledgers this process may be the primary of (<see cref="BeginClosing"/>).</summary>
    internal sealed class ClosingScope(LedgerWriterRegistry registry, ImmutableArray<LedgerId> ledgers) : IDisposable
    {
        private int _ended;

        /// <summary>Ends the closing: a ledger the view still lists open becomes unserved.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ended, 1) == 0)
            {
                registry.EndClosing(ledgers);
            }
        }
    }
}
