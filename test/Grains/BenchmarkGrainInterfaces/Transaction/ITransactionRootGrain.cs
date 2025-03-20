// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace BenchmarkGrainInterfaces.Transaction;

public interface ITransactionRootGrain : IGrainWithGuidKey
{
    [Transaction(TransactionOption.Create)]
    Task Run(List<int> grains);
}
