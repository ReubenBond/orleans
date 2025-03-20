// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Orleans.Transactions.State;

internal interface IActivationLifetime
{
    CancellationToken OnDeactivating { get; }

    IDisposable BlockDeactivation();
}
