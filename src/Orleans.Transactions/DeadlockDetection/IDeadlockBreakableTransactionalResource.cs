using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace Orleans.Transactions.DeadlockDetection;

internal interface IDeadlockBreakableTransactionalResource
{
    Task BreakLocks();
}

internal interface IDeadlockResourceExtension : IGrainExtension
{
    [AlwaysInterleave]
    [Transaction(TransactionOption.Suppress)]
    Task BreakLocks(string resourceId);
}
