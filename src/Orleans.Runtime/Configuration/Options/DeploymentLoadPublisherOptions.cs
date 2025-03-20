// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Orleans.Configuration;

/// <summary>
/// Options for configuring deployment load publishing.
/// </summary>
public class DeploymentLoadPublisherOptions
{
    /// <summary>
    /// Interval in which deployment statistics are published.
    /// </summary>
    public TimeSpan DeploymentLoadPublisherRefreshTime { get; set; } = DEFAULT_DEPLOYMENT_LOAD_PUBLISHER_REFRESH_TIME;
    public static readonly TimeSpan DEFAULT_DEPLOYMENT_LOAD_PUBLISHER_REFRESH_TIME = TimeSpan.FromSeconds(1);
}