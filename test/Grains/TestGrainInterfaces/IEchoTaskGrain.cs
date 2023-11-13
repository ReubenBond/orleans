using Orleans.Runtime;

namespace UnitTests.GrainInterfaces
{
    /// <summary>
    ///  A simple echo grain
    /// </summary>
    [Alias("UnitTests.GrainInterfaces.IEchoGrain")]
    public interface IEchoGrain : IGrainWithGuidKey
    {
        [Alias("GetLastEcho")]
        Task<string> GetLastEcho();

        [Alias("Echo")]
        Task<string> Echo(string data);
        [Alias("EchoError")]
        Task<string> EchoError(string data);
        [Alias("EchoNullable")]
        Task<Nullable<DateTime>> EchoNullable(Nullable<DateTime> value);
    }

    [GenerateMethodSerializers(typeof(GrainReference))]
    [Alias("UnitTests.GrainInterfaces.IEchoTaskGrain")]
    public interface IEchoTaskGrain : IGrainWithGuidKey
    {
        [Alias("GetMyIdAsync")]
        Task<int> GetMyIdAsync();

        [Alias("GetLastEchoAsync")]
        Task<string> GetLastEchoAsync();

        [Alias("EchoAsync")]
        Task<string> EchoAsync(string data);
        [Alias("EchoErrorAsync")]
        Task<string> EchoErrorAsync(string data);

        [ResponseTimeout("00:00:05")]
        [Alias("BlockingCallTimeoutAsync")]
        Task<int> BlockingCallTimeoutAsync(TimeSpan delay);

        [Alias("BlockingCallTimeoutNoResponseTimeoutOverrideAsync")]
        Task<int> BlockingCallTimeoutNoResponseTimeoutOverrideAsync(TimeSpan delay);

        [Alias("PingAsync")]
        Task PingAsync();

        [Alias("PingLocalSiloAsync")]
        Task PingLocalSiloAsync();
        [Alias("PingRemoteSiloAsync")]
        Task PingRemoteSiloAsync(SiloAddress siloAddress);
        [Alias("PingOtherSiloAsync")]
        Task PingOtherSiloAsync();
        [Alias("PingClusterMemberAsync")]
        Task PingClusterMemberAsync();
    }

    [Alias("UnitTests.GrainInterfaces.IBlockingEchoTaskGrain")]
    public interface IBlockingEchoTaskGrain : IGrainWithIntegerKey
    {
        [Alias("GetMyId")]
        Task<int> GetMyId();

        [Alias("GetLastEcho")]
        Task<string> GetLastEcho();

        [Alias("Echo")]
        Task<string> Echo(string data);
        [Alias("CallMethodTask_Await")]
        Task<string> CallMethodTask_Await(string data);
        [Alias("CallMethodAV_Await")]
        Task<string> CallMethodAV_Await(string data);
        [Alias("CallMethodTask_Block")]
        Task<string> CallMethodTask_Block(string data);
        [Alias("CallMethodAV_Block")]
        Task<string> CallMethodAV_Block(string data);
    }

    [Alias("UnitTests.GrainInterfaces.IReentrantBlockingEchoTaskGrain")]
    public interface IReentrantBlockingEchoTaskGrain : IGrainWithIntegerKey
    {
        [Alias("GetMyId")]
        Task<int> GetMyId();

        [Alias("GetLastEcho")]
        Task<string> GetLastEcho();

        [Alias("Echo")]
        Task<string> Echo(string data);
        [Alias("CallMethodTask_Await")]
        Task<string> CallMethodTask_Await(string data);
        [Alias("CallMethodAV_Await")]
        Task<string> CallMethodAV_Await(string data);
        [Alias("CallMethodTask_Block")]
        Task<string> CallMethodTask_Block(string data);
        [Alias("CallMethodAV_Block")]
        Task<string> CallMethodAV_Block(string data);
    }

    [Alias("UnitTests.GrainInterfaces.IDebuggerHelperTestGrain")]
    public interface IDebuggerHelperTestGrain : IGrain
    {
        [Alias("OrleansDebuggerHelper_GetGrainInstance_Test")]
        Task OrleansDebuggerHelper_GetGrainInstance_Test();
    }
}
