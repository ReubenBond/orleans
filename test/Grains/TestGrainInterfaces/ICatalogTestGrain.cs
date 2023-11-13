namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.ICatalogTestGrain")]
    public interface ICatalogTestGrain : IGrainWithIntegerKey
    {
        [Alias("Initialize")]
        Task Initialize();
        [Alias("BlastCallNewGrains")]
        Task BlastCallNewGrains(int nGrains, long startingKey, int nCallsToEach);
    }
}
