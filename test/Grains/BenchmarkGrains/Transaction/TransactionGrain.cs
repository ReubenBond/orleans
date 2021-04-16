using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.CodeGeneration;
using Orleans.Transactions.Abstractions;
using BenchmarkGrainInterfaces.Transaction;

namespace BenchmarkGrains.Transaction
{
    [Serializable]
    [Orleans.GenerateSerializer]
    public class Info
    {
        [Orleans.Id(0)]
        public int Value { get; set; }
    }

    public class TransactionGrain : Grain, ITransactionGrain
    {
        private ITransactionalState<Info> info;

        public TransactionGrain(
            [TransactionalState("Info")] ITransactionalState<Info> info)
        {
            this.info = info ?? throw new ArgumentNullException(nameof(info));
        }

        public Task Run()
        {
            return this.info.PerformUpdate(s => s.Value += 1);
        }
    }
}
