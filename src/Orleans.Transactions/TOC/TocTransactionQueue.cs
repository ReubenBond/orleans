using System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.State;

namespace Orleans.Transactions.TOC;

internal class TocTransactionQueue<TService>(
    TService service,
    IOptions<TransactionalStateOptions> options,
    ParticipantId resource,
    IGrainContext grainContext,
    ITransactionalStateStorage<TransactionCommitter<TService>.OperationState> storage,
    TimeProvider timeProvider,
    ILogger logger)
    : TransactionQueue<TransactionCommitter<TService>.OperationState>(options, resource, grainContext, storage, timeProvider, logger)
            where TService : class
{
    protected override void OnLocalCommit(TransactionRecord<TransactionCommitter<TService>.OperationState> entry)
    {
        StorageBatch.AddStorePreCondition(() => entry.State.Operation.Commit(entry.TransactionId, service));
        base.OnLocalCommit(entry);
    }
}
