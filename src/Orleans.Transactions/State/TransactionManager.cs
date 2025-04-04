using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions.State;

internal sealed class TransactionManager<TState> : ITransactionManager where TState : class, new()
{
    private readonly TransactionQueue<TState> _queue;

    public TransactionManager(TransactionQueue<TState> queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        _queue = queue;
    }

    public async Task<TransactionalStatus> PrepareAndCommit(Guid transactionId, AccessCounter accessCount, DateTime timeStamp, List<ParticipantId> writeResources, int totalResources)
    {
        // validate the lock
        var (status, record) = await _queue.RWLock.ValidateLock(transactionId, accessCount);
        var valid = status == TransactionalStatus.Ok;

        record.Timestamp = timeStamp;
        record.Role = CommitRole.LocalCommit; // we are the TM
        record.WaitCount = totalResources - 1;
        record.WaitingSince = DateTime.UtcNow;
        record.WriteParticipants = writeResources;
        record.PromiseForTA = new TaskCompletionSource<TransactionalStatus>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!valid)
        {
            await _queue.NotifyOfAbort(record, status, exception: null);
        }
        else
        {
            _queue.Clock.Merge(record.Timestamp);
        }

        _queue.RWLock.Notify();
        return await record.PromiseForTA.Task;
    }

    public Task Prepared(Guid transactionId, DateTime timeStamp, ParticipantId resource, TransactionalStatus status)
    {
        return _queue.NotifyOfPrepared(transactionId, timeStamp, status);
    }

    public async Task Ping(Guid transactionId, DateTime timeStamp, ParticipantId resource)
    {
        await _queue.Ready();
        await _queue.NotifyOfPing(transactionId, timeStamp, resource);
    }
}
