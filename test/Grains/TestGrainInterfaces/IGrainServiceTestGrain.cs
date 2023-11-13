using Orleans.Runtime;

namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.IGrainServiceTestGrain")]
    public interface IGrainServiceTestGrain : IGrainWithIntegerKey
    {
        [Alias("GetHelloWorldUsingCustomService")]
        Task<string> GetHelloWorldUsingCustomService();
        [Alias("CallHasStarted")]
        Task<bool> CallHasStarted();
        [Alias("CallHasStartedInBackground")]
        Task<bool> CallHasStartedInBackground();
        [Alias("CallHasInit")]
        Task<bool> CallHasInit();
        [Alias("GetServiceConfigProperty")]
        Task<string> GetServiceConfigProperty();
        [Alias("EchoViaExtension")]
        Task<string> EchoViaExtension(string what);
    }

    [Alias("UnitTests.GrainInterfaces.IEchoExtension")]
    public interface IEchoExtension : IGrainExtension
    {
        [Alias("Echo")]
        Task<string> Echo(string what);
    }
}
