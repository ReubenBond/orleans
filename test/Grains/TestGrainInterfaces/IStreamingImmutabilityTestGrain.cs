namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.IStreamingImmutabilityTestGrain")]
    public interface IStreamingImmutabilityTestGrain : IGrainWithGuidKey
    {
        [Alias("SubscribeToStream")]
        Task SubscribeToStream(Guid guid, string providerName);
        [Alias("UnsubscribeFromStream")]
        Task UnsubscribeFromStream();
        [Alias("SendTestObject")]
        Task SendTestObject(string providerName);
        [Alias("SetTestObjectStringProperty")]
        Task SetTestObjectStringProperty(string value);
        [Alias("GetTestObjectStringProperty")]
        Task<string> GetTestObjectStringProperty();
        [Alias("GetSiloIdentifier")]
        Task<string> GetSiloIdentifier();

    }
}