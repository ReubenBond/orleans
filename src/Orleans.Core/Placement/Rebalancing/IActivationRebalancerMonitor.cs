// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

namespace Orleans.Placement.Rebalancing;

[Alias("IActivationRebalancerMonitor")]
internal interface IActivationRebalancerMonitor : ISystemTarget, IActivationRebalancer
{
    /// <summary>
    /// The period on which the <see cref="IActivationRebalancerWorker"/> must report back to the monitor.
    /// </summary>
    public static readonly TimeSpan WorkerReportPeriod = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Invoked periodically by the <see cref="IActivationRebalancerWorker"/>.
    /// </summary>
    [Alias("Report")] Task Report(RebalancingReport report);
}
