using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Timers.Internal;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.Diagnostics;
using Orleans.Transactions.State;
using TestExtensions;
using Xunit;

namespace Orleans.Transactions.Tests;

[TestCategory("BVT"), TestCategory("Transactions")]
public class TransactionRecoveryLatencyTests
{
    [Fact]
    public void RestoredRemoteCommitUsesBoundedExponentialPingRetry()
    {
        var frequency = TimeSpan.FromSeconds(60);
        var sentAt = new DateTime(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);
        var record = new TransactionRecord<TestState>
        {
            Role = CommitRole.RemoteCommit,
            LastSent = DateTime.MinValue,
            IsRestoredRemoteCommit = true,
        };

        Assert.Equal(DateTime.MinValue, record.GetNextRemotePingAt(frequency));

        foreach (var expectedDelay in new[] { 1, 2, 4, 8, 16, 32, 60, 60 })
        {
            record.RecordRemotePingSent(sentAt);
            Assert.Equal(sentAt.AddSeconds(expectedDelay), record.GetNextRemotePingAt(frequency));
            sentAt = record.GetNextRemotePingAt(frequency);
        }
    }

    [Fact]
    public void FreshRemoteCommitRetainsFirstPingGrace()
    {
        var frequency = TransactionalStateOptions.DefaultRemoteTransactionPingFrequency;
        var sentAt = new DateTime(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);
        var record = new TransactionRecord<TestState>
        {
            Role = CommitRole.RemoteCommit,
            LastSent = sentAt,
        };

        Assert.Equal(sentAt.AddSeconds(60), record.GetNextRemotePingAt(frequency));

        record.RecordRemotePingSent(sentAt.Add(frequency));

        Assert.Equal(sentAt.AddSeconds(120), record.GetNextRemotePingAt(frequency));
    }

    [Fact]
    public async Task LocalAbortCompletesManagerDecisionBeforeSlowCancelFanOut()
    {
        var resource = CreateParticipant("manager", ParticipantId.Role.Manager);
        var remote = CreateParticipant("remote", ParticipantId.Role.Resource);
        var cancelGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activationId = ActivationId.NewId();
        var identity = new TransactionDiagnosticEvents.TransactionDiagnosticIdentity(null, activationId);
        var queue = new GatedCancelTransactionQueue(resource, cancelGate.Task, identity);
        var transactionId = Guid.NewGuid();
        var timeStamp = new DateTime(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);
        var promise = new TaskCompletionSource<TransactionalStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var record = new TransactionRecord<TestState>
        {
            Role = CommitRole.LocalCommit,
            TransactionId = transactionId,
            Timestamp = timeStamp,
            PromiseForTA = promise,
            WriteParticipants = [resource, remote],
        };
        var observer = new RecordingObserver(transactionId);
        using var subscription = TransactionDiagnosticEvents.AllEvents.Subscribe(observer);

        var notification = queue.NotifyOfAbort(record, TransactionalStatus.PrepareTimeout, exception: null);
        var status = await promise.Task;

        Assert.Equal(TransactionalStatus.PrepareTimeout, status);
        Assert.False(notification.IsCompleted);
        Assert.Collection(
            observer.Events,
            evt => Assert.IsType<TransactionDiagnosticEvents.TransactionManagerAbortDecisionCompleted>(evt),
            evt => Assert.IsType<TransactionDiagnosticEvents.CancelFanOutStarted>(evt),
            evt => Assert.IsType<TransactionDiagnosticEvents.CancelSendStarted>(evt));
        Assert.All(observer.Events, evt => Assert.Equal(activationId, evt.ActivationId));

        cancelGate.TrySetResult();
        await notification;

        Assert.Contains(observer.Events, evt => evt is TransactionDiagnosticEvents.CancelSendCompleted);
        Assert.IsType<TransactionDiagnosticEvents.CancelFanOutCompleted>(observer.Events.Last());
    }

    [Fact]
    public async Task ManagerAbortReturnsFromTransactionAgentBeforeOwnedCancelFanOutCompletes()
    {
        var manager = CreateParticipant(
            "manager",
            ParticipantId.Role.Manager | ParticipantId.Role.Resource);
        var remote = CreateParticipant("remote", ParticipantId.Role.Resource);
        var cancelGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new GatedCancelTransactionQueue(manager, cancelGate.Task, default);
        var protocol = new ManagerAbortProtocol(queue);
        var agent = new TransactionAgent(
            new TestClock(),
            NullLogger<TransactionAgent>.Instance,
            new TransactionAgentStatistics(),
            new NeverOverloaded(),
            protocol);
        var timeStamp = new DateTime(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);
        var transaction = new TransactionInfo(Guid.NewGuid(), timeStamp, timeStamp);
        transaction.RecordWrite(manager, timeStamp);
        transaction.RecordWrite(remote, timeStamp);

        var (status, exception) = await agent.Resolve(transaction);

        Assert.Equal(TransactionalStatus.PrepareTimeout, status);
        Assert.Null(exception);
        var managerFanOut = Assert.IsAssignableFrom<Task>(protocol.ManagerFanOutTask);
        Assert.False(managerFanOut.IsCompleted);
        Assert.Equal(1, queue.CancelSendCount);
        Assert.Equal(0, protocol.TransactionAgentCancelCount);

        cancelGate.TrySetResult();
        await managerFanOut;

        Assert.Equal(1, queue.CancelSendCount);
        Assert.Equal(0, protocol.TransactionAgentCancelCount);
    }

    [Fact]
    public async Task RepeatedRecoveryPingsRemainIdempotent()
    {
        var manager = CreateParticipant("manager", ParticipantId.Role.Manager);
        var remote = CreateParticipant("remote", ParticipantId.Role.Resource);
        var queue = new GatedCancelTransactionQueue(manager, Task.CompletedTask, default);
        var transactionId = Guid.NewGuid();
        var timeStamp = new DateTime(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);
        var observer = new RecordingObserver(transactionId);
        using var subscription = TransactionDiagnosticEvents.AllEvents.Subscribe(observer);

        await queue.NotifyOfPing(transactionId, timeStamp, remote);
        await queue.NotifyOfPing(transactionId, timeStamp, remote);

        Assert.Equal(2, observer.Events.OfType<TransactionDiagnosticEvents.CancelSendStarted>().Count());
        Assert.Equal(2, observer.Events.OfType<TransactionDiagnosticEvents.CancelSendCompleted>().Count());
        Assert.All(
            observer.Events.OfType<TransactionDiagnosticEvents.CancelSendEvent>(),
            evt =>
            {
                Assert.Equal(TransactionalStatus.PresumedAbort, evt.Status);
                Assert.Equal(TransactionDiagnosticEvents.CancelReason.RecoveryPing, evt.Reason);
            });
    }

    private static ParticipantId CreateParticipant(string name, ParticipantId.Role role) => new(name, null!, role);

    private sealed class TestState
    {
    }

    private sealed class GatedCancelTransactionQueue : TransactionQueue<TestState>
    {
        private readonly Task cancelGate;
        private int cancelSendCount;

        public int CancelSendCount => cancelSendCount;

        public GatedCancelTransactionQueue(
            ParticipantId resource,
            Task cancelGate,
            TransactionDiagnosticEvents.TransactionDiagnosticIdentity identity)
            : base(
                Options.Create(new TransactionalStateOptions()),
                resource,
                static () => { },
                null!,
                new TestClock(),
                NullLogger.Instance,
                null!,
                new TestActivationLifetime(),
                identity)
        {
            this.cancelGate = cancelGate;
        }

        protected override async Task SendCancel(
            ParticipantId target,
            Guid transactionId,
            DateTime timeStamp,
            TransactionalStatus status,
            TransactionDiagnosticEvents.CancelReason reason)
        {
            Interlocked.Increment(ref cancelSendCount);
            TransactionDiagnosticEvents.EmitCancelSendStarted(
                Resource,
                transactionId,
                timeStamp,
                target,
                isSelf: false,
                status,
                reason,
                DiagnosticIdentity);
            await cancelGate;
            TransactionDiagnosticEvents.EmitCancelSendCompleted(
                Resource,
                transactionId,
                timeStamp,
                target,
                isSelf: false,
                status,
                reason,
                DiagnosticIdentity);
        }
    }

    private sealed class ManagerAbortProtocol(GatedCancelTransactionQueue queue) : ITransactionAgentProtocol
    {
        public Task? ManagerFanOutTask { get; private set; }
        public int TransactionAgentCancelCount { get; private set; }

        public void Prepare(
            ParticipantId participant,
            Guid transactionId,
            AccessCounter accessCount,
            DateTime timeStamp,
            ParticipantId transactionManager)
        {
        }

        public Task<TransactionalStatus> PrepareAndCommit(
            ParticipantId transactionManager,
            Guid transactionId,
            AccessCounter accessCount,
            DateTime timeStamp,
            List<ParticipantId> writeResources,
            int totalParticipants)
        {
            var promise = new TaskCompletionSource<TransactionalStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            var record = new TransactionRecord<TestState>
            {
                Role = CommitRole.LocalCommit,
                TransactionId = transactionId,
                Timestamp = timeStamp,
                PromiseForTA = promise,
                WriteParticipants = writeResources,
            };

            ManagerFanOutTask = queue.NotifyOfAbort(record, TransactionalStatus.PrepareTimeout, exception: null);
            return promise.Task;
        }

        public Task Cancel(
            ParticipantId participant,
            Guid transactionId,
            DateTime timeStamp,
            TransactionalStatus status)
        {
            TransactionAgentCancelCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class NeverOverloaded : ITransactionOverloadDetector
    {
        public bool IsOverloaded() => false;
    }

    private sealed class TestClock : IClock
    {
        public DateTime UtcNow() => new(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);
    }

    private sealed class TestActivationLifetime : IActivationLifetime
    {
        public CancellationToken OnDeactivating => CancellationToken.None;

        public IDisposable BlockDeactivation() => NullDisposable.Instance;
    }

    private sealed class NullDisposable : IDisposable
    {
        public static NullDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }

    private sealed class RecordingObserver(Guid transactionId) : IObserver<TransactionDiagnosticEvents.TransactionDiagnosticEvent>
    {
        private readonly ConcurrentQueue<TransactionDiagnosticEvents.TransactionDiagnosticEvent> events = new();

        public IReadOnlyList<TransactionDiagnosticEvents.TransactionDiagnosticEvent> Events => events.ToArray();

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(TransactionDiagnosticEvents.TransactionDiagnosticEvent value)
        {
            if (value is TransactionDiagnosticEvents.TransactionEvent transactionEvent
                && transactionEvent.TransactionId == transactionId)
            {
                events.Enqueue(value);
            }
        }
    }
}
