using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace Orleans.Transactions.DeadlockDetection
{
    internal class DeadlockDetectionLockObserver :
        SystemTarget,
        ITransactionalLockObserver,
        ILocalDeadlockDetector,
        ILifecycleParticipant<ISiloLifecycle>
    {
        private readonly LockTracker lockTracker = new LockTracker();
        private readonly ConcurrentDictionary<long, LockInfo[]> snapshots = new();

        private readonly ILogger<DeadlockDetectionLockObserver> logger;

        private readonly IGrainFactory grainFactory;

        private readonly IDeadlockListener?[] deadlockListeners;

        private readonly TimeProvider timeProvider;
        private readonly DeadlockDetectionOptions options;

        internal static readonly GrainType GrainType = SystemTargetGrainId.CreateGrainType("txn.deadlock");

        public DeadlockDetectionLockObserver(
            SystemTargetShared shared,
            IGrainFactory grainFactory,
            IEnumerable<IDeadlockListener> deadlockListeners,
            TimeProvider timeProvider,
            IOptions<DeadlockDetectionOptions> options)
            : base(GrainType, shared)
        {
            this.logger = shared.LoggerFactory.CreateLogger<DeadlockDetectionLockObserver>();
            this.grainFactory = grainFactory;
            this.deadlockListeners = deadlockListeners.Cast<IDeadlockListener?>().ToArray();
            this.timeProvider = timeProvider;
            this.options = options.Value;
            shared.ActivationDirectory.RecordNewTarget(this);
        }

        public TimeSpan DetectionTimeout => this.options.DeadlockDetectionTimeout;

        public void OnResourceRequested(Guid transactionId, ParticipantId resourceId) =>
            this.lockTracker.TrackWait(resourceId, transactionId);

        public void OnResourceLocked(Guid transactionId, ParticipantId resourceId) =>
            this.lockTracker.TrackEnterLock(resourceId, transactionId);

        public void OnResourceUnlocked(Guid transactionId, ParticipantId resourceId) =>
            this.lockTracker.TrackExitLock(resourceId, transactionId);

        public async Task StartDeadlockDetection(ParticipantId resource, IEnumerable<Guid> lockedBy)
        {
            // Because we don't actually await this call (to avoid messing up transactional state on an error), we wrap it in
            // a try catch.
            try
            {
                var startTime = this.UtcNow;
                var (_, locks) = this.lockTracker.CaptureSnapshot();
                var localGraph = new WaitForGraph(locks).GetConnectedSubGraph(lockedBy, new[] { resource });
                if (localGraph.DetectCycles(out var cycles))
                {
                    var tasks = cycles.Select(async c =>
                    {
                        await c.BreakLocks();
                        this.NotifyDeadlockListeners(startTime, this.UtcNow, c);
                    });
                    await Task.WhenAll(tasks);
                }
                else
                {
                    await this.grainFactory.GetGrain<IDeadlockDetector>(0).CheckForDeadlocks(new CollectLocksResponse
                    {
                        Locks = localGraph.ToLockKeys(),
                        BatchId = null,
                        MaxVersion = null,
                        SiloAddress = this.Silo
                    });
                }
            }
            catch (Exception e)
            {
                this.logger.LogError(e, "deadlock detection threw an exception");
            }
        }

        private void NotifyDeadlockListeners(DateTime startTime, DateTime now, IList<LockInfo> locksInCycle)
        {
            for(var i=0; i < this.deadlockListeners.Length; i ++)
            {
                var listener = this.deadlockListeners[i];
                if (listener == null) continue;
                try
                {
                    listener.DeadlockDetected(locksInCycle, startTime, true, 0, now - startTime);
                }
                catch (Exception e)
                {
                    this.logger.LogError(e, $"Error while notifying local deadlock listener {listener}.  It will be removed");
                    this.deadlockListeners[i] = null; // Not sure about removing them, but seems safer for now
                }
            }
        }

        public async Task CollectLocks(CollectLocksRequest request)
        {
            long responseMaxVersion;
            LockInfo[] snapshot;
            if (request.MaxVersion == null)
            {
                (responseMaxVersion, snapshot) = this.lockTracker.CaptureSnapshot();
                if (!this.snapshots.TryAdd(responseMaxVersion, snapshot))
                {
                    throw new InvalidOperationException($"Duplicate deadlock snapshot version {responseMaxVersion}.");
                }

                this.ExpireSnapshot(responseMaxVersion).Ignore();
            }
            else
            {
                responseMaxVersion = request.MaxVersion.Value;
                if (!this.snapshots.TryGetValue(responseMaxVersion, out snapshot!))
                {
                    throw new InvalidOperationException($"Deadlock snapshot {responseMaxVersion} is no longer available.");
                }
            }

            var wfg = new WaitForGraph(snapshot).GetConnectedSubGraph(request.TransactionIds, Enumerable.Empty<ParticipantId>());

            await this.grainFactory.GetGrain<IDeadlockDetector>(0).CheckForDeadlocks(new CollectLocksResponse
            {
                BatchId = request.BatchId,
                Locks = wfg.ToLockKeys(),
                MaxVersion = responseMaxVersion,
                SiloAddress = this.Silo
            });
        }

        private async Task ExpireSnapshot(long version)
        {
            var retention = TimeSpan.FromTicks(
                this.options.DeadlockRequestTimeout.Ticks * (this.options.MaxDeadlockRequests + 1L));
            await Task.Delay(retention, this.timeProvider, CancellationToken.None);
            this.snapshots.TryRemove(version, out _);
        }

        private DateTime UtcNow => this.timeProvider.GetUtcNow().UtcDateTime;

        void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
        {
        }

    }
}