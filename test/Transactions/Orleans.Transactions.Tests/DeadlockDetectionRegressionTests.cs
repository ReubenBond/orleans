using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.CodeGeneration;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Serialization.Invocation;
using Orleans.Storage;
using Orleans.Timers.Internal;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.DeadlockDetection;
using Orleans.Transactions.Diagnostics;
using Orleans.Transactions.State;
using TestExtensions;
using Xunit;

namespace Orleans.Transactions.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Transactions")]
[TestCategory("BVT"), TestCategory("Transactions")]
public class DeadlockDetectionRegressionTests
{
    [Fact]
    public async Task LockExpiryIsProcessedWhenDeadlockDetectionTimeoutExceedsLockTimeout()
    {
        var scheduler = new CountingTaskScheduler();
        await Task.Factory.StartNew(
            VerifyLockExpiryWithLaterDeadlockDeadline,
            CancellationToken.None,
            TaskCreationOptions.None,
            scheduler).Unwrap();

        Assert.True(
            scheduler.ScheduledTaskCount < 100,
            $"Lock expiry scheduled {scheduler.ScheduledTaskCount} work items, indicating a busy notification loop.");
    }

    private static async Task VerifyLockExpiryWithLaterDeadlockDeadline()
    {
        var resource = CreateParticipant("expiry-resource");
        var transactionId = Guid.NewGuid();
        var lockTimeout = TimeSpan.FromMilliseconds(50);
        var detectionTimeout = TimeSpan.FromSeconds(5);
        var lifetime = new TestActivationLifetime();
        var lockObserver = new RecordingLockObserver(detectionTimeout);
        var queue = CreateQueue(resource, lifetime, lockObserver, lockTimeout);
        var expired = new LockExpiredObserver(transactionId);
        using var subscription = TransactionDiagnosticEvents.AllEvents.Subscribe(expired);

        try
        {
            await queue.RWLock.EnterLock(
                transactionId,
                DateTime.UtcNow,
                default,
                isRead: false,
                exclusiveLock: false,
                static () => 0);
            var waitingOperation = queue.RWLock.EnterLock(
                Guid.NewGuid(),
                DateTime.UtcNow.AddTicks(1),
                default,
                isRead: false,
                exclusiveLock: false,
                static () => 1);
            Assert.False(waitingOperation.IsCompleted);

            TransactionDiagnosticEvents.LockExpired expiry;
            try
            {
                expiry = await expired.Event.Task.WaitAsync(
                    TimeSpan.FromMilliseconds(750),
                    TestContext.Current.CancellationToken);
            }
            catch (TimeoutException)
            {
                Assert.Fail(
                    $"Held lock {transactionId} on {resource.Name} did not expire within 750ms. "
                    + $"LockTimeout={lockTimeout}, DeadlockDetectionTimeout={detectionTimeout}.");
                return;
            }

            (TransactionalStatus Status, TransactionRecord<TestState>? State) validation = default;
            for (var attempt = 0; attempt < 50; attempt++)
            {
                validation = await queue.RWLock.ValidateLock(
                    transactionId,
                    new AccessCounter { Writes = 1 });
                if (validation.Status == TransactionalStatus.BrokenLock)
                {
                    break;
                }

                await Task.Delay(
                    TimeSpan.FromMilliseconds(10),
                    TestContext.Current.CancellationToken);
            }

            Assert.Equal(transactionId, expiry.TransactionId);
            Assert.Equal(resource, expiry.Resource);
            Assert.Equal(TransactionDiagnosticEvents.LockExpirationKind.HeldLock, expiry.Kind);
            Assert.True(expiry.ObservedAt >= expiry.Deadline);
            Assert.Equal(TransactionalStatus.BrokenLock, validation.Status);
            Assert.NotNull(validation.State);
            Assert.Equal(transactionId, validation.State.TransactionId);
            Assert.Equal(0, lockObserver.DetectionStartCount);
        }
        finally
        {
            lifetime.Cancel();
        }
    }

    [Fact]
    public async Task CycleFormedAfterInitialAcyclicScanIsDetected()
    {
        var silo = SiloAddress.New(IPAddress.Loopback, 11_111, 1);
        var (detector, localDetectors, listener) = CreateDetector(silo);
        var transactionOne = Guid.NewGuid();
        var transactionTwo = Guid.NewGuid();
        var resourceOne = CreateParticipant("cycle-resource-1");
        var resourceTwo = CreateParticipant("cycle-resource-2");
        var acyclicSnapshot = new[]
        {
            Lock(resourceOne, transactionOne),
            Wait(resourceTwo, transactionOne),
            Lock(resourceTwo, transactionTwo),
        };

        await detector.CheckForDeadlocks(Response(silo, batchId: null, acyclicSnapshot));

        Assert.Empty(listener.DetectedCycles);
        var request = Assert.Single(localDetectors[silo].Requests);
        Assert.NotEqual(Guid.Empty, request.BatchId);
        Assert.Equal(
            new[] { transactionOne, transactionTwo }.Order(),
            request.TransactionIds.Order());

        LockInfo[] cycleSnapshot =
        [
            .. acyclicSnapshot,
            Wait(resourceOne, transactionTwo),
        ];
        await detector.CheckForDeadlocks(Response(silo, request.BatchId, cycleSnapshot));

        var detected = Assert.Single(listener.DetectedCycles);
        AssertSameLocks(cycleSnapshot, detected);
        Assert.Equal(0, listener.NotDetectedCount);
    }

    [Fact]
    public async Task NewWaiterRearmsDetectionAfterAnAcyclicScan()
    {
        var resource = CreateParticipant("rearm-resource");
        var lifetime = new TestActivationLifetime();
        var lockObserver = new RecordingLockObserver(TimeSpan.FromMilliseconds(50));
        var queue = CreateQueue(
            resource,
            lifetime,
            lockObserver,
            lockTimeout: TimeSpan.FromSeconds(10));

        try
        {
            await queue.RWLock.EnterLock(
                Guid.NewGuid(),
                DateTime.UtcNow,
                default,
                isRead: true,
                exclusiveLock: false,
                static () => 1);
            _ = queue.RWLock.EnterLock(
                Guid.NewGuid(),
                DateTime.UtcNow.AddTicks(1),
                default,
                isRead: false,
                exclusiveLock: false,
                static () => 2);

            await lockObserver.WaitForDetectionCount(1);

            Assert.Equal(
                3,
                await queue.RWLock.EnterLock(
                    Guid.NewGuid(),
                    DateTime.UtcNow.AddTicks(2),
                    default,
                    isRead: true,
                    exclusiveLock: false,
                    static () => 3));

            await lockObserver.WaitForDetectionCount(2);
            Assert.Equal(2, lockObserver.DetectionStartCount);
        }
        finally
        {
            lifetime.Cancel();
        }
    }

    [Fact]
    public async Task CoherentPerSiloSnapshotReplacesStaleEdgesAfterLockHandoff()
    {
        var siloOne = SiloAddress.New(IPAddress.Loopback, 11_121, 1);
        var siloTwo = SiloAddress.New(IPAddress.Loopback, 11_122, 2);
        var (detector, localDetectors, listener) = CreateDetector(siloOne, siloTwo);
        var originalOwner = Guid.NewGuid();
        var newOwner = Guid.NewGuid();
        var resource = CreateParticipant("handoff-resource");

        await detector.CheckForDeadlocks(
            Response(siloOne, batchId: null, [Lock(resource, originalOwner)]));

        var firstOwnerRequest = Assert.Single(localDetectors[siloOne].Requests);
        var firstPeerRequest = Assert.Single(localDetectors[siloTwo].Requests);
        Assert.Equal([originalOwner], firstPeerRequest.TransactionIds);
        Assert.Equal(firstOwnerRequest.BatchId, firstPeerRequest.BatchId);

        var handedOffSnapshot = new[]
        {
            Lock(resource, newOwner),
            Wait(resource, originalOwner),
        };
        var coherentGraph = new WaitForGraph(handedOffSnapshot);
        Assert.False(coherentGraph.DetectCycles(out var coherentCycles));
        Assert.Empty(coherentCycles);

        await detector.CheckForDeadlocks(
            Response(siloOne, firstOwnerRequest.BatchId, handedOffSnapshot));
        await detector.CheckForDeadlocks(
            Response(siloTwo, firstPeerRequest.BatchId, []));

        Assert.Equal(2, localDetectors[siloOne].Requests.Count);
        Assert.Equal(2, localDetectors[siloTwo].Requests.Count);
        var refreshBatchId = localDetectors[siloOne].Requests[1].BatchId;
        Assert.Equal(firstPeerRequest.BatchId, refreshBatchId);
        Assert.Equal(refreshBatchId, localDetectors[siloTwo].Requests[1].BatchId);
        Assert.Equal(1, localDetectors[siloOne].Requests[1].MaxVersion);
        Assert.Equal(1, localDetectors[siloTwo].Requests[1].MaxVersion);
        Assert.Equal(
            new[] { originalOwner, newOwner }.Order(),
            localDetectors[siloOne].Requests[1].TransactionIds.Order());

        await detector.CheckForDeadlocks(Response(siloOne, refreshBatchId, handedOffSnapshot));
        await detector.CheckForDeadlocks(Response(siloTwo, refreshBatchId, []));

        Assert.Empty(listener.DetectedCycles);
        Assert.Equal(1, listener.NotDetectedCount);
    }

    [Fact]
    public async Task DuplicateDelayedDeadlockBreakDoesNotAbortSubsequentlyPromotedGroup()
    {
        var resource = CreateParticipant("break-resource");
        var firstTransaction = Guid.NewGuid();
        var promotedTransaction = Guid.NewGuid();
        var lifetime = new TestActivationLifetime();
        var lockObserver = new RecordingLockObserver(TimeSpan.FromSeconds(10));
        var queue = CreateQueue(
            resource,
            lifetime,
            lockObserver,
            lockTimeout: TimeSpan.FromSeconds(10));
        var transactionalResource = new TransactionalResource<TestState>(queue);

        try
        {
            await queue.RWLock.EnterLock(
                firstTransaction,
                DateTime.UtcNow,
                default,
                isRead: false,
                exclusiveLock: false,
                static () => 1);
            var promotedOperation = queue.RWLock.EnterLock(
                promotedTransaction,
                DateTime.UtcNow.AddTicks(1),
                default,
                isRead: false,
                exclusiveLock: false,
                static () => 2);

            Assert.False(promotedOperation.IsCompleted);

            await transactionalResource.BreakLocks([firstTransaction]);
            Assert.Equal(
                2,
                await promotedOperation.WaitAsync(
                    TimeSpan.FromSeconds(1),
                    TestContext.Current.CancellationToken));

            var beforeDuplicate = await queue.RWLock.ValidateLock(
                promotedTransaction,
                new AccessCounter { Writes = 1 });
            Assert.Equal(TransactionalStatus.Ok, beforeDuplicate.Status);
            Assert.Contains(promotedTransaction, lockObserver.LockedTransactions);

            await transactionalResource.BreakLocks([firstTransaction]);

            var afterDuplicate = await queue.RWLock.ValidateLock(
                promotedTransaction,
                new AccessCounter { Writes = 1 });
            Assert.Equal(TransactionalStatus.Ok, afterDuplicate.Status);
            Assert.Equal(promotedTransaction, afterDuplicate.State.TransactionId);
            Assert.DoesNotContain(promotedTransaction, lockObserver.UnlockedTransactions);
            Assert.Single(lockObserver.UnlockedTransactions);
            Assert.Contains(firstTransaction, lockObserver.UnlockedTransactions);
        }
        finally
        {
            lifetime.Cancel();
        }
    }

    private static (
        DeadlockDetector Detector,
        IReadOnlyDictionary<SiloAddress, RecordingLocalDeadlockDetector> LocalDetectors,
        RecordingDeadlockListener Listener)
        CreateDetector(params SiloAddress[] silos)
    {
        var statusOracle = Substitute.For<ISiloStatusOracle>();
        statusOracle.GetApproximateSiloStatuses(Arg.Any<bool>())
            .Returns(silos.ToDictionary(address => address, _ => SiloStatus.Active));

        var internalFactoryType = Assembly.Load("Orleans.Core")
            .GetType("Orleans.IInternalGrainFactory", throwOnError: true)!;
        var internalGrainFactory = Substitute.For([internalFactoryType], []);
        var getSystemTarget = internalFactoryType
            .GetMethods()
            .Single(method =>
                method.Name == "GetSystemTarget"
                && method.IsGenericMethodDefinition
                && method.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(ILocalDeadlockDetector));
        var localDetectors = new Dictionary<SiloAddress, RecordingLocalDeadlockDetector>();
        foreach (var address in silos)
        {
            var recorder = new RecordingLocalDeadlockDetector();
            var localProxy = (ILocalDeadlockDetector)getSystemTarget.Invoke(
                internalGrainFactory,
                [DeadlockDetectionLockObserver.GrainType, address])!;
            localProxy
                .CollectLocks(Arg.Do<CollectLocksRequest>(recorder.Record))
                .Returns(Task.CompletedTask);
            localDetectors.Add(address, recorder);
        }
        var services = new SingleServiceProvider(internalFactoryType, internalGrainFactory);
        var listener = new RecordingDeadlockListener();
        var detector = new DeadlockDetector(
            NullLogger<DeadlockDetector>.Instance,
            Options.Create(new DeadlockDetectionOptions
            {
                DeadlockRequestTimeout = TimeSpan.FromSeconds(10),
                MaxConcurrentDeadlockAnalysis = 3,
                MaxDeadlockRequests = 5,
            }),
            statusOracle,
            services,
            [listener],
            TimeProvider.System);
        return (detector, localDetectors, listener);
    }

    private static TransactionQueue<TestState> CreateQueue(
        ParticipantId resource,
        IActivationLifetime lifetime,
        ITransactionalLockObserver lockObserver,
        TimeSpan lockTimeout)
        => new(
            Options.Create(new TransactionalStateOptions
            {
                LockTimeout = lockTimeout,
                LockAcquireTimeout = TimeSpan.FromSeconds(10),
            }),
            resource,
            static () => { },
            null!,
            new TestClock(),
            NullLogger.Instance,
            null!,
            lifetime,
            default,
            lockObserver);

    private static CollectLocksResponse Response(
        SiloAddress silo,
        Guid? batchId,
        IList<LockInfo> locks)
        => new()
        {
            SiloAddress = silo,
            BatchId = batchId,
            Locks = locks,
            MaxVersion = 1,
        };

    private static LockInfo Lock(ParticipantId resource, Guid transaction)
        => LockInfo.ForLock(resource, transaction);

    private static LockInfo Wait(ParticipantId resource, Guid transaction)
        => LockInfo.ForWait(resource, transaction);

    private static ParticipantId CreateParticipant(string name)
    {
        var grainId = GrainId.Create(GrainType.Create("deadlock-test"), IdSpan.Create(name));
        return new ParticipantId(
            name,
            new TestGrainReference(grainId),
            ParticipantId.Role.Resource);
    }

    private static void AssertSameLocks(IEnumerable<LockInfo> expected, IEnumerable<LockInfo> actual)
    {
        var expectedSet = new HashSet<LockInfo>(expected, LockInfo.EqualityComparer);
        var actualSet = new HashSet<LockInfo>(actual, LockInfo.EqualityComparer);
        Assert.True(
            expectedSet.SetEquals(actualSet),
            $"Expected locks [{string.Join(", ", expectedSet)}], actual [{string.Join(", ", actualSet)}].");
    }

    private sealed class TestState;

    private sealed class CountingTaskScheduler : TaskScheduler
    {
        private int scheduledTaskCount;

        public int ScheduledTaskCount => Volatile.Read(ref scheduledTaskCount);

        protected override IEnumerable<Task> GetScheduledTasks() => [];

        protected override void QueueTask(Task task)
        {
            Interlocked.Increment(ref scheduledTaskCount);
            ThreadPool.QueueUserWorkItem(
                static state =>
                {
                    var (scheduler, queuedTask) = ((CountingTaskScheduler, Task))state!;
                    scheduler.TryExecuteTask(queuedTask);
                },
                (this, task));
        }

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
    }

    private sealed class SingleServiceProvider(Type serviceType, object service) : IServiceProvider
    {
        public object? GetService(Type requestedType)
            => requestedType == serviceType ? service : null;
    }

    private sealed class RecordingLocalDeadlockDetector : ILocalDeadlockDetector
    {
        public List<CollectLocksRequest> Requests { get; } = [];

        public void Record(CollectLocksRequest request) => Requests.Add(request);

        public Task CollectLocks(CollectLocksRequest request) => Task.CompletedTask;
    }

    private sealed class RecordingDeadlockListener : IDeadlockListener
    {
        public List<LockInfo[]> DetectedCycles { get; } = [];
        public int NotDetectedCount { get; private set; }

        public void DeadlockDetected(
            IEnumerable<LockInfo> locks,
            DateTime analysisStartedAt,
            bool detectedLocally,
            int requestsToDetection,
            TimeSpan analysisDuration)
            => DetectedCycles.Add(locks.ToArray());

        public void DeadlockNotDetected(
            DateTime analysisStartedAt,
            int requestsMade,
            TimeSpan analysisDuration,
            bool isDefinite)
            => NotDetectedCount++;
    }

    private sealed class RecordingLockObserver(TimeSpan detectionTimeout) : ITransactionalLockObserver
    {
        private readonly ConcurrentQueue<Guid> lockedTransactions = new();
        private readonly ConcurrentQueue<Guid> unlockedTransactions = new();
        private readonly ConcurrentQueue<TaskCompletionSource> detectionWaiters = new();
        private int detectionStartCount;

        public TimeSpan DetectionTimeout { get; } = detectionTimeout;
        public int DetectionStartCount => Volatile.Read(ref detectionStartCount);
        public IReadOnlyList<Guid> LockedTransactions => lockedTransactions.ToArray();
        public IReadOnlyList<Guid> UnlockedTransactions => unlockedTransactions.ToArray();

        public void OnResourceRequested(Guid transactionId, ParticipantId resourceId)
        {
        }

        public void OnResourceLocked(Guid transactionId, ParticipantId resourceId)
            => lockedTransactions.Enqueue(transactionId);

        public void OnResourceUnlocked(Guid transactionId, ParticipantId resourceId)
            => unlockedTransactions.Enqueue(transactionId);

        public Task StartDeadlockDetection(
            ParticipantId lockedResource,
            IEnumerable<Guid> lockedByTransactions)
        {
            Interlocked.Increment(ref detectionStartCount);
            while (detectionWaiters.TryDequeue(out var waiter))
            {
                waiter.TrySetResult();
            }

            return Task.CompletedTask;
        }

        public async Task WaitForDetectionCount(int expected)
        {
            while (this.DetectionStartCount < expected)
            {
                var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                this.detectionWaiters.Enqueue(waiter);
                if (this.DetectionStartCount >= expected)
                {
                    waiter.TrySetResult();
                }

                await waiter.Task.WaitAsync(
                    TimeSpan.FromSeconds(1),
                    TestContext.Current.CancellationToken);
            }
        }
    }

    private sealed class LockExpiredObserver(Guid transactionId)
        : IObserver<TransactionDiagnosticEvents.TransactionDiagnosticEvent>
    {
        public TaskCompletionSource<TransactionDiagnosticEvents.LockExpired> Event { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void OnCompleted()
        {
        }

        public void OnError(Exception error) => Event.TrySetException(error);

        public void OnNext(TransactionDiagnosticEvents.TransactionDiagnosticEvent value)
        {
            if (value is TransactionDiagnosticEvents.LockExpired expired
                && expired.TransactionId == transactionId)
            {
                Event.TrySetResult(expired);
            }
        }
    }

    private sealed class TestActivationLifetime : IActivationLifetime
    {
        private readonly CancellationTokenSource cancellation = new();

        public CancellationToken OnDeactivating => cancellation.Token;

        public IDisposable BlockDeactivation() => NullDisposable.Instance;

        public void Cancel() => cancellation.Cancel();
    }

    private sealed class TestClock : IClock
    {
        public DateTime UtcNow() => DateTime.UtcNow;
    }

    private sealed class TestGrainReference(GrainId grainId)
        : GrainReference(
            new GrainReferenceShared(
                grainId.Type,
                default,
                interfaceVersion: 0,
                runtime: TestGrainReferenceRuntime.Instance,
                invokeMethodOptions: default,
                codecProvider: null!,
                copyContextPool: null!,
                serviceProvider: null!),
            grainId.Key);

    private sealed class TestGrainReferenceRuntime : IGrainReferenceRuntime
    {
        public static TestGrainReferenceRuntime Instance { get; } = new();

        public object Cast(IAddressable grain, Type interfaceType)
        {
            Assert.Equal(typeof(IDeadlockResourceExtension), interfaceType);
            return NoOpDeadlockResourceExtension.Instance;
        }

        public ValueTask<T?> InvokeMethodAsync<T>(
            GrainReference reference,
            IInvokable request,
            InvokeMethodOptions options)
            => throw new NotSupportedException();

        public ValueTask InvokeMethodAsync(
            GrainReference reference,
            IInvokable request,
            InvokeMethodOptions options)
            => throw new NotSupportedException();

        public void InvokeMethod(
            GrainReference reference,
            IInvokable request,
            InvokeMethodOptions options)
            => throw new NotSupportedException();
    }

    private sealed class NoOpDeadlockResourceExtension : IDeadlockResourceExtension
    {
        public static NoOpDeadlockResourceExtension Instance { get; } = new();

        public Task BreakLocks(string resourceId, List<Guid> expectedTransactions) => Task.CompletedTask;
    }

    private sealed class NullDisposable : IDisposable
    {
        public static NullDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
