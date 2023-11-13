namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.ISimpleDIGrain")]
    public interface ISimpleDIGrain : IGrainWithIntegerKey
    {
        [Alias("GetLongValue")]
        Task<long> GetLongValue();
        [Alias("GetStringValue")]
        Task<string> GetStringValue();
        [Alias("DoDeactivate")]
        Task DoDeactivate();
    }

    [Alias("UnitTests.GrainInterfaces.IDIGrainWithInjectedServices")]
    public interface IDIGrainWithInjectedServices : ISimpleDIGrain
    {
        [Alias("GetGrainFactoryId")]
        Task<long> GetGrainFactoryId();
        [Alias("GetInjectedSingletonServiceValue")]
        Task<string> GetInjectedSingletonServiceValue();
        [Alias("GetInjectedScopedServiceValue")]
        Task<string> GetInjectedScopedServiceValue();
        [Alias("AssertCanResolveSameServiceInstances")]
        Task AssertCanResolveSameServiceInstances();
    }
}
