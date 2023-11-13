namespace BenchmarkGrainInterfaces.Transaction
{
    [Alias("BenchmarkGrainInterfaces.Transaction.ITransactionRootGrain")]
    public interface ITransactionRootGrain : IGrainWithGuidKey
    {
        [Transaction(TransactionOption.Create)]
        [Alias("Run")]
        Task Run(List<int> grains);
    }
}
