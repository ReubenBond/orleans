// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Orleans.Runtime;

internal class GrainContextAccessor : IGrainContextAccessor
{
    private readonly HostedClient _hostedClient;

    public GrainContextAccessor(HostedClient hostedClient)
    {
        _hostedClient = hostedClient;
    }

    public IGrainContext GrainContext => RuntimeContext.Current ?? _hostedClient;
}
