namespace BenchmarkGrainInterfaces.Transaction
{
    public interface ITransactionGrain : IGrainWithIntegerKey
    {
        [Transaction(TransactionOption.CreateOrJoin)]
        ValueTask Run();
    }
}
