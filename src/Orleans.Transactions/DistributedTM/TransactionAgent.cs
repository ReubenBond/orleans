using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions;

internal sealed class TransactionAgent : ITransactionAgent
{
    private readonly ILogger _logger;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly CausalClock _clock;
    private readonly ITransactionAgentStatistics _statistics;
    private readonly ITransactionOverloadDetector _overloadDetector;

    public TransactionAgent(TimeProvider timeProvider, ILogger<TransactionAgent> logger, ITransactionAgentStatistics statistics, ITransactionOverloadDetector overloadDetector)
    {
        _clock = new CausalClock(timeProvider);
        _logger = logger;
        _statistics = statistics;
        _overloadDetector = overloadDetector;
    }

    public Task<TransactionInfo> StartTransaction(bool readOnly, TimeSpan timeout)
    {
        if (_overloadDetector.IsOverloaded())
        {
            _statistics.TrackTransactionThrottled();
            throw new OrleansStartTransactionFailedException(new OrleansTransactionOverloadException());
        }

        var guid = Guid.NewGuid();
        DateTime ts = _clock.UtcNow();

        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("{TotalMilliseconds} start transaction {TransactionId} at {TimeStamp}", _stopwatch.Elapsed.TotalMilliseconds.ToString("f2"), guid, ts.ToString("o"));
        _statistics.TrackTransactionStarted();
        return Task.FromResult(new TransactionInfo(guid, ts, ts));
    }

    public async Task<(TransactionalStatus, Exception)> Resolve(TransactionInfo transactionInfo)
    {
        transactionInfo.TimeStamp = _clock.MergeUtcNow(transactionInfo.TimeStamp);

        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("{ElapsedMilliseconds} prepare {TransactionInfo}", _stopwatch.Elapsed.TotalMilliseconds.ToString("f2"), transactionInfo);

        if (transactionInfo.Participants.Count == 0)
        {
            _statistics.TrackTransactionSucceeded();
            return (TransactionalStatus.Ok, null);
        }

        KeyValuePair<ParticipantId, AccessCounter>? manager;

        List<ParticipantId> writeParticipants;
        List<KeyValuePair<ParticipantId, AccessCounter>> resources;
        CollateParticipants(transactionInfo.Participants, out writeParticipants, out resources, out manager);
        try
        {
            var (status, exception) = (writeParticipants == null)
                ? await CommitReadOnlyTransaction(transactionInfo, resources)
                : await CommitReadWriteTransaction(transactionInfo, writeParticipants, resources, manager.Value);
            if (status == TransactionalStatus.Ok)
                _statistics.TrackTransactionSucceeded();
            else
                _statistics.TrackTransactionFailed();
            return (status, exception);
        }
        catch (Exception)
        {
            _statistics.TrackTransactionFailed();
            throw;
        }
    }

    private async Task<(TransactionalStatus, Exception)> CommitReadOnlyTransaction(TransactionInfo transactionInfo, List<KeyValuePair<ParticipantId, AccessCounter>> resources)
    {
        TransactionalStatus status = TransactionalStatus.Ok;
        Exception exception;

        var tasks = new List<Task<TransactionalStatus>>();
        try
        {
            foreach (KeyValuePair<ParticipantId, AccessCounter> resource in resources)
            {
                tasks.Add(resource.Key.Id.AsReference<ITransactionalResourceExtension>()
                    .CommitReadOnly(resource.Key.Name, transactionInfo.TransactionId, resource.Value, transactionInfo.TimeStamp));
            }

            // wait for all responses
            TransactionalStatus[] results = await Task.WhenAll(tasks);

            // examine the return status
            foreach (var s in results)
            {
                if (s != TransactionalStatus.Ok)
                {
                    status = s;
                    if (_logger.IsEnabled(LogLevel.Debug))
                        _logger.LogDebug("{TotalMilliseconds} fail {TransactionId} prepare response status={status}", _stopwatch.Elapsed.TotalMilliseconds.ToString("f2"), transactionInfo.TransactionId, status);
                    break;
                }
            }

            exception = null;
        }
        catch (TimeoutException ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("{TotalMilliseconds} timeout {TransactionId} on CommitReadOnly", _stopwatch.Elapsed.TotalMilliseconds.ToString("f2"), transactionInfo.TransactionId);
            status = TransactionalStatus.ParticipantResponseTimeout;
            exception = ex;
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("{TotalMilliseconds} failure {TransactionId} CommitReadOnly", _stopwatch.Elapsed.TotalMilliseconds.ToString("f2"), transactionInfo.TransactionId);
            _logger.LogWarning(ex, "Unknown error while commiting readonly transaction {TransactionId}", transactionInfo.TransactionId);
            status = TransactionalStatus.PresumedAbort;
            exception = ex;
        }

        if (status != TransactionalStatus.Ok)
        {
            try
            {
                await Task.WhenAll(resources.Select(r => r.Key.Id.AsReference<ITransactionalResourceExtension>()
                    .Abort(r.Key.Name, transactionInfo.TransactionId)));
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug(
                        ex,
                        "{TotalMilliseconds} failure aborting {TransactionId} CommitReadOnly",
                        _stopwatch.Elapsed.TotalMilliseconds.ToString("f2"),
                        transactionInfo.TransactionId);
                _logger.LogWarning(
                    ex,
                    "Failed to abort readonly transaction {TransactionId}",
                    transactionInfo.TransactionId);
            }
        }

        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace(
                "{ElapsedMilliseconds} finish (reads only) {TransactionId}",
                transactionInfo.TransactionId,
                _stopwatch.Elapsed.TotalMilliseconds.ToString("f2"));

        return (status, exception);
    }

    private async Task<(TransactionalStatus, Exception)> CommitReadWriteTransaction(TransactionInfo transactionInfo, List<ParticipantId> writeResources, List<KeyValuePair<ParticipantId, AccessCounter>> resources, KeyValuePair<ParticipantId, AccessCounter> manager)
    {
        TransactionalStatus status = TransactionalStatus.Ok;
        Exception exception;

        try
        {
            foreach (var p in resources)
            {
                if (p.Key.Equals(manager.Key))
                    continue;
                // one-way prepare message
                p.Key.Id.AsReference<ITransactionalResourceExtension>()
                    .Prepare(p.Key.Name, transactionInfo.TransactionId, p.Value, transactionInfo.TimeStamp, manager.Key)
                    .Ignore();
            }

            // wait for the TM to commit the transaction
            status = await manager.Key.Id.AsReference<ITransactionManagerExtension>()
                .PrepareAndCommit(manager.Key.Name, transactionInfo.TransactionId, manager.Value, transactionInfo.TimeStamp, writeResources, resources.Count);
            exception = null;
        }
        catch (TimeoutException ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("{TotalMilliseconds} timeout {TransactionId} on CommitReadWriteTransaction", _stopwatch.Elapsed.TotalMilliseconds.ToString("f2"), transactionInfo.TransactionId);
            status = TransactionalStatus.TMResponseTimeout;
            exception = ex;
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("{TotalMilliseconds} failure {TransactionId} CommitReadWriteTransaction", _stopwatch.Elapsed.TotalMilliseconds.ToString("f2"), transactionInfo.TransactionId);
            _logger.LogWarning(ex, "Unknown error while committing transaction {TransactionId}", transactionInfo.TransactionId);
            status = TransactionalStatus.PresumedAbort;
            exception = ex;
        }

        if (status != TransactionalStatus.Ok)
        {
            try
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug("{TotalMilliseconds} failed {TransactionId} with status={Status}", _stopwatch.Elapsed.TotalMilliseconds.ToString("f2"), transactionInfo.TransactionId, status);

                // notify participants
                if (status.DefinitelyAborted())
                {
                    await Task.WhenAll(writeResources
                        .Where(p => !p.Equals(manager.Key))
                        .Select(p => p.Id.AsReference<ITransactionalResourceExtension>()
                            .Cancel(p.Name, transactionInfo.TransactionId, transactionInfo.TimeStamp, status)));
                }
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug("{TotalMilliseconds} failure aborting {TransactionId} CommitReadWriteTransaction", _stopwatch.Elapsed.TotalMilliseconds.ToString("f2"), transactionInfo.TransactionId);
                _logger.LogWarning(ex, "Failed to abort transaction {TransactionId}", transactionInfo.TransactionId);
            }
        }

        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("{TotalMilliseconds} finish {TransactionId}", _stopwatch.Elapsed.TotalMilliseconds.ToString("f2"), transactionInfo.TransactionId);

        return (status, exception);
    }

    public async Task Abort(TransactionInfo transactionInfo)
    {
        _statistics.TrackTransactionFailed();

        List<ParticipantId> participants = transactionInfo.Participants.Keys.ToList();

        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("Abort {TransactionInfo} {Participants}", transactionInfo, string.Join(",", participants.Select(p => p.ToString())));

        // send one-way abort messages to release the locks and roll back any updates
        await Task.WhenAll(participants.Select(p => p.Id.AsReference<ITransactionalResourceExtension>()
            .Abort(p.Name, transactionInfo.TransactionId)));
    }

    private void CollateParticipants(Dictionary<ParticipantId, AccessCounter> participants, out List<ParticipantId> writers, out List<KeyValuePair<ParticipantId, AccessCounter>> resources, out KeyValuePair<ParticipantId, AccessCounter>? manager)
    {
        writers = null;
        resources = null;
        manager = null;
        KeyValuePair<ParticipantId, AccessCounter>? priorityManager = null;
        foreach (KeyValuePair<ParticipantId, AccessCounter> participant in participants)
        {
            ParticipantId id = participant.Key;
            // priority manager
            if (id.IsPriorityManager())
            {
                manager = priorityManager = (priorityManager == null)
                    ? participant
                    : throw new ArgumentOutOfRangeException(nameof(participants), "Only one priority transaction manager allowed in transaction");
            }
            // resource
            if(id.IsResource())
            {
                if(resources == null)
                {
                    resources = new List<KeyValuePair<ParticipantId, AccessCounter>>();
                }
                resources.Add(participant);
                if(participant.Value.Writes > 0)
                {
                    if (writers == null)
                    {
                        writers = new List<ParticipantId>();
                    }
                    writers.Add(id);
                }
            }
            // manager
            if (manager == null && id.IsManager() && participant.Value.Writes > 0)
            {
                manager = participant;
            }
        }
    }
}