using Orleans.Runtime;

namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.IStressTestGrain")]
    internal interface IStressTestGrain : IGrainWithIntegerKey
    {
        [Alias("GetLabel")]
        Task<string> GetLabel();

        [Alias("SetLabel")]
        Task SetLabel(string label);

        [Alias("PingOthers")]
        Task PingOthers(long[] others);

        [Alias("LookUpMany")]
        Task<List<Tuple<GrainId, int, List<Tuple<SiloAddress, ActivationId>>>>> LookUpMany(SiloAddress destination, List<Tuple<GrainId, int>> grainAndETagList, int retries = 0);

        [Alias("Send")]
        Task Send(byte[] data);

        [Alias("Echo")]
        Task<byte[]> Echo(byte[] data);

        [Alias("Ping")]
        Task Ping(byte[] data);

        [Alias("PingWithDelay")]
        Task PingWithDelay(byte[] data, TimeSpan delay);

        [Alias("GetGrainReference")]
        Task<IStressTestGrain> GetGrainReference();

        [Alias("DeactivateSelf")]
        Task DeactivateSelf();
    }
}
