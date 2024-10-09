using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.TestKit.Correctnesss;

namespace Orleans.Transactions.TestKit
{
    [StatelessWorker]
    public class TransactionCoordinatorGrain : Grain, ITransactionCoordinatorGrain
    {
        public Task MultiGrainSet(List<ITransactionTestGrain> grains, int numberToAdd)
        {
            return Task.WhenAll(grains.Select(g => g.Set(numberToAdd)));
        }

        public Task MultiGrainAdd(List<ITransactionTestGrain> grains, int numberToAdd)
        {
            return Task.WhenAll(grains.Select(g => g.Add(numberToAdd)));
        }

        public Task MultiGrainDouble(List<ITransactionTestGrain> grains)
        {
            return Task.WhenAll(grains.Select(Double));
        }

        public Task OrphanCallTransaction(ITransactionTestGrain grain)
        {
            _ = grain.Add(1000);
            return Task.CompletedTask;
        }

        public async Task AddAndThrow(ITransactionTestGrain grain, int numberToAdd)
        {
            await grain.Add(numberToAdd);
            throw new InvalidOperationException("This should abort the transaction");
        }

        public async Task MultiGrainAddAndThrow(List<ITransactionTestGrain> grain, List<ITransactionTestGrain> grains, int numberToAdd)
        {
            await Task.WhenAll(grains.Select(g => g.Add(numberToAdd)));
            await Task.WhenAll(grain.Select(tg => tg.AddAndThrow(numberToAdd)));
        }

        public Task MultiGrainSetBit(List<ITransactionalBitArrayGrain> grains, int bitIndex)
        {
            return Task.WhenAll(grains.Select(g => g.SetBit(bitIndex)));
        }

        public Task MultiGrainAdd(ITransactionCommitterTestGrain committer, ITransactionCommitOperation<IRemoteCommitService> operation, List<ITransactionTestGrain> grains, int numberToAdd)
        {
            List<Task> tasks = new List<Task>();
            tasks.AddRange(grains.Select(g => g.Add(numberToAdd)));
            tasks.Add(committer.Commit(operation));
            return Task.WhenAll(tasks);
        }

        private async Task Double(ITransactionTestGrain grain)
        {
            int[] values = await grain.Get();
            await grain.Add(values[0]);
        }

        public async Task MultiGrainDoubleByRWRW(List<ITransactionTestGrain> grains, int numberToAdd)
        {
            await Task.WhenAll(grains.Select(g => g.Get()));
            await Task.WhenAll(grains.Select(g => g.Add(numberToAdd)));
            await Task.WhenAll(grains.Select(g => g.Get()));
            await Task.WhenAll(grains.Select(g => g.Add(numberToAdd)));
        }

        public async Task MultiGrainDoubleByWRWR(List<ITransactionTestGrain> grains, int numberToAdd)
        {
            await Task.WhenAll(grains.Select(g => g.Add(numberToAdd)));
            await Task.WhenAll(grains.Select(g => g.Get()));
            await Task.WhenAll(grains.Select(g => g.Add(numberToAdd)));
            await Task.WhenAll(grains.Select(g => g.Get()));
        }
    }
}
