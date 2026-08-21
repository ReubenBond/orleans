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

            public ISet<ParticipantId> Grains { get; } = new HashSet<ParticipantId>();

            public SiloInfo(SiloAddress address)
            {
                this.Address = address;
            }
        }

        private class Batch
        {
            public Guid Id { get; }
            public Dictionary<SiloAddress, SiloInfo> SiloInfos { get; }

            public WaitForGraph? WaitForGraph { get; set; }
            public bool Changed { get; set; }
            public ISet<Guid> KnownTransactions { get; } = new HashSet<Guid>();
            public ISet<ParticipantId> KnownResources { get; } = new HashSet<ParticipantId>();
            public ISet<Guid> NewTransactions { get; } = new HashSet<Guid>();

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
                if (this.GetOrCreateBatch(message, out var candidateBatch, out var created) && candidateBatch is not null)
                {
                    batch = candidateBatch;
                    if (created)
                    {
                        this.batches[batch.Id] = batch;
                        await this.UpdateBatch(batch, message);
                    }
                    else
                    {
                        await this.MergeLocks(batch, message, siloInfo: null);
                    }
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

        private bool GetOrCreateBatch(CollectLocksResponse message, out Batch? batch, out bool created)
        {


            // look for a batch that has transactions/resources overlapping those in this message
            // before starting a new one
            var transactionsInMessage = new HashSet<Guid>(message.Locks.Select(l => l.TxId));
            var resourcesInMessage = new HashSet<ParticipantId>(message.Locks.Select(l => l.Resource));
            foreach (var existingBatch in this.batches.Values)
            {
                if (this.logger.IsEnabled(LogLevel.Trace))
                {
                    this.logger.LogTrace($"checking for overlap between messageTxs {string.Join(",", transactionsInMessage)}" +
                                         $" and batch tx {string.Join(",", existingBatch.KnownTransactions)}");
                }

                if (existingBatch.KnownTransactions.Overlaps(transactionsInMessage) ||
                    existingBatch.KnownResources.Overlaps(resourcesInMessage))
                {
                    if (this.logger.IsEnabled(LogLevel.Trace))
                    {
                        this.logger.LogTrace("joining an existing batch with overlapping transactions");
                    }
                    batch = existingBatch;
                    created = false;
                    return true;
                }
            }

            if (this.batches.Count >= this.options.MaxConcurrentDeadlockAnalysis)
            {
                batch = null;
                created = false;
                return false;
            }

            if (this.logger.IsEnabled(LogLevel.Trace))
            {
                this.logger.LogTrace("No existing batches found - starting a new one");
            }
            batch = new Batch(this.GetSiloAddresses(), this.UtcNow);
            created = true;
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

            if (siloInfo.Status == SiloStatus.ReceivedLocks)
            {
                this.logger.LogWarning("Silo {SiloAddress} sent locks twice", message.SiloAddress);
                return;
            }

            siloInfo.Status = SiloStatus.ReceivedLocks;

            if (message.MaxVersion != null)
            {
                siloInfo.MaxVersion = Math.Max(message.MaxVersion.Value, siloInfo.MaxVersion.GetValueOrDefault(0));
            }

            await this.MergeLocks(batch, message, siloInfo);
            if (!this.batches.ContainsKey(batch.Id))
            {
                return;
            }

            await this.AdvanceBatch(batch);
        }

        private async Task MergeLocks(Batch batch, CollectLocksResponse message, SiloInfo? siloInfo)
        {
            WaitForGraph updatedGraph;
            bool graphChanged;
            if (batch.WaitForGraph == null)
            {
                updatedGraph = new WaitForGraph(message.Locks);
                graphChanged = true;
            }
            else
            {
                graphChanged = batch.WaitForGraph.MergeWith(message.Locks, out updatedGraph);
            }

            if(graphChanged)
            {
                batch.WaitForGraph = updatedGraph;
                if (batch.WaitForGraph.DetectCycles(out var cycles))
                {
                    var tasks = cycles.Select(c => this.BreakLocks(batch, c));
                    await Task.WhenAll(tasks);
                    return;
                }
            }

            foreach (var lockInfo in message.Locks)
            {
                if (batch.KnownTransactions.Add(lockInfo.TxId))
                {
                    batch.NewTransactions.Add(lockInfo.TxId);
                }

                batch.KnownResources.Add(lockInfo.Resource);

                siloInfo?.Grains.Add(lockInfo.Resource);
            }

            return;
        }

        private Task AdvanceBatch(Batch batch)
        {
            var waitingForResponses = false;
            foreach (var silo in batch.SiloInfos.Values)
            {
                if (silo.Status == SiloStatus.BeforeRequest)
                {
                    this.RequestLocksFromSilo(batch, silo, batch.KnownTransactions);
                }

                waitingForResponses |= silo.Status == SiloStatus.WaitingForLocks;
            }

            if (waitingForResponses)
            {
                return Task.CompletedTask;
            }

            batch.RequestCount++;
            if (batch.RequestCount >= this.options.MaxDeadlockRequests)
            {
                this.EndBatch(batch, EndBatchReason.OutOfRequests);
                return Task.CompletedTask;
            }

            if (batch.NewTransactions.Count == 0)
            {
                this.EndBatch(batch, EndBatchReason.Stable);
                return Task.CompletedTask;
            }

            var newTransactions = batch.NewTransactions.ToArray();
            batch.NewTransactions.Clear();
            var requestedSilo = false;
            foreach (var silo in batch.SiloInfos.Values)
            {
                if (silo.Status != SiloStatus.Dead)
                {
                    requestedSilo = true;
                    this.RequestLocksFromSilo(batch, silo, newTransactions);
                }
            }

            if (!requestedSilo)
            {
                this.EndBatch(batch, EndBatchReason.OutOfRequests);
            }

            return Task.CompletedTask;
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
            await this.AdvanceBatch(batch);
        }

        private IEnumerable<SiloAddress> GetSiloAddresses() => this.siloStatusOracle.GetApproximateSiloStatuses(true).Keys;

        private DateTime UtcNow => this.timeProvider.GetUtcNow().UtcDateTime;
    }
}