using Orleans.Concurrency;
using Orleans.Runtime;

namespace UnitTests.GrainInterfaces
{
    [Alias("UnitTests.GrainInterfaces.ITestGrain")]
    public interface ITestGrain : IGrainWithIntegerKey
    {
        // duplicate to verify identity
        [Alias("GetKey")]
        Task<long> GetKey();

        // separate label that can be set
        [Alias("GetLabel")]
        Task<string> GetLabel();

        [Alias("SetLabel")]
        Task SetLabel(string label);

        [Alias("GetRuntimeInstanceId")]
        Task<string> GetRuntimeInstanceId();

        [Alias("GetActivationId")]
        Task<string> GetActivationId();

        [Alias("GetGrainReference")]
        Task<ITestGrain> GetGrainReference();

        [Alias("TestRequestContext")]
        Task<Tuple<string, string>> TestRequestContext();

        [Alias("GetMultipleGrainInterfaces_Array")]
        Task<IGrain[]> GetMultipleGrainInterfaces_Array();

        [Alias("GetMultipleGrainInterfaces_List")]
        Task<List<IGrain>> GetMultipleGrainInterfaces_List();

        [Alias("StartTimer")]
        Task StartTimer();

        [ResponseTimeout("00:00:01")]
        [Alias("DoLongAction")]
        Task DoLongAction(TimeSpan timespan, string str);
    }

    [Alias("UnitTests.GrainInterfaces.ITestGrainLongOnActivateAsync")]
    public interface ITestGrainLongOnActivateAsync : IGrainWithIntegerKey
    {
        [Alias("GetKey")]
        Task<long> GetKey();
    }

    [Alias("UnitTests.GrainInterfaces.IGuidTestGrain")]
    public interface IGuidTestGrain : IGrainWithGuidKey
    {
        // duplicate to verify identity
        [Alias("GetKey")]
        Task<Guid> GetKey();

        // separate label that can be set
        [Alias("GetLabel")]
        Task<string> GetLabel();

        [Alias("SetLabel")]
        Task SetLabel(string label);

        [Alias("GetRuntimeInstanceId")]
        Task<string> GetRuntimeInstanceId();

        [Alias("GetActivationId")]
        Task<string> GetActivationId();

        [Alias("GetSiloAddress")]
        Task<SiloAddress> GetSiloAddress();
    }

    [Alias("UnitTests.GrainInterfaces.IOneWayGrain")]
    public interface IOneWayGrain : IGrainWithGuidKey
    {
        [OneWay]
        [Alias("Notify")]
        Task Notify(ISimpleGrainObserver observer);

        [OneWay]
        [Alias("NotifyValueTask")]
        ValueTask NotifyValueTask(ISimpleGrainObserver observer);

        [OneWay]
        [Alias("ThrowsOneWay")]
        Task ThrowsOneWay();

        [OneWay]
        [Alias("ThrowsOneWayValueTask")]
        ValueTask ThrowsOneWayValueTask();

        [Alias("NotifyOtherGrain")]
        Task<bool> NotifyOtherGrain(IOneWayGrain otherGrain, ISimpleGrainObserver observer);

        [Alias("NotifyOtherGrainValueTask")]
        Task<bool> NotifyOtherGrainValueTask(IOneWayGrain otherGrain, ISimpleGrainObserver observer);

        [Alias("GetOtherGrain")]
        Task<IOneWayGrain> GetOtherGrain();

        [Alias("NotifyOtherGrain1")]
        Task NotifyOtherGrain();

        [Alias("GetCount")]
        Task<int> GetCount();

        [Alias("Deactivate")]
        Task Deactivate();

        [Alias("GetSiloAddress")]
        Task<SiloAddress> GetSiloAddress();

        [Alias("GetPrimaryForGrain")]
        Task<SiloAddress> GetPrimaryForGrain();

        [Alias("GetActivationId")]
        Task<string> GetActivationId();

        [Alias("GetActivationAddress")]
        Task<string> GetActivationAddress(IGrain grain);

        [Alias("SignalSelfViaOther")]
        Task SignalSelfViaOther();

        [OneWay]
        [Alias("SendSignalTo")]
        Task SendSignalTo(IOneWayGrain grain);

        [AlwaysInterleave]
        [Alias("WaitForSignal")]
        Task<(int NumSignals, string SignallerId)> WaitForSignal();

        [AlwaysInterleave]
        [Alias("Signal")]
        Task Signal(string id);
    }

    [Alias("UnitTests.GrainInterfaces.ICanBeOneWayGrain")]
    public interface ICanBeOneWayGrain : IGrainWithGuidKey
    {
        [Alias("Notify")]
        Task Notify(ISimpleGrainObserver observer);

        [Alias("NotifyValueTask")]
        ValueTask NotifyValueTask(ISimpleGrainObserver observer);

        [Alias("Throws")]
        Task Throws();

        [Alias("ThrowsValueTask")]
        ValueTask ThrowsValueTask();

        [Alias("GetCount")]
        Task<int> GetCount();
    }
}
