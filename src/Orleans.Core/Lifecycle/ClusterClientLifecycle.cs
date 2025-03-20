// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;

namespace Orleans
{
    /// <summary>
    /// Implementation of <see cref="IClusterClientLifecycle"/>.
    /// </summary>
    internal class ClusterClientLifecycle : LifecycleSubject, IClusterClientLifecycle
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ClusterClientLifecycle"/> class.
        /// </summary>
        /// <param name="logger">The logger.</param>
        public ClusterClientLifecycle(ILogger logger) : base(logger)
        {
        }
    }
}