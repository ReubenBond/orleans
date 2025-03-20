// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Orleans.Streams;

namespace Orleans.Hosting
{
    /// <summary>
    /// Deployment configuration that reads from orleans cluster configuration
    /// </summary>
    public class StaticClusterDeploymentOptions : IDeploymentConfiguration
    {
        /// <summary>
        /// Gets or sets the silo names.
        /// </summary>
        /// <value>The silo names.</value>
        public IList<string> SiloNames { get; set; } = new List<string>();

        /// <inheritdoc/>
        IList<string> IDeploymentConfiguration.GetAllSiloNames()
        {
            return this.SiloNames;
        }
    }
}
