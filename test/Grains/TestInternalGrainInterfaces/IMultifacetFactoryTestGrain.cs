namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.IMultifacetFactoryTestGrain")]
    public interface IMultifacetFactoryTestGrain : IGrainWithIntegerKey
    {
        [Alias("GetReader")]
        Task<IMultifacetReader> GetReader(IMultifacetTestGrain grain);
        [Alias("GetReader1")]
        Task<IMultifacetReader> GetReader();
        [Alias("GetWriter")]
        Task<IMultifacetWriter> GetWriter(IMultifacetTestGrain grain);
        [Alias("GetWriter1")]
        Task<IMultifacetWriter> GetWriter();
        [Alias("SetReader")]
        Task SetReader(IMultifacetReader reader);
        [Alias("SetWriter")]
        Task SetWriter(IMultifacetWriter writer);
    }
}