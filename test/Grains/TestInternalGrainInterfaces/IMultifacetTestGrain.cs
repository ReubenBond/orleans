namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.IMultifacetTestGrain")]
    public interface IMultifacetTestGrain : IMultifacetReader, IMultifacetWriter
    {
    }
}
