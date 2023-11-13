namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.IActivityGrain")]
    public interface IActivityGrain : IGrainWithIntegerKey
    {
        [Alias("GetActivityId")]
        Task<ActivityData> GetActivityId();
    }

    [GenerateSerializer]
    [Alias("UnitTests.GrainInterfaces.ActivityData")]
    public class ActivityData
    {
        [Id(0)]
        public string Id { get; set; }

        [Id(1)]
        public string TraceState { get; set; }

        [Id(2)]
        public List<KeyValuePair<string, string>> Baggage { get; set; }
    }
}
