using System.Collections.Concurrent;
using Orleans.Transactions.Diagnostics;
using TestExtensions;
using Xunit;

namespace Orleans.Transactions.Tests;

[TestCategory("BVT"), TestCategory("Transactions")]
public class TransactionDiagnosticEventsTests
{
    [Fact]
    public void RecoveryEventsDeliverExpectedPayloads()
    {
        var resource = CreateParticipant("resource", ParticipantId.Role.Resource);
        var participant = CreateParticipant("participant", ParticipantId.Role.Resource);
        var manager = CreateParticipant("manager", ParticipantId.Role.Manager);
        var transactionId = Guid.NewGuid();
        var timeStamp = new DateTime(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);
        var deadline = timeStamp.AddSeconds(20);
        var observedAt = deadline.AddMilliseconds(25);
        var sentAt = timeStamp.AddSeconds(1);
        var scheduledAt = sentAt.AddSeconds(60);
        var observer = new RecordingObserver();

        using var subscription = TransactionDiagnosticEvents.AllEvents.Subscribe(observer);

        TransactionDiagnosticEvents.EmitTransactionManagerWaitingForPrepared(resource, transactionId, timeStamp, 2, deadline);
        TransactionDiagnosticEvents.EmitPreparedReceived(
            resource,
            transactionId,
            timeStamp,
            participant,
            TransactionalStatus.Ok,
            remainingCount: 1);
        TransactionDiagnosticEvents.EmitPrepareTimedOut(resource, transactionId, timeStamp, 1, deadline);
        TransactionDiagnosticEvents.EmitRemotePreparePersisted(resource, transactionId, timeStamp, manager);
        TransactionDiagnosticEvents.EmitRemotePreparedSent(resource, transactionId, timeStamp, manager, sentAt);
        TransactionDiagnosticEvents.EmitRemoteRecoveryPingScheduled(resource, transactionId, timeStamp, manager, scheduledAt);
        TransactionDiagnosticEvents.EmitRemoteRecoveryPingSent(resource, transactionId, timeStamp, manager, sentAt);
        TransactionDiagnosticEvents.EmitQueueRestoreStarted(resource);
        TransactionDiagnosticEvents.EmitQueueRestoreCompleted(resource, 42, 2, 3);
        TransactionDiagnosticEvents.EmitLockExpired(resource, transactionId, deadline, observedAt);
        TransactionDiagnosticEvents.EmitLockBroken(
            resource,
            transactionId,
            TransactionDiagnosticEvents.LockBreakReason.Expired);
        TransactionDiagnosticEvents.EmitStorageConflictDetected(resource, storageOutcomeInDoubt: true, 4);
        TransactionDiagnosticEvents.EmitAbortAndRestoreStarted(
            resource,
            TransactionalStatus.StorageConflict,
            storageOutcomeInDoubt: true,
            queuedTransactionCount: 4);
        TransactionDiagnosticEvents.EmitAbortAndRestoreCompleted(
            resource,
            TransactionalStatus.StorageConflict,
            storageOutcomeInDoubt: true);

        var waiting = observer.Single<TransactionDiagnosticEvents.TransactionManagerWaitingForPrepared>(resource);
        Assert.Equal(transactionId, waiting.TransactionId);
        Assert.Equal(timeStamp, waiting.TimeStamp);
        Assert.Equal(2, waiting.WaitCount);
        Assert.Equal(deadline, waiting.Deadline);

        var prepared = observer.Single<TransactionDiagnosticEvents.PreparedReceived>(resource);
        Assert.Equal(participant, prepared.Participant);
        Assert.Equal(TransactionalStatus.Ok, prepared.Status);
        Assert.Equal(1, prepared.RemainingCount);

        var timedOut = observer.Single<TransactionDiagnosticEvents.PrepareTimedOut>(resource);
        Assert.Equal(1, timedOut.RemainingCount);
        Assert.Equal(deadline, timedOut.Deadline);

        Assert.Equal(manager, observer.Single<TransactionDiagnosticEvents.RemotePreparePersisted>(resource).TransactionManager);
        Assert.Equal(sentAt, observer.Single<TransactionDiagnosticEvents.RemotePreparedSent>(resource).SentAt);
        Assert.Equal(scheduledAt, observer.Single<TransactionDiagnosticEvents.RemoteRecoveryPingScheduled>(resource).ScheduledAt);
        Assert.Equal(sentAt, observer.Single<TransactionDiagnosticEvents.RemoteRecoveryPingSent>(resource).SentAt);
        Assert.NotNull(observer.Single<TransactionDiagnosticEvents.QueueRestoreStarted>(resource));

        var restored = observer.Single<TransactionDiagnosticEvents.QueueRestoreCompleted>(resource);
        Assert.Equal(42, restored.CommittedSequence);
        Assert.Equal(2, restored.RecoveredPendingCount);
        Assert.Equal(3, restored.RecoveredCommitCount);

        var expired = observer.Single<TransactionDiagnosticEvents.LockExpired>(resource);
        Assert.Equal(transactionId, expired.TransactionId);
        Assert.Equal(deadline, expired.Deadline);
        Assert.Equal(observedAt, expired.ObservedAt);

        var broken = observer.Single<TransactionDiagnosticEvents.LockBroken>(resource);
        Assert.Equal(TransactionDiagnosticEvents.LockBreakReason.Expired, broken.Reason);

        var conflict = observer.Single<TransactionDiagnosticEvents.StorageConflictDetected>(resource);
        Assert.True(conflict.StorageOutcomeInDoubt);
        Assert.Equal(4, conflict.QueuedTransactionCount);

        var restoreStarted = observer.Single<TransactionDiagnosticEvents.AbortAndRestoreStarted>(resource);
        Assert.Equal(TransactionalStatus.StorageConflict, restoreStarted.Status);
        Assert.True(restoreStarted.StorageOutcomeInDoubt);
        Assert.Equal(4, restoreStarted.QueuedTransactionCount);

        var restoreCompleted = observer.Single<TransactionDiagnosticEvents.AbortAndRestoreCompleted>(resource);
        Assert.Equal(TransactionalStatus.StorageConflict, restoreCompleted.Status);
        Assert.True(restoreCompleted.StorageOutcomeInDoubt);
    }

    [Fact]
    public void OnlyStorageWriteCompletedPropagatesObserverExceptions()
    {
        var resource = CreateParticipant("fault-target", ParticipantId.Role.Resource);
        using var subscription = TransactionDiagnosticEvents.AllEvents.Subscribe(new ThrowingObserver());

        TransactionDiagnosticEvents.EmitQueueRestoreStarted(resource);

        Assert.Throws<InvalidOperationException>(
            () => TransactionDiagnosticEvents.EmitStorageWriteCompleted(resource, "etag", 1, 1));
    }

    private static ParticipantId CreateParticipant(string name, ParticipantId.Role role) => new(name, null!, role);

    private sealed class RecordingObserver : IObserver<TransactionDiagnosticEvents.TransactionDiagnosticEvent>
    {
        private readonly ConcurrentQueue<TransactionDiagnosticEvents.TransactionDiagnosticEvent> events = new();

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(TransactionDiagnosticEvents.TransactionDiagnosticEvent value) => events.Enqueue(value);

        public T Single<T>(ParticipantId resource)
            where T : TransactionDiagnosticEvents.TransactionDiagnosticEvent
            => Assert.Single(events.OfType<T>(), evt => evt.Resource.Name == resource.Name);
    }

    private sealed class ThrowingObserver : IObserver<TransactionDiagnosticEvents.TransactionDiagnosticEvent>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(TransactionDiagnosticEvents.TransactionDiagnosticEvent value)
            => throw new InvalidOperationException("Observer fault");
    }
}
