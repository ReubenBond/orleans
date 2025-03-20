// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Orleans.Transactions.TestKit
{
    public interface IControlledTransactionFaultInjector : ITransactionFaultInjector
    {
        bool InjectBeforeStore { get; set; }
        bool InjectAfterStore { get; set; }
    }
}
