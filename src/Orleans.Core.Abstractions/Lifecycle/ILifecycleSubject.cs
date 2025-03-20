// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Orleans
{
    /// <summary>
    /// Both a lifecycle observer and observable lifecycle.
    /// </summary>
    public interface ILifecycleSubject : ILifecycleObservable, ILifecycleObserver
    {
    }
}
