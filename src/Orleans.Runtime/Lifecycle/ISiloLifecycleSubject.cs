// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Orleans.Runtime
{
    /// <summary>
    /// Observable silo lifecycle and observer.
    /// </summary>
    public interface ISiloLifecycleSubject : ISiloLifecycle, ILifecycleObserver
    {
    }
}
