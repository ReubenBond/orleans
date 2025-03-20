// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Orleans.Configuration;

namespace Orleans.Hosting;

public static class ClientBuilderExtensions
{
    /// <summary>
    /// Configure cluster client to use event hub persistent streams.
    /// </summary>
    public static IClientBuilder AddEventHubStreams(
       this IClientBuilder builder,
       string name,
       Action<IClusterClientEventHubStreamConfigurator> configure)
    {
        var configurator = new ClusterClientEventHubStreamConfigurator(name,builder);
        configure?.Invoke(configurator);
        return builder;
    }

    /// <summary>
    /// Configure cluster client to use event hub persistent streams with default settings.
    /// </summary>
    public static IClientBuilder AddEventHubStreams(
        this IClientBuilder builder,
        string name, Action<EventHubOptions> configureEventHub)
    {
        builder.AddEventHubStreams(name, b=>b.ConfigureEventHub(ob => ob.Configure(configureEventHub)));
        return builder;
    }
}