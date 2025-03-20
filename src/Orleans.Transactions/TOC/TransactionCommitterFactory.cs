// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.DependencyInjection;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions
{
    public class TransactionCommitterFactory : ITransactionCommitterFactory
    {
        private readonly IGrainContextAccessor contextAccessor;

        public TransactionCommitterFactory(IGrainContextAccessor contextAccessor)
        {
            this.contextAccessor = contextAccessor;
        }

        public ITransactionCommitter<TService> Create<TService>(ITransactionCommitterConfiguration config) where TService : class
        {
            var currentContext = contextAccessor.GrainContext;
            TransactionCommitter<TService> transactionalState = ActivatorUtilities.CreateInstance<TransactionCommitter<TService>>(currentContext.ActivationServices, config, this.contextAccessor);
            transactionalState.Participate(currentContext.ObservableLifecycle);
            return transactionalState;
        }
    }
}
