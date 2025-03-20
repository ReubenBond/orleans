// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Orleans.Serialization;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions;

public class DefaultTransactionDataCopier<TData> : ITransactionDataCopier<TData>
{
    private readonly DeepCopier<TData> deepCopier;

    public DefaultTransactionDataCopier(DeepCopier<TData> deepCopier)
    {
        this.deepCopier = deepCopier;
    }

    public TData DeepCopy(TData original)
    {
        return (TData)this.deepCopier.Copy(original);
    }
}
