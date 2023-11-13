namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.ISimpleGenericGrain`1")]
    public interface ISimpleGenericGrain<T> : IGrainWithIntegerKey
    {
        [Alias("Set")]
        Task Set(T t);

        [Alias("Transform")]
        Task Transform();

        [Alias("Get")]
        Task<T> Get();

        [Alias("CompareGrainReferences")]
        Task CompareGrainReferences(ISimpleGenericGrain<T> clientRef);
    }
}
