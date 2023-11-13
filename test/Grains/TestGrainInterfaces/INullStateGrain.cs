namespace UnitTests.GrainInterfaces
{
    [GenerateSerializer]
    [Alias("UnitTests.GrainInterfaces.NullableState")]
    public class NullableState
    {
        [Id(0)]
        public string Name { get; set; }
    }

    [Alias("UnitTests.GrainInterfaces.INullStateGrain")]
    public interface INullStateGrain : IGrainWithIntegerKey
    {
        [Alias("SetStateAndDeactivate")]
        Task SetStateAndDeactivate(NullableState state);
        [Alias("GetState")]
        Task<NullableState> GetState();
    }
}