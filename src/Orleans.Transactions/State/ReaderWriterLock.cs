using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions.State;

internal class ReadWriteLock<TState>
    where TState : class, new()
{
    private readonly TransactionalStateOptions _options;
    private readonly TransactionQueue<TState> _queue;
    private readonly BatchWorker _lockWorker;
    private readonly BatchWorker _storageWorker;
    private readonly ILogger _logger;
    private readonly ActivationLifetime _activationLifetime;

    // the linked list of lock groups
    // the head is the group that is currently holding the lock
    private LockGroup _currentGroup;

    // cache the last known minimum so we don't have to recompute it as much
    private DateTime _cachedMin = DateTime.MaxValue;
    private Guid _cachedMinId;

    // group of non-conflicting transactions collectively acquiring/releasing the lock
    private sealed class LockGroup : Dictionary<Guid, TransactionRecord<TState>>
    {
        public int FillCount;
        public List<Action> Tasks; // the tasks for executing the waiting operations
        public LockGroup Next; // queued-up transactions waiting to acquire lock
        public DateTime? Deadline;
        public void Reset()
        {
            FillCount = 0;
            Tasks = null;
            Deadline = null;
            Clear();
        }
    }

    public ReadWriteLock(
        IOptions<TransactionalStateOptions> options,
        TransactionQueue<TState> queue,
        BatchWorker storageWorker,
        ILogger logger,
        ActivationLifetime activationLifetime)
    {
        _options = options.Value;
        _queue = queue;
        _storageWorker = storageWorker;
        _logger = logger;
        _activationLifetime = activationLifetime;
        _lockWorker = new BatchWorkerFromDelegate(LockWork, _activationLifetime.OnDeactivating);
    }

    public async Task<TResult> EnterLock<TResult>(Guid transactionId, DateTime priority,
        AccessCounter counter, bool isRead, Func<TResult> task)
    {
        bool rollbacksOccurred = false;
        List<Task> cleanup = new List<Task>();

        await _queue.Ready();

        // search active transactions
        if (Find(transactionId, isRead, out var group, out var record))
        {
            // check if we lost some reads or writes already
            if (counter.Reads > record.NumberReads || counter.Writes > record.NumberWrites)
            {
                throw new OrleansBrokenTransactionLockException(transactionId.ToString(), "when re-entering lock");
            }

            // check if the operation conflicts with other transactions in the group
            if (HasConflict(isRead, priority, transactionId, group, out var resolvable))
            {
                if (!resolvable)
                {
                    throw new OrleansTransactionLockUpgradeException(transactionId.ToString());
                }

                // rollback all conflicts
                var conflicts = Conflicts(transactionId, group).ToList();

                if (conflicts.Count > 0)
                {
                    foreach (var r in conflicts)
                    {
                        cleanup.Add(Rollback(r, true));
                        rollbacksOccurred = true;
                    }
                }
            }
        }
        else
        {
            // check if we were supposed to already hold this lock
            if (counter.Reads + counter.Writes > 0)
            {
                throw new OrleansBrokenTransactionLockException(transactionId.ToString(), "when trying to re-enter lock");
            }

            // update the lock deadline
            if (group == _currentGroup)
            {
                group.Deadline = DateTime.UtcNow + _options.LockTimeout;

                if (_logger.IsEnabled(LogLevel.Trace))
                    _logger.LogTrace("Set lock expiration at {Deadline}", group.Deadline.Value.ToString("o"));
            }

            // create a new record for this transaction
            record = new TransactionRecord<TState>
            {
                TransactionId = transactionId,
                Priority = priority,
                Deadline = DateTime.UtcNow + _options.LockAcquireTimeout
            };

            group.Add(transactionId, record);
            group.FillCount++;

            if (_logger.IsEnabled(LogLevel.Trace))
            {
                if (group == _currentGroup)
                    _logger.LogTrace("Enter-lock {TransactionId} Fill count={FillCount}", transactionId, group.FillCount);
                else
                    _logger.LogTrace("Enter-lock-queue {TransactionId} Fill count={FillCount}", transactionId, group.FillCount);
            }
        }

        var result =
            new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        void completion()
        {
            try
            {
                result.TrySetResult(task());
            }
            catch (Exception exception)
            {
                result.TrySetException(exception);
            }
        }

        if (group != _currentGroup)
        {
            // task will be executed once its group acquires the lock

            if (group.Tasks == null)
                group.Tasks = new List<Action>();

            group.Tasks.Add(completion);
        }
        else
        {
            // execute task right now
            completion();
        }

        if (isRead)
        {
            record.AddRead();
        }
        else
        {
            record.AddWrite();
        }

        if (rollbacksOccurred)
        {
            _lockWorker.Notify();
        }
        else if (group.Deadline.HasValue)
        {
            _lockWorker.Notify(group.Deadline.Value);
        }

        await Task.WhenAll(cleanup);
        return await result.Task;
    }

    public async Task<(TransactionalStatus Status, TransactionRecord<TState> State)> ValidateLock(Guid transactionId, AccessCounter accessCount)
    {
        if (_currentGroup == null || !_currentGroup.TryGetValue(transactionId, out TransactionRecord<TState> record))
        {
            return (TransactionalStatus.BrokenLock, new TransactionRecord<TState>());
        }

        if (record.NumberReads != accessCount.Reads
            || record.NumberWrites != accessCount.Writes)
        {
            await Rollback(transactionId, true);
            return (TransactionalStatus.LockValidationFailed, record);
        }

        return (TransactionalStatus.Ok, record);
    }

    public void Notify()
    {
        _lockWorker.Notify();
    }

    public bool TryGetRecord(Guid transactionId, out TransactionRecord<TState> record)
    {
        return _currentGroup.TryGetValue(transactionId, out record);
    }

    public Task AbortExecutingTransactions(Exception exception)
    {
        if (_currentGroup != null)
        {
            Task[] pending = _currentGroup.Select(g => BreakLock(g.Key, g.Value, exception)).ToArray();
            _currentGroup.Reset();
            return Task.WhenAll(pending);
        }
        return Task.CompletedTask;
    }

    private Task BreakLock(Guid transactionId, TransactionRecord<TState> entry, Exception exception)
    {
        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("Break-lock for transaction {TransactionId}", transactionId);

        return _queue.NotifyOfAbort(entry, TransactionalStatus.BrokenLock, exception);
    }

    public void AbortQueuedTransactions()
    {
        var pos = _currentGroup?.Next;
        while (pos != null)
        {
            if (pos.Tasks != null)
            {
                foreach (var t in pos.Tasks)
                {
                    // running the task will abort the transaction because it is not in currentGroup
                    t();
                }
            }
            pos.Clear();
            pos = pos.Next;
        }
        if (_currentGroup != null)
            _currentGroup.Next = null;
    }

    public void Rollback(Guid guid) => _currentGroup?.Remove(guid);

    public Task Rollback(Guid guid, bool notify)
    {
        // no-op if the transaction never happened or already rolled back
        if (_currentGroup == null || !_currentGroup.Remove(guid, out var record))
        {
            return Task.CompletedTask;
        }

        // notify remote listeners
        return notify ? _queue.NotifyOfAbort(record, TransactionalStatus.BrokenLock, exception: null) : Task.CompletedTask;
    }

    private async Task LockWork()
    {
        // Stop pumping lock work if this activation is stopping/stopped.
        if (_activationLifetime.OnDeactivating.IsCancellationRequested) return;
        using var _ = _activationLifetime.BlockDeactivation();
        var now = DateTime.UtcNow;

        if (_currentGroup == null)
        {
            return;
        }

        // check if there are any group members that are ready to exit the lock
        if (_currentGroup.Count > 0)
        {
            if (LockExits(out var single, out var multiple))
            {
                if (single != null)
                {
                    await _queue.EnqueueCommit(single);
                }
                else if (multiple != null)
                {
                    foreach (var r in multiple)
                    {
                        await _queue.EnqueueCommit(r);
                    }
                }

                _lockWorker.Notify();
                _storageWorker.Notify();
            }

            else if (_currentGroup.Deadline.HasValue)
            {
                if (_currentGroup.Deadline.Value < now)
                {
                    // the lock group has timed out.
                    string txlist = string.Join(",", _currentGroup.Keys.Select(g => g.ToString()));
                    TimeSpan late = now - _currentGroup.Deadline.Value;
                    _logger.LogWarning("Break-lock timeout for transactions {TransactionIds}. {Late}ms late", txlist, Math.Floor(late.TotalMilliseconds));
                    await AbortExecutingTransactions(exception: null);
                    _lockWorker.Notify();
                }
                else
                {
                    if (_logger.IsEnabled(LogLevel.Trace))
                        _logger.LogTrace("Recheck lock expiration at {Deadline}", _currentGroup.Deadline.Value.ToString("o"));

                    // check again when the group expires
                    _lockWorker.Notify(_currentGroup.Deadline.Value);
                }
            }
            else
            {
                string txlist = string.Join(",", _currentGroup.Keys.Select(g => g.ToString()));
                _logger.LogWarning("Deadline not set for transactions {TransactionIds}", txlist);
            }
        }
        else
        {
            // the lock is empty, a new group can enter
            _currentGroup = _currentGroup.Next;

            if (_currentGroup != null)
            {
                _currentGroup.Deadline = now + _options.LockTimeout;

                // discard expired waiters that have no chance to succeed
                // because they have been waiting for the lock for a longer timespan than the 
                // total transaction timeout
                foreach (var kvp in _currentGroup)
                {
                    if (now > kvp.Value.Deadline)
                    {
                        _currentGroup.Remove(kvp.Key);

                        if (_logger.IsEnabled(LogLevel.Trace))
                            _logger.LogTrace("Expire-lock-waiter {Key}", kvp.Key);
                    }
                }

                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.LogTrace(
                        "Lock group size={Count} deadline={Deadline}",
                        _currentGroup.Count,
                        _currentGroup.Deadline is { } deadline ? deadline.ToString("O") : "none");
                    foreach (var kvp in _currentGroup)
                        _logger.LogTrace("Enter-lock {Key}", kvp.Key);
                }

                // execute all the read and update tasks
                if (_currentGroup.Tasks != null)
                {
                    foreach (var t in _currentGroup.Tasks)
                    {
                        t();
                    }
                }

                _lockWorker.Notify();
            }
        }
    }

    private bool Find(Guid guid, bool isRead, out LockGroup group, out TransactionRecord<TState> record)
    {
        if (_currentGroup == null)
        {
            group = _currentGroup = new LockGroup();
            record = null;
            return false;
        }

        group = null;
        var pos = _currentGroup;

        while (true)
        {
            if (pos.TryGetValue(guid, out record))
            {
                group = pos;
                return true;
            }

            // if we have not found a place to insert this op yet, and there is room, and no conflicts, use this one
            if (group == null
                && pos.FillCount < _options.MaxLockGroupSize
                && !HasConflict(isRead, DateTime.MaxValue, guid, pos, out _))
            {
                group = pos;
            }

            if (pos.Next == null) // we did not find this tx.
            {
                // add a new empty group to insert this tx, if we have not found one yet
                if (group == null)
                {
                    group = pos.Next = new LockGroup();
                }

                return false;
            }

            pos = pos.Next;
        }
    }

    private static bool HasConflict(bool isRead, DateTime priority, Guid transactionId, LockGroup group, out bool resolvable)
    {
        bool foundResolvableConflicts = false;

        foreach (var kvp in group)
        {
            if (kvp.Key != transactionId)
            {
                if (isRead && kvp.Value.NumberWrites == 0)
                {
                }
                else
                {
                    if (priority > kvp.Value.Priority)
                    {
                        resolvable = false;
                        return true;
                    }

                    foundResolvableConflicts = true;
                }
            }
        }

        resolvable = foundResolvableConflicts;
        return foundResolvableConflicts;
    }

    private static IEnumerable<Guid> Conflicts(Guid transactionId, LockGroup group)
    {
        foreach (var kvp in group)
        {
            if (kvp.Key != transactionId)
            {
                yield return kvp.Key;
            }
        }
    }

    private bool LockExits(out TransactionRecord<TState> single, out List<TransactionRecord<TState>> multiple)
    {
        single = null;
        multiple = null;

        // fast-path the one-element case
        if (_currentGroup.Count == 1)
        {
            var kvp = _currentGroup.First();
            if (kvp.Value.Role == CommitRole.NotYetDetermined) // has not received commit from TA
            {
                return false;
            }

            single = kvp.Value;

            _currentGroup.Remove(single.TransactionId);

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "Exit-lock {TransactionId} {Timestamp}",
                    single.TransactionId,
                    single.Timestamp.ToString("o"));
            }

            return true;
        }

        // find the current minimum, if we don't have a valid cache of it
        if (_cachedMin == DateTime.MaxValue
            || !_currentGroup.TryGetValue(_cachedMinId, out var record)
            || record.Role != CommitRole.NotYetDetermined
            || record.Timestamp != _cachedMin)
        {
            _cachedMin = DateTime.MaxValue;
            foreach (var kvp in _currentGroup)
            {
                if (kvp.Value.Role == CommitRole.NotYetDetermined) // has not received commit from TA
                {
                    if (_cachedMin > kvp.Value.Timestamp)
                    {
                        _cachedMin = kvp.Value.Timestamp;
                        _cachedMinId = kvp.Key;
                    }
                }
            }
        }

        // find released entries
        foreach (var kvp in _currentGroup)
        {
            if (kvp.Value.Role != CommitRole.NotYetDetermined) // ready to commit
            {
                if (kvp.Value.Timestamp < _cachedMin)
                {
                    if (multiple == null)
                    {
                        multiple = new List<TransactionRecord<TState>>();
                    }
                    multiple.Add(kvp.Value);
                }
            }
        }

        if (multiple == null)
        {
            return false;
        }

        multiple.Sort(Comparer);

        for (int i = 0; i < multiple.Count; i++)
        {
            _currentGroup.Remove(multiple[i].TransactionId);

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "Exit-lock ({Current}/{Count}) {TransactionId} {Timestamp}",
                    i,
                    multiple.Count,
                    multiple[i].TransactionId,
                    multiple[i].Timestamp.ToString("o"));
            }
        }

        return true;
    }

    private static int Comparer(TransactionRecord<TState> a, TransactionRecord<TState> b)
    {
        return a.Timestamp.CompareTo(b.Timestamp);
    }
}