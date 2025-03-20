// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Orleans.Concurrency;
using BenchmarkGrainInterfaces.Transaction;

namespace BenchmarkGrains.Transaction
{
    [Reentrant]
    [StatelessWorker]
    public class TransactionRootGrain : Grain, ITransactionRootGrain
    {
        public Task Run(List<int> grains)
        {
            return Task.WhenAll(grains.Select(id => GrainFactory.GetGrain<ITransactionGrain>(id).Run()));
        }
    }
}
