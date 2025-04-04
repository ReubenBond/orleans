using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Storage;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions.State;

internal class TransactionQueue<TState>
    where TState : class, new()
{
    private readonly TransactionalStateOptions _options;
    private readonly ParticipantId _resource;
    private readonly IGrainContext _grainContext;
    private readonly ITransactionalStateStorage<TState> _storage;
    private readonly BatchWorker _storageWorker;
    private readonly ILogger _logger;
    private readonly ActivationLifetime _activationLifetime;
    private readonly ConfirmationWorker<TState> _confirmationWorker;
    private CommitQueue<TState> _commitQueue;
    private Task _readyTask;

    protected StorageBatch<TState> StorageBatch { get; private set; }

    private int _failCounter;

    // collection tasks
    private readonly Dictionary<DateTime, PreparedMessages> _unprocessedPreparedMessages;
    private sealed class PreparedMessages(TransactionalStatus status)
    {
        public int Count;
        public TransactionalStatus Status = status;
    }

    private TState _stableState;
    private long _stableSequenceNumber;
    public ReadWriteLock<TState> RWLock { get; }
    public CausalClock Clock { get; }

    public TransactionQueue(
        IOptions<TransactionalStateOptions> options,
        ParticipantId resource,
        IGrainContext grainContext,
        ITransactionalStateStorage<TState> storage,
        TimeProvider timeProvider,
        ILogger logger)
    {
        _options = options.Value;
        _resource = resource;
        _grainContext = grainContext;
        _storage = storage;
        Clock = new CausalClock(timeProvider);
        _logger = logger;
        _activationLifetime = new ActivationLifetime(grainContext);
        _storageWorker = new BatchWorkerFromDelegate(StorageWork, _activationLifetime.OnDeactivating);
        RWLock = new ReadWriteLock<TState>(options, this, _storageWorker, logger, _activationLifetime);
        _confirmationWorker = new ConfirmationWorker<TState>(options, _resource, _storageWorker, () => StorageBatch, _logger, _activationLifetime);
        _unprocessedPreparedMessages = [];
        _commitQueue = new CommitQueue<TState>();
        _readyTask = Task.CompletedTask;
    }

    public async Task EnqueueCommit(TransactionRecord<TState> record)
    {
        try
        {
            if (_logger.IsEnabled(LogLevel.Trace))
                _logger.LogTrace("Start two-phase-commit {TransactionId} {Timestamp}", record.TransactionId, record.Timestamp.ToString("O"));

            _commitQueue.Add(record);

            // additional actions for each commit type
            switch (record.Role)
            {
                case CommitRole.ReadOnly:
                    {
                        // no extra actions needed
                        break;
                    }

                case CommitRole.LocalCommit:
                    {
                        // process prepared messages received ahead of time
                        if (_unprocessedPreparedMessages.TryGetValue(record.Timestamp, out PreparedMessages info))
                        {
                            if (info.Status == TransactionalStatus.Ok)
                            {
                                record.WaitCount -= info.Count;
                            }
                            else
                            {
                                await AbortCommits(info.Status, _commitQueue.Count - 1);

                                RWLock.Notify();
                            }
                            _unprocessedPreparedMessages.Remove(record.Timestamp);
                        }
                        break;
                    }

                case CommitRole.RemoteCommit:
                    {

                        // optimization: can immediately proceed if dependency is implied
                        bool behindRemoteEntryBySameTM = false;
                            /* disabled - jbragg - TODO - revisit
                            commitQueue.Count >= 2
                            && commitQueue[commitQueue.Count - 2] is TransactionRecord<TState> rce
                            && rce.Role == CommitRole.RemoteCommit
                            && rce.TransactionManager.Equals(record.TransactionManager);
                            */

                        if (record.NumberWrites > 0)
                        {
                            StorageBatch.Prepare(record.SequenceNumber, record.TransactionId, record.Timestamp, record.TransactionManager, record.State);
                        }
                        else
                        {
                            StorageBatch.Read(record.Timestamp);
                        }

                        StorageBatch.FollowUpAction(() =>
                        {
                            if (_logger.IsEnabled(LogLevel.Trace))
                            {
                                _logger.LogTrace("Persisted {Record}", record);
                            }

                            record.PrepareIsPersisted = true;

                            if (behindRemoteEntryBySameTM)
                            {
                                if (_logger.IsEnabled(LogLevel.Trace))
                                {
                                    _logger.LogTrace("Sending immediate prepared {Record}", record);
                                }
                                // can send prepared message immediately after persisting prepare record
                                record.TransactionManager.Id.AsReference<ITransactionManagerExtension>()
                                      .Prepared(record.TransactionManager.Name, record.TransactionId, record.Timestamp, _resource, TransactionalStatus.Ok)
                                      .Ignore();
                                record.LastSent = DateTime.UtcNow;
                            }
                        });
                        break;
                    }

                default:
                    {
                        _logger.LogError(777, "internal error: impossible case {CommitRole}", record.Role);
                        throw new NotSupportedException($"{record.Role} is not a supported CommitRole.");
                    }
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, $"Transaction abort due to internal error in {nameof(EnqueueCommit)}");
            await NotifyOfAbort(record, TransactionalStatus.UnknownException, exception);
        }
    }

    public async Task NotifyOfPrepared(Guid transactionId, DateTime timeStamp, TransactionalStatus status)
    {
        var pos = _commitQueue.Find(transactionId, timeStamp);
        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("NotifyOfPrepared - TransactionId:{TransactionId} Timestamp:{Timestamp}, TransactionalStatus{TransactionalStatus}", transactionId, timeStamp, status);

        if (pos != -1)
        {

            var localEntry = _commitQueue[pos];

            if (localEntry.Role != CommitRole.LocalCommit)
            {
                _logger.LogError($"Transaction abort due to internal error in {nameof(NotifyOfPrepared)}: Wrong commit type");
                throw new InvalidOperationException($"Wrong commit type: {localEntry.Role}");
            }

            if (status == TransactionalStatus.Ok)
            {
                localEntry.WaitCount--;

                _storageWorker.Notify();
            }
            else
            {
                await AbortCommits(status, pos);

                RWLock.Notify();
            }
        }
        else
        {
            // this message has arrived ahead of the commit request - we need to remember it
            if (!_unprocessedPreparedMessages.TryGetValue(timeStamp, out PreparedMessages info))
            {
                _unprocessedPreparedMessages[timeStamp] = info = new PreparedMessages(status);
            }
            if (status == TransactionalStatus.Ok)
            {
                info.Count++;
            }
            else
            {
                info.Status = status;
            }

            // TODO fix memory leak if corresponding commit messages never arrive
        }
    }

    public async Task NotifyOfPrepare(Guid transactionId, AccessCounter accessCount, DateTime timeStamp, ParticipantId transactionManager)
    {
        var locked = await RWLock.ValidateLock(transactionId, accessCount);
        var status = locked.Item1;
        var record = locked.Item2;
        var valid = status == TransactionalStatus.Ok;

        record.Timestamp = timeStamp;
        record.Role = CommitRole.RemoteCommit; // we are not the TM
        record.TransactionManager = transactionManager;
        record.LastSent = null;
        record.PrepareIsPersisted = false;

        if (!valid)
        {
            await NotifyOfAbort(record, status, exception: null);
        }
        else
        {
            Clock.Merge(record.Timestamp);
        }

        RWLock.Notify();
    }

    public async Task NotifyOfAbort(TransactionRecord<TState> entry, TransactionalStatus status, Exception exception)
    {
        switch (entry.Role)
        {
            case CommitRole.NotYetDetermined:
                {
                    // cannot notify anyone. TA will detect broken lock during prepare.
                    break;
                }
            case CommitRole.RemoteCommit:
                {
                    if (_logger.IsEnabled(LogLevel.Trace))
                        _logger.LogTrace("Aborting status={Status} {Entry}", status, entry);

                    entry.ConfirmationResponsePromise?.TrySetException(new OrleansException($"Confirm failed: Status {status}"));

                    if (entry.LastSent.HasValue)
                        return; // cannot abort anymore if we already sent prepare-ok message

                    if (_logger.IsEnabled(LogLevel.Trace))
                        _logger.LogTrace("Aborting via Prepared. Status={Status} Entry={Entry}", status, entry);

                    entry.TransactionManager.Id.AsReference<ITransactionManagerExtension>()
                         .Prepared(entry.TransactionManager.Name, entry.TransactionId, entry.Timestamp, _resource, status)
                         .Ignore();
                    break;
                }
            case CommitRole.LocalCommit:
                {
                    if (_logger.IsEnabled(LogLevel.Trace))
                        _logger.LogTrace("Aborting status={Status} {Entry}", status, entry);

                    try
                    {
                        // tell remote participants
                        await Task.WhenAll(entry.WriteParticipants
                            .Where(p => !p.Equals(_resource))
                            .Select(p => p.Id.AsReference<ITransactionalResourceExtension>()
                                 .Cancel(p.Name, entry.TransactionId, entry.Timestamp, status)));
                    }
                    catch(Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to notify all transaction participants of cancellation.  TransactionId: {TransactionId}, Timestamp: {Timestamp}, Status: {Status}", entry.TransactionId, entry.Timestamp, status);
                    }

                    // reply to transaction agent
                    if (exception is not null)
                    {
                        entry.PromiseForTA.TrySetException(exception);
                    }
                    else
                    {
                        entry.PromiseForTA.TrySetResult(status);
                    }

                    break;
                }
            case CommitRole.ReadOnly:
                {
                    if (_logger.IsEnabled(LogLevel.Trace))
                        _logger.LogTrace("Aborting status={Status} {Entry}", status, entry);

                    // reply to transaction agent
                    if (exception is not null)
                    {
                        entry.PromiseForTA.TrySetException(exception);
                    }
                    else
                    {
                        entry.PromiseForTA.TrySetResult(status);
                    }

                    break;
                }
            default:
                {
                    _logger.LogError(777, "internal error: impossible case {CommitRole}", entry.Role);
                    throw new NotSupportedException($"{entry.Role} is not a supported CommitRole.");
                }
        }
    }

    public async Task NotifyOfPing(Guid transactionId, DateTime timeStamp, ParticipantId resource)
    {
        if (_commitQueue.Find(transactionId, timeStamp) != -1)
        {
            // no need to take special action now - the transaction is still
            // in the commit queue and its status is not yet determined.
            // confirmation or cancellation will be sent after committing or aborting.

            if (_logger.IsEnabled(LogLevel.Trace))
                _logger.LogTrace("Received ping for {TransactionId}, irrelevant (still processing)", transactionId);

            _storageWorker.Notify(); // just in case the worker fell asleep or something
        }
        else
        {
            if (!_confirmationWorker.IsConfirmed(transactionId))
            {
                if (_logger.IsEnabled(LogLevel.Trace))
                    _logger.LogTrace("Received ping for {TransactionId}, unknown - presumed abort", transactionId);

                // we never heard of this transaction - so it must have aborted
                await resource.Id.AsReference<ITransactionalResourceExtension>()
                        .Cancel(resource.Name, transactionId, timeStamp, TransactionalStatus.PresumedAbort);
            }
        }
    }

    public async Task NotifyOfConfirm(Guid transactionId, DateTime timeStamp)
    {
        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("NotifyOfConfirm: {TransactionId} {TimeStamp}", transactionId, timeStamp);

        // find in queue
        var pos = _commitQueue.Find(transactionId, timeStamp);

        if (pos == -1)
            return; // must have already been confirmed

        var remoteEntry = _commitQueue[pos];

        if (remoteEntry.Role != CommitRole.RemoteCommit)
        {
            _logger.LogError($"Internal error in {nameof(NotifyOfConfirm)}: wrong commit type");
            throw new InvalidOperationException($"Wrong commit type: {remoteEntry.Role}");
        }

        // setting this field makes this entry ready for batching

        remoteEntry.ConfirmationResponsePromise = remoteEntry.ConfirmationResponsePromise ?? new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _storageWorker.Notify();

        // now we wait for the batch to finish

        await remoteEntry.ConfirmationResponsePromise.Task;
    }

    public async Task NotifyOfCancel(Guid transactionId, DateTime timeStamp, TransactionalStatus status)
    {
        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("{MethodName}. TransactionId: {TransactionId}, TimeStamp: {TimeStamp} Status: {TransactionalStatus}", nameof(NotifyOfCancel), transactionId, timeStamp, status);

        // find in queue
        var pos = _commitQueue.Find(transactionId, timeStamp);

        if (pos == -1)
            return;

        StorageBatch.Cancel(_commitQueue[pos].SequenceNumber);

        await AbortCommits(status, pos);

        _storageWorker.Notify();

        RWLock.Notify();
    }

    /// <summary>
    /// called on activation, and when recovering from storage conflicts or other exceptions.
    /// </summary>
    public async Task NotifyOfRestore()
    {
        try
        {
            await Ready();
        }
        finally
        {
            _readyTask = Restore();
        }
        await _readyTask;
    }

    /// <summary>
    /// Ensures queue is ready to process requests.
    /// </summary>
    /// <returns></returns>
    public Task Ready()
    {
        if (_readyTask.Status == TaskStatus.RanToCompletion)
        {
            return _readyTask;
        }
        return ReadyAsync();
        async Task ReadyAsync()
        {
            try
            {
                await _readyTask;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Exception in TransactionQueue");
                await AbortAndRestore(TransactionalStatus.UnknownException, exception);
            }
        }
    }

    private async Task Restore()
    {
        TransactionalStorageLoadResponse<TState> loadresponse = await _storage.Load();

        StorageBatch = new StorageBatch<TState>(loadresponse);

        _stableState = loadresponse.CommittedState;
        _stableSequenceNumber = loadresponse.CommittedSequenceId;

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "Load v{StableSequenceNumber} {PendingStatesCount}p {CommitRecordsCount}c",
                _stableSequenceNumber,
                loadresponse.PendingStates.Count,
                StorageBatch.MetaData.CommitRecords.Count);
        }

        // ensure clock is consistent with loaded state
        Clock.Merge(StorageBatch.MetaData.TimeStamp);

        // resume prepared transactions (not TM)
        foreach (var pr in loadresponse.PendingStates.OrderBy(ps => ps.TimeStamp))
        {
            if (pr.SequenceId > loadresponse.CommittedSequenceId && pr.TransactionManager.Id != null)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug("Recover two-phase-commit {TransactionId}", pr.TransactionId);

                ParticipantId tm = pr.TransactionManager;

                _commitQueue.Add(new TransactionRecord<TState>
                {
                    Role = CommitRole.RemoteCommit,
                    TransactionId = Guid.Parse(pr.TransactionId),
                    Timestamp = pr.TimeStamp,
                    State = pr.State,
                    SequenceNumber = pr.SequenceId,
                    TransactionManager = tm,
                    PrepareIsPersisted = true,
                    LastSent = default(DateTime),
                    ConfirmationResponsePromise = null,
                    NumberWrites = 1 // was a writing transaction
                });
                _stableSequenceNumber = pr.SequenceId;
            }
        }

        // resume committed transactions (on TM)
        foreach (var kvp in StorageBatch.MetaData.CommitRecords)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug(
                    "Recover commit confirmation {Key}",
                    kvp.Key);
            _confirmationWorker.Add(kvp.Key, kvp.Value.Timestamp, kvp.Value.WriteParticipants);
        }

        // check for work
        _storageWorker.Notify();
        RWLock.Notify();
    }

    public void GetMostRecentState(out TState state, out long sequenceNumber)
    {
        if (_commitQueue.Count == 0)
        {
            state = _stableState;
            sequenceNumber = _stableSequenceNumber;
        }
        else
        {
            var record = _commitQueue.Last;
            state = record.State;
            sequenceNumber = record.SequenceNumber;
        }
    }

    public int BatchableOperationsCount()
    {
        int count = 0;
        int pos = _commitQueue.Count - 1;
        while (pos >= 0 && _commitQueue[pos].Batchable)
        {
            pos--;
            count++;
        }
        return count;
    }

    private async Task StorageWork()
    {
        // Stop if this activation is stopping/stopped.
        if (_activationLifetime.OnDeactivating.IsCancellationRequested) return;

        using (_activationLifetime.BlockDeactivation())
        {
            try
            {
                // count committable entries at the bottom of the commit queue
                int committableEntries = 0;
                while (committableEntries < _commitQueue.Count && _commitQueue[committableEntries].ReadyToCommit)
                {
                    committableEntries++;
                }

                // process all committable entries, assembling a storage batch
                if (committableEntries > 0)
                {
                    // process all committable entries, adding storage events to the storage batch
                    CollectEventsForBatch(committableEntries);

                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        var recordString = _commitQueue.Count > committableEntries ? _commitQueue[committableEntries].ToString() : "";
                        _logger.LogDebug(
                            "BatchCommit: {CommittableEntries} Leave: {UncommittableEntries}, Record: {Record}",
                            committableEntries,
                            _commitQueue.Count - committableEntries,
                            recordString);
                    }
                }
                else
                {
                    // send or re-send messages and detect timeouts
                    await CheckProgressOfCommitQueue();
                }

                // store the current storage batch, if it is not empty
                StorageBatch<TState> batchBeingSentToStorage = null;
                if (StorageBatch.BatchSize > 0)
                {
                    // get the next batch in place so it can be filled while we store the old one
                    batchBeingSentToStorage = StorageBatch;
                    StorageBatch = new StorageBatch<TState>(batchBeingSentToStorage);

                    try
                    {
                        if (await batchBeingSentToStorage.CheckStorePreConditions())
                        {
                            // perform the actual store, and record the e-tag
                            StorageBatch.ETag = await batchBeingSentToStorage.Store(_storage);
                            _failCounter = 0;
                        }
                        else
                        {
                            _logger.LogWarning("Store pre conditions not met.");
                            await AbortAndRestore(TransactionalStatus.CommitFailure, exception: null);
                            return;
                        }
                    }
                    catch (InconsistentStateException exception)
                    {
                        _logger.LogWarning(888, exception, "Reload from storage triggered by e-tag mismatch.");
                        await AbortAndRestore(TransactionalStatus.StorageConflict, exception, true);
                        return;
                    }
                    catch (Exception exception)
                    {
                        _logger.LogWarning(exception, "Storage exception in storage worker.");
                        await AbortAndRestore(TransactionalStatus.UnknownException, exception);
                        return;
                    }
                }

                if (committableEntries > 0)
                {
                    // update stable state
                    var lastCommittedEntry = _commitQueue[committableEntries - 1];
                    _stableState = lastCommittedEntry.State;
                    _stableSequenceNumber = lastCommittedEntry.SequenceNumber;
                    if (_logger.IsEnabled(LogLevel.Trace))
                        _logger.LogTrace("Stable state version: {StableSequenceNumber}", _stableSequenceNumber);

                    // remove committed entries from commit queue
                    _commitQueue.RemoveFromFront(committableEntries);
                    _storageWorker.Notify();  // we have to re-check for work
                }

                if (batchBeingSentToStorage != null)
                {
                    batchBeingSentToStorage.RunFollowUpActions();
                    _storageWorker.Notify();  // we have to re-check for work
                }
            }
            catch (Exception exception)
            {
                _logger.LogWarning(888, exception, "Exception in storageWorker.  Retry {FailCounter}", _failCounter);
                await AbortAndRestore(TransactionalStatus.UnknownException, exception);
            }
        }
    }

    private Task AbortAndRestore(TransactionalStatus status, Exception exception, bool force = false)
    {
        _readyTask = Bail(status, exception, force);
        return _readyTask;
    }

    private async Task Bail(TransactionalStatus status, Exception exception, bool force = false)
    {
        List<Task> pending = new List<Task>();
        pending.Add(RWLock.AbortExecutingTransactions(exception));
        RWLock.AbortQueuedTransactions();

        // abort all entries in the commit queue
        foreach (var entry in _commitQueue.Elements)
        {
            pending.Add(NotifyOfAbort(entry, status, exception: exception));
        }

        _commitQueue.Clear();

        await Task.WhenAll(pending);
        if (++_failCounter >= 10 || force)
        {
            const string message = "StorageWorker triggering grain Deactivation";
            _logger.LogDebug(message);
            _grainContext.Deactivate(new DeactivationReason(DeactivationReasonCode.RuntimeRequested, message));
        }
        await Restore();
    }

    private async Task CheckProgressOfCommitQueue()
    {
        if (_commitQueue.Count > 0)
        {
            var bottom = _commitQueue[0];
            var now = DateTime.UtcNow;

            if (_logger.IsEnabled(LogLevel.Trace))
                _logger.LogTrace("{CommitQueueSize} entries in queue waiting for bottom: {BottomEntry}", _commitQueue.Count, bottom);

            switch (bottom.Role)
            {
                case CommitRole.LocalCommit:
                    {
                        // check for timeout periodically
                        if (bottom.WaitingSince + _options.PrepareTimeout <= now)
                        {
                            await AbortCommits(TransactionalStatus.PrepareTimeout);
                            RWLock.Notify();
                        }
                        else
                        {
                            _storageWorker.Notify(bottom.WaitingSince + _options.PrepareTimeout);
                        }
                        break;
                    }

                case CommitRole.RemoteCommit:
                    {
                        if (bottom.PrepareIsPersisted && !bottom.LastSent.HasValue)
                        {
                            // send PreparedMessage to remote TM
                            bottom.TransactionManager.Id.AsReference<ITransactionManagerExtension>()
                                  .Prepared(bottom.TransactionManager.Name, bottom.TransactionId, bottom.Timestamp, _resource, TransactionalStatus.Ok)
                                  .Ignore();                                
                                
                            bottom.LastSent = now;

                            if (_logger.IsEnabled(LogLevel.Trace))
                                _logger.LogTrace("Sent Prepared {BottomEntry}", bottom);

                            if (bottom.IsReadOnly)
                            {
                                _storageWorker.Notify(); // we are ready to batch now
                            }
                            else
                            {
                                _storageWorker.Notify(bottom.LastSent.Value + _options.RemoteTransactionPingFrequency);
                            }
                        }
                        else if (!bottom.IsReadOnly && bottom.LastSent.HasValue)
                        {
                            // send ping messages periodically to reactivate crashed TMs

                            if (bottom.LastSent + _options.RemoteTransactionPingFrequency <= now)
                            {
                                if (_logger.IsEnabled(LogLevel.Trace))
                                    _logger.LogTrace("Sent ping {BottomEntry}", bottom);
                                bottom.TransactionManager.Id.AsReference<ITransactionManagerExtension>()
                                      .Ping(bottom.TransactionManager.Name, bottom.TransactionId, bottom.Timestamp, _resource).Ignore();
                                bottom.LastSent = now;
                            }
                            _storageWorker.Notify(bottom.LastSent.Value + _options.RemoteTransactionPingFrequency);
                        }

                        break;
                    }

                default:
                    {
                        _logger.LogError(777, "internal error: impossible case {CommitRole}", bottom.Role);
                        throw new NotSupportedException($"{bottom.Role} is not a supported CommitRole.");
                    }
            }
        }
    }

    private void CollectEventsForBatch(int batchSize)
    {
        // collect events for batch
        for (int i = 0; i < batchSize; i++)
        {
            TransactionRecord<TState> entry = _commitQueue[i];

            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.LogTrace("Committing {Entry}", entry);
            }

            switch (entry.Role)
            {
                case CommitRole.LocalCommit:
                    {
                        OnLocalCommit(entry);
                        break;
                    }

                case CommitRole.RemoteCommit:
                    {
                        if (entry.ConfirmationResponsePromise == null)
                        {
                            // this is a read-only participant that has sent
                            // its prepared message.
                            // So we are really done and need not store or do anything.
                        }
                        else
                        {
                            // we must confirm in storage, and then respond to TM so it can collect
                            StorageBatch.Confirm(entry.SequenceNumber);
                            StorageBatch.FollowUpAction(() =>
                            {
                                entry.ConfirmationResponsePromise.TrySetResult(true);
                                if (_logger.IsEnabled(LogLevel.Trace))
                                {
                                    _logger.LogTrace(
                                        "Confirmed remote commit v{SequenceNumber}. TransactionId:{TransactionId} Timestamp:{Timestamp} TransactionManager:{TransactionManager}",
                                        entry.SequenceNumber,
                                        entry.TransactionId,
                                        entry.Timestamp,
                                        entry.TransactionManager);
                                }
                            });
                        }

                        break;
                    }

                case CommitRole.ReadOnly:
                    {
                        // we are a participant of a read-only transaction. Must store timestamp and then respond.
                        StorageBatch.Read(entry.Timestamp);
                        StorageBatch.FollowUpAction(() =>
                        {
                            entry.PromiseForTA.TrySetResult(TransactionalStatus.Ok);
                        });

                        break;
                    }

                default:
                    {
                        _logger.LogError(777, "internal error: impossible case {CommitRole}", entry.Role);
                        throw new NotSupportedException($"{entry.Role} is not a supported CommitRole.");
                    }
            }
        }
    }

    protected virtual void OnLocalCommit(TransactionRecord<TState> entry)
    {
        StorageBatch.Prepare(entry.SequenceNumber, entry.TransactionId, entry.Timestamp, entry.TransactionManager, entry.State);
        StorageBatch.Commit(entry.TransactionId, entry.Timestamp, entry.WriteParticipants);
        StorageBatch.Confirm(entry.SequenceNumber);

        // after store, send response back to TA
        StorageBatch.FollowUpAction(() =>
        {
            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.LogTrace(
                    "Locally committed {TransactionId} {Timestamp}",
                    entry.TransactionId,
                    entry.Timestamp.ToString("O"));
            }
            entry.PromiseForTA.TrySetResult(TransactionalStatus.Ok);
        });

        if (entry.WriteParticipants.Count > 1)
        {
            // after committing, we need to run a task to confirm and collect
            StorageBatch.FollowUpAction(() =>
            {
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.LogTrace(
                        "Adding confirmation to worker for {TransactionId} {Timestamp}",
                        entry.TransactionId,
                        entry.Timestamp.ToString("O"));
                }
                _confirmationWorker.Add(entry.TransactionId, entry.Timestamp, entry.WriteParticipants);
            });
        }
        else
        {
            // there are no remote write participants to notify, so we can finish it all in one shot
            StorageBatch.Collect(entry.TransactionId);
        }
    }

    private async Task AbortCommits(TransactionalStatus status, int from = 0)
    {
        List<Task> pending = new List<Task>();

        // Empty the back of the commit queue, starting at specified position
        for (int i = from; i < _commitQueue.Count; i++)
        {
            pending.Add(NotifyOfAbort(_commitQueue[i], i == from ? status : TransactionalStatus.CascadingAbort, exception: null));
        }
        
        _commitQueue.RemoveFromBack(_commitQueue.Count - from);

        pending.Add(RWLock.AbortExecutingTransactions(exception: null));
        await Task.WhenAll(pending);
    }
}
