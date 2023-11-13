using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.TestKit.Correctnesss;

namespace Orleans.Transactions.TestKit
{
    [Alias("Orleans.Transactions.TestKit.ITransactionCoordinatorGrain")]
    public interface ITransactionCoordinatorGrain : IGrainWithGuidKey
    {
        [Transaction(TransactionOption.Create)]
        [Alias("MultiGrainSet")]
        Task MultiGrainSet(List<ITransactionTestGrain> grains, int numberToAdd);

        [Transaction(TransactionOption.Create)]
        [Alias("MultiGrainAdd")]
        Task MultiGrainAdd(List<ITransactionTestGrain> grains, int numberToAdd);

        [Transaction(TransactionOption.Create)]
        [Alias("MultiGrainDouble")]
        Task MultiGrainDouble(List<ITransactionTestGrain> grains);

        [Transaction(TransactionOption.Create)]
        [Alias("MultiGrainDoubleByRWRW")]
        Task MultiGrainDoubleByRWRW(List<ITransactionTestGrain> grains, int numberToAdd);

        [Transaction(TransactionOption.Create)]
        [Alias("MultiGrainDoubleByWRWR")]
        Task MultiGrainDoubleByWRWR(List<ITransactionTestGrain> grains, int numberToAdd);

        [Transaction(TransactionOption.Create)]
        [Alias("OrphanCallTransaction")]
        Task OrphanCallTransaction(ITransactionTestGrain grain);

        [Transaction(TransactionOption.Create)]
        [Alias("AddAndThrow")]
        Task AddAndThrow(ITransactionTestGrain grain, int numberToAdd);

        [Transaction(TransactionOption.Create)]
        [Alias("MultiGrainAddAndThrow")]
        Task MultiGrainAddAndThrow(List<ITransactionTestGrain> grain, List<ITransactionTestGrain> grains, int numberToAdd);

        [Transaction(TransactionOption.Create)]
        [Alias("MultiGrainSetBit")]
        Task MultiGrainSetBit(List<ITransactionalBitArrayGrain> grains, int bitIndex);

        [Transaction(TransactionOption.Create)]
        [Alias("MultiGrainAdd1")]
        Task MultiGrainAdd(ITransactionCommitterTestGrain committer, ITransactionCommitOperation<IRemoteCommitService> operation, List<ITransactionTestGrain> grains, int numberToAdd);
    }
}
