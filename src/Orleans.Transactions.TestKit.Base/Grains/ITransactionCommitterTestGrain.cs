
using Orleans.Transactions.Abstractions;
using System.Threading.Tasks;

namespace Orleans.Transactions.TestKit
{
    [Alias("Orleans.Transactions.TestKit.ITransactionCommitterTestGrain")]
    public interface ITransactionCommitterTestGrain : IGrainWithGuidKey
    {
        [Transaction(TransactionOption.Join)]
        [Alias("Commit")]
        Task Commit(ITransactionCommitOperation<IRemoteCommitService> operation);
    }
}
