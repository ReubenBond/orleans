using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using AsyncEx = Nito.AsyncEx;

namespace Orleans.MetadataStore
{
    [GenerateSerializer]
    public enum ReplicationStatus
    {
        Failed,
        Uncertain,
        Success
    }

    public delegate TValue ChangeFunction<in TArg, TValue>(TValue existingValue, TArg newValue);

    [GenerateSerializer]
    public enum ConfigOperation
    {
        Read,
        Update,
    }

    public abstract class Proposer<TOperation, TValue> : Proposer<TOperation, TValue>.ITestAccessor
    {
        private readonly ILogger _log;
        private readonly IAcceptorRouter<TValue> _acceptors;
        private readonly Guid _serverId;
        private Ballot? _prepared;
        private TValue _cachedValue;

        public Proposer(ILocalConfiguration localConfiguration, Guid serverId, ILogger log, IAcceptorRouter<TValue> acceptors)
        {
            LocalConfiguration = localConfiguration;
            _serverId = serverId;
            NextBallot = new Ballot(0, serverId);
            _log = log;
            _acceptors = acceptors;
        }

        protected ILocalConfiguration LocalConfiguration { get; }

        internal Ballot NextBallot { get; set; }

        Ballot ITestAccessor.Ballot { get => NextBallot; set => NextBallot = value; }
        Ballot? ITestAccessor.Prepared { get => _prepared; set => _prepared = value; }
        TValue ITestAccessor.CachedValue { get => _cachedValue; set => _cachedValue = value; }

        protected abstract TValue ApplyOperation(TOperation operation, TValue current);

        protected abstract void OnCommittedValue(TValue committed);

        protected async ValueTask<(ReplicationStatus Status, TValue Value)> TryCommit(
            TOperation operation,
            CancellationToken cancellationToken,
            int numRetries)
        {
            var state = CommitState.Create(operation, cancellationToken, numRetries);
            state.AcceptPreparesSuccessor = true;
            do
            {
                (var status, state) = await TryCommitInternal(state);
                if (status is ReplicationStatus.Success)
                {
                    return (status, state.CurrentValue);
                }
            } while (state.RemainingRetries >= 0);

            return (ReplicationStatus.Failed, state.CurrentValue);
        }

        private async Task<(ReplicationStatus Status, CommitState state)> TryCommitInternal(CommitState state)
        {
            --state.RemainingRetries;

            if (state.Stage is CommitStage.Prepare)
            {
                // Configuration is observed once per attempt.
                // If this server's configuration changes while this proposer is still attempting to commit a value, the commit will
                // continue under the old configuration. If that configuration has already been observed by some of the acceptors,
                // the commit may fail and in that case the proposer may retry.
                state.Configuration = LocalConfiguration.CommittedConfiguration;

                // Select a ballot number for this attempt. The ballot must be consistent between propose and accept for the attempt.
                state.AttemptBallot = NextBallot = state.PrepareFastRound switch
                {
                    true => NextBallot.FastRoundSuccessor(),
                    _ => NextBallot.Successor(_serverId),
                };

                if (_prepared.HasValue && _prepared.Value == state.AttemptBallot)
                {
                    // If this node is leader, attempt to skip the prepare phase and at go straight to another accept
                    // phase, assuming that the value has not changed since this proposer last had a value accepted.
                    state.CurrentValue = _cachedValue;

                    LogSkippingPrepare(state.CurrentValue);
                }
                else
                {
                    // Try to obtain a quorum of promises from the acceptors and simultaneously learn the currently accepted value.
                    bool prepareSuccess;
                    (prepareSuccess, state.CurrentValue) = await TryPrepare(state.AttemptBallot, state.Configuration, state.CancellationToken);
                    _cachedValue = state.CurrentValue;
                    if (!prepareSuccess)
                    {
                        if (NextBallot > state.AttemptBallot)
                        {
                            // A conflict was encountered, so revert back to a regular
                            state.PrepareFastRound = false;
                        }

                        // Allow the proposer to retry in order to hide harmless fast-forward events.
                        if (state.RemainingRetries > 0)
                        {
                            LogPrepareFailed();
                            return (ReplicationStatus.Failed, state);
                        }

                        LogPrepareFailedFinal();
                        return (ReplicationStatus.Failed, state);
                    }

                    _prepared = state.AttemptBallot;
                    LogPrepareSuccess(state.CurrentValue);
                }

                state.Stage = CommitStage.Accept;
            }

            if (state.Stage is CommitStage.Accept)
            {
                // Modify the currently accepted value and attempt to have it accepted on all acceptors.
                var newValue = ApplyOperation(state.Operation, state.CurrentValue);
                LogAcceptStarted(newValue);

                var acceptSuccess = await TryAccept(
                    state.AttemptBallot,
                    newValue,
                    state.Configuration,
                    new AcceptOptions { PrepareSuccessor = state.AcceptPreparesSuccessor },
                    state.CancellationToken);

                if (acceptSuccess)
                {
                    // The accept succeeded, this proposer can attempt to use the current accept as a promise for a subsequent accept as an optimization.
                    LogAcceptSucceded(newValue);

                    _prepared = state.AcceptPreparesSuccessor switch
                    {
                        true => state.AttemptBallot.Successor(state.AttemptBallot.Proposer),
                        _ => state.AttemptBallot
                    };

                    _cachedValue = newValue;
                    state.CurrentValue = newValue;
                    OnCommittedValue(newValue);
                    return (ReplicationStatus.Success, state);
                }

                // Since the accept did not succeed, this proposer should issue a prepare before trying to have its next value accepted.
                state.Stage = CommitStage.Prepare;

                if (state.RemainingRetries > 0)
                {
                    // This attempt may have failed because another proposer interfered, so attempt again to have this value accepted.
                    LogAcceptFailed();

                    return (ReplicationStatus.Uncertain, state);
                }

                // It is possible that the value was committed successfully without this node receiving a quorum of acknowledgements,
                // so the result is uncertain.
                // For example, an acceptor's acknowledgement message may have been lost in transmission due to a transient network fault.
                LogAcceptFailedFinal();

                return (ReplicationStatus.Uncertain, state);
            }
            else
            {
                throw new InvalidOperationException($"Unexpected stage {state.Stage}");
            }
        }

        private async Task<(bool, TValue)> TryPrepare(Ballot prepareBallot, ClusterConfiguration config, CancellationToken cancellationToken)
        {
            if (config.Members is null)
            {
                return (false, default);
            }

            var prepareTasks = new List<Task<PrepareResponse<TValue>>>(config.Members.Length);
            foreach (var server in config.Members)
            {
                var prepareTask = _acceptors.Prepare(server, config.Stamp, prepareBallot).AsTask();
                prepareTasks.Add(prepareTask);
            }

            // Run a Prepare round in order to learn the current value of the register and secure a promise that a quorum
            // of nodes which accept our new value.
            var requiredConfirmations = config.Members.Length / 2 + 1;
            var remainingAllowedFailures = prepareTasks.Count - requiredConfirmations;
            var selectedValue = default(TValue);
            var maxAccepted = Ballot.Zero;
            var maxConflict = Ballot.Zero;
            var valueConflicts = default(List<(TValue Value, int Count)>);
            var selectedValueCount = 0;
            while (prepareTasks.Count > 0 && requiredConfirmations > 0 && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var resultTask = await Task.WhenAny(prepareTasks);
                    _ = prepareTasks.Remove(resultTask);
                    var prepareResult = await resultTask;
                    switch (prepareResult)
                    {
                        case (PrepareStatus.Success, var accepted, var value):
                            --requiredConfirmations;
                            if (accepted >= maxAccepted)
                            {
                                maxAccepted = accepted;
                                selectedValue = value;
                                valueConflicts?.Clear();
                                selectedValueCount = 0;
                            }
                            else if (
                                accepted == maxAccepted
                                && maxAccepted.IsFastRoundBallot
                                && (selectedValue is null && value is not null || selectedValue is not null && !selectedValue.Equals(value)))
                            {
                                if (valueConflicts is null)
                                {
                                    valueConflicts = new List<(TValue Value, int Count)>
                                    {
                                        (selectedValue, selectedValueCount),
                                        (value, 1)
                                    };
                                }
                                else
                                {
                                    var index = valueConflicts.FindIndex(entry => entry.Value.Equals(value));
                                    if (index < 0)
                                    {
                                        valueConflicts.Add((value, 1));
                                    }
                                    else
                                    {
                                        valueConflicts[index] = (valueConflicts[index].Value, valueConflicts[index].Count + 1);
                                    }
                                }
                            }

                            if (selectedValue is not null && selectedValue.Equals(value))
                            {
                                ++selectedValueCount;
                            }

                            break;
                        case (PrepareStatus.Conflict, var conflicting):
                            --remainingAllowedFailures;
                            if (conflicting > maxConflict)
                            {
                                maxConflict = conflicting;
                            }

                            break;
                        case (PrepareStatus.ConfigConflict, _, var value):
                            --remainingAllowedFailures;

                            break;
                    }
                }
                catch (Exception exception)
                {
                    --remainingAllowedFailures;
                    LogPrepareException(exception);
                }

                if (remainingAllowedFailures < 0)
                {
                    break;
                }
            }

            // Advance the ballot to the highest conflicting ballot to improve the likelihood of the next attempt succeeding.
            if (maxConflict > prepareBallot)
            {
                NextBallot = prepareBallot.AdvancePast(maxConflict);
            }

            cancellationToken.ThrowIfCancellationRequested();

            var achievedQuorum = requiredConfirmations == 0;
            if (valueConflicts is { })
            {
                // Apply the Fast Paxos Coordinator's Rule by selecting the value with the highest occurrence.
                foreach (var (value, count) in valueConflicts)
                {
                    if (count > selectedValueCount)
                    {
                        selectedValue = value;
                        selectedValueCount = count;
                    }
                }
            }

            return (achievedQuorum, selectedValue);
        }

        private async Task<bool> TryAccept(Ballot thisBallot, TValue newValue, ClusterConfiguration config, AcceptOptions options, CancellationToken cancellationToken)
        {
            // The prepare phase succeeded, proceed to propagate the new value to all acceptors.
            var acceptTasks = new List<Task<AcceptResponse>>(config.Members.Length);
            foreach (var server in config.Members)
            {
                var acceptTask = _acceptors.Accept(server, config.Stamp, thisBallot, newValue, options).AsTask();
                acceptTasks.Add(acceptTask);
            }

            var requiredConfirmations = config.Members.Length / 2 + 1;
            var remainingAllowedFailures = acceptTasks.Count - requiredConfirmations;
            var maxConflict = Ballot.Zero;
            while (acceptTasks.Count > 0 && requiredConfirmations > 0 && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var resultTask = await Task.WhenAny(acceptTasks);
                    _ = acceptTasks.Remove(resultTask);
                    var acceptResult = await resultTask;
                    switch (acceptResult)
                    {
                        case { Status: AcceptStatus.Success }:
                            --requiredConfirmations;
                            break;
                        case (AcceptStatus.Conflict, var conflicting):
                            --remainingAllowedFailures;
                            if (conflicting > maxConflict)
                            {
                                maxConflict = conflicting;
                            }

                            break;
                        case (AcceptStatus.ConfigConflict, var conflicting):
                            // Nothing needs to be done when encountering a configuration conflict, however it
                            // poses a good opportunity to ensure that this node's configuration is up-to-date.
                            --remainingAllowedFailures;
                            break;
                    }

                    if (requiredConfirmations <= 0 || remainingAllowedFailures < 0)
                    {
                        break;
                    }
                }
                catch (Exception exception)
                {
                    LogAcceptException(exception);
                }
            }

            // Advance the ballot past the highest conflicting ballot to improve the likelihood of the next Prepare succeeding.
            if (maxConflict > thisBallot)
            {
                NextBallot = NextBallot.AdvancePast(maxConflict);
            }

            cancellationToken.ThrowIfCancellationRequested();

            var achievedQuorum = requiredConfirmations == 0;
            return achievedQuorum;
        }

        private void LogPrepareException(Exception exception)
        {
            if (_log.IsEnabled(LogLevel.Warning))
            {
                _log.LogWarning($"Exception during Prepare: {exception}");
            }
        }

        private void LogAcceptException(Exception exception)
        {
            if (_log.IsEnabled(LogLevel.Warning))
            {
                _log.LogWarning($"Exception during Accept: {exception}");
            }
        }

        [Conditional("DEBUG")]
        private void LogSkippingPrepare(TValue currentValue)
        {
            if (_log.IsEnabled(LogLevel.Trace))
            {
                _log.LogTrace($"Will attempt Accept using cached value, {currentValue}");
            }
        }

        [Conditional("DEBUG")]
        private void LogPrepareFailed()
        {
            if (_log.IsEnabled(LogLevel.Trace))
            {
                _log.LogTrace("Prepare failed, will retry.");
            }
        }

        [Conditional("DEBUG")]
        private void LogAcceptStarted(TValue newValue)
        {
            if (_log.IsEnabled(LogLevel.Trace))
            {
                _log.LogTrace($"Trying to have new value {newValue} accepted.");
            }
        }

        [Conditional("DEBUG")]
        private void LogAcceptSucceded(TValue newValue)
        {
            if (_log.IsEnabled(LogLevel.Trace))
            {
                _log.LogTrace($"Successfully updated value to {newValue}.");
            }
        }

        [Conditional("DEBUG")]
        private void LogAcceptFailedFinal()
        {
            if (_log.IsEnabled(LogLevel.Trace))
            {
                _log.LogTrace("Accept failed, no remaining retries.");
            }
        }

        [Conditional("DEBUG")]
        private void LogAcceptFailed()
        {
            if (_log.IsEnabled(LogLevel.Trace))
            {
                _log.LogTrace("Accept failed, will retry. No longer assuming leadership.");
            }
        }

        [Conditional("DEBUG")]
        private void LogPrepareFailedFinal()
        {
            if (_log.IsEnabled(LogLevel.Trace))
            {
                _log.LogTrace("Prepare failed, no remaining retries.");
            }
        }

        [Conditional("DEBUG")]
        private void LogPrepareSuccess(TValue currentValue)
        {
            if (_log.IsEnabled(LogLevel.Trace))
            {
                _log.LogTrace($"Prepare succeeded, learned current value: {currentValue}");
            }
        }

        public interface ITestAccessor
        {
            Ballot Ballot { get; set; }
            Ballot? Prepared { get; set; }
            TValue CachedValue { get; set; }
        }

        private enum CommitStage
        {
            Prepare,
            Accept
        }

        private record struct CommitState
        {
            public static CommitState Create(TOperation operation, CancellationToken cancellationToken, int remainingRetries) => new()
            {
                Operation = operation,
                CancellationToken = cancellationToken,
                RemainingRetries = remainingRetries,
            };

            public CommitStage Stage { get; set; }
            public Ballot AttemptBallot { get; set; }
            public TOperation Operation { get; init; }
            public TValue CurrentValue { get; set; }
            public CancellationToken CancellationToken { get; init; }
            public ClusterConfiguration Configuration { get; set; }
            public int RemainingRetries { get; set; }
            public bool AcceptPreparesSuccessor { get; set; }
            public bool PrepareFastRound { get; set; }
        }
    }

    public sealed class ConfigProposer : Proposer<(ConfigOperation, ClusterConfiguration), ClusterConfiguration>
    {
        private readonly AsyncEx.AsyncLock _lockObj;

        public ConfigProposer(ILocalConfiguration localConfiguration, Guid serverId, ILogger log, IAcceptorRouter<ClusterConfiguration> acceptors) : base(localConfiguration, serverId, log, acceptors)
        {
            _lockObj = new AsyncEx.AsyncLock();
        }

        protected override ClusterConfiguration ApplyOperation((ConfigOperation, ClusterConfiguration) arg, ClusterConfiguration currentValue) => arg switch
        {
            (ConfigOperation.Read, _) => currentValue,
            (ConfigOperation.Update, var updated) when currentValue is null || updated.Version.IsSuccessorTo(currentValue.Version) => updated,
            (ConfigOperation.Update, var updated) when !updated.Version.IsSuccessorTo(currentValue.Version) => currentValue,
            _ => throw new InvalidOperationException(),
        };

        public async Task<(ReplicationStatus Status, ClusterConfiguration Value)> TryRead(CancellationToken cancellationToken)
        {
            using (await _lockObj.LockAsync(cancellationToken))
            {
                return await base.TryCommit((ConfigOperation.Read, null), cancellationToken, numRetries: 1);
            }
        }

        public async Task<(ReplicationStatus Status, ClusterConfiguration Value)> TryUpdate(ClusterConfiguration updatedValue, int numRetries = 1, CancellationToken cancellationToken = default)
        {
            using (await _lockObj.LockAsync(cancellationToken))
            {
                return await TryCommit((ConfigOperation.Update, updatedValue), cancellationToken, numRetries: numRetries);
            }
        }

        protected override void OnCommittedValue(ClusterConfiguration committed)
        {
            LocalConfiguration.OnCommittedConfiguration(committed);
        }
    }

    /*
    * This is *intentionally* a relatively heavy proposer which is designed for higher-throughput scenarios, such as a partition in a key-value store.
    * Proposer actor has a queue of requests, each containing a completion source
    * Adding to the queue involves fetching a new completion source from a pool
    * Actor follows a simple loop of observing configuration, forming a batch of commands to process, and processing them.
    * Batches which are formed from the request queue have the following characteristics:
    *   * Read operations are ordered after write operations
    * The messaging loop is awoken any time a new operation is added to the queue and any time an outbound request completes
     */

    internal class BatchProposer<TStateMachine>
    {
        private readonly ConcurrentQueue<Operation<TStateMachine>> _workItems = new();
        private readonly SingleWaiterAutoResetEvent _workSignal = new() { RunContinuationsAsynchronously = false };
        private readonly ILogger _logger;
        private readonly Task _workTask;

        public BatchProposer(ILogger logger)
        {
            _logger = logger;
            _workTask = Task.Run(Process);
        }

        private async Task Process()
        {
            List<Operation<TStateMachine>> pendingOperations = new();
            while (true)
            {
                try
                {
                    while (_workItems.TryDequeue(out var op))
                    {
                        pendingOperations.Add(op);
                    }

                    await _workSignal.WaitAsync();
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Error processing operations");
                }
            }
        }
    }

    public abstract class Operation<TStateMachine>
    {
        public abstract bool IsReadOnly { get; }

        /*
        TODO: Do we need this for cluster membership? Are there any other scenarios where it's needed?
        /// <summary>
        /// The state machine value passed to <see cref="Apply"/> must be committed and cannot be an intermediate result.
        /// </summary>
        public abstract bool IsRequireCommittedInput { get; }
        */

        /// <summary>
        /// Executed to apply an update to the state machine
        /// </summary>
        /// <param name="stateMachine"></param>
        /// <returns></returns>
        public abstract bool Apply(TStateMachine stateMachine);

        /// <summary>
        /// Executed once the operation is successfully committed.
        /// </summary>
        public abstract void OnCommitted();
    }
}