namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.ICollectionTestGrain")]
    public interface ICollectionTestGrain : IGrainWithIntegerKey
    {
        [Alias("GetAge")]
        Task<TimeSpan> GetAge();

        [Alias("IncrCounter")]
        Task<int> IncrCounter();

        [Alias("DeactivateSelf")]
        Task DeactivateSelf();

        [Alias("SetOther")]
        Task SetOther(ICollectionTestGrain other);

        [Alias("GetOtherAge")]
        Task<TimeSpan> GetOtherAge();

        [Alias("GetGrainReference")]
        Task<ICollectionTestGrain> GetGrainReference();

        [Alias("GetRuntimeInstanceId")]
        Task<string> GetRuntimeInstanceId();

        [Alias("StartTimer")]
        Task StartTimer(TimeSpan timerPeriod, TimeSpan delayPeriod);
    }
}
