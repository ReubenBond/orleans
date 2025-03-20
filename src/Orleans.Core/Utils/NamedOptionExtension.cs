// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans;

/// <summary>
/// Extensions for working with named option classes.
/// </summary>
public static class NamedOptionExtensions
{
    /// <summary>
    /// Gets a named options instance.
    /// </summary>
    /// <typeparam name="TOption">The type of the t option.</typeparam>
    /// <param name="services">The services.</param>
    /// <param name="name">The name.</param>
    /// <returns>TOption.</returns>
    public static TOption GetOptionsByName<TOption>(this IServiceProvider services, string name)
        where TOption : class, new()
    {
        return services.GetRequiredService<IOptionsMonitor<TOption>>().Get(name);
    }
}
