using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace Orleans.Transactions.Abstractions
{
    [Alias("Orleans.Transactions.Abstractions.ITransactionManagerExtension")]
    public interface ITransactionManagerExtension : IGrainExtension
    {
        [AlwaysInterleave]
        [Transaction(TransactionOption.Suppress)]
        [Alias("PrepareAndCommit")]
        Task<TransactionalStatus> PrepareAndCommit(string resourceId, Guid transactionId, AccessCounter accessCount, DateTime timeStamp, List<ParticipantId> writeResources, int totalParticipants);

        [AlwaysInterleave]
        [Transaction(TransactionOption.Suppress)]
        [OneWay]
        [Alias("Prepared")]
        Task Prepared(string resourceId, Guid transactionId, DateTime timestamp, ParticipantId resource, TransactionalStatus status);

        [AlwaysInterleave]
        [Transaction(TransactionOption.Suppress)]
        [OneWay]
        [Alias("Ping")]
        Task Ping(string resourceId, Guid transactionId, DateTime timeStamp, ParticipantId resource);
    }
}
