// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Orleans.TestingHost
{
    /// <summary>
    /// Allows implementations to configure the silo builder when starting up each silo in the test cluster.
    /// </summary>
    public interface ISiloConfigurator
    {
        /// <summary>
        /// Configures the silo builder.
        /// </summary>
        /// <param name="siloBuilder">The silo builder.</param>
        void Configure(ISiloBuilder siloBuilder);
    }
}