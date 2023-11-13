namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.IClientAddressableTestGrain")]
    public interface IClientAddressableTestGrain : IGrainWithIntegerKey
    {
        [Alias("SetTarget")]
        Task SetTarget(IClientAddressableTestClientObject target);
        [Alias("HappyPath")]
        Task<string> HappyPath(string message);
        [Alias("SadPath")]
        Task SadPath(string message);
        [Alias("MicroSerialStressTest")]
        Task MicroSerialStressTest(int iterationCount);
        [Alias("MicroParallelStressTest")]
        Task MicroParallelStressTest(int iterationCount);
    }
}
