namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.IInitialStateGrain")]
    public interface IInitialStateGrain : IGrainWithIntegerKey
    {
        [Alias("GetNames")]
        Task<List<string>> GetNames();
        [Alias("AddName")]
        Task AddName(string name);
    }
}
