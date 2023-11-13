using System.Threading.Tasks;
using Orleans.Concurrency;

namespace Orleans.Runtime
{
    [Alias("Orleans.Runtime.IDeploymentLoadPublisher")]
    internal interface IDeploymentLoadPublisher : ISystemTarget
    {
        [OneWay]
        [Alias("UpdateRuntimeStatistics")]
        Task UpdateRuntimeStatistics(SiloAddress siloAddress, SiloRuntimeStatistics siloStats);
    }
}
