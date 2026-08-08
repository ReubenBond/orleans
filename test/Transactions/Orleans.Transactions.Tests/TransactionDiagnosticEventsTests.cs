using System.Collections.Concurrent;
using System.Collections.Immutable;
using Orleans.Storage;
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
        var transactionIds = ImmutableArray.Create(transactionId);
        var conflictException = new InconsistentStateException(
            "DynamoDB transactional state storage conflict.",
            storedEtag: "1",
            currentEtag: "2");
        var loadException = new InconsistentStateException(
            "Could not load a consistent DynamoDB transactional state snapshot.",
            storedEtag: "1",
            currentEtag: "2");
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
        TransactionDiagnosticEvents.EmitQueueRestoreStarted(resource, transactionIds);
        TransactionDiagnosticEvents.EmitQueueRestoreCompleted(resource, 42, 2, 3, transactionIds);
        TransactionDiagnosticEvents.EmitQueueRestoreFailed(
            resource,
            loadException,
            storageConflict: true,
            transactionIds);
        TransactionDiagnosticEvents.EmitStorageConflictDetected(
            resource,
            TransactionDiagnosticEvents.StorageOperation.Load,
            storageOutcomeInDoubt: false,
            queuedTransactionCount: transactionIds.Length,
            exception: loadException,
            transactionIds: transactionIds);
        TransactionDiagnosticEvents.EmitLockExpired(resource, transactionId, deadline, observedAt);
        TransactionDiagnosticEvents.EmitLockBroken(
            resource,
            transactionId,
            TransactionDiagnosticEvents.LockBreakReason.Expired);
        TransactionDiagnosticEvents.EmitStorageConflictDetected(
            resource,
            TransactionDiagnosticEvents.StorageOperation.Store,
            storageOutcomeInDoubt: true,
            queuedTransactionCount: 4,
            exception: conflictException,
            transactionIds: transactionIds);
        TransactionDiagnosticEvents.EmitAbortAndRestoreStarted(
            resource,
            TransactionalStatus.StorageConflict,
            storageOutcomeInDoubt: true,
            queuedTransactionCount: 4,
            transactionIds: transactionIds);
        TransactionDiagnosticEvents.EmitAbortAndRestoreCompleted(
            resource,
            TransactionalStatus.StorageConflict,
            storageOutcomeInDoubt: true,
            transactionIds: transactionIds);

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
        Assert.Equal(
            transactionIds,
            observer.Single<TransactionDiagnosticEvents.QueueRestoreStarted>(resource).TransactionIds);

        var restored = observer.Single<TransactionDiagnosticEvents.QueueRestoreCompleted>(resource);
        Assert.Equal(42, restored.CommittedSequence);
        Assert.Equal(2, restored.RecoveredPendingCount);
        Assert.Equal(3, restored.RecoveredCommitCount);
        Assert.Equal(transactionIds, restored.TransactionIds);

        var restoreFailed = observer.Single<TransactionDiagnosticEvents.QueueRestoreFailed>(resource);
        Assert.True(restoreFailed.StorageConflict);
        Assert.Equal(typeof(InconsistentStateException).FullName, restoreFailed.ExceptionType);
        Assert.Equal(loadException.Message, restoreFailed.ExceptionMessage);
        Assert.Equal(transactionIds, restoreFailed.TransactionIds);

        var expired = observer.Single<TransactionDiagnosticEvents.LockExpired>(resource);
        Assert.Equal(transactionId, expired.TransactionId);
        Assert.Equal(deadline, expired.Deadline);
        Assert.Equal(observedAt, expired.ObservedAt);

        var broken = observer.Single<TransactionDiagnosticEvents.LockBroken>(resource);
        Assert.Equal(TransactionDiagnosticEvents.LockBreakReason.Expired, broken.Reason);

        var conflicts = observer.All<TransactionDiagnosticEvents.StorageConflictDetected>(resource);
        var storeConflict = Assert.Single(
            conflicts,
            conflict => conflict.Operation == TransactionDiagnosticEvents.StorageOperation.Store);
        Assert.True(storeConflict.StorageOutcomeInDoubt);
        Assert.Equal(4, storeConflict.QueuedTransactionCount);
        Assert.Equal(conflictException.Message, storeConflict.ExceptionMessage);
        Assert.Equal(transactionIds, storeConflict.TransactionIds);

        var loadConflict = Assert.Single(
            conflicts,
            conflict => conflict.Operation == TransactionDiagnosticEvents.StorageOperation.Load);
        Assert.False(loadConflict.StorageOutcomeInDoubt);
        Assert.Equal(transactionIds.Length, loadConflict.QueuedTransactionCount);
        Assert.Equal(loadException.Message, loadConflict.ExceptionMessage);
        Assert.Equal(transactionIds, loadConflict.TransactionIds);

        var restoreStarted = observer.Single<TransactionDiagnosticEvents.AbortAndRestoreStarted>(resource);
        Assert.Equal(TransactionalStatus.StorageConflict, restoreStarted.Status);
        Assert.True(restoreStarted.StorageOutcomeInDoubt);
        Assert.Equal(4, restoreStarted.QueuedTransactionCount);
        Assert.Equal(transactionIds, restoreStarted.TransactionIds);

        var restoreCompleted = observer.Single<TransactionDiagnosticEvents.AbortAndRestoreCompleted>(resource);
        Assert.Equal(TransactionalStatus.StorageConflict, restoreCompleted.Status);
        Assert.True(restoreCompleted.StorageOutcomeInDoubt);
        Assert.Equal(transactionIds, restoreCompleted.TransactionIds);
    }

    [Fact]
    public void OnlyStorageWriteCompletedPropagatesObserverExceptions()
    {
        var resource = CreateParticipant("fault-target", ParticipantId.Role.Resource);
        using var subscription = TransactionDiagnosticEvents.AllEvents.Subscribe(new ThrowingObserver());

        TransactionDiagnosticEvents.EmitQueueRestoreStarted(resource, ImmutableArray<Guid>.Empty);

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

        public IEnumerable<T> All<T>(ParticipantId resource)
            where T : TransactionDiagnosticEvents.TransactionDiagnosticEvent
            => events.OfType<T>().Where(evt => evt.Resource.Name == resource.Name);
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
