namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.IStreamInterceptionGrain")]
    public interface IStreamInterceptionGrain : IGrainWithGuidKey
    {
        [Alias("GetLastStreamValue")]
        Task<int> GetLastStreamValue();
    }
}