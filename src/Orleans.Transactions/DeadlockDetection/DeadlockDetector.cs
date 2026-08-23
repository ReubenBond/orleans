using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Concurrency;
using Orleans.Runtime;
using Orleans.Timers.Internal;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions.DeadlockDetection
{

    [Reentrant]
    public class DeadlockDetector : Grain, IDeadlockDetector
    {

        private enum SiloStatus
        {
            BeforeRequest, WaitingForLocks, ReceivedLocks, Dead
        }

        private enum EndBatchReason
        {
            OutOfRequests, Stable
        }

        private class SiloInfo
        {
            public SiloAddress Address { get; }
            public SiloStatus Status { get; set; } = SiloStatus.BeforeRequest;
            public DateTime RequestDeadline { get; set; }

            public long? MaxVersion { get; set; }
            public LockInfo[] Snapshot { get; set; } = [];

            public SiloInfo(SiloAddress address)
            {
                this.Address = address;
            }
        }

        private class Batch
        {
            public Guid Id { get; }
            public Dictionary<SiloAddress, SiloInfo> SiloInfos { get; }

            public ISet<Guid> KnownTransactions { get; } = new HashSet<Guid>();
            public ISet<Guid> RequestedTransactions { get; } = new HashSet<Guid>();

            public int RequestCount { get; set; }

            public Batch(IEnumerable<SiloAddress> silos, DateTime analysisStartTime)
            {
                this.Id = Guid.NewGuid();
                this.AnalysisStartTime = analysisStartTime;
                this.SiloInfos = new Dictionary<SiloAddress, SiloInfo>();
                foreach (var address in silos)
                {
                    this.SiloInfos[address] = new SiloInfo(address);
                }
            }

            public DateTime AnalysisStartTime { get; }
        }

        private readonly ISiloStatusOracle siloStatusOracle;
        private readonly ILogger<DeadlockDetector> logger;
        private readonly IDictionary<Guid, Batch> batches =
            new Dictionary<Guid, Batch>();

        private readonly IInternalGrainFactory internalGrainFactory;
        private readonly IDeadlockListener?[] deadlockListeners;
        private readonly DeadlockDetectionOptions options;
        private readonly TimeProvider timeProvider;

        public DeadlockDetector(
            ILogger<DeadlockDetector> logger,
            IOptions<DeadlockDetectionOptions> options,
            ISiloStatusOracle siloStatusOracle,
            IServiceProvider serviceProvider,
            IEnumerable<IDeadlockListener> deadlockListeners,
            TimeProvider timeProvider)
        {
            this.logger = logger;
            this.options = options.Value;
            this.siloStatusOracle = siloStatusOracle;
            this.internalGrainFactory = serviceProvider.GetRequiredService<IInternalGrainFactory>();
            this.deadlockListeners = deadlockListeners.Cast<IDeadlockListener?>().ToArray();
            this.timeProvider = timeProvider;
        }

        public async Task CheckForDeadlocks(CollectLocksResponse message)
        {
            if (this.logger.IsEnabled(LogLevel.Trace))
                this.logger.LogTrace(
                    $"CheckForDeadlocks({message.BatchId},{message.SiloAddress},{message.MaxVersion})");

            Batch batch;
            if (message.BatchId == null)
            {
                if (this.TryCreateBatch(out var newBatch) && newBatch is not null)
                {
                    batch = newBatch;
                    this.batches.Add(batch.Id, batch);
                    this.AcceptInitiatingSnapshot(batch, message);
                    await this.AdvanceBatch(batch);
                }
                else
                {
                    if (this.logger.IsEnabled(LogLevel.Trace))
                    {
                        this.logger.LogTrace("Deadlock detection was rate limited");
                    }
                    return;
                }

                return;
            }
            else if(!this.batches.TryGetValue(message.BatchId.Value, out var existingBatch))
            {
                if (this.logger.IsEnabled(LogLevel.Trace))
                {
                    this.logger.LogTrace("received message for missing batch: {BatchId}", message.BatchId.Value);
                }
                return;
            }
            else
            {
                batch = existingBatch;
            }

            await this.UpdateBatch(batch, message);
        }

        private bool TryCreateBatch(out Batch? batch)
        {
            if (this.batches.Count >= this.options.MaxConcurrentDeadlockAnalysis)
            {
                batch = null;
                return false;
            }

            if (this.logger.IsEnabled(LogLevel.Trace))
            {
                this.logger.LogTrace("No existing batches found - starting a new one");
            }
            batch = new Batch(this.GetSiloAddresses(), this.UtcNow);
            return true;
        }

        private async Task UpdateBatch(Batch batch, CollectLocksResponse message)
        {
            if (!batch.SiloInfos.TryGetValue(message.SiloAddress, out var siloInfo))
            {
                this.logger.LogWarning(
                    "Got a collect locks request for a silo that didn't exist when detection started: {SiloAddress}",
                    message.SiloAddress);
                return;
            }

            if (siloInfo.Status == SiloStatus.Dead)
            {
                this.logger.LogWarning("Silo {SiloAddress} responded {MsLate}ms late",
                    message.SiloAddress, (this.UtcNow - siloInfo.RequestDeadline).TotalMilliseconds);
                return;
            }

            if (siloInfo.Status != SiloStatus.WaitingForLocks)
            {
                this.logger.LogWarning(
                    "Silo {SiloAddress} sent an unexpected deadlock snapshot while in state {Status}",
                    message.SiloAddress,
                    siloInfo.Status);
                return;
            }

            if (message.MaxVersion is not { } responseVersion)
            {
                this.logger.LogWarning("Silo {SiloAddress} returned a deadlock snapshot without a version", message.SiloAddress);
                return;
            }

            if (siloInfo.MaxVersion is { } expectedVersion && expectedVersion != responseVersion)
            {
                this.logger.LogWarning(
                    "Silo {SiloAddress} returned deadlock snapshot version {ResponseVersion}, expected {ExpectedVersion}",
                    message.SiloAddress,
                    responseVersion,
                    expectedVersion);
                return;
            }

            siloInfo.MaxVersion ??= responseVersion;
            siloInfo.Snapshot = [.. message.Locks];
            siloInfo.Status = SiloStatus.ReceivedLocks;
            AddKnownLocks(batch, message.Locks);
            await this.AdvanceBatch(batch);
        }

        private void AcceptInitiatingSnapshot(Batch batch, CollectLocksResponse message)
        {
            if (!batch.SiloInfos.TryGetValue(message.SiloAddress, out var siloInfo))
            {
                siloInfo = new SiloInfo(message.SiloAddress);
                batch.SiloInfos.Add(message.SiloAddress, siloInfo);
            }

            siloInfo.Snapshot = [.. message.Locks];
            AddKnownLocks(batch, message.Locks);
        }

        private static void AddKnownLocks(Batch batch, IEnumerable<LockInfo> locks)
        {
            foreach (var lockInfo in locks)
            {
                batch.KnownTransactions.Add(lockInfo.TxId);
            }
        }

        private async Task AdvanceBatch(Batch batch)
        {
            foreach (var silo in batch.SiloInfos.Values)
            {
                if (silo.Status == SiloStatus.BeforeRequest)
                {
                    this.StartRound(batch);
                    return;
                }
            }

            if (batch.SiloInfos.Values.Any(static silo => silo.Status == SiloStatus.WaitingForLocks))
            {
                return;
            }

            var graph = new WaitForGraph(
                batch.SiloInfos.Values
                    .Where(static silo => silo.Status != SiloStatus.Dead)
                    .SelectMany(static silo => silo.Snapshot));
            if (graph.DetectCycles(out var cycles))
            {
                await Task.WhenAll(cycles.Select(cycle => this.BreakLocks(batch, cycle)));
                return;
            }

            if (batch.RequestedTransactions.SetEquals(batch.KnownTransactions))
            {
                this.EndBatch(batch, EndBatchReason.Stable);
                return;
            }

            this.StartRound(batch);
        }

        private void StartRound(Batch batch)
        {
            if (++batch.RequestCount > this.options.MaxDeadlockRequests)
            {
                this.EndBatch(batch, EndBatchReason.OutOfRequests);
                return;
            }

            batch.RequestedTransactions.Clear();
            batch.RequestedTransactions.UnionWith(batch.KnownTransactions);
            var requestSent = false;
            foreach (var silo in batch.SiloInfos.Values)
            {
                if (silo.Status != SiloStatus.Dead)
                {
                    requestSent = true;
                    this.RequestLocksFromSilo(batch, silo, batch.RequestedTransactions);
                }
            }

            if (!requestSent)
            {
                this.EndBatch(batch, EndBatchReason.OutOfRequests);
            }
        }

        private Task BreakLocks(Batch batch, IEnumerable<LockInfo> cycle)
        {
            var locks = cycle.ToArray();
            this.NotifyDeadlockDetected(batch, locks);
            this.batches.Remove(batch.Id);
            return locks.BreakLocks();
        }

        private void EndBatch(Batch batch, EndBatchReason reason)
        {
            this.NotifyDetectionFailed(batch, reason);
            this.batches.Remove(batch.Id);
        }

        private void NotifyDeadlockDetected(Batch batch, IEnumerable<LockInfo> cycle) =>
            this.RunListeners(l => l.DeadlockDetected(cycle, batch.AnalysisStartTime, false, batch.RequestCount,
                this.UtcNow - batch.AnalysisStartTime));

        private void NotifyDetectionFailed(Batch batch, EndBatchReason reason) =>
            this.RunListeners(l => l.DeadlockNotDetected(batch.AnalysisStartTime, batch.RequestCount,
                this.UtcNow - batch.AnalysisStartTime, reason == EndBatchReason.Stable));

        private void RunListeners(Action<IDeadlockListener> action)
        {
            for (var i = 0; i < this.deadlockListeners.Length; i++)
            {
                var listener = this.deadlockListeners[i];
                if (listener == null) continue;
                try
                {
                    action(listener);
                }
                catch (Exception e)
                {
                    // TODO jjmason - I'm not sure about removing listeners like this.  We do have to be really careful
                    // about throwing exceptions from within the transaction infrastructure though.
                    this.logger.LogError(e, "Error notifying global deadlock listener {listener}, will be removed", listener);
                    this.deadlockListeners[i] = null;
                }
            }
        }

        private void RequestLocksFromSilo(Batch batch, SiloInfo silo, IEnumerable<Guid> transactions)
        {
            silo.Status = SiloStatus.WaitingForLocks;
            silo.RequestDeadline = this.UtcNow + this.options.DeadlockRequestTimeout;
            this.MonitorRequestTimeout(batch.Id, silo.Address, silo.RequestDeadline).Ignore();
            var lockObserver =
                this.internalGrainFactory.GetSystemTarget<ILocalDeadlockDetector>(
                    DeadlockDetectionLockObserver.GrainType, silo.Address);
            lockObserver.CollectLocks(new CollectLocksRequest
            {
                BatchId = batch.Id, MaxVersion = silo.MaxVersion, TransactionIds = transactions.ToList()
            }).Ignore();
        }

        private async Task MonitorRequestTimeout(Guid batchId, SiloAddress siloAddress, DateTime requestDeadline)
        {
            await Task.Delay(this.options.DeadlockRequestTimeout, this.timeProvider, CancellationToken.None);

            if (!this.batches.TryGetValue(batchId, out var batch)
                || !batch.SiloInfos.TryGetValue(siloAddress, out var silo)
                || silo.Status != SiloStatus.WaitingForLocks
                || silo.RequestDeadline != requestDeadline)
            {
                return;
            }

            silo.Status = SiloStatus.Dead;
            silo.Snapshot = [];
            await this.AdvanceBatch(batch);
        }

        private IEnumerable<SiloAddress> GetSiloAddresses() => this.siloStatusOracle.GetApproximateSiloStatuses(true).Keys;

        private DateTime UtcNow => this.timeProvider.GetUtcNow().UtcDateTime;
    }
}