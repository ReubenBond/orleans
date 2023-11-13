namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.ISerializerPresenceTest")]
    internal interface ISerializerPresenceTest : IGrainWithGuidKey
    {
        [Alias("SerializerExistsForType")]
        Task<bool> SerializerExistsForType(System.Type param);

        [Alias("TakeSerializedData")]
        Task TakeSerializedData(object data);
    }
}
