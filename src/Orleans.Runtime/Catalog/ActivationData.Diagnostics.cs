#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Serialization.TypeSystem;
namespace Orleans.Runtime;

internal sealed partial class ActivationData
{
    /// <summary>
    /// Check whether this activation is overloaded.
    /// Returns LimitExceededException if overloaded, otherwise <c>null</c>c>
    /// </summary>
    /// <returns>Returns LimitExceededException if overloaded, otherwise <c>null</c>c></returns>
    public LimitExceededException? CheckOverloaded()
    {
        string limitName = nameof(SiloMessagingOptions.MaxEnqueuedRequestsHardLimit);
        int maxRequestsHardLimit = _shared.MessagingOptions.MaxEnqueuedRequestsHardLimit;
        int maxRequestsSoftLimit = _shared.MessagingOptions.MaxEnqueuedRequestsSoftLimit;
        if (PlacementStrategy is StatelessWorkerPlacement)
        {
            limitName = nameof(SiloMessagingOptions.MaxEnqueuedRequestsHardLimit_StatelessWorker);
            maxRequestsHardLimit = _shared.MessagingOptions.MaxEnqueuedRequestsHardLimit_StatelessWorker;
            maxRequestsSoftLimit = _shared.MessagingOptions.MaxEnqueuedRequestsSoftLimit_StatelessWorker;
        }

        if (maxRequestsHardLimit <= 0 && maxRequestsSoftLimit <= 0) return null; // No limits are set

        int count = GetRequestCount();

        if (maxRequestsHardLimit > 0 && count > maxRequestsHardLimit) // Hard limit
        {
            _shared.Logger.LogWarning(
                (int)ErrorCode.Catalog_Reject_ActivationTooManyRequests,
                "Overload - {Count} enqueued requests for activation {Activation}, exceeding hard limit rejection threshold of {HardLimit}",
                count,
                this,
                maxRequestsHardLimit);

            return new LimitExceededException(limitName, count, maxRequestsHardLimit, ToString());
        }

        if (maxRequestsSoftLimit > 0 && count > maxRequestsSoftLimit) // Soft limit
        {
            _shared.Logger.LogWarning(
                (int)ErrorCode.Catalog_Warn_ActivationTooManyRequests,
                "Hot - {Count} enqueued requests for activation {Activation}, exceeding soft limit warning threshold of {SoftLimit}",
                count,
                this,
                maxRequestsSoftLimit);
            return null;
        }

        return null;
    }

    public void AnalyzeWorkload(DateTime now, IMessageCenter messageCenter, MessageFactory messageFactory, SiloMessagingOptions options)
    {
        var slowRunningRequestDuration = options.RequestProcessingWarningTime;
        var longQueueTimeDuration = options.RequestQueueDelayWarningTime;

        List<string>? diagnostics = null;
        lock (this)
        {
            if (State != ActivationState.Valid)
            {
                return;
            }

            if (_blockingRequest is not null)
            {
                var message = _blockingRequest;
                TimeSpan? timeSinceQueued = default;
                if (_runningRequests.TryGetValue(message, out var waitTime))
                {
                    timeSinceQueued = waitTime.Elapsed;
                }

                var executionTime = _busyDuration.Elapsed;
                if (executionTime >= slowRunningRequestDuration)
                {
                    GetStatusList(ref diagnostics);
                    if (timeSinceQueued.HasValue)
                    {
                        diagnostics.Add($"Message {message} was enqueued {timeSinceQueued} ago and has now been executing for {executionTime}.");
                    }
                    else
                    {
                        diagnostics.Add($"Message {message} has been executing for {executionTime}.");
                    }

                    var response = messageFactory.CreateDiagnosticResponseMessage(message, isExecuting: true, isWaiting: false, diagnostics);
                    messageCenter.SendMessage(response);
                }
            }

            foreach (var running in _runningRequests)
            {
                var message = running.Key;
                var runDuration = running.Value;
                if (ReferenceEquals(message, _blockingRequest)) continue;

                // Check how long they've been executing.
                var executionTime = runDuration.Elapsed;
                if (executionTime >= slowRunningRequestDuration)
                {
                    // Interleaving message X has been executing for a long time
                    GetStatusList(ref diagnostics);
                    var messageDiagnostics = new List<string>(diagnostics)
                    {
                        $"Interleaving message {message} has been executing for {executionTime}."
                    };

                    var response = messageFactory.CreateDiagnosticResponseMessage(message, isExecuting: true, isWaiting: false, messageDiagnostics);
                    messageCenter.SendMessage(response);
                }
            }

            var queueLength = 1;
            foreach (var pair in _waitingRequests)
            {
                var message = pair.Message;
                var queuedTime = pair.QueuedTime.Elapsed;
                if (queuedTime >= longQueueTimeDuration)
                {
                    // Message X has been enqueued on the target grain for Y and is currently position QueueLength in queue for processing.
                    GetStatusList(ref diagnostics);
                    var messageDiagnostics = new List<string>(diagnostics)
                    {
                       $"Message {message} has been enqueued on the target grain for {queuedTime} and is currently position {queueLength} in queue for processing."
                    };

                    var response = messageFactory.CreateDiagnosticResponseMessage(message, isExecuting: false, isWaiting: true, messageDiagnostics);
                    messageCenter.SendMessage(response);
                }

                queueLength++;
            }
        }

        void GetStatusList([NotNull] ref List<string>? diagnostics)
        {
            if (diagnostics is not null) return;

            diagnostics = new List<string>
            {
                ToDetailedString(),
                $"TaskScheduler status: {_workItemGroup.DumpStatus()}"
            };
        }
    }

    internal int GetRequestCount()
    {
        lock (this)
        {
            return _runningRequests.Count + WaitingCount;
        }
    }

    public override string ToString() => $"[Activation: {Address.SiloAddress}/{Address.GrainId}{ActivationId}{GetActivationInfoString()} State={State}]";

    internal string ToDetailedString(bool includeExtraDetails = false)
    {
        lock (this)
        {
            var currentlyExecuting = includeExtraDetails ? _blockingRequest : null;
            return @$"[Activation: {Address.SiloAddress}/{Address.GrainId}{ActivationId} {GetActivationInfoString()} State={State} NonReentrancyQueueSize={WaitingCount} NumRunning={_runningRequests.Count} IdlenessTimeSpan={GetIdleness()} CollectionAgeLimit={_shared.CollectionAgeLimit}{(currentlyExecuting != null ? " CurrentlyExecuting=" : null)}{currentlyExecuting}]";
        }
    }

    private string GetActivationInfoString()
    {
        var placement = PlacementStrategy?.GetType().Name;
        var grainTypeName = _shared.GrainTypeName ?? GrainInstance switch
        {
            { } grainInstance => RuntimeTypeNameFormatter.Format(grainInstance.GetType()),
            _ => null
        };
        return grainTypeName is null ? $"#Placement={placement}" : $"#GrainType={grainTypeName} Placement={placement}";
    }
}
