namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.IIdleActivationGcTestGrain1")]
    public interface IIdleActivationGcTestGrain1 : IGrainWithGuidKey
    {
        [Alias("Nop")]
        Task Nop();
    }

    [Alias("UnitTests.GrainInterfaces.IIdleActivationGcTestGrain2")]
    public interface IIdleActivationGcTestGrain2 : IGrainWithGuidKey
    {
        [Alias("Nop")]
        Task Nop();
    }

    [Alias("UnitTests.GrainInterfaces.IBusyActivationGcTestGrain1")]
    public interface IBusyActivationGcTestGrain1 : IGrainWithGuidKey
    {
        [Alias("Nop")]
        Task Nop();
        [Alias("Delay")]
        Task Delay(TimeSpan dt);
        [Alias("IdentifyActivation")]
        Task<string> IdentifyActivation();
        [Alias("EnableBurstOnCollection")]
        Task EnableBurstOnCollection(int count);
    }

    [Alias("UnitTests.GrainInterfaces.IBusyActivationGcTestGrain2")]
    public interface IBusyActivationGcTestGrain2 : IGrainWithGuidKey
    {
        [Alias("Nop")]
        Task Nop();
    }

    [Alias("UnitTests.GrainInterfaces.ICollectionSpecificAgeLimitForTenSecondsActivationGcTestGrain")]
    public interface ICollectionSpecificAgeLimitForTenSecondsActivationGcTestGrain : IGrainWithGuidKey
    {
        [Alias("Nop")]
        Task Nop();
    }

    [Alias("UnitTests.GrainInterfaces.ICollectionSpecificAgeLimitForZeroSecondsActivationGcTestGrain")]
    public interface ICollectionSpecificAgeLimitForZeroSecondsActivationGcTestGrain : IGrainWithGuidKey
    {
        [Alias("Nop")]
        Task Nop();
    }

    [Alias("UnitTests.GrainInterfaces.IStatelessWorkerActivationCollectorTestGrain1")]
    public interface IStatelessWorkerActivationCollectorTestGrain1 : IGrainWithGuidKey
    {
        [Alias("Nop")]
        Task Nop();
        [Alias("Delay")]
        Task Delay(TimeSpan dt);
        [Alias("IdentifyActivation")]
        Task<string> IdentifyActivation();
    }
}
