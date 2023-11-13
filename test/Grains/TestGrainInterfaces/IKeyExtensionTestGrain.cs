namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.IKeyExtensionTestGrain")]
    public interface IKeyExtensionTestGrain : IGrainWithGuidCompoundKey
    {
        [Alias("GetGrainReference")]
        Task<IKeyExtensionTestGrain> GetGrainReference();
        [Alias("GetActivationId")]
        Task<string> GetActivationId();
    }
}
