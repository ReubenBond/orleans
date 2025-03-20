// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Threading;

namespace Orleans.Transactions.State
{
    internal interface IActivationLifetime
    {
        CancellationToken OnDeactivating { get; }

        IDisposable BlockDeactivation();
    }
}
