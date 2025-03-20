// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Threading.Tasks;

namespace Orleans.Transactions.Abstractions
{
    public interface ITransactionCommitOperation<TService>
        where TService : class
    {
        Task<bool> Commit(Guid transactionId, TService service);
    }

    public interface ITransactionCommitter<TService>
        where TService : class
    {
        Task OnCommit(ITransactionCommitOperation<TService> operation);
    }
}
