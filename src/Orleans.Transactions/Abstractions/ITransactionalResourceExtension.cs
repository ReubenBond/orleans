using System;
using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace Orleans.Transactions.Abstractions
{
    [Alias("Orleans.Transactions.Abstractions.ITransactionalResourceExtension")]
    public interface ITransactionalResourceExtension : IGrainExtension
    {
        [AlwaysInterleave]
        [Transaction(TransactionOption.Suppress)]
        [Alias("CommitReadOnly")]
        Task<TransactionalStatus> CommitReadOnly(string resourceId, Guid transactionId, AccessCounter accessCount, DateTime timeStamp);

        [AlwaysInterleave]
        [Transaction(TransactionOption.Suppress)]
        [Alias("Abort")]
        Task Abort(string resourceId, Guid transactionId);

        [AlwaysInterleave]
        [Transaction(TransactionOption.Suppress)]
        [Alias("Cancel")]
        Task Cancel(string resourceId, Guid transactionId, DateTime timeStamp, TransactionalStatus status);

        [AlwaysInterleave]
        [Transaction(TransactionOption.Suppress)]
        [Alias("Confirm")]
        Task Confirm(string resourceId, Guid transactionId, DateTime timeStamp);

        [AlwaysInterleave]
        [Transaction(TransactionOption.Suppress)]
        [OneWay]
        [Alias("Prepare")]
        Task Prepare(string resourceId, Guid transactionId, AccessCounter accessCount, DateTime timeStamp, ParticipantId transactionManager);
    }
}
