// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;

namespace Orleans.Streams
{
    /// <summary>
    /// Interface for accessing the deployment configuration.
    /// </summary>
    public interface IDeploymentConfiguration
    {
        /// <summary>
        /// Get the silo instance names for all configured silos.
        /// </summary>
        /// <returns>The list of silo names.</returns>
        IList<string> GetAllSiloNames();
    }
}
