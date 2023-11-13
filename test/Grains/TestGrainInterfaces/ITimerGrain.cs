namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.ITimerGrain")]
    public interface ITimerGrain : IGrainWithIntegerKey
    {
        [Alias("StopDefaultTimer")]
        Task StopDefaultTimer();
        [Alias("GetTimerPeriod")]
        Task<TimeSpan> GetTimerPeriod();
        [Alias("GetCounter")]
        Task<int> GetCounter();
        [Alias("SetCounter")]
        Task SetCounter(int value);
        [Alias("StartTimer")]
        Task StartTimer(string timerName);
        [Alias("StopTimer")]
        Task StopTimer(string timerName);
        [Alias("LongWait")]
        Task LongWait(TimeSpan time);
        [Alias("Deactivate")]
        Task Deactivate();
    }

    [Alias("UnitTests.GrainInterfaces.ITimerCallGrain")]
    public interface ITimerCallGrain : IGrainWithIntegerKey
    {
        [Alias("GetTickCount")]
        Task<int> GetTickCount();
        [Alias("GetException")]
        Task<Exception> GetException();

        [Alias("StartTimer")]
        Task StartTimer(string name, TimeSpan delay);
        [Alias("StopTimer")]
        Task StopTimer(string name);
    }

    [Alias("UnitTests.GrainInterfaces.ITimerRequestGrain")]
    public interface ITimerRequestGrain : IGrainWithIntegerKey
    {
        [Alias("StartAndWaitTimerTick")]
        Task StartAndWaitTimerTick(TimeSpan dueTime);

        [Alias("StartStuckTimer")]
        Task StartStuckTimer(TimeSpan dueTime);

        [Alias("GetRuntimeInstanceId")]
        Task<string> GetRuntimeInstanceId();
    }
}
