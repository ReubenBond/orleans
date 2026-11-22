using System.Collections.Concurrent;
using System.Diagnostics;

namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// One node's delivery of the decided batches of every ledger it replicates to its <see cref="LedgerApplier"/>, in slot
/// order, as resolved rows through one <see cref="LedgerSegmentView"/> per ledger (ledgers.md §8.5): an open ledger's
/// below each contiguous tail the node learns from its own replica, and, once the node's map view lists the ledger
/// <c>Closed(T)</c>, the rest below <c>T</c>. It reads every batch through the node's <see cref="LedgerReader"/>, so a
/// batch the node's own replica lacks comes from another (LP-I10), and the applier reaches every closed ledger's <c>T</c>.
/// </summary>
/// <remarks>
/// <para>
/// Each delivery starts from the applier's <see cref="LedgerApplier.NextSlotAsync"/>, its durable watermark. A watermark
/// in the middle of a segment makes the first scan rewind to the segment's start and resolve from there, and the applier
/// receives the rows from its watermark on (LG-I13). The view stays across the delivery's scans, so a scan resumes where
/// the last one stopped without rewinding.
/// </para>
/// <para>
/// The loop follows the node's map view, which other callers refresh: it starts a delivery for each ledger the view
/// lists with the node, passes it each newer listing, and once the view no longer lists the ledger, stops its delivery
/// and tells the applier (<see cref="LedgerApplier.RemoveAsync"/>). An open ledger's delivery learns the tail every
/// <see cref="LearnInterval"/>, the writer's <see cref="LedgerWriter.ContiguousTailInterval"/>, from the replica on the
/// node, without a seal check (LP-I11). Backpressure and failures repetition may change back off, jittered, from
/// <see cref="LearnInterval"/> to a second; a <c>Dropped</c> answer waits for the view's next listing. Any other failure
/// faults <see cref="Completion"/> and stops what met it: a delivery, or, for a removal the applier failed, the loop.
/// </para>
/// </remarks>
public sealed class LedgerApplyLoop : IAsyncDisposable
{
    /// <summary>How often an open ledger's delivery learns its contiguous tail: the writer's explicit-tail interval.</summary>
    internal static readonly TimeSpan LearnInterval = LedgerWriter.ContiguousTailInterval;

    private static readonly TimeSpan MaximumDelay = TimeSpan.FromSeconds(1);

    private readonly LedgerReader _reader;
    private readonly LedgerApplier _applier;
    private readonly TimeProvider _timeProvider;
    private readonly NodeId _node;
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Mutated only by the loop's run; ConcurrentDictionary allows lock-free Wake lookups.
    private readonly ConcurrentDictionary<LedgerId, Delivery> _deliveries = [];
    private readonly Task _run;
    private int _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="LedgerApplyLoop"/> class, which starts following the reader's map view
    /// at once.
    /// </summary>
    /// <param name="reader">The node's reader, whose local node is the node; the caller disposes it after the loop.</param>
    /// <param name="applier">What the node applies the batches to.</param>
    /// <param name="timeProvider">The clock of the learns and the backoff.</param>
    /// <exception cref="ArgumentException"><paramref name="reader"/> names no local node.</exception>
    public LedgerApplyLoop(LedgerReader reader, LedgerApplier applier, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(applier);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _node = reader.LocalNode ?? throw new ArgumentException("The apply loop runs on a node, so its reader names that node.", nameof(reader));
        _reader = reader;
        _applier = applier;
        _timeProvider = timeProvider;

        // The loop serves no caller, so it runs in none's execution context (conventions.md §12).
        using (ExecutionContext.SuppressFlow())
        {
            _run = Task.Run(RunAsync, CancellationToken.None);
        }
    }

    /// <summary>
    /// Gets a task which completes once the loop is disposed, or faults with the failure which stopped a ledger's
    /// delivery: corruption, a failure repetition cannot change, or the applier's.
    /// </summary>
    public Task Completion => _completion.Task;

    /// <summary>
    /// Wakes <paramref name="ledger"/>'s delivery, when the loop runs one, so an open ledger learns its contiguous tail
    /// at once rather than waiting for the next <see cref="LearnInterval"/>.
    /// </summary>
    /// <param name="ledger">The ledger to wake.</param>
    public void Wake(LedgerId ledger)
    {
        if (_deliveries.TryGetValue(ledger, out var delivery))
        {
            delivery.Wake();
        }
    }

    /// <summary>
    /// Stops every delivery and waits for it; a batch the applier was applying completes or observes the cancellation.
    /// </summary>
    /// <returns>A task which completes once every delivery has ended.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        await _run.ConfigureAwait(false);
        _stop.Dispose();
        _ = _completion.TrySetResult();
    }

    private async Task RunAsync()
    {
        var token = _stop.Token;
        try
        {
            var view = _reader.Map.Current;
            while (true)
            {
                await ReconcileAsync(view, token).ConfigureAwait(false);
                view = await _reader.Map.WaitForChangeAsync(view.Epoch, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception failure)
        {
            _ = _completion.TrySetException(failure);
        }
        finally
        {
            await Task.WhenAll(_deliveries.Values.Select(static delivery => delivery.StopAsync())).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Starts a delivery for each ledger <paramref name="view"/> lists with the node, passes each running one its newer
    /// listing, and stops and removes each one whose ledger the view no longer lists: a ledger's replicas are fixed for its
    /// life, so it left the map.
    /// </summary>
    private async Task ReconcileAsync(LedgerMapValue view, CancellationToken cancellationToken)
    {
        foreach (var (id, delivery) in _deliveries.ToList())
        {
            if (view.Get(id) is { } listed)
            {
                delivery.Update(listed);
                continue;
            }

            await delivery.StopAsync().ConfigureAwait(false);
            _ = _deliveries.TryRemove(id, out _);
            await _applier.RemoveAsync(id, cancellationToken).ConfigureAwait(false);
        }

        foreach (var listed in view.Ledgers)
        {
            if (listed.IsOnNode(_node) && !_deliveries.ContainsKey(listed.Id))
            {
                _deliveries[listed.Id] = new Delivery(this, listed, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Returns whether <paramref name="failure"/> is one repetition may change: every replica failure behind a quorum's
    /// <see cref="AggregateException"/> is transient (ledger-replicas.md §6.4).
    /// </summary>
    private static bool IsTransient(Exception failure) =>
        failure is AggregateException { InnerExceptions: var errors } && errors.All(static error => LedgerReplicaFailures.IsTransient(error, CancellationToken.None));

    private enum ScanEnd
    {
        Reached,
        Dropped,
        Backpressure,
    }

    /// <summary>One ledger's delivery: its listing, which the loop updates, and its run.</summary>
    private sealed class Delivery
    {
        private readonly LedgerApplyLoop _loop;
        private readonly LedgerId _id;
        private readonly CancellationTokenSource _stop;
        private readonly Lock _lock = new();
        private readonly Task _run;
        private LedgerMetadata _listed;
        private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _signaled;

        public Delivery(LedgerApplyLoop loop, LedgerMetadata listed, CancellationToken loopToken)
        {
            _loop = loop;
            _id = listed.Id;
            _listed = listed;
            _stop = CancellationTokenSource.CreateLinkedTokenSource(loopToken);
            _run = RunAsync(_stop.Token);
        }

        /// <summary>Passes the delivery the view's newer listing of its ledger, waking it when the listing changed.</summary>
        public void Update(LedgerMetadata listed)
        {
            TaskCompletionSource? changed;
            lock (_lock)
            {
                if (listed.Equals(_listed))
                {
                    return;
                }

                _listed = listed;
                changed = SignalLocked();
            }

            _ = changed?.TrySetResult();
        }

        /// <summary>Wakes the delivery at once so an open ledger checks its contiguous tail without waiting.</summary>
        public void Wake()
        {
            TaskCompletionSource? changed;
            lock (_lock)
            {
                changed = SignalLocked();
            }

            _ = changed?.TrySetResult();
        }

        public async Task StopAsync()
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            await _run.ConfigureAwait(false);
            _stop.Dispose();
        }

        private TaskCompletionSource? SignalLocked()
        {
            if (_signaled)
            {
                return null;
            }

            _signaled = true;
            return _changed;
        }

        private (LedgerMetadata Listed, Task Changed) Listing()
        {
            lock (_lock)
            {
                if (_signaled)
                {
                    _signaled = false;
                    _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                return (_listed, _changed.Task);
            }
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            // Yield, so the loop's reconciliation goes on while the delivery starts.
            await Task.Yield();
            try
            {
                await DeliverAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (ObjectDisposedException) when (_loop._reader.IsDisposed)
            {
            }
            catch (Exception failure)
            {
                _ = _loop._completion.TrySetException(failure);
            }
        }

        private async Task DeliverAsync(CancellationToken cancellationToken)
        {
            var next = await _loop._applier.NextSlotAsync(_id, cancellationToken).ConfigureAwait(false);
            using var view = new LedgerSegmentView(_id);
            var attempt = 0;
            while (true)
            {
                var (listed, changed) = Listing();
                var end = listed.Status.Value is LedgerStatus.Closed closed ? closed.T : (Slot?)null;
                ScanEnd reached;
                try
                {
                    var target = end ?? await _loop._reader.LearnTailAsync(listed, cancellationToken).ConfigureAwait(false);
                    (next, reached) = next < target
                        ? await ScanAsync(listed, next, target, view, cancellationToken).ConfigureAwait(false)
                        : (next, ScanEnd.Reached);
                }
                catch (Exception failure) when (IsTransient(failure) && !cancellationToken.IsCancellationRequested)
                {
                    reached = ScanEnd.Backpressure;
                }

                switch (reached)
                {
                    case ScanEnd.Reached when end is { } t:
                        Debug.Assert(next == t, "A closed ledger's delivery ends at its T.");
                        return;
                    case ScanEnd.Reached:
                        attempt = 0;
                        if (!changed.IsCompleted)
                        {
                            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            var delay = Task.Delay(LearnInterval, _loop._timeProvider, delayCts.Token);
                            _ = await Task.WhenAny(changed, delay).ConfigureAwait(false);
                            if (!delay.IsCompleted)
                            {
                                await delayCts.CancelAsync().ConfigureAwait(false);
                            }
                        }

                        break;
                    case ScanEnd.Dropped:
                        // The reader re-read the map, which revokes the ledger; the loop stops the delivery on its listing.
                        await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
                        break;
                    case ScanEnd.Backpressure:
                        await Task.Delay(Backoff.Delay(attempt++, LearnInterval, MaximumDelay, Random.Shared.NextDouble()), _loop._timeProvider, cancellationToken).ConfigureAwait(false);
                        break;
                    default:
                        throw new UnreachableException("A scan ends in one of its cases.");
                }

                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        /// <summary>
        /// Delivers the batches from <paramref name="next"/> below <paramref name="below"/> in slot order, returning the
        /// slot it reached and why it stopped there.
        /// </summary>
        private async Task<(Slot Next, ScanEnd End)> ScanAsync(LedgerMetadata listed, Slot next, Slot below, LedgerSegmentView view, CancellationToken cancellationToken)
        {
            await foreach (var item in _loop._reader.ScanAsync(listed, next, below, view, cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                var read = item.Read;
                if (read.TryGetValue(out LedgerRead.Data data))
                {
                    using (data)
                    {
                        await _loop._applier.ApplyAsync(_id, item, cancellationToken).ConfigureAwait(false);
                        next = data.Slot + 1;
                    }
                }
                else
                {
                    return (next, read.Value is ReplicaWriteOutcome.Dropped ? ScanEnd.Dropped : ScanEnd.Backpressure);
                }
            }

            return (next, ScanEnd.Reached);
        }
    }
}
