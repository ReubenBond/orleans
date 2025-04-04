using System;
using System.Threading.Tasks;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions.State;

internal class TransactionalResource<TState> : ITransactionalResource
           where TState : class, new()
{
    private readonly TransactionQueue<TState> _queue;

    public TransactionalResource(TransactionQueue<TState> queue)
    {
        _queue = queue;
    }

    public async Task<TransactionalStatus> CommitReadOnly(Guid transactionId, AccessCounter accessCount, DateTime timeStamp)
    {
        // validate the lock
        var (status, record) = await _queue.RWLock.ValidateLock(transactionId, accessCount);
        var valid = status == TransactionalStatus.Ok;

        record.Timestamp = timeStamp;
        record.Role = CommitRole.ReadOnly;
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

    public async Task Abort(Guid transactionId)
    {
        await _queue.Ready();
        // release the lock
        _queue.RWLock.Rollback(transactionId);

        _queue.RWLock.Notify();
    }

    public async Task Cancel(Guid transactionId, DateTime timeStamp, TransactionalStatus status)
    {
        await _queue.Ready();
        await _queue.NotifyOfCancel(transactionId, timeStamp, status);
    }

    public async Task Confirm(Guid transactionId, DateTime timeStamp)
    {
        await _queue.Ready();
        await _queue.NotifyOfConfirm(transactionId, timeStamp);
    }

    public async Task Prepare(Guid transactionId, AccessCounter accessCount, DateTime timeStamp, ParticipantId transactionManager)
    {
        await _queue.NotifyOfPrepare(transactionId, accessCount, timeStamp, transactionManager);
    }
}
