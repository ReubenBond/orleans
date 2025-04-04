using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions;

internal sealed class TransactionManagerExtension(IGrainContext grainContext) : ITransactionManagerExtension
{
    private readonly ResourceFactoryRegistry<ITransactionManager> _factories = grainContext.GetResourceFactoryRegistry<ITransactionManager>();
    private readonly Dictionary<string, ITransactionManager> _managers = [];

    public Task Ping(string resourceId, Guid transactionId, DateTime timeStamp, ParticipantId resource)
        => GetManager(resourceId).Ping(transactionId, timeStamp, resource);

    public Task<TransactionalStatus> PrepareAndCommit(string resourceId, Guid transactionId, AccessCounter accessCount, DateTime timeStamp, List<ParticipantId> writeResources, int totalResources)
        => GetManager(resourceId).PrepareAndCommit(transactionId, accessCount, timeStamp, writeResources, totalResources);

    public Task Prepared(string resourceId, Guid transactionId, DateTime timestamp, ParticipantId resource, TransactionalStatus status)
        => GetManager(resourceId).Prepared(transactionId, timestamp, resource, status);

    private ITransactionManager GetManager(string resourceId)
    {
        if (!_managers.TryGetValue(resourceId, out var manager))
        {
            _managers[resourceId] = manager = _factories[resourceId].Invoke();
        }

        return manager;
    }
}
