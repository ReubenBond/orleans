// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Orleans.Transactions.Abstractions;

[AttributeUsage(AttributeTargets.Parameter)]
public class TransactionCommitterAttribute : Attribute, IFacetMetadata, ITransactionCommitterConfiguration
{
    public string ServiceName { get; }
    public string StorageName { get; }

    public TransactionCommitterAttribute(string serviceName, string storageName = null)
    {
        this.ServiceName = serviceName;
        this.StorageName = storageName;
    }
}
