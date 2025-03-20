// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Hosting;

namespace Orleans.TestingHost;

/// <summary>
/// Allows implementations to configure the host builder when starting up each silo in the test cluster.
/// </summary>
public interface IHostConfigurator
{
    /// <summary>
    /// Configures the host builder.
    /// </summary>
    /// <param name="hostBuilder">The host builder.</param>
    void Configure(IHostBuilder hostBuilder);
}