using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Transactions.Diagnostics;

namespace Orleans.Transactions.TestKit;

internal sealed class TransactionRecoveryEventObserver : IObserver<TransactionDiagnosticEvents.TransactionDiagnosticEvent>, IDisposable
{
    private readonly object lockObj = new();
    private readonly Func<ParticipantId, bool> candidateFilter;
    private readonly IDisposable subscription;
    private readonly long startedAt = Stopwatch.GetTimestamp();
    private readonly List<RecoveryTransition> timeline = [];
    private readonly List<Waiter> waiters = [];
    private HashSet<GrainId>? relevantGrains;
    private long nextSequence;
    private bool disposed;

    public TransactionRecoveryEventObserver(IEnumerable<GrainId> candidateGrains)
        : this(CreateCandidateFilter(candidateGrains))
    {
    }

    internal TransactionRecoveryEventObserver(Func<ParticipantId, bool> candidateFilter)
    {
        this.candidateFilter = candidateFilter;
        this.subscription = TransactionDiagnosticEvents.AllEvents.Subscribe(this);
    }

    public long LatestRelevantSequence
    {
        get
        {
            lock (this.lockObj)
            {
                for (var i = this.timeline.Count - 1; i >= 0; i--)
                {
                    if (this.IsCurrentlyRelevant(this.timeline[i]))
                    {
                        return this.timeline[i].Sequence;
                    }
                }

                return 0;
            }
        }
    }

    public void SetRelevantGrains(IEnumerable<GrainId> grainIds)
    {
        List<(Waiter Waiter, RecoveryTransition Transition)> completed = [];
        lock (this.lockObj)
        {
            this.ThrowIfDisposed();
            this.relevantGrains = grainIds.ToHashSet();
            for (var i = this.waiters.Count - 1; i >= 0; i--)
            {
                var waiter = this.waiters[i];
                var transition = this.FindTransitionAfter(waiter.AfterSequence);
                if (transition is not null)
                {
                    this.waiters.RemoveAt(i);
                    completed.Add((waiter, transition));
                }
            }
        }

        foreach (var item in completed)
        {
            item.Waiter.Completion.TrySetResult(item.Transition);
        }
    }

    public async Task<RecoveryTransition> WaitForNextTransitionAsync(
        long afterSequence,
        long deadline,
        CancellationToken cancellationToken = default)
    {
        Waiter waiter;
        lock (this.lockObj)
        {
            this.ThrowIfDisposed();
            var existing = this.FindTransitionAfter(afterSequence);
            if (existing is not null)
            {
                return existing;
            }

            waiter = new(afterSequence);
            this.waiters.Add(waiter);
        }

        try
        {
            var now = Stopwatch.GetTimestamp();
            if (now >= deadline)
            {
                throw new TimeoutException();
            }

            return await waiter.Completion.Task.WaitAsync(Stopwatch.GetElapsedTime(now, deadline), cancellationToken);
        }
        catch (TimeoutException)
        {
            this.RemoveWaiter(waiter);
            throw new TimeoutException(
                $"No relevant transaction recovery transition was observed before the watchdog deadline."
                + Environment.NewLine
                + this.FormatTimeline());
        }
        catch (OperationCanceledException)
        {
            this.RemoveWaiter(waiter);
            throw;
        }
    }

    public IReadOnlyList<RecoveryTransition> GetTimeline()
    {
        lock (this.lockObj)
        {
            return this.timeline.Where(this.IsCurrentlyRelevant).ToArray();
        }
    }

    public string FormatTimeline()
    {
        var entries = this.GetTimeline();
        if (entries.Count == 0)
        {
            return "Transaction recovery timeline: <no relevant events>";
        }

        return "Transaction recovery timeline:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, entries.Select(FormatTransition));
    }

    public static string FormatTransition(RecoveryTransition transition)
        => $"  sequence={transition.Sequence}, observedAt={transition.ObservedAtUtc:O}, elapsed={transition.Elapsed}, "
            + $"kind={transition.Kind}, transactions={FormatTransactionIds(transition.TransactionIds)}, "
            + $"resource={transition.ResourceName}, grain={transition.GrainId?.ToString() ?? "<none>"}, "
            + $"silo={transition.SiloAddress?.ToString() ?? "<none>"}, activation={transition.ActivationId}, "
            + $"status={transition.Status ?? "<none>"}";

    public void Dispose()
    {
        List<Waiter> waiters;
        lock (this.lockObj)
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            waiters = [.. this.waiters];
            this.waiters.Clear();
        }

        this.subscription.Dispose();
        foreach (var waiter in waiters)
        {
            waiter.Completion.TrySetException(new ObjectDisposedException(nameof(TransactionRecoveryEventObserver)));
        }
    }

    void IObserver<TransactionDiagnosticEvents.TransactionDiagnosticEvent>.OnCompleted()
    {
    }

    void IObserver<TransactionDiagnosticEvents.TransactionDiagnosticEvent>.OnError(Exception error)
    {
    }

    void IObserver<TransactionDiagnosticEvents.TransactionDiagnosticEvent>.OnNext(
        TransactionDiagnosticEvents.TransactionDiagnosticEvent value)
    {
        if (!this.candidateFilter(value.Resource)
            || !TryCreateTransition(value, Stopwatch.GetElapsedTime(this.startedAt), out var transition))
        {
            return;
        }

        List<Waiter> completed = [];
        lock (this.lockObj)
        {
            if (this.disposed)
            {
                return;
            }

            transition = transition with { Sequence = ++this.nextSequence };
            this.timeline.Add(transition);
            if (!this.IsCurrentlyRelevant(transition))
            {
                return;
            }

            for (var i = this.waiters.Count - 1; i >= 0; i--)
            {
                if (transition.Sequence > this.waiters[i].AfterSequence)
                {
                    completed.Add(this.waiters[i]);
                    this.waiters.RemoveAt(i);
                }
            }
        }

        foreach (var waiter in completed)
        {
            waiter.Completion.TrySetResult(transition);
        }
    }

    private static Func<ParticipantId, bool> CreateCandidateFilter(IEnumerable<GrainId> candidateGrains)
    {
        var candidates = candidateGrains.ToHashSet();
        return resource => resource.Reference is not null && candidates.Contains(resource.Reference.GrainId);
    }

    private static bool TryCreateTransition(
        TransactionDiagnosticEvents.TransactionDiagnosticEvent evt,
        TimeSpan elapsed,
        out RecoveryTransition transition)
    {
        var kind = evt switch
        {
            TransactionDiagnosticEvents.RemotePreparePersisted => RecoveryTransitionKind.RemotePreparePersisted,
            TransactionDiagnosticEvents.RemotePreparedSent => RecoveryTransitionKind.RemotePreparedSent,
            TransactionDiagnosticEvents.PrepareTimedOut => RecoveryTransitionKind.PrepareTimedOut,
            TransactionDiagnosticEvents.RemoteRecoveryPingSent => RecoveryTransitionKind.RemoteRecoveryPingSent,
            TransactionDiagnosticEvents.TransactionCancelCompleted => RecoveryTransitionKind.TransactionCancelCompleted,
            TransactionDiagnosticEvents.TransactionConfirmCompleted => RecoveryTransitionKind.TransactionConfirmCompleted,
            TransactionDiagnosticEvents.CancelSendStarted => RecoveryTransitionKind.CancelSendStarted,
            TransactionDiagnosticEvents.CancelSendCompleted => RecoveryTransitionKind.CancelSendCompleted,
            TransactionDiagnosticEvents.CancelSendFailed => RecoveryTransitionKind.CancelSendFailed,
            TransactionDiagnosticEvents.ReadyWaitStarted => RecoveryTransitionKind.ReadyWaitStarted,
            TransactionDiagnosticEvents.ReadyWaitCompleted => RecoveryTransitionKind.ReadyWaitCompleted,
            TransactionDiagnosticEvents.ReadyWaitFailed => RecoveryTransitionKind.ReadyWaitFailed,
            TransactionDiagnosticEvents.DeactivationRequested => RecoveryTransitionKind.DeactivationRequested,
            TransactionDiagnosticEvents.StorageConflictDetected => RecoveryTransitionKind.StorageConflict,
            TransactionDiagnosticEvents.AbortAndRestoreCompleted => RecoveryTransitionKind.AbortAndRestoreCompleted,
            TransactionDiagnosticEvents.QueueRestoreCompleted => RecoveryTransitionKind.QueueRestoreCompleted,
            TransactionDiagnosticEvents.QueueRestoreFailed => RecoveryTransitionKind.QueueRestoreFailed,
            TransactionDiagnosticEvents.LockBroken => RecoveryTransitionKind.LockBroken,
            _ => (RecoveryTransitionKind?)null,
        };

        if (kind is null)
        {
            transition = null!;
            return false;
        }

        var transactionIds = evt switch
        {
            TransactionDiagnosticEvents.TransactionEvent transactionEvent => ImmutableArray.Create(transactionEvent.TransactionId),
            TransactionDiagnosticEvents.LockBroken lockBroken => ImmutableArray.Create(lockBroken.TransactionId),
            TransactionDiagnosticEvents.StorageConflictDetected conflict => conflict.TransactionIds,
            TransactionDiagnosticEvents.AbortAndRestoreCompleted restored => restored.TransactionIds,
            TransactionDiagnosticEvents.QueueRestoreCompleted restored => restored.TransactionIds,
            TransactionDiagnosticEvents.QueueRestoreFailed failed => failed.TransactionIds,
            TransactionDiagnosticEvents.ReadyWaitEvent ready when ready.TransactionId is { } transactionId =>
                ImmutableArray.Create(transactionId),
            TransactionDiagnosticEvents.DeactivationRequested deactivation => deactivation.TransactionIds,
            _ => ImmutableArray<Guid>.Empty,
        };
        var status = evt switch
        {
            TransactionDiagnosticEvents.PrepareTimedOut timedOut => $"remaining={timedOut.RemainingCount}",
            TransactionDiagnosticEvents.TransactionCancelCompleted canceled =>
                $"{canceled.Status}, queueEntryFound={canceled.QueueEntryFound}, succeeded={canceled.Succeeded}",
            TransactionDiagnosticEvents.TransactionConfirmCompleted confirmed =>
                $"{confirmed.Status}, queueEntryFound={confirmed.QueueEntryFound}, succeeded={confirmed.Succeeded}",
            TransactionDiagnosticEvents.CancelSendStarted cancel =>
                $"{cancel.Status}, target={cancel.Target.Name}, isSelf={cancel.IsSelf}, reason={cancel.Reason}",
            TransactionDiagnosticEvents.CancelSendCompleted cancel =>
                $"{cancel.Status}, target={cancel.Target.Name}, isSelf={cancel.IsSelf}, reason={cancel.Reason}",
            TransactionDiagnosticEvents.CancelSendFailed cancel =>
                $"{cancel.Status}, target={cancel.Target.Name}, isSelf={cancel.IsSelf}, reason={cancel.Reason}, "
                + $"exception={cancel.ExceptionType}",
            TransactionDiagnosticEvents.ReadyWaitStarted => "started",
            TransactionDiagnosticEvents.ReadyWaitCompleted ready =>
                $"recoveredAfterFailure={ready.RecoveredAfterFailure}",
            TransactionDiagnosticEvents.ReadyWaitFailed ready => $"exception={ready.ExceptionType}",
            TransactionDiagnosticEvents.DeactivationRequested deactivation =>
                $"{deactivation.Status}, failureCount={deactivation.FailureCount}",
            TransactionDiagnosticEvents.StorageConflictDetected conflict =>
                $"operation={conflict.Operation}, storageOutcomeInDoubt={conflict.StorageOutcomeInDoubt}, "
                + $"queued={conflict.QueuedTransactionCount}, exception={conflict.ExceptionType}",
            TransactionDiagnosticEvents.AbortAndRestoreCompleted restored =>
                $"{restored.Status}, storageOutcomeInDoubt={restored.StorageOutcomeInDoubt}",
            TransactionDiagnosticEvents.QueueRestoreCompleted restored =>
                $"pending={restored.RecoveredPendingCount}, commits={restored.RecoveredCommitCount}",
            TransactionDiagnosticEvents.QueueRestoreFailed failed =>
                $"storageConflict={failed.StorageConflict}, exception={failed.ExceptionType}",
            TransactionDiagnosticEvents.LockBroken broken => broken.Reason.ToString(),
            _ => null,
        };

        transition = new(
            Sequence: 0,
            ObservedAtUtc: DateTime.UtcNow,
            elapsed,
            kind.Value,
            transactionIds,
            evt.Resource.Name,
            evt.Resource.Reference is null ? null : evt.Resource.Reference.GrainId,
            evt.SiloAddress,
            evt.ActivationId,
            status);
        return true;
    }

    private static string FormatTransactionIds(ImmutableArray<Guid> transactionIds)
        => transactionIds.IsDefaultOrEmpty ? "<none>" : $"[{string.Join(",", transactionIds)}]";

    private bool IsCurrentlyRelevant(RecoveryTransition transition)
        => this.relevantGrains is null
            || transition.GrainId is { } grainId && this.relevantGrains.Contains(grainId);

    private RecoveryTransition? FindTransitionAfter(long sequence)
        => this.timeline.FirstOrDefault(transition => transition.Sequence > sequence && this.IsCurrentlyRelevant(transition));

    private void RemoveWaiter(Waiter waiter)
    {
        lock (this.lockObj)
        {
            this.waiters.Remove(waiter);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
    }

    private sealed class Waiter(long afterSequence)
    {
        public long AfterSequence { get; } = afterSequence;
        public TaskCompletionSource<RecoveryTransition> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal enum RecoveryTransitionKind
    {
        RemotePreparePersisted,
        RemotePreparedSent,
        PrepareTimedOut,
        RemoteRecoveryPingSent,
        TransactionCancelCompleted,
        TransactionConfirmCompleted,
        CancelSendStarted,
        CancelSendCompleted,
        CancelSendFailed,
        ReadyWaitStarted,
        ReadyWaitCompleted,
        ReadyWaitFailed,
        DeactivationRequested,
        StorageConflict,
        AbortAndRestoreCompleted,
        QueueRestoreCompleted,
        QueueRestoreFailed,
        LockBroken,
    }

    internal sealed record RecoveryTransition(
        long Sequence,
        DateTime ObservedAtUtc,
        TimeSpan Elapsed,
        RecoveryTransitionKind Kind,
        ImmutableArray<Guid> TransactionIds,
        string ResourceName,
        GrainId? GrainId,
        SiloAddress? SiloAddress,
        ActivationId ActivationId,
        string? Status)
    {
        public Guid? TransactionId => this.TransactionIds.Length == 1 ? this.TransactionIds[0] : null;
    }
}
