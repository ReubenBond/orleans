using System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.State;

namespace Orleans.Transactions.TOC
{
    internal class TocTransactionQueue<TService> : TransactionQueue<TransactionCommitter<TService>.OperationState>
                where TService : class
    {
        private readonly TService service;

        public TocTransactionQueue(
            TService service,
            IOptions<TransactionalStateOptions> options,
            ParticipantId resource,
            Action deactivate,
            ITransactionalStateStorage<TransactionCommitter<TService>.OperationState> storage,
            TimeProvider timeProvider,
            ILogger logger,
            ActivationLifetime activationLifetime)
            : base(options, resource, deactivate, storage, timeProvider, logger, activationLifetime)
        {
            this.service = service;
        }

        protected override void OnLocalCommit(TransactionRecord<TransactionCommitter<TService>.OperationState> entry)
        {
            base.storageBatch.AddStorePreCondition(() => entry.State.Operation.Commit(entry.TransactionId, this.service));
            base.OnLocalCommit(entry);
        }
    }
}
