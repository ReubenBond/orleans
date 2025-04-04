using Orleans.Serialization;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions;

public class DefaultTransactionDataCopier<TData>(DeepCopier<TData> deepCopier) : ITransactionDataCopier<TData>
{
    public TData DeepCopy(TData original) => deepCopier.Copy(original);
}