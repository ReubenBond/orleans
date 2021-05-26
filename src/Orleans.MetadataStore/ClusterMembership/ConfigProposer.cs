using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using AsyncEx = Nito.AsyncEx;

namespace Orleans.MetadataStore
{
    public class ConfigProposer : ConfigProposer.ITestAccessor
    {
        private readonly AsyncEx.AsyncLock _lockObj;
        private readonly ILogger _log;
        private readonly IConfigAcceptorManager  _acceptors;
        private readonly Func<ExpandedReplicaSetConfiguration> _getConfiguration;
        private bool _skipPrepare;
        private int _nextInstanceId;
        private ClusterMembers _cachedValue;
        private ConfigBallot _ballot;

        public ConfigProposer(ConfigBallot initialBallot, Func<ExpandedReplicaSetConfiguration> getConfiguration, ILogger log, IConfigAcceptorManager acceptors)
        {
            _lockObj = new AsyncEx.AsyncLock();
            _ballot = initialBallot;
            _getConfiguration = getConfiguration;
            _log = log;
            _acceptors = acceptors;
        }

        internal ConfigBallot Ballot { get => _ballot; set => _ballot = value; }

        ConfigBallot ITestAccessor.Ballot { get => _ballot; set => _ballot = value; }
        bool ITestAccessor.SkipPrepare { get => _skipPrepare; set => _skipPrepare = value; }
        ClusterMembers ITestAccessor.CachedValue { get => _cachedValue; set => _cachedValue = value; }

        public async Task<(ReplicationStatus Status, ClusterMembers Value)> TryUpdate<TArg>(TArg value, ChangeFunction<TArg, ClusterMembers> changeFunction, CancellationToken cancellationToken)
        {
            using (await _lockObj.LockAsync(cancellationToken))
            {
                return await TryUpdateInternal(value, changeFunction, cancellationToken, numRetries: 1);
            }
        }

        private async Task<(ReplicationStatus Status, ClusterMembers Value)> TryUpdateInternal<TArg>(
            TArg value,
            ChangeFunction<TArg, ClusterMembers> changeFunction,
            CancellationToken cancellationToken,
            int numRetries)
        {
            // Configuration is observed once per attempt.
            // If this node's configuration changes while this proposer is still attempting to commit a value, the commit will
            // continue under the old configuration. If that configuration has already been observed by some of the acceptors,
            // the commit may fail and in that case the proposer may retry.
            var config = _getConfiguration();

            // Select a ballot number for this attempt. The ballot must be consistent between propose and accept for the attempt.
            var prepareBallot = _ballot = _ballot.Successor();

            ClusterMembers currentValue;
            if (_skipPrepare)
            {
                // If this node is leader, attempt to skip the prepare phase and at go straight to another accept
                // phase, assuming that the value has not changed since this proposer last had a value accepted.
                currentValue = _cachedValue;

                LogSkippingPrepare(currentValue);
            }
            else
            {
                // Try to obtain a quorum of promises from the acceptors and simultaneously learn the currently accepted value.
                bool prepareSuccess;
                (prepareSuccess, currentValue) = await TryPrepare(prepareBallot, config, cancellationToken);
                _cachedValue = currentValue;
                if (!prepareSuccess)
                {
                    // Allow the proposer to retry in order to hide harmless fast-forward events.
                    if (numRetries > 0)
                    {
                        LogPrepareFailed();

                        return await TryUpdateInternal(value, changeFunction, cancellationToken, numRetries - 1);
                    }

                    LogPrepareFailedFinal();
                    return (ReplicationStatus.Failed, currentValue);
                }

                LogPrepareSuccess(currentValue);
            }

            // Modify the currently accepted value and attempt to have it accepted on all acceptors.
            var newValue = changeFunction(currentValue, value);
            LogAcceptStarted(newValue);

            var acceptSuccess = await TryAccept(prepareBallot, newValue, config, cancellationToken);
            if (acceptSuccess)
            {
                // The accept succeeded, this proposer can attempt to use the current accept as a promise for a subsequent accept as an optimization.
                LogAcceptSucceded(newValue);

                _skipPrepare = true;
                _cachedValue = newValue;
                return (ReplicationStatus.Success, newValue);
            }

            // Since the accept did not succeed, this proposer should issue a prepare before trying to have its next value accepted.
            _skipPrepare = false;

            if (numRetries > 0)
            {
                // This attempt may have failed because another proposer interfered, so attempt again to have this value accepted.
                LogAcceptFailed();

                return await TryUpdateInternal(value, changeFunction, cancellationToken, numRetries - 1);
            }

            // It is possible that the value was committed successfully without this node receiving a quorum of acknowledgements,
            // so the result is uncertain.
            // For example, an acceptor's acknowledgement message may have been lost in transmission due to a transient network fault.
            LogAcceptFailedFinal();

            return (ReplicationStatus.Uncertain, currentValue);
        }

        private async Task<(bool, ClusterMembers)> TryPrepare(ConfigBallot prepareBallot, ClusterMembers config, CancellationToken cancellationToken)
        {
            if (config.Members is null)
            {
                return (false, default);
            }

            var prepareTasks = new List<Task<ConfigPrepareResponse>>(config.Members.Length);
            foreach (var server in config.Members)
            {
                var prepareTask = _acceptors.Prepare(server, prepareBallot).AsTask();
                prepareTasks.Add(prepareTask);
            }

            // Run a Prepare round in order to learn the current value of the register and secure a promise that a quorum
            // of nodes which accept our new value.
            var requiredConfirmations = config.PrepareQuorum;
            var remainingAllowedFailures = prepareTasks.Count - requiredConfirmations;
            var currentValue = default(ClusterMembers);
            var maxSuccess = ConfigBallot.Zero;
            var maxConflict = ConfigBallot.Zero;
            while (prepareTasks.Count > 0 && requiredConfirmations > 0 && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var resultTask = await Task.WhenAny(prepareTasks);
                    _ = prepareTasks.Remove(resultTask);
                    var prepareResult = await resultTask;
                    switch (prepareResult)
                    {
                        case (PrepareStatus.Success, var promised, var value):
                            --requiredConfirmations;
                            if (promised >= maxSuccess)
                            {
                                maxSuccess = promised;
                                currentValue = value;
                            }

                            break;
                        case (PrepareStatus.Conflict, var conflicting):
                            --remainingAllowedFailures;
                            if (conflicting > maxConflict)
                            {
                                maxConflict = conflicting;
                            }

                            break;
                        case (PrepareStatus.ConfigConflict, var conflicting):
                            --remainingAllowedFailures;
                            // Nothing needs to be done when encountering a configuration conflict, however it
                            // poses a good opportunity to ensure that this node's configuration is up-to-date.
                            // TODO: Signal to configuration manager that we need to update configuration.
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
            if (maxConflict > _ballot)
            {
                _ballot = _ballot.AdvanceTo(maxConflict);
            }

            cancellationToken.ThrowIfCancellationRequested();

            var achievedQuorum = requiredConfirmations == 0;
            return (achievedQuorum, currentValue);
        }

        private SiloAddress SelectInstance(SiloAddress[] acceptors)
        {
            if (_nextInstanceId >= acceptors.Length)
            {
                _nextInstanceId = 0;
            }

            return acceptors[_nextInstanceId++];
        }

        private async Task<bool> TryAccept(ConfigBallot thisBallot, ClusterMembers newValue, ClusterMembers config, CancellationToken cancellationToken)
        {
            // The prepare phase succeeded, proceed to propagate the new value to all acceptors.
            var acceptTasks = new List<Task<ConfigAcceptResponse>>(config.Members.Length);
            foreach (var server in config.Members)
            {
                var acceptTask = _acceptors.Accept(server, thisBallot, newValue).AsTask();
                acceptTasks.Add(acceptTask);
            }

            var requiredConfirmations = config.AcceptQuorum;
            var remainingAllowedFailures = acceptTasks.Count - requiredConfirmations;
            var maxConflict = ConfigBallot.Zero;
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
                            // TODO: Signal to configuration manager that we need to update configuration?
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
                _ballot = _ballot.AdvanceTo(maxConflict);
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
        private void LogSkippingPrepare(ClusterMembers currentValue)
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
        private void LogAcceptStarted(ClusterMembers newValue)
        {
            if (_log.IsEnabled(LogLevel.Trace))
            {
                _log.LogTrace($"Trying to have new value {newValue} accepted.");
            }
        }

        [Conditional("DEBUG")]
        private void LogAcceptSucceded(ClusterMembers newValue)
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
        private void LogPrepareSuccess(ClusterMembers currentValue)
        {
            if (_log.IsEnabled(LogLevel.Trace))
            {
                _log.LogTrace($"Prepare succeeded, learned current value: {currentValue}");
            }
        }

        public interface ITestAccessor
        {
            ConfigBallot Ballot { get; set; }
            bool SkipPrepare { get; set; }
            ClusterMembers CachedValue { get; set; }
        }
    }
}