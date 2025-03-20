// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Orleans.Transactions.Abstractions
{
    public interface ITransactionCommitterConfiguration
    {
        string ServiceName { get; }
        string StorageName { get; }
    }
}
