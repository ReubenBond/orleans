// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;

namespace Orleans.Configuration
{
    /// <summary>Configures development clustering options</summary>
    public class DevelopmentClusterMembershipOptions
    {
        /// <summary>
        /// Gets or sets the seed node to find the membership system grain.
        /// </summary>
        public IPEndPoint PrimarySiloEndpoint { get; set; }
    }
}