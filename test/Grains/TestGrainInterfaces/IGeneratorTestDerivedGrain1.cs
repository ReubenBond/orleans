namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.IGeneratorTestDerivedGrain1")]
    public interface IGeneratorTestDerivedGrain1 : IGeneratorTestGrain
    {
        [Alias("ByteAppend")]
        Task<byte[]> ByteAppend(byte[] data);
    }
}