using Orleans.Runtime;
using Orleans.Services;

namespace Tester
{
    [Alias("Tester.ITestGrainService")]
    public interface ITestGrainService : IGrainService
    {
        [Alias("GetHelloWorldUsingCustomService")]
        Task<string> GetHelloWorldUsingCustomService(GrainReference reference);
        [Alias("HasStarted")]
        Task<bool> HasStarted();
        [Alias("HasStartedInBackground")]
        Task<bool> HasStartedInBackground();
        [Alias("HasInit")]
        Task<bool> HasInit();
        [Alias("GetServiceConfigProperty")]
        Task<string> GetServiceConfigProperty();
    }

    public interface ITestGrainServiceClient : IGrainServiceClient<ITestGrainService>
    {
        Task<string> GetHelloWorldUsingCustomService();
        Task<bool> HasStarted();
        Task<bool> HasStartedInBackground();
        Task<bool> HasInit();
        Task<string> GetServiceConfigProperty();
        Task<string> EchoViaExtension(string what);
    }
}