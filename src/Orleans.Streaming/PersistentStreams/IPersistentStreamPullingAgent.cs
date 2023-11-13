using System.Threading.Tasks;
using Orleans.Providers.Streams.Common;

namespace Orleans.Streams
{
    [Alias("Orleans.Streams.IPersistentStreamPullingAgent")]
    internal interface IPersistentStreamPullingAgent : ISystemTarget, IStreamProducerExtension
    {
        [Alias("Initialize")]
        Task Initialize();
        [Alias("Shutdown")]
        Task Shutdown();
    }

    [Alias("Orleans.Streams.IPersistentStreamPullingManager")]
    internal interface IPersistentStreamPullingManager : ISystemTarget
    {
        [Alias("Initialize")]
        Task Initialize();
        [Alias("Stop")]
        Task Stop();
        [Alias("StartAgents")]
        Task StartAgents();
        [Alias("StopAgents")]
        Task StopAgents();
        [Alias("ExecuteCommand")]
        Task<object> ExecuteCommand(PersistentStreamProviderCommand command, object arg);
    }
}
