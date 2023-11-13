using System.Threading.Tasks;
using Orleans.EventSourcing;
using Orleans.Runtime;

namespace Orleans.SystemTargetInterfaces
{
    /// <summary>
    /// The  protocol gateway is a relay that forwards incoming protocol messages from other clusters
    /// to the appropriate grain in this cluster.
    /// </summary>
    [Alias("Orleans.SystemTargetInterfaces.ILogConsistencyProtocolGateway")]
    internal interface ILogConsistencyProtocolGateway : ISystemTarget
    {
        [Alias("RelayMessage")]
        Task<ILogConsistencyProtocolMessage> RelayMessage(GrainId id, ILogConsistencyProtocolMessage payload);
    }
}
