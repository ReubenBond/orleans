using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// One sequencer lifetime: a bounded window of slots, granted from slot 0 and counted complete only through
/// <see cref="CompleteSlotAsync"/>, in slot order (ledger-protocol.md §2.6).
/// </summary>
/// <remarks>
/// "The sequencer supports a simple acquireSlot / completeSlot API." (research/papers/logdrive/paper.md, §4.) A
/// <see cref="Ledger"/> creates it with its one <see cref="LedgerWriter"/>: a lifetime is soft state in its primary's
/// process, which only <see cref="Stop"/> ends.
/// </remarks>
internal sealed class Sequencer
{
    // Each slot marked complete above the completed tail, with the waiter its completion returns once every lower slot
    // has completed.
    private readonly Dictionary<long, TaskCompletionSource> _completionWaiters = [];

    // The acquisitions waiting for the window, in request order: each is granted the next slot in turn (LP-I2).
    private readonly LinkedList<Acquisition> _acquisitions = [];
    private readonly Lock _lock = new();
    private readonly TaskCompletionSource<SequencerStopReason> _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Action<SequencerStopReason>? _onStop;
    private long _completedTail;
    private long _nextSlot;
    // Written under the lock, and read without it by IsStopped, which a primary's every lookup asks.
    private volatile SequencerStopReason _stopReason;

    /// <summary>
    /// Initializes a new instance of the <see cref="Sequencer"/> class: a new lifetime under a fresh instance ID, whose
    /// first slot is 0 (LP-I15).
    /// </summary>
    /// <param name="windowSize">The window <c>K</c>: the most acquired slots which are not completed (LP-I2).</param>
    internal Sequencer(int windowSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowSize);
        WindowSize = windowSize;
        InstanceId = SequencerInstanceId.New();
    }

    /// <summary>
    /// Gets the lifetime's instance ID, which names no other lifetime (LP-I15).
    /// </summary>
    internal SequencerInstanceId InstanceId { get; }

    /// <summary>
    /// Gets the window <c>K</c>.
    /// </summary>
    internal int WindowSize { get; }

    /// <summary>
    /// Gets the completed tail: the first slot which has not completed.
    /// </summary>
    internal Slot CompletedTail
    {
        get
        {
            lock (_lock)
            {
                return new Slot(_completedTail);
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether the lifetime is stopped.
    /// </summary>
    internal bool IsStopped => _stopReason != SequencerStopReason.None;

    internal SequencerStopReason StopReason => _stopReason;

    /// <summary>
    /// Gets or sets the observer which receives each step of the lifetime, outside its lock: each slot it grants, each
    /// completion its writer calls and each which returns, and each stop request. Deterministic simulation logs the steps
    /// as the ledger trace's writer steps (verification.md §5.1); tests record them.
    /// </summary>
    internal Action<SequencerStep>? Observer { get; set; }

    /// <summary>
    /// Gets a task which completes with the stop's reason once the lifetime is stopped.
    /// </summary>
    internal Task<SequencerStopReason> Stopped => _stopped.Task;

    /// <summary>
    /// Gets or sets a test seam awaited before each acquisition, so a test can hold one while it acts.
    /// </summary>
    internal Func<CancellationToken, ValueTask>? BeforeAcquire { get; set; }

    /// <summary>
    /// Gets or sets a test seam awaited before each completion, so a test can hold one or fail it.
    /// </summary>
    internal Func<SlotGrant, CancellationToken, ValueTask>? BeforeComplete { get; set; }

    /// <summary>
    /// Grants the next slot, waiting while <c>K</c> acquired slots are not completed (LP-I2). Acquisitions are granted
    /// in the order of their requests, so a caller whose requests follow one another gets ascending slots. Cancellation
    /// while waiting consumes no slot.
    /// </summary>
    /// <exception cref="SequencerStoppedException">The lifetime is stopped.</exception>
    /// <exception cref="OverflowException">The slot would be <c>2^63 − 1</c>, past the replica contract's domain.</exception>
    internal ValueTask<SlotGrant> AcquireSlotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return BeforeAcquire is { } before
            ? AcquireAfterAsync(before, cancellationToken)
            : AcquireInOrderAsync(cancellationToken);
    }

    /// <summary>
    /// Marks <paramref name="grant"/>'s slot complete and returns once every lower slot has completed (LP-I3). Idempotent.
    /// Cancelling a waiting completion leaves the slot marked complete. Its caller reports the slot's
    /// <see cref="SequencerStepKind.Completed"/> step once it returns.
    /// </summary>
    /// <exception cref="ArgumentException">This lifetime did not grant <paramref name="grant"/>.</exception>
    /// <exception cref="SequencerStoppedException">The lifetime is stopped.</exception>
    internal ValueTask CompleteSlotAsync(SlotGrant grant, CancellationToken cancellationToken = default)
    {
        if (grant.Lifetime != InstanceId)
        {
            throw new ArgumentException("The slot was not granted by this sequencer lifetime.", nameof(grant));
        }

        Observer?.Invoke(SequencerStep.Completing(grant.Slot));
        return BeforeComplete is { } before
            ? CompleteAfterAsync(before, grant, cancellationToken)
            : new ValueTask(MarkCompleteAsync(grant.Slot.Value, cancellationToken));
    }

    /// <summary>
    /// Permanently stops the lifetime: it grants and completes no slot afterwards, and every waiter fails with
    /// <see cref="SequencerStoppedException"/> (LP-I13). Idempotent: the first reason stands. It cannot fail.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="reason"/> is <c>None</c> or undefined.</exception>
    internal void Stop(SequencerStopReason reason)
    {
        if (reason == SequencerStopReason.None || !Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason), reason, "The stop reason is invalid.");
        }

        TaskCompletionSource[] completionWaiters = [];
        Acquisition[] acquisitions = [];
        Action<SequencerStopReason>? onStop = null;
        var stopped = false;
        lock (_lock)
        {
            if (_stopReason == SequencerStopReason.None)
            {
                _stopReason = reason;
                stopped = true;
                completionWaiters = [.. _completionWaiters.Values];
                acquisitions = [.. _acquisitions];
                _acquisitions.Clear();
                onStop = _onStop;
            }
        }

        if (stopped)
        {
            var failure = new SequencerStoppedException(reason);
            foreach (var waiter in completionWaiters)
            {
                _ = waiter.TrySetException(failure);
            }

            foreach (var acquisition in acquisitions)
            {
                _ = acquisition.TrySetException(failure);
            }

            onStop?.Invoke(reason);
            _ = _stopped.TrySetResult(reason);
        }

        Observer?.Invoke(SequencerStep.Stopped(reason));
    }

    /// <summary>
    /// Sets the action the stop invokes once, synchronously, outside the lifetime's lock, with its reason: the lifetime's
    /// writer's, so a stop schedules no work of its own. Ordered against <see cref="Stop"/> by the lifetime's lock.
    /// </summary>
    /// <param name="onStop">The action.</param>
    /// <param name="stoppedWith">The reason of the stop which came first, when the lifetime is already stopped.</param>
    /// <returns><see langword="false"/>, setting nothing, when the lifetime is already stopped.</returns>
    internal bool TrySetOnStop(Action<SequencerStopReason> onStop, out SequencerStopReason stoppedWith)
    {
        lock (_lock)
        {
            stoppedWith = _stopReason;
            if (_stopReason != SequencerStopReason.None)
            {
                return false;
            }

            _onStop = onStop;
            return true;
        }
    }

    private static TaskCompletionSource CreateWaiter() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async ValueTask<SlotGrant> AcquireAfterAsync(Func<CancellationToken, ValueTask> before, CancellationToken cancellationToken)
    {
        await before(cancellationToken).ConfigureAwait(false);
        return await AcquireInOrderAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Grants the next slot at once when no earlier acquisition waits and the window has room; otherwise queues the
    /// acquisition behind the earlier ones until a completion grants it its slot.
    /// </summary>
    private ValueTask<SlotGrant> AcquireInOrderAsync(CancellationToken cancellationToken)
    {
        Acquisition? acquisition = null;
        SlotGrant grant;
        lock (_lock)
        {
            ThrowIfStopped();
            if (_acquisitions.Count != 0 || !TryGrant(out grant))
            {
                grant = default;
                acquisition = new Acquisition(this);
                acquisition.Node = _acquisitions.AddLast(acquisition);
            }
        }

        if (acquisition is null)
        {
            Observer?.Invoke(SequencerStep.Acquired(grant.Slot));
            return ValueTask.FromResult(grant);
        }

        return cancellationToken.CanBeCanceled
            ? new ValueTask<SlotGrant>(acquisition.WaitAsync(cancellationToken))
            : new ValueTask<SlotGrant>(acquisition.Task);
    }

    /// <summary>
    /// Withdraws a queued acquisition which its caller cancelled; one already granted keeps its slot.
    /// </summary>
    private void Withdraw(Acquisition acquisition, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (acquisition.Node?.List is null)
            {
                return;
            }

            _acquisitions.Remove(acquisition.Node);
        }

        _ = acquisition.TrySetCanceled(cancellationToken);
    }

    /// <summary>
    /// Grants the next slot when the window has room; the caller holds the lock.
    /// </summary>
    /// <exception cref="OverflowException">The slot would be <c>2^63 − 1</c>, past the replica contract's domain.</exception>
    private bool TryGrant(out SlotGrant grant)
    {
        if (_nextSlot - _completedTail >= WindowSize)
        {
            grant = default;
            return false;
        }

        if (_nextSlot == long.MaxValue)
        {
            throw new OverflowException("The sequencer would grant slot 2^63 − 1, past the replica contract's domain.");
        }

        grant = new SlotGrant(new Slot(_nextSlot), InstanceId);
        _nextSlot++;
        return true;
    }

    private async ValueTask CompleteAfterAsync(
        Func<SlotGrant, CancellationToken, ValueTask> before,
        SlotGrant grant,
        CancellationToken cancellationToken)
    {
        await before(grant, cancellationToken).ConfigureAwait(false);
        await MarkCompleteAsync(grant.Slot.Value, cancellationToken).ConfigureAwait(false);
    }

    private Task MarkCompleteAsync(long slot, CancellationToken cancellationToken)
    {
        Task completion;
        List<TaskCompletionSource>? releasedWaiters = null;
        Acquisition? granted = null;
        lock (_lock)
        {
            if (_stopReason != SequencerStopReason.None)
            {
                return Task.FromException(new SequencerStoppedException(_stopReason));
            }

            Debug.Assert(slot < _nextSlot, "A lifetime completes only the slots it granted.");
            if (slot < _completedTail)
            {
                return Task.CompletedTask;
            }

            if (slot != _completedTail)
            {
                ref var waiter = ref CollectionsMarshal.GetValueRefOrAddDefault(_completionWaiters, slot, out var exists);
                if (!exists)
                {
                    waiter = CreateWaiter();
                }

                completion = waiter!.Task;
            }
            else
            {
                // "In turn, each completeSlot blocks until all prior slots are completed."
                // (research/papers/logdrive/paper.md, §4.)
                _completedTail++;
                while (_completionWaiters.Remove(_completedTail, out var completedWaiter))
                {
                    (releasedWaiters ??= []).Add(completedWaiter);
                    _completedTail++;
                }

                // The window has room again: grant the waiting acquisitions their slots in request order, so a
                // cancellation which arrives later finds each already granted. They are completed after the lock, in
                // the same order, through a chain the acquisitions carry.
                Acquisition? last = null;
                while (_acquisitions.First is { } first && first.Value.TryTakeGrant(this))
                {
                    _acquisitions.RemoveFirst();
                    if (last is null)
                    {
                        granted = first.Value;
                    }
                    else
                    {
                        last.NextGranted = first.Value;
                    }

                    last = first.Value;
                }

                completion = Task.CompletedTask;
            }
        }

        if (releasedWaiters is not null)
        {
            foreach (var waiter in releasedWaiters)
            {
                _ = waiter.TrySetResult();
            }
        }

        for (var acquisition = granted; acquisition is not null; acquisition = acquisition.NextGranted)
        {
            if (acquisition.Exhausted is { } exhausted)
            {
                _ = acquisition.TrySetException(exhausted);
                continue;
            }

            Observer?.Invoke(SequencerStep.Acquired(acquisition.Grant.Slot));
            _ = acquisition.TrySetResult(acquisition.Grant);
        }

        return cancellationToken.CanBeCanceled && !completion.IsCompleted ? completion.WaitAsync(cancellationToken) : completion;
    }

    private void ThrowIfStopped()
    {
        if (_stopReason != SequencerStopReason.None)
        {
            throw new SequencerStoppedException(_stopReason);
        }
    }

    /// <summary>
    /// One acquisition waiting for the window, completed with its grant, or failed by a stop. It carries its lifetime, so
    /// it is its own cancellation state.
    /// </summary>
    private sealed class Acquisition(Sequencer sequencer) : TaskCompletionSource<SlotGrant>(TaskCreationOptions.RunContinuationsAsynchronously)
    {
        /// <summary>Gets or sets its place in the queue, which leaves the list once it is granted or withdrawn.</summary>
        public LinkedListNode<Acquisition>? Node { get; set; }

        /// <summary>Gets the slot granted to it, under the lifetime's lock.</summary>
        public SlotGrant Grant { get; private set; }

        /// <summary>Gets the failure of a grant past the end of the slots, under the lifetime's lock.</summary>
        public OverflowException? Exhausted { get; private set; }

        /// <summary>Gets or sets the next acquisition the same completion granted, which it completes after the lock.</summary>
        public Acquisition? NextGranted { get; set; }

        /// <summary>
        /// Takes the next slot for this acquisition while the window has room; the caller holds the lifetime's lock.
        /// </summary>
        public bool TryTakeGrant(Sequencer lifetime)
        {
            try
            {
                if (!lifetime.TryGrant(out var grant))
                {
                    return false;
                }

                Grant = grant;
            }
            catch (OverflowException exhausted)
            {
                Exhausted = exhausted;
            }

            return true;
        }

        /// <summary>Waits for the grant, withdrawing the acquisition when <paramref name="cancellationToken"/> fires first.</summary>
        public async Task<SlotGrant> WaitAsync(CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.UnsafeRegister(
                static (state, token) =>
                {
                    var acquisition = (Acquisition)state!;
                    acquisition.Lifetime.Withdraw(acquisition, token);
                },
                this);
            return await Task.ConfigureAwait(false);
        }

        private Sequencer Lifetime => sequencer;
    }
}
