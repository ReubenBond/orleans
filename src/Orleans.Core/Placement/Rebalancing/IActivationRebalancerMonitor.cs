using System;
using System.Threading.Tasks;


namespace Orleans.Placement.Rebalancing;

[Alias("IActivationRebalancerMonitor")]
internal interface IActivationRebalancerMonitor : ISystemTarget, IActivationRebalancer
{
    /// <summary>
    /// The period on which the <see cref="IActivationRebalancerWorker"/> must heartbeat to the designated monitor.
    /// </summary>
    public static readonly TimeSpan WorkerHeartbeatPeriod = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Invoked periodically by the <see cref="IActivationRebalancerWorker"/> on the designated monitor.
    /// </summary>
    [Alias("Heartbeat")] Task Heartbeat();
}
