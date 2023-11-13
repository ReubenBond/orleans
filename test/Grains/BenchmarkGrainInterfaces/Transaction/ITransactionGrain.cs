namespace BenchmarkGrainInterfaces.Transaction
{
    [Alias("BenchmarkGrainInterfaces.Transaction.ITransactionGrain")]
    public interface ITransactionGrain : IGrainWithIntegerKey
    {
        [Transaction(TransactionOption.CreateOrJoin)]
        [Alias("Run")]
        Task Run();
    }
}
