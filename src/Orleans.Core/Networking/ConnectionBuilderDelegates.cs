// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Connections;

namespace Orleans.Configuration;

internal class ConnectionBuilderDelegates
{
    private readonly List<Action<IConnectionBuilder>> configurationDelegates = new List<Action<IConnectionBuilder>>();

    public void Add(Action<IConnectionBuilder> configure)
        => this.configurationDelegates.Add(configure ?? throw new ArgumentNullException(nameof(configure)));

    public void Invoke(IConnectionBuilder builder)
    {
        foreach (var configureDelegate in this.configurationDelegates)
        {
            configureDelegate(builder);
        }
    }
}
