// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;

namespace Orleans.Statistics
{
    /// <summary>
    /// Provides functionality for accessing statistics relating to the application environment.
    /// </summary>
    [Obsolete($"This functionality will be removed, use {nameof(IEnvironmentStatisticsProvider)}.{nameof(IEnvironmentStatisticsProvider.GetEnvironmentStatistics)} instead.")]
    public interface IAppEnvironmentStatistics
    {
        /// <summary>
        /// Gets the total memory usage, in bytes, if available.
        /// </summary>
        long? MemoryUsage { get; }
    }
}