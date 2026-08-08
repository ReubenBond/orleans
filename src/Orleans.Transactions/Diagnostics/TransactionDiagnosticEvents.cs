using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Orleans.Transactions.Diagnostics;

internal static class TransactionDiagnosticEvents
{
    internal const string ListenerName = "Orleans.Transactions";

    private static readonly DiagnosticListener Listener = new(ListenerName);

    internal static IObservable<TransactionDiagnosticEvent> AllEvents { get; } = new Observable();

    internal abstract class TransactionDiagnosticEvent(ParticipantId resource)
    {
        public readonly ParticipantId Resource = resource;
    }

    internal sealed class StorageWriteCompleted(
        ParticipantId resource,
        string? eTag,
        int batchSize,
        int commitCount) : TransactionDiagnosticEvent(resource)
    {
        public readonly string? ETag = eTag;
        public readonly int BatchSize = batchSize;
        public readonly int CommitCount = commitCount;
    }

    internal abstract class TransactionEvent(
        ParticipantId resource,
        Guid transactionId,
        DateTime timeStamp) : TransactionDiagnosticEvent(resource)
    {
        public readonly Guid TransactionId = transactionId;
        public readonly DateTime TimeStamp = timeStamp;
    }

    internal sealed class TransactionManagerWaitingForPrepared(
        ParticipantId resource,
        Guid transactionId,
        DateTime timeStamp,
        int waitCount,
        DateTime deadline) : TransactionEvent(resource, transactionId, timeStamp)
    {
        public readonly int WaitCount = waitCount;
        public readonly DateTime Deadline = deadline;
    }

    internal sealed class PreparedReceived(
        ParticipantId resource,
        Guid transactionId,
        DateTime timeStamp,
        ParticipantId participant,
        TransactionalStatus status,
        int? remainingCount) : TransactionEvent(resource, transactionId, timeStamp)
    {
        public readonly ParticipantId Participant = participant;
        public readonly TransactionalStatus Status = status;
        public readonly int? RemainingCount = remainingCount;
    }

    internal sealed class PrepareTimedOut(
        ParticipantId resource,
        Guid transactionId,
        DateTime timeStamp,
        int remainingCount,
        DateTime deadline) : TransactionEvent(resource, transactionId, timeStamp)
    {
        public readonly int RemainingCount = remainingCount;
        public readonly DateTime Deadline = deadline;
    }

    internal sealed class RemotePreparePersisted(
        ParticipantId resource,
        Guid transactionId,
        DateTime timeStamp,
        ParticipantId transactionManager) : TransactionEvent(resource, transactionId, timeStamp)
    {
        public readonly ParticipantId TransactionManager = transactionManager;
    }

    internal sealed class RemotePreparedSent(
        ParticipantId resource,
        Guid transactionId,
        DateTime timeStamp,
        ParticipantId transactionManager,
        DateTime sentAt) : TransactionEvent(resource, transactionId, timeStamp)
    {
        public readonly ParticipantId TransactionManager = transactionManager;
        public readonly DateTime SentAt = sentAt;
    }

    internal sealed class RemoteRecoveryPingScheduled(
        ParticipantId resource,
        Guid transactionId,
        DateTime timeStamp,
        ParticipantId transactionManager,
        DateTime scheduledAt) : TransactionEvent(resource, transactionId, timeStamp)
    {
        public readonly ParticipantId TransactionManager = transactionManager;
        public readonly DateTime ScheduledAt = scheduledAt;
    }

    internal sealed class RemoteRecoveryPingSent(
        ParticipantId resource,
        Guid transactionId,
        DateTime timeStamp,
        ParticipantId transactionManager,
        DateTime sentAt) : TransactionEvent(resource, transactionId, timeStamp)
    {
        public readonly ParticipantId TransactionManager = transactionManager;
        public readonly DateTime SentAt = sentAt;
    }

    internal sealed class QueueRestoreStarted(ParticipantId resource) : TransactionDiagnosticEvent(resource);

    internal sealed class QueueRestoreCompleted(
        ParticipantId resource,
        long committedSequence,
        int recoveredPendingCount,
        int recoveredCommitCount) : TransactionDiagnosticEvent(resource)
    {
        public readonly long CommittedSequence = committedSequence;
        public readonly int RecoveredPendingCount = recoveredPendingCount;
        public readonly int RecoveredCommitCount = recoveredCommitCount;
    }

    internal sealed class LockExpired(
        ParticipantId resource,
        Guid transactionId,
        DateTime deadline,
        DateTime observedAt) : TransactionDiagnosticEvent(resource)
    {
        public readonly Guid TransactionId = transactionId;
        public readonly DateTime Deadline = deadline;
        public readonly DateTime ObservedAt = observedAt;
    }

    internal enum LockBreakReason
    {
        Conflict,
        ValidationFailure,
        Expired,
        TransactionAbort,
        StorageRecovery,
    }

    internal sealed class LockBroken(
        ParticipantId resource,
        Guid transactionId,
        LockBreakReason reason) : TransactionDiagnosticEvent(resource)
    {
        public readonly Guid TransactionId = transactionId;
        public readonly LockBreakReason Reason = reason;
    }

    internal sealed class StorageConflictDetected(
        ParticipantId resource,
        bool storageOutcomeInDoubt,
        int queuedTransactionCount) : TransactionDiagnosticEvent(resource)
    {
        public readonly bool StorageOutcomeInDoubt = storageOutcomeInDoubt;
        public readonly int QueuedTransactionCount = queuedTransactionCount;
    }

    internal sealed class AbortAndRestoreStarted(
        ParticipantId resource,
        TransactionalStatus status,
        bool storageOutcomeInDoubt,
        int queuedTransactionCount) : TransactionDiagnosticEvent(resource)
    {
        public readonly TransactionalStatus Status = status;
        public readonly bool StorageOutcomeInDoubt = storageOutcomeInDoubt;
        public readonly int QueuedTransactionCount = queuedTransactionCount;
    }

    internal sealed class AbortAndRestoreCompleted(
        ParticipantId resource,
        TransactionalStatus status,
        bool storageOutcomeInDoubt) : TransactionDiagnosticEvent(resource)
    {
        public readonly TransactionalStatus Status = status;
        public readonly bool StorageOutcomeInDoubt = storageOutcomeInDoubt;
    }

    internal static void EmitStorageWriteCompleted(ParticipantId resource, string? eTag, int batchSize, int commitCount)
    {
        if (!Listener.IsEnabled(nameof(StorageWriteCompleted)))
        {
            return;
        }

        Emit(resource, eTag, batchSize, commitCount);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(ParticipantId resource, string? eTag, int batchSize, int commitCount)
        {
            // Observer exceptions intentionally propagate so tests can inject post-write faults.
            Listener.Write(nameof(StorageWriteCompleted), new StorageWriteCompleted(resource, eTag, batchSize, commitCount));
        }
    }

    internal static void EmitTransactionManagerWaitingForPrepared(
        ParticipantId resource,
        Guid transactionId,
        DateTime timeStamp,
        int waitCount,
        DateTime deadline)
    {
        if (Listener.IsEnabled(nameof(TransactionManagerWaitingForPrepared)))
        {
            Write(
                nameof(TransactionManagerWaitingForPrepared),
                new TransactionManagerWaitingForPrepared(resource, transactionId, timeStamp, waitCount, deadline));
        }
    }

    internal static void EmitPreparedReceived(
        ParticipantId resource,
        Guid transactionId,
        DateTime timeStamp,
        ParticipantId participant,
        TransactionalStatus status,
        int? remainingCount)
    {
        if (Listener.IsEnabled(nameof(PreparedReceived)))
        {
            Write(
                nameof(PreparedReceived),
                new PreparedReceived(resource, transactionId, timeStamp, participant, status, remainingCount));
        }
    }

    internal static void EmitPrepareTimedOut(
        ParticipantId resource,
        Guid transactionId,
        DateTime timeStamp,
        int remainingCount,
        DateTime deadline)
    {
        if (Listener.IsEnabled(nameof(PrepareTimedOut)))
        {
            Write(nameof(PrepareTimedOut), new PrepareTimedOut(resource, transactionId, timeStamp, remainingCount, deadline));
        }
    }

    internal static void EmitRemotePreparePersisted(
        ParticipantId resource,
        Guid transactionId,
        DateTime timeStamp,
        ParticipantId transactionManager)
    {
        if (Listener.IsEnabled(nameof(RemotePreparePersisted)))
        {
            Write(
                nameof(RemotePreparePersisted),
                new RemotePreparePersisted(resource, transactionId, timeStamp, transactionManager));
        }
    }

    internal static void EmitRemotePreparedSent(
        ParticipantId resource,
        Guid transactionId,
        DateTime timeStamp,
        ParticipantId transactionManager,
        DateTime sentAt)
    {
        if (Listener.IsEnabled(nameof(RemotePreparedSent)))
        {
            Write(
                nameof(RemotePreparedSent),
                new RemotePreparedSent(resource, transactionId, timeStamp, transactionManager, sentAt));
        }
    }

    internal static void EmitRemoteRecoveryPingScheduled(
        ParticipantId resource,
        Guid transactionId,
        DateTime timeStamp,
        ParticipantId transactionManager,
        DateTime scheduledAt)
    {
        if (Listener.IsEnabled(nameof(RemoteRecoveryPingScheduled)))
        {
            Write(
                nameof(RemoteRecoveryPingScheduled),
                new RemoteRecoveryPingScheduled(resource, transactionId, timeStamp, transactionManager, scheduledAt));
        }
    }

    internal static void EmitRemoteRecoveryPingSent(
        ParticipantId resource,
        Guid transactionId,
        DateTime timeStamp,
        ParticipantId transactionManager,
        DateTime sentAt)
    {
        if (Listener.IsEnabled(nameof(RemoteRecoveryPingSent)))
        {
            Write(
                nameof(RemoteRecoveryPingSent),
                new RemoteRecoveryPingSent(resource, transactionId, timeStamp, transactionManager, sentAt));
        }
    }

    internal static void EmitQueueRestoreStarted(ParticipantId resource)
    {
        if (Listener.IsEnabled(nameof(QueueRestoreStarted)))
        {
            Write(nameof(QueueRestoreStarted), new QueueRestoreStarted(resource));
        }
    }

    internal static void EmitQueueRestoreCompleted(
        ParticipantId resource,
        long committedSequence,
        int recoveredPendingCount,
        int recoveredCommitCount)
    {
        if (Listener.IsEnabled(nameof(QueueRestoreCompleted)))
        {
            Write(
                nameof(QueueRestoreCompleted),
                new QueueRestoreCompleted(resource, committedSequence, recoveredPendingCount, recoveredCommitCount));
        }
    }

    internal static void EmitLockExpired(
        ParticipantId resource,
        Guid transactionId,
        DateTime deadline,
        DateTime observedAt)
    {
        if (Listener.IsEnabled(nameof(LockExpired)))
        {
            Write(nameof(LockExpired), new LockExpired(resource, transactionId, deadline, observedAt));
        }
    }

    internal static void EmitLockBroken(ParticipantId resource, Guid transactionId, LockBreakReason reason)
    {
        if (Listener.IsEnabled(nameof(LockBroken)))
        {
            Write(nameof(LockBroken), new LockBroken(resource, transactionId, reason));
        }
    }

    internal static void EmitStorageConflictDetected(
        ParticipantId resource,
        bool storageOutcomeInDoubt,
        int queuedTransactionCount)
    {
        if (Listener.IsEnabled(nameof(StorageConflictDetected)))
        {
            Write(
                nameof(StorageConflictDetected),
                new StorageConflictDetected(resource, storageOutcomeInDoubt, queuedTransactionCount));
        }
    }

    internal static void EmitAbortAndRestoreStarted(
        ParticipantId resource,
        TransactionalStatus status,
        bool storageOutcomeInDoubt,
        int queuedTransactionCount)
    {
        if (Listener.IsEnabled(nameof(AbortAndRestoreStarted)))
        {
            Write(
                nameof(AbortAndRestoreStarted),
                new AbortAndRestoreStarted(resource, status, storageOutcomeInDoubt, queuedTransactionCount));
        }
    }

    internal static void EmitAbortAndRestoreCompleted(
        ParticipantId resource,
        TransactionalStatus status,
        bool storageOutcomeInDoubt)
    {
        if (Listener.IsEnabled(nameof(AbortAndRestoreCompleted)))
        {
            Write(
                nameof(AbortAndRestoreCompleted),
                new AbortAndRestoreCompleted(resource, status, storageOutcomeInDoubt));
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Write(string eventName, TransactionDiagnosticEvent evt)
    {
        try
        {
            Listener.Write(eventName, evt);
        }
        catch (Exception)
        {
            // Recovery diagnostics are observational. StorageWriteCompleted remains the sole fault-injection event.
        }
    }

    private sealed class Observable : IObservable<TransactionDiagnosticEvent>
    {
        public IDisposable Subscribe(IObserver<TransactionDiagnosticEvent> observer) => Listener.Subscribe(new Observer(observer));

        private sealed class Observer(IObserver<TransactionDiagnosticEvent> observer) : IObserver<KeyValuePair<string, object?>>
        {
            public void OnCompleted() => observer.OnCompleted();
            public void OnError(Exception error) => observer.OnError(error);

            public void OnNext(KeyValuePair<string, object?> value)
            {
                if (value.Value is TransactionDiagnosticEvent evt)
                {
                    observer.OnNext(evt);
                }
            }
        }
    }
}
